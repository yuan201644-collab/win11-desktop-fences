using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Tests.Win32;

public sealed class FakeDesktopIconProvider : IDesktopIconProvider
{
    private readonly Dictionary<int, PointI> _pos = new();
    public IntPtr Handle => IntPtr.Zero;
    public bool IsAvailable => true;
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
    public bool DisableAutoArrange() => true;

    public void Dispose() { }
}
