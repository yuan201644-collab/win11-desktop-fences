using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Tests.Win32;

public sealed class FakeDesktopIconProvider : IDesktopIconProvider
{
    private readonly Dictionary<int, PointI> _pos = new();
    public IntPtr Handle => IntPtr.Zero;
    public bool IsAvailable { get; set; } = true;
    public int IconSpacingX { get; set; } = 96;
    public int IconSpacingY { get; set; } = 96;
    public List<DesktopIcon> Icons { get; } = new();

    public int Count => Icons.Count;
    // Project each icon's current position from the recorded store so callers (and the controller's
    // collapse/expand orchestration) observe parked/restored positions the same way the real provider
    // re-reads live desktop state on every GetIcons() call.
    public IReadOnlyList<DesktopIcon> GetIcons() =>
        Icons.Select(ic => new DesktopIcon(ic.Index, ic.Name, ic.Path,
            _pos.TryGetValue(ic.Index, out var p) ? p : ic.Position)).ToList();
    public PointI GetPosition(int index) => _pos.TryGetValue(index, out var p) ? p : new PointI(0, 0);
    public void SetPosition(int index, PointI position) => _pos[index] = position;

    // Test control: the fake desktop never has auto-arrange on by default, and "disabling" it is a no-op.
    public bool IsAutoArrangeOn { get; set; }
    // Lets a test simulate auto-arrange that CANNOT be turned off (the real Windows shell sometimes
    // refuses), so the collapse/arrange guard paths that refuse rather than half-apply can be exercised.
    public bool DisableAutoArrangeResult { get; set; } = true;
    public bool DisableAutoArrange() => DisableAutoArrangeResult;

    // Recovery seam: a test sets IsAvailable=false (shell gone), later flips it back, and the
    // controller's per-tick TryRecover attempt should resume. Return value mirrors the real
    // provider: true when (now) available.
    public Func<bool>? TryRecoverHook { get; set; }
    public bool TryRecover() => TryRecoverHook?.Invoke() ?? IsAvailable;

    public void Dispose() { }
}
