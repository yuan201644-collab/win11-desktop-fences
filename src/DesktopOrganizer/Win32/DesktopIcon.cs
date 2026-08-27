using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

public sealed record DesktopIcon(int Index, string Name, string? Path, PointI Position);
