# M2 — Desktop Hook: locate SysListView32, read & write real icon positions

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Every task ends with a `git commit`; the review gate runs after each commit.

**Goal:** Cross from "pure logic" into "really drives the desktop". Locate the desktop `SysListView32` via P/Invoke, enumerate the real icons (name + stable path via PIDL + grid position), and rewrite their grid positions so the M1 classifier's output can be physically laid into fence grids. Ship a proof-of-concept that arranges real desktop icons into a computed grid, verified interactively on the real desktop.

**Architecture:** All Win32 lives in `DesktopOrganizer/Win32/`, wrapped in typed methods and a single `IDesktopIconProvider` abstraction so the pure grid math stays unit-testable in `Core`. Path identity is resolved PIDL-based via the desktop `IShellFolder` enumeration (robust across Windows versions) and joined to listview items by display name. The M2 coordinator (`DesktopLayoutService`) wires provider → `ClassifierEngine` → `GridLayoutCalculator` → provider.

**Tech Stack:** .NET 9 (`net9.0` Core / `net9.0-windows` app), xUnit, P/Invoke (user32/shell32/ole32), CommunityToolkit.Mvvm (debug button only).

**Spec:** `docs/superpowers/specs/2026-08-27-desktop-organizer-design.md` (§3.3, §4, §6, §7)

## Global Constraints

- `net9.0` Core / `net9.0-windows` app; `Nullable` + `ImplicitUsings` + `TreatWarningsAsErrors` + `AnalysisLevel latest` are set in `Directory.Build.props` — **zero warnings** required. `AnalysisLevel latest` flags leaked `IntPtr` handles → wrap every OS handle in a `SafeHandle` (no raw `CloseHandle` in business code).
- Namespaces: P/Invoke + provider in `DesktopOrganizer.Win32`; pure layout in `DesktopOrganizer.Core.Layout`; coordinator in `DesktopOrganizer.Services`.
- Commits: English Conventional Commits (`feat:` / `test:` / `fix:` / `refactor:`), single `main`, small incremental commits.
- P/Invoke is **confined to `Win32/`** — no raw `DllImport` in business/ViewModel code.
- Cross-process listview reads require memory allocated inside Explorer (`VirtualAllocEx` + `ReadProcessMemory` + `VirtualFreeEx`); never pass a local `POINT`/`LVITEMW` pointer to a foreign HWND.
- **Auto-arrange trap:** if the desktop listview has `LVS_AUTOARRANGE` (style bit `0x0100`) set, `LVM_SETITEMPOSITION` is silently ignored. The provider must detect this and surface a clear error rather than silently failing.
- Cannot unit-test Win32 interplay on CI/headless. Per spec §7, those paths are covered by **manual verification on the real desktop** + a temporary debug button. Pure logic (grid math, coordinate join, struct layout) gets xUnit tests.

---

### Task 1: Layout primitives — `PointI`, `RectI`, `GridLayoutCalculator` (Core, pure)

**Files:**
- Create: `src/DesktopOrganizer.Core/Layout/PointI.cs`
- Create: `src/DesktopOrganizer.Core/Layout/RectI.cs`
- Create: `src/DesktopOrganizer.Core/Layout/GridLayoutCalculator.cs`
- Test: `src/DesktopOrganizer.Tests/Layout/GridLayoutCalculatorTests.cs`

**Interfaces:**
- `readonly record struct PointI(int X, int Y)` — minimal, no `System.Drawing` dependency in Core.
- `readonly record struct RectI(int X, int Y, int Width, int Height)` with computed `Left/Top/Right/Bottom`, `Contains(PointI)`, `Inflate(int dx, int dy)`.
- `sealed class GridLayoutCalculator` — pure row-major grid math, no Win32, fully unit-testable.
  - `static IReadOnlyList<PointI> Compute(RectI fence, int count, int columns, int cellWidth, int cellHeight, int padX = 0, int padY = 0)`
    - Returns one top-left `PointI` per icon, row-major, wrapping every `columns`.
    - Cell origin = `(fence.X + padX, fence.Y + padY)`; next in row `+= cellWidth`; new row `+= cellHeight`.
    - `count <= 0` → empty list. `columns < 1` → treated as 1. Extra rows may extend past `fence.Bottom` (caller clips later — M3).

- [ ] **Step 1: Write the failing test**

