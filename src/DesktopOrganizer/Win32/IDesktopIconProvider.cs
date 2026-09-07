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

    /// <summary>True when the desktop listview's "Auto arrange" style is on — in which case
    /// <see cref="SetPosition"/> is ignored (or throws), so callers must clear it first.</summary>
    bool IsAutoArrangeOn { get; }

    /// <summary>Clears the desktop listview's "Auto arrange" style so <see cref="SetPosition"/> is
    /// honored. Returns true when auto-arrange is off afterwards.</summary>
    bool DisableAutoArrange();

    /// <summary>Reports the desktop's icon-grid pitch when it is known. The drag snap preview uses
    /// it to show where the box will land BEFORE release; false (unknown pitch) means "no preview".
    /// Grid phase is deliberately NOT reported — the snapped displacement of a group is
    /// phase-independent (every on-lattice start point moves by the same snapped delta).</summary>
    bool TryGetLatticeCell(out int cellCx, out int cellCy);

    /// <summary>Attempts to re-acquire the desktop hook after it went stale (an Explorer restart
    /// invalidates the cached window handle and the cross-process channel). Returns true when the
    /// provider is available again. A provider that never went stale should return its current
    /// availability without doing anything.</summary>
    bool TryRecover();
}
