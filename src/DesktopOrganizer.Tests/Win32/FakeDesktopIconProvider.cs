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
    public void SetPosition(int index, PointI position) => _pos[index] = SetPositionSnapHook?.Invoke(position) ?? position;

    // Lattice seam: the fake desktop has no grid, so writes store verbatim — which is also what
    // the real provider does before its first lattice observation. A test can install this hook to
    // emulate Explorer's quantization on every write, and pin the fence-follows-measured-icons
    // contract (2026-09-06 third drift incident: the fence must walk the displacement the icons
    // ACTUALLY took after quantization — precomputing it from one anchor icon diverged the pair).
    public Func<PointI, PointI>? SetPositionSnapHook { get; set; }

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