```csharp
using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Layout;

public class GridLayoutCalculatorTests
{
    private static RectI Fence() => new(100, 200, 400, 300);

    [Fact]
    public void ZeroCount_ReturnsEmpty()
        => Assert.Empty(GridLayoutCalculator.Compute(Fence(), 0, 3, 100, 100));

    [Fact]
    public void FirstIcon_AtFenceOriginPlusPadding()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 1, 3, 100, 100, 10, 20);
        Assert.Equal(new PointI(110, 220), Assert.Single(pts));
    }

    [Fact]
    public void WrapsAtColumnCount()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 4, 3, 100, 100);
        Assert.Equal(new PointI(100, 200), pts[0]);   // row 0 col 0
        Assert.Equal(new PointI(200, 200), pts[1]);   // row 0 col 1
        Assert.Equal(new PointI(300, 200), pts[2]);   // row 0 col 2
        Assert.Equal(new PointI(100, 300), pts[3]);   // row 1 col 0 (wrapped)
    }

    [Fact]
    public void ColumnsMinOne_WhenColumnsTooSmall()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 2, 0, 50, 50);
        Assert.Equal(new PointI(100, 200), pts[0]);
        Assert.Equal(new PointI(100, 250), pts[1]);   // single column, stacked
    }
}
```

- [ ] **Step 2: Run test to verify it fails** → compile error (`PointI`/`RectI`/`GridLayoutCalculator` missing).

- [ ] **Step 3: Write minimal implementation**

`PointI.cs`:
```csharp
namespace DesktopOrganizer.Core.Layout;

public readonly record struct PointI(int X, int Y);
```

`RectI.cs`:
```csharp
namespace DesktopOrganizer.Core.Layout;

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
}
```

`GridLayoutCalculator.cs`:
```csharp
using System.Collections.Generic;

namespace DesktopOrganizer.Core.Layout;

public sealed class GridLayoutCalculator
{
    public static IReadOnlyList<PointI> Compute(
        RectI fence, int count, int columns, int cellWidth, int cellHeight, int padX = 0, int padY = 0)
    {
        var result = new List<PointI>();
        if (count <= 0) return result;
        var cols = columns < 1 ? 1 : columns;
        var ox = fence.X + padX;
        var oy = fence.Y + padY;
        for (var i = 0; i < count; i++)
        {
            var row = i / cols;
            var col = i % cols;
            result.Add(new PointI(ox + col * cellWidth, oy + row * cellHeight));
        }
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes** → `dotnet test` PASS.
- [ ] **Step 5: Commit**
```bash
git add src/DesktopOrganizer.Core/Layout src/DesktopOrganizer.Tests/Layout
git commit -m "feat(core): add PointI/RectI and grid layout calculator"
```

---

### Task 2: `IDesktopIconProvider` abstraction + `DesktopIcon` model (app)

**Files:**
- Create: `src/DesktopOrganizer/Win32/DesktopIcon.cs`
- Create: `src/DesktopOrganizer/Win32/IDesktopIconProvider.cs`
- Test: `src/DesktopOrganizer.Tests/Win32/FakeDesktopIconProvider.cs` (fake, used by Task 8 integration test)

**Interfaces:**
- `sealed record DesktopIcon(int Index, string Name, string? Path, PointI Position)` — `Position` is in listview client coordinates; converter to screen lives in the provider/coordinator.
- `interface IDesktopIconProvider` (all members null-safe, never throws for a single missing icon — skip & log):
  - `IntPtr Handle { get; }` — the `SysListView32` HWND (0 if unavailable).
  - `bool IsAvailable { get; }`
  - `int Count { get; }`
  - `IReadOnlyList<DesktopIcon> GetIcons();`
  - `PointI GetPosition(int index);`
  - `void SetPosition(int index, PointI position);` — throws `DesktopAutoArrangeException` if `LVS_AUTOARRANGE` is on.
  - `int IconSpacingX { get; }` / `int IconSpacingY { get; }` — from `LVM_GETITEMSPACING`, so callers align targets to Explorer's snap grid.

- [ ] **Step 1: Write the fake (compile target for later tests)**

```csharp
using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Tests.Win32;

public sealed class FakeDesktopIconProvider : DesktopOrganizer.Win32.IDesktopIconProvider
{
    private readonly Dictionary<int, PointI> _pos = new();
    public IntPtr Handle => IntPtr.Zero;
    public bool IsAvailable => true;
    public int IconSpacingX { get; set; } = 96;
    public int IconSpacingY { get; set; } = 96;
    public List<DesktopIcon> Icons { get; } = new();

