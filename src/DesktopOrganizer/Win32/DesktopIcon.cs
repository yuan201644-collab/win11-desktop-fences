using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Win32;

/// <summary>A desktop icon. <see cref="Position"/> is in screen coordinates (same space as WPF/System.Windows).</summary>
public sealed record DesktopIcon(int Index, string Name, string? Path, PointI Position);
