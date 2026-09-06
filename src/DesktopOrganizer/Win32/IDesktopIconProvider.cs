using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public interface IDesktopIconProvider : IDisposable
{
    IntPtr Handle { get; }
    bool IsAvailable { get; }
    int Count { get; }
    int IconSpacingX { get; }
    int IconSpacingY { get; }
    IReadOnlyList<DesktopIcon> GetIcons();
    PointI GetPosition(int index);
    void SetPosition(int index, PointI position);

    /// <summary>The displacement a rigid icon group ACTUALLY takes when moving from
    /// <paramref name="from"/> by <paramref name="delta"/>: with Explorer's "align to grid" on,
    /// every written position is quantized onto the 76×82 lattice, so the effective displacement is
    /// delta snapped through the lattice — not delta itself. The drag-restore path must move the
    /// fence by THIS vector (not the cursor delta), or the box and its icons drift apart by up to
    /// half a cell in a fresh random direction on every gesture. A provider without a lattice
    /// (test fakes, grid not yet observed) returns <paramref name="delta"/> unchanged.</summary>
    PointI QuantizeDelta(PointI from, PointI delta);

    /// <summary>True when the desktop listview's "Auto arrange" style is on — in which case
    /// <see cref="SetPosition"/> is ignored (or throws), so callers must clear it first.</summary>
    bool IsAutoArrangeOn { get; }

    /// <summary>Clears the desktop listview's "Auto arrange" style so <see cref="SetPosition"/> is
    /// honored. Returns true when auto-arrange is off afterwards.</summary>
    bool DisableAutoArrange();

    /// <summary>Attempts to re-acquire the desktop hook after it went stale (an Explorer restart
    /// invalidates the cached window handle and the cross-process channel). Returns true when the
    /// provider is available again. A provider that never went stale should return its current
    /// availability without doing anything.</summary>
    bool TryRecover();
}