    public int Count => Icons.Count;
    public IReadOnlyList<DesktopIcon> GetIcons() => Icons;
    public PointI GetPosition(int index) => _pos.TryGetValue(index, out var p) ? p : new PointI(0, 0);
    public void SetPosition(int index, PointI position) => _pos[index] = position;
}
```

- [ ] **Step 2: Write the interface + model**

`DesktopIcon.cs`:
```csharp
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed record DesktopIcon(int Index, string Name, string? Path, PointI Position);
```

`IDesktopIconProvider.cs`:
```csharp
using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public interface IDesktopIconProvider
{
    IntPtr Handle { get; }
    bool IsAvailable { get; }
    int Count { get; }
    int IconSpacingX { get; }
    int IconSpacingY { get; }
    IReadOnlyList<DesktopIcon> GetIcons();
    PointI GetPosition(int index);
    void SetPosition(int index, PointI position);
}
```

- [ ] **Step 3: Build to verify the fake + interface compile** → `dotnet build` zero warnings.
- [ ] **Step 4: Commit**
```bash
git add src/DesktopOrganizer/Win32/DesktopIcon.cs src/DesktopOrganizer/Win32/IDesktopIconProvider.cs src/DesktopOrganizer.Tests/Win32
git commit -m "feat(win32): add IDesktopIconProvider abstraction and DesktopIcon model"
```

---

### Task 3: `NativeMethods` — all P/Invoke declarations (Win32/)

**Files:**
- Create: `src/DesktopOrganizer/Win32/NativeMethods.cs`
- Create: `src/DesktopOrganizer/Win32/SafeRemoteBufferHandle.cs` (SafeHandle for `VirtualAllocEx`/`VirtualFreeEx`)
- Test: `src/DesktopOrganizer.Tests/Win32/NativeStructTests.cs` (struct layout/size only — CI-safe)

**Interfaces:**
- Confine every `DllImport` here. Wrap allocated memory in a `SafeHandle` (`SafeRemoteBufferHandle`) so `AnalysisLevel latest` does not flag a leak.
- Expose the constants the provider needs.

- [ ] **Step 1: Write the failing test (struct layout)**

```csharp
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Win32;

public class NativeStructTests
{
    [Fact]
    public void LvItemW_HasExpectedFieldOffsets()
    {
        // mask(4) iItem(4) iSubItem(4) state(4) stateMask(4) pszText(8) cchTextMax(4)
        // iImage(4) lParam(8) ... -> size >= 44 on 64-bit
        Assert.True(Marshal.SizeOf<LVITEMW>() >= 44);
    }
}
```

- [ ] **Step 2: Write the implementation**

`SafeRemoteBufferHandle.cs`:
```csharp
using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class SafeRemoteBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly IntPtr _process;
    private readonly IntPtr _size;

    internal SafeRemoteBufferHandle(IntPtr process, IntPtr ptr, IntPtr size) : base(true)
    {
        _process = process;
        _size = size;
        SetHandle(ptr);
    }

    [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
    protected override bool ReleaseHandle()
        => NativeMethods.VirtualFreeEx(_process, handle, _size, 0x8000); // MEM_RELEASE
}
```

`NativeMethods.cs` (key declarations — expand as needed):
```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Win32;

internal static class NativeMethods
{
    // Window hierarchy
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpClassName, string? lpWindowName);

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // Cross-process messaging
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // Remote process memory
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    // LVITEMW (minimal, fields we use)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    // ListView messages
    internal const uint LVM_FIRST = 0x1000;
    internal const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    internal const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;   // 0x1010
    internal const uint LVM_SETITEMPOSITION = LVM_FIRST + 15;   // 0x100F
    internal const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;     // 0x1073
    internal const uint LVM_GETITEMW = LVM_FIRST + 75;          // 0x104B
    internal const uint LVM_GETITEMSPACING = LVM_FIRST + 51;    // 0x1033

    // LVITEM masks
    internal const uint LVIF_TEXT = 0x0001;
    internal const uint LVIF_PARAM = 0x0004;

    // Styles / constants
    internal const int GWL_STYLE = -16;
    internal const int LVS_AUTOARRANGE = 0x0100;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint SMTO_NORMAL = 0x0000;
    internal const uint PROCESS_VM_OPERATION = 0x0008;
    internal const uint PROCESS_VM_READ = 0x0010;
    internal const uint PROCESS_VM_WRITE = 0x0020;
    internal const uint MEM_COMMIT = 0x1000;
    internal const uint MEM_RELEASE = 0x8000;
    internal const uint PAGE_READWRITE = 0x04;
    internal const int MAX_PATH = 260;
}
```

- [ ] **Step 3: Build to verify structs + P/Invoke compile with zero warnings** → `dotnet build` PASS.
- [ ] **Step 4: Commit**
```bash
git add src/DesktopOrganizer/Win32/NativeMethods.cs src/DesktopOrganizer/Win32/SafeRemoteBufferHandle.cs src/DesktopOrganizer.Tests/Win32
git commit -m "feat(win32): add NativeMethods P/Invoke and safe remote buffer handle"
```

---

### Task 4: `DesktopWindowLocator` — find the desktop `SysListView32` (Win32/)

**Files:**
- Create: `src/DesktopOrganizer/Win32/DesktopWindowLocator.cs`
- Create: `src/DesktopOrganizer/Win32/DesktopWindowNotFoundException.cs`

**Interfaces:**
- `static IntPtr FindDesktopListView()` — returns the `SysListView32` HWND.
  - Try `FindWindow("Progman", null)` → `FindWindowEx(..., "SHELLDLL_DefView", null)` → `FindWindowEx(..., "SysListView32", null)`.
  - If `SHELLDLL_DefView` not found directly under Progman (Win10/11 per-monitor wallpaper), `EnumWindows` to find the `WorkerW` that has a `SHELLDLL_DefView` descendant, then descend to `SysListView32`.
- Throws `DesktopWindowNotFoundException` (typed) when no desktop listview is found (shell not loaded / Explorer restart) — caller keeps the app alive in tray per spec §6.

- [ ] **Step 1: Write the exception + locator**

`DesktopWindowNotFoundException.cs`:
```csharp
using System;

