namespace DesktopOrganizer.Core.Config;

/// <summary>
/// A single 32-bit ARGB color. Plain bytes so Core stays free of UI/OS color types
/// (System.Drawing / System.Windows.Media); the overlay converts these to a WPF brush.
/// </summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

/// <summary>
/// User-tunable colors for the fence overlay: the translucent box fill, its border,
/// the title-bar band at each box's top, and the title text on that band. Pure data,
/// persisted as JSON and unit-tested. Defaults are the soft, low-key palette that does
/// not tint the wallpaper.
/// </summary>
public sealed record OverlayAppearance(ArgbColor Fill, ArgbColor Border, ArgbColor Header, ArgbColor HeaderText)
{
    public static OverlayAppearance Default => new(
        Fill: ArgbColor.FromArgb(0x0E, 0x2E, 0x3A, 0x5A),
        Border: ArgbColor.FromArgb(0x7D, 0xC6, 0xCF, 0xE8),
        Header: ArgbColor.FromArgb(0x9C, 0x4E, 0x68, 0x92),
        HeaderText: ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
}