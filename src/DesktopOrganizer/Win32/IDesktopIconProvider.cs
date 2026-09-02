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
}