namespace DesktopOrganizer.Win32;

public sealed class DesktopWindowNotFoundException : Exception
{
    public DesktopWindowNotFoundException(string message) : base(message) { }
}
```

`DesktopWindowLocator.cs`:
```csharp
using System;

namespace DesktopOrganizer.Win32;

public static class DesktopWindowLocator
{
    public static IntPtr FindDesktopListView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var defView = progman != IntPtr.Zero
            ? NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;

        IntPtr workerW = IntPtr.Zero;
        if (defView == IntPtr.Zero)
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    workerW = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (workerW != IntPtr.Zero)
                defView = NativeMethods.FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (defView == IntPtr.Zero)
            throw new DesktopWindowNotFoundException("Desktop SHELLDLL_DefView not found (shell not ready?).");

        var listView = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero)
            throw new DesktopWindowNotFoundException("Desktop SysListView32 not found.");
        return listView;
    }
}
```

- [ ] **Step 2: Build (no unit test — needs real desktop; covered by Task 8 manual verification).**
- [ ] **Step 3: Commit**
```bash
git add src/DesktopOrganizer/Win32/DesktopWindowLocator.cs src/DesktopOrganizer/Win32/DesktopWindowNotFoundException.cs
git commit -m "feat(win32): add DesktopWindowLocator with WorkerW fallback"
```

---

### Task 5: `LvItemMarshaller` — cross-process LVITEM/POINT read helper (Win32/)

**Files:**
- Create: `src/DesktopOrganizer/Win32/LvItemMarshaller.cs`
- Test: `src/DesktopOrganizer.Tests/Win32/LvItemMarshallerTests.cs` (struct round-trip via local process — CI-safe because we allocate in *our own* process here)

**Interfaces:**
- Helper that, given the listview HWND + target process handle, performs the allocate-in-target → send → read-back dance for a string (`GetItemText`) and a `POINT` (`GetItemPosition`). All buffers wrapped in `SafeRemoteBufferHandle`; `OpenProcess` handle wrapped in `SafeProcessHandle`.
- This is the single place that knows the remote-memory choreography, so the provider stays readable.

- [ ] **Step 1: Write the failing test (round-trip a string + point in our own process)**

```csharp
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Win32;

public class LvItemMarshallerTests
{
    [Fact]
    public void RoundTripsUnicodeViaLocalProcess()
    {
        // Use our own process id so no cross-process privilege is needed in CI.
        var pid = Environment.ProcessId;
        using var m = new LvItemMarshaller(pid);
        Assert.Equal("héllo桌面", m.RoundTripString("héllo桌面"));
    }
}
```

> The marshaller's `RoundTripString` is a CI-safe self-test path that allocates in the *current* process; the real `ReadItemText`/`ReadItemPosition` overloads take an HWND and reuse the same alloc/read logic against Explorer's pid.

- [ ] **Step 2: Write the implementation**

`LvItemMarshaller.cs`:
```csharp
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Win32;

internal sealed class LvItemMarshaller : IDisposable
{
    private readonly SafeProcessHandle _process;

