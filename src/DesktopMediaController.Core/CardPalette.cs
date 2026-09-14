namespace DesktopMediaController.Core;

/// <summary>
/// Preset primary colors and the single-color derivation used by the card's right-click color menu.
/// Picking one primary derives the four accent channels below, so a palette click is always coherent
/// instead of asking the user to tune four channels. Pure math on bytes — unit-tested.
/// Mirrors FencePalette in DesktopOrganizer.Core.
/// </summary>
public static class CardPalette
{
    /// <summary>Preset themes offered in the color menu. Index 0 is the shipping default blue.</summary>
    public static readonly CardTheme[] Presets =
    {
        CardTheme.Default,
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x2E, 0x7D, 0x5B)), // green
        FromPrimary(ArgbColor.FromArgb(0xFF, 0xB2, 0x6E, 0x2E)), // amber
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x9C, 0x3B, 0x3B)), // brick red
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x6A, 0x4E, 0x9C)), // violet
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x2E, 0x8B, 0x8B)), // teal
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x3A, 0x6E, 0xA5)), // sky blue
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x8B, 0x5E, 0x3C)), // brown
        FromPrimary(ArgbColor.FromArgb(0xFF, 0x6E, 0x6E, 0x6E)), // gray
        FromPrimary(ArgbColor.FromArgb(0xFF, 0xC9, 0x9A, 0x3E)), // gold
    };

    /// <summary>
    /// Background fill hues offered in the color menu. Opaque here so the swatch shows the true colour;
    /// the card's actual transparency comes from the separate slider and is kept when a hue is picked.
    /// </summary>
    public static readonly ArgbColor[] BackgroundPresets =
    {
        ArgbColor.FromArgb(0xFF, 0x1A, 0x1A, 0x20), // near-black
        ArgbColor.FromArgb(0xFF, 0x00, 0x00, 0x00), // black
        ArgbColor.FromArgb(0xFF, 0x10, 0x18, 0x26), // dark blue
        ArgbColor.FromArgb(0xFF, 0x0E, 0x1A, 0x12), // dark green
        ArgbColor.FromArgb(0xFF, 0x22, 0x2A, 0x36), // slate
        ArgbColor.FromArgb(0xFF, 0x2A, 0x1A, 0x10), // dark amber
        ArgbColor.FromArgb(0xFF, 0xE8, 0xE8, 0xEC), // light
    };

    /// <summary>
    /// Derives the four accent channels from one primary color. Progress is the color itself; the border
    /// is the same color at ~30% alpha; the label (source chip, pinned pin) is the color pushed most of
    /// the way toward white at ~70% alpha so it reads as a soft tint on the dark card; the sung lyric
    /// color is the color lifted just slightly so it sits a touch brighter than the progress fill. The
    /// background is the fixed dark fill — it is tuned on its own, not from the accent.
    /// </summary>
    public static CardTheme FromPrimary(ArgbColor primary)
    {
        byte a = primary.A == 0 ? (byte)0xFF : primary.A;
        var opaque = primary with { A = a };
        return new CardTheme(
            Accent: opaque,
            Sung: Lighten(opaque, 0.06f),
            Progress: opaque,
            Border: WithAlpha(opaque, (byte)(a * 0x4D / 0xFF)),
            Label: WithAlpha(Lighten(opaque, 0.62f), (byte)(a * 0xB3 / 0xFF)),
            Background: CardTheme.DarkBackground);
    }

    /// <summary>
    /// The lyric panel's fill: the card background pushed one step darker, so the panel reads as an
    /// inset of the card while staying in the same colour family. Derived, not a fourth channel —
    /// one less thing for the color menu to expose.
    /// </summary>
    public static ArgbColor PanelFromBackground(ArgbColor background) => Darken(background, 0.72);

    /// <summary>
    /// Perceived brightness of the background on the 0–255 YIQ luma scale — the cheap standard for
    /// "will white text survive on this". Alpha is ignored on purpose: the slider scales the whole
    /// card's translucency and the desktop behind it is unknowable, so the picked colour is what the
    /// decision is made on.
    /// </summary>
    public static int Luma(ArgbColor c) => (299 * c.R + 587 * c.G + 114 * c.B) / 1000;

    /// <summary>Luma at or above which the neutral text tiers flip to the ink family.</summary>
    /// <remarks>145 sits between the darkest background preset's luma (26) and the light preset's
    /// (232) with room on both sides; mid-greys near it are genuinely ambiguous either way.</remarks>
    public const int LightBackgroundLuma = 145;

    /// <summary>True when the neutral whites must flip to ink for the text to stay readable.</summary>
    public static bool IsLightBackground(ArgbColor background) => Luma(background) >= LightBackgroundLuma;

    /// <summary>Scales a colour's RGB toward black, keeping its alpha. <paramref name="factor"/> of 0
    /// gives black, 1 gives the input untouched.</summary>
    public static ArgbColor Darken(ArgbColor c, double factor)
    {
        var f = Math.Clamp(factor, 0d, 1d);
        return ArgbColor.FromArgb(c.A,
            (byte)Math.Round(c.R * f),
            (byte)Math.Round(c.G * f),
            (byte)Math.Round(c.B * f));
    }

    private static ArgbColor Lighten(ArgbColor c, float t) =>
        ArgbColor.FromArgb(c.A,
            (byte)(c.R + (255 - c.R) * t),
            (byte)(c.G + (255 - c.G) * t),
            (byte)(c.B + (255 - c.B) * t));

    private static ArgbColor WithAlpha(ArgbColor c, byte a) => c with { A = a };
}
