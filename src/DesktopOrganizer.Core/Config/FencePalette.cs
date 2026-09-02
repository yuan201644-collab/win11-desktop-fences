namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Preset palettes and the primary-color derivation used by the header's 换色 submenu. Picking a
/// single primary color derives all four <see cref="OverlayAppearance"/> channels so one click gives
/// a coherent look (translucent fill, stronger border/header, readable title text) instead of asking
/// the user to tune four channels per box. Pure math on bytes — unit-tested.
/// </summary>
public static class FencePalette
{
    /// <summary>Preset primary colors offered in the 换色 submenu.</summary>
    public static readonly ArgbColor[] Presets =
    {
        ArgbColor.FromArgb(0xFF, 0x4E, 0x68, 0x92), // slate blue (matches the default)
        ArgbColor.FromArgb(0xFF, 0x2E, 0x7D, 0x5B), // green
        ArgbColor.FromArgb(0xFF, 0xB2, 0x6E, 0x2E), // amber
        ArgbColor.FromArgb(0xFF, 0x9C, 0x3B, 0x3B), // brick red
        ArgbColor.FromArgb(0xFF, 0x6A, 0x4E, 0x9C), // violet
        ArgbColor.FromArgb(0xFF, 0x2E, 0x8B, 0x8B), // teal
        ArgbColor.FromArgb(0xFF, 0x3A, 0x6E, 0xA5), // sky blue
        ArgbColor.FromArgb(0xFF, 0x8B, 0x5E, 0x3C), // brown
        ArgbColor.FromArgb(0xFF, 0x6E, 0x6E, 0x6E), // gray
        ArgbColor.FromArgb(0xFF, 0xC9, 0x9A, 0x3E), // gold
    };

    /// <summary>
    /// Derives a full four-channel appearance from one primary color. Fill is the color at low alpha
    /// so the box reads as a soft tint; border/header carry it at higher alpha; the title text is
    /// white unless the primary is bright (luminance over a threshold), where black reads better.
    /// </summary>
    public static OverlayAppearance FromPrimary(ArgbColor primary)
    {
        byte a = primary.A == 0 ? (byte)0xFF : primary.A;
        var opaque = primary with { A = a };
        var darkText = Luminance(opaque) > 0.55;

        return new OverlayAppearance(
            Fill: opaque with { A = (byte)(a * 0x14 / 0xFF) },         // ~8% of the picked alpha
            Border: opaque with { A = (byte)(a * 0x7D / 0xFF) },       // ~49%
            Header: opaque with { A = (byte)(a * 0x9C / 0xFF) },       // ~61%
            HeaderText: darkText
                ? ArgbColor.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
                : ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Rec709 luma of the RGB channels (alpha ignored) normalized to 0..1.</summary>
    private static double Luminance(ArgbColor c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
