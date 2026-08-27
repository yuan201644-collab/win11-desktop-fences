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