    internal LvItemMarshaller(int processId)
    {
        var h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
            false, processId);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _process = new SafeProcessHandle(h, ownsHandle: true);
    }

    internal string ReadItemText(IntPtr listView, int index, uint msg, int maxChars = NativeMethods.MAX_PATH)
    {
        var size = (IntPtr)(maxChars * 2);
        using var buf = Alloc(size);
        var item = new NativeMethods.LVITEMW
        {
            mask = NativeMethods.LVIF_TEXT,
            iItem = index,
            pszText = buf.DangerousGetHandle(),
            cchTextMax = maxChars,
        };
        Write(item, buf);
        var pItem = MarshalToRemote(item, out var remoteItem);
        try
        {
            Send(listView, msg, (IntPtr)index, pItem);
            var bytes = new byte[maxChars * 2];
            if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), bytes, size, out _))
                return string.Empty;
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally { buf.Dispose(); }
    }

    internal (int X, int Y) ReadItemPosition(IntPtr listView, int index)
    {
        var size = (IntPtr)8;
        using var buf = Alloc(size);
        var pItem = Marshal.AllocHGlobal(8);
        try
        {
            Send(listView, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)index, buf.DangerousGetHandle());
            var bytes = new byte[8];
            if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), bytes, size, out _))
                return (0, 0);
            return (BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
        }
        finally { Marshal.FreeHGlobal(pItem); }
    }

    private SafeRemoteBufferHandle Alloc(IntPtr size)
    {
        var ptr = NativeMethods.VirtualAllocEx(_process.DangerousGetHandle(), IntPtr.Zero, size,
            NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
        if (ptr == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SafeRemoteBufferHandle(_process.DangerousGetHandle(), ptr, size);
    }

    private void Write(NativeMethods.LVITEMW item, SafeRemoteBufferHandle buf)
    {
        var data = new byte[Marshal.SizeOf<NativeMethods.LVITEMW>()];
        var p = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.StructureToPtr(item, p, false);
            Marshal.Copy(p, data, 0, data.Length);
        }
        finally { Marshal.FreeHGlobal(p); }
        if (!NativeMethods.WriteProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), data, (IntPtr)data.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr MarshalToRemote(NativeMethods.LVITEMW item, out SafeRemoteBufferHandle remote)
    {
        var data = new byte[Marshal.SizeOf<NativeMethods.LVITEMW>()];
        var p = Marshal.AllocHGlobal(data.Length);
        try { Marshal.StructureToPtr(item, p, false); Marshal.Copy(p, data, 0, data.Length); }
        finally { Marshal.FreeHGlobal(p); }
        remote = Alloc((IntPtr)data.Length);
        if (!NativeMethods.WriteProcessMemory(_process.DangerousGetHandle(), remote.DangerousGetHandle(), data, (IntPtr)data.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return remote.DangerousGetHandle();
    }

    private static void Send(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (NativeMethods.SendMessageTimeout(hwnd, msg, w, l,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL, 2000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // CI-safe self round-trip used by tests
    internal string RoundTripString(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        using var buf = Alloc((IntPtr)bytes.Length);
        if (!NativeMethods.WriteProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), bytes, (IntPtr)bytes.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var outBytes = new byte[bytes.Length];
        if (!NativeMethods.ReadProcessMemory(_process.DangerousGetHandle(), buf.DangerousGetHandle(), outBytes, (IntPtr)bytes.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Encoding.Unicode.GetString(outBytes).TrimEnd('\0');
    }

    public void Dispose() => _process.Dispose();
}
```

> Note: `SafeProcessHandle` is `Microsoft.Win32.SafeHandles.SafeProcessHandle` (BCL). Add `using Microsoft.Win32.SafeHandles;`.

- [ ] **Step 3: Run test** → `dotnet test` PASS (self round-trip in current process).
- [ ] **Step 4: Commit**
```bash
git add src/DesktopOrganizer/Win32/LvItemMarshaller.cs src/DesktopOrganizer.Tests/Win32
git commit -m "feat(win32): add cross-process LVITEM/POINT marshaller"
```

---

### Task 6: `SysListView32Provider` — implement `IDesktopIconProvider` (Win32/)

**Files:**
- Create: `src/DesktopOrganizer/Win32/SysListView32Provider.cs`
- Create: `src/DesktopOrganizer/Win32/DesktopAutoArrangeException.cs`
- Create: `src/DesktopOrganizer/Win32/DesktopShellEnumerator.cs` (PIDL → path via `IShellFolder`)
- Test: manual verification on real desktop (no CI). A `FakeDesktopIconProvider` already exists (Task 2) for the coordinator integration test.

**Interfaces:**
- `SysListView32Provider` : `IDesktopIconProvider`
  - ctor resolves HWND via `DesktopWindowLocator`; `IsAvailable` false if `DesktopWindowNotFoundException`.
  - `GetIcons()`: count via `LVM_GETITEMCOUNT`; for each index, `Name` via `LvItemMarshaller.ReadItemText(LVM_GETITEMTEXTW)`; `Position` via `ReadItemPosition`; `Path` resolved by `DesktopShellEnumerator` keyed by `Name` (PIDL → parsing name). Skips individual failures (log + continue).
  - `IconSpacingX/Y`: `SendMessage(LVM_GETITEMSPACING, TRUE)` returns `MAKELONG(cx, cy)`.
  - `SetPosition(index, p)`: if `(GetWindowLong(Handle, GWL_STYLE) & LVS_AUTOARRANGE) != 0` → throw `DesktopAutoArrangeException` (clear, actionable message: turn off "Auto arrange" in desktop View menu). Else `SendMessage(LVM_SETITEMPOSITION, index, MAKELPARAM(p.X, p.Y))`.
- `DesktopShellEnumerator`: enumerate desktop `IShellFolder` (`SHGetDesktopFolder` + `EnumObjects`), for each item get display name (`SHGDN_NORMAL`) and parsing name (`SHGDN_FORPARSING`) via `GetDisplayNameOf`. Expose `IReadOnlyDictionary<string, string> DisplayNameToPath`. This is the PIDL-based path source of truth; joined to listview items by display name (documented limitation: duplicate display names resolve to first match — M5 can harden via lParam PIDL extraction).

- [ ] **Step 1: Write the exception**

```csharp
using System;

namespace DesktopOrganizer.Win32;

public sealed class DesktopAutoArrangeException : Exception
{
    public DesktopAutoArrangeException(string message) : base(message) { }
}
```

- [ ] **Step 2: Write `DesktopShellEnumerator` (PIDL-based path resolution)**

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopOrganizer.Win32;

internal static class DesktopShellEnumerator
{
    public static IReadOnlyDictionary<string, string> DisplayNameToPath()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var desktop = (IShellFolder)null!;
        SHGetDesktopFolder(out desktop);
        try
        {
            desktop.EnumObjects(IntPtr.Zero,
                SHCONTF.FOLDER | SHCONTF.NONFOLDER | SHCONTF.INCLUDEHIDDEN,
                out var enumId);
            if (enumId == null) return map;
            var pidls = new IntPtr[1];
            while (enumId.Next(1, pidls, IntPtr.Zero) == 0)
            {
                var pidl = pidls[0];
                var display = GetDisplayName(desktop, pidl, SHGDN.NORMAL);
                var path = GetDisplayName(desktop, pidl, SHGDN.FORPARSING);
                if (!string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(path))
                    map[display] = path;
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally { Marshal.ReleaseComObject(desktop); }
        return map;
    }

    private static string GetDisplayName(IShellFolder folder, IntPtr pidl, SHGDN flags)
    {
        var strret = new STRRET();
        folder.GetDisplayNameOf(pidl, flags, out strret);
        return strret.cStr is not null ? Marshal.PtrToStringUni(strret.cStr) ?? string.Empty : string.Empty;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [Flags] private enum SHCONTF : uint { FOLDER = 0x20, NONFOLDER = 0x40, INCLUDEHIDDEN = 0x80 }
    [Flags] private enum SHGDN : uint { NORMAL = 0x0, FORPARSING = 0x8000 }
}
```

> Full `IShellFolder`/`IEnumIDList`/`STRRET` COM definitions are required; define minimal `[ComImport]` interfaces in this file (omit here for brevity — mirror the standard pinvoke.net signatures, `PreserveSig` where needed). Keep all COM in `Win32/`.

- [ ] **Step 3: Write `SysListView32Provider`**

```csharp
using System;
using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed class SysListView32Provider : IDesktopIconProvider, IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly bool _available;
    private readonly Dictionary<string, string> _nameToPath;
    private LvItemMarshaller? _marshaller;

    public SysListView32Provider()
    {
        try
        {
            _hwnd = DesktopWindowLocator.FindDesktopListView();
            _available = _hwnd != IntPtr.Zero;
            _nameToPath = _available ? DesktopShellEnumerator.DisplayNameToPath() : new();
        }
        catch (DesktopWindowNotFoundException)
        {
            _hwnd = IntPtr.Zero; _available = false; _nameToPath = new();
        }
    }

    public IntPtr Handle => _hwnd;
    public bool IsAvailable => _available;
    public int IconSpacingX => Spacing(1);
    public int IconSpacingY => Spacing(0);

    private int Spacing(int which)
    {
        var r = NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_GETITEMSPACING, (IntPtr)1, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out var res);
        if (r == IntPtr.Zero) return 96;
        var v = (int)res;
        return which == 1 ? (v & 0xFFFF) : (v >> 16);
    }

    public int Count
    {
        get
        {
            if (!_available) return 0;
            NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 2000, out var c);
            return (int)c;
        }
    }

    public IReadOnlyList<DesktopIcon> GetIcons()
    {
        var result = new List<DesktopIcon>();
        if (!_available) return result;
        EnsureMarshaller();
        var n = Count;
        for (var i = 0; i < n; i++)
        {
            try
            {
                var name = _marshaller!.ReadItemText(_hwnd, i, NativeMethods.LVM_GETITEMTEXTW);
                var (x, y) = _marshaller.ReadItemPosition(_hwnd, i);
                _nameToPath.TryGetValue(name, out var path);
                result.Add(new DesktopIcon(i, name, path, new PointI(x, y)));
            }
            catch (Win32Exception) { /* skip one icon, keep going */ }
        }
        return result;
    }

    public PointI GetPosition(int index)
    {
        EnsureMarshaller();
        var (x, y) = _marshaller!.ReadItemPosition(_hwnd, index);
        return new PointI(x, y);
    }

    public void SetPosition(int index, PointI position)
    {
        if (!_available) return;
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.LVS_AUTOARRANGE) != 0)
            throw new DesktopAutoArrangeException(
                "Desktop has 'Auto arrange' ON — positions are ignored. Turn it off (right-click desktop → View → uncheck Auto arrange) and retry.");
        var lp = (IntPtr)((position.Y << 16) | (position.X & 0xFFFF));
        NativeMethods.SendMessageTimeout(_hwnd, NativeMethods.LVM_SETITEMPOSITION, (IntPtr)index, lp,
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out _);
    }

    private void EnsureMarshaller()
    {
        if (_marshaller is not null) return;
        NativeMethods.GetWindowThreadProcessId(_hwnd, out var pid);
        _marshaller = new LvItemMarshaller(pid);
    }

    public void Dispose() => _marshaller?.Dispose();
}
```

- [ ] **Step 4: Build** → `dotnet build -c Release` zero warnings (watch `AnalysisLevel latest` on the COM/IntPtr usage — wrap or suppress with justification, never silently).
- [ ] **Step 5: Commit**
```bash
git add src/DesktopOrganizer/Win32/SysListView32Provider.cs src/DesktopOrganizer/Win32/DesktopShellEnumerator.cs src/DesktopOrganizer/Win32/DesktopAutoArrangeException.cs
git commit -m "feat(win32): implement SysListView32Provider with PIDL path resolution"
```

---

### Task 7: `DesktopLayoutService` — M2 coordinator + debug harness (app)

**Files:**
- Create: `src/DesktopOrganizer/Services/DesktopLayoutService.cs`
- Edit: `src/DesktopOrganizer/MainWindow.xaml` + `MainWindow.xaml.cs` (temporary "Arrange (M2 PoC)" button — removed/repurposed in M6)

**Interfaces:**
- `DesktopLayoutService` ties provider → `ClassifierEngine` → `GridLayoutCalculator` → provider.
  - `void ArrangeIntoFence(RectI fence, int columns)`: read icons via `IDesktopIconProvider`; build `IconEntry` per icon (Name, Path, LinkTargetApp=null for now — M4 resolves .lnk targets); classify each; compute grid targets via `GridLayoutCalculator.Compute` using `IconSpacingX/Y` as cell size; `SetPosition` each icon. Returns a per-icon (classified category, applied position) report for logging/verification.
  - Guard: if `!provider.IsAvailable` → log + no-op; if `SetPosition` throws `DesktopAutoArrangeException` → surface message to UI (don't crash).
- Debug button calls `ArrangeIntoFence(new RectI(0, 0, 800, 600), columns: 4)` so we can watch real icons rearrange on the desktop.

- [ ] **Step 1: Write the service**

```csharp
using System.Collections.Generic;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Services;

public sealed class DesktopLayoutService
{
    private readonly IDesktopIconProvider _provider;
    private readonly ClassifierEngine _engine;
    private readonly ClassifierConfig _config;

    public DesktopLayoutService(IDesktopIconProvider provider, ClassifierEngine engine, ClassifierConfig config)
    {
        _provider = provider; _engine = engine; _config = config;
    }

    public IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> ArrangeIntoFence(
        RectI fence, int columns)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, Category, PointI)>();
        var icons = _provider.GetIcons();
        var targets = GridLayoutCalculator.Compute(fence, icons.Count, columns,
            _provider.IconSpacingX, _provider.IconSpacingY);
        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < icons.Count && i < targets.Count; i++)
        {
            var icon = icons[i];
            var entry = new IconEntry(icon.Index, icon.Name, icon.Path ?? string.Empty, null);
            var category = _engine.Classify(entry, _config);
            try { _provider.SetPosition(icon.Index, targets[i]); }
            catch (DesktopAutoArrangeException ex) { throw; } // bubble to UI
            report.Add((icon, category, targets[i]));
        }
        return report;
    }
}
```

- [ ] **Step 2: Wire a temporary debug button in `MainWindow`** (behind a `#if DEBUG` or a clearly-marked dev command; removed in M6). Calls `ArrangeIntoFence`.
- [ ] **Step 3: Manual verification on real desktop** (spec §7):
  1. Build & run (`dotnet run --project src/DesktopOrganizer`).
  2. Confirm desktop has "Auto arrange" OFF.
  3. Click the debug button → real desktop icons move into a 4-column grid at (0,0).
  4. Re-run after dragging icons apart → they snap back into the grid (proves read+write both work).
  5. Verify `IconSpacingX/Y` matches the visual snap pitch.
- [ ] **Step 4: Commit**
```bash
git add src/DesktopOrganizer/Services/DesktopLayoutService.cs src/DesktopOrganizer/MainWindow.xaml src/DesktopOrganizer/MainWindow.xaml.cs
git commit -m "feat(app): add DesktopLayoutService coordinator and M2 PoC debug button"
```

---

### Task 8: Full suite green + Release zero-warning build + leakage check

**Files:** (no new code — verification pass)

- [ ] **Step 1: Run the entire test suite** → `dotnet test` all PASS.
- [ ] **Step 2: Full Release build with warnings-as-errors** → `dotnet build -c Release` succeeds, 0 warnings, 0 errors.
- [ ] **Step 3: Confirm P/Invoke is confined to `Win32/`** — grep for `DllImport` outside `src/DesktopOrganizer/Win32/`; expect none.
- [ ] **Step 4: Confirm no raw `IntPtr` handle leaks** — `AnalysisLevel latest` already enforces; manually confirm `SafeHandle` wrapping on `OpenProcess`/`VirtualAllocEx`.
- [ ] **Step 5: Commit (only if changed) + push**
```bash
git add -A
git commit -m "test: finalize M2 desktop-hook verification" --allow-empty
git push origin main
```

---

## Self-Review

- **Spec coverage (§3.3)**: locate `SysListView32` (Task 4), enumerate via `LVM_GETITEMCOUNT`/`LVM_GETITEMTEXTW`/`LVM_GETITEMPOSITION` (Task 6), write via `LVM_SETITEMPOSITION` (Task 6), grid compute+apply (Task 1 + Task 7). ✓
- **Spec coverage (§4)**: `RectI`/`PointI` introduced (Task 1), `IconEntry` already in Core. ✓
- **Spec coverage (§6)**: `DesktopWindowNotFoundException` + `IsAvailable` keep app alive when shell missing; per-icon failures skipped, never move files. ✓
- **PIDL identity (user-chosen)**: paths resolved via desktop `IShellFolder` (`SHGDN_FORPARSING`) joined by display name (Task 6). Limitation documented; lParam-PIDL extraction deferred to M5 hardening. ✓
- **Auto-arrange trap**: detected in `SetPosition`, throws clear `DesktopAutoArrangeException`. ✓
- **Testability**: pure grid math (Task 1) + struct layout (Task 3) + marshaller self round-trip (Task 5) + fake provider (Task 2) are unit-tested on CI; real Win32 interplay verified manually (Task 7 step 3). ✓
- **Warnings-as-errors**: `SafeRemoteBufferHandle`/`SafeProcessHandle` + COM confined to `Win32/`. ✓
- **Placeholder scan**: every step has concrete code or a named file; COM interface bodies noted as "mirror pinvoke.net" with explicit instruction to keep them in `Win32/`. ✓
- **Type consistency**: `DesktopIcon(Index, Name, Path, Position)`, `IDesktopIconProvider` members, `GridLayoutCalculator.Compute(RectI,int,int,int,int,int,int)`, `ArrangeIntoFence(RectI,int)` — names stable across tasks. ✓
