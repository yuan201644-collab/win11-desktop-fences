namespace DesktopMediaController.Core;

/// <summary>
/// The neutral text/glyph tier set, derived from the background's lightness.
/// </summary>
/// <remarks>
/// The background is user-configurable, so the whites the card has always used are wrong the moment
/// the user picks a light one. Rather than a second color channel for the user to manage, the tiers
/// come in exactly two families — white-on-dark and ink-on-light, same alpha structure — and
/// <see cref="For"/> picks by perceived brightness. The accent (sung prefix, source chip) is
/// deliberately NOT here: it is the one color the user chose, and it does not move.
/// </remarks>
public sealed record CardTextTiers(
    /// <summary>Track title, the card's largest type.</summary>
    ArgbColor Title,
    /// <summary>Artist, quiet beside the title.</summary>
    ArgbColor Secondary,
    /// <summary>Unsung lyric runs.</summary>
    ArgbColor LyricIdle,
    /// <summary>The "no lyrics yet" status line.</summary>
    ArgbColor LyricStatus,
    /// <summary>Elapsed / total time.</summary>
    ArgbColor Time,
    /// <summary>Transport prev/next glyphs.</summary>
    ArgbColor Icon,
    /// <summary>Play/pause glyph and the current line's unsung prefix.</summary>
    ArgbColor IconStrong,
    /// <summary>The pin glyph while unpinned.</summary>
    ArgbColor PinGlyph,
    /// <summary>The close glyph.</summary>
    ArgbColor CloseGlyph,
    /// <summary>Progress bar fill.</summary>
    ArgbColor ProgressFill,
    /// <summary>Progress bar groove.</summary>
    ArgbColor ProgressTrack)
{
    private static readonly ArgbColor White = ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly ArgbColor Ink = ArgbColor.FromArgb(0xFF, 0x14, 0x14, 0x18);

    /// <summary>The shipping family: white tiers for the dark card.</summary>
    public static readonly CardTextTiers Dark = From(White);

    /// <summary>The flipped family: the same alphas over near-black ink.</summary>
    public static readonly CardTextTiers Light = From(Ink);

    /// <summary>Picks the family by the background's perceived brightness.</summary>
    public static CardTextTiers For(ArgbColor background) =>
        CardPalette.IsLightBackground(background) ? Light : Dark;

    private static CardTextTiers From(ArgbColor baseColor) => new(
        Title: baseColor with { A = 0xF2 },
        Secondary: baseColor with { A = 0xB0 },
        LyricIdle: baseColor with { A = 0x59 },
        LyricStatus: baseColor with { A = 0x73 },
        Time: baseColor with { A = 0xAA },
        Icon: baseColor with { A = 0xE6 },
        IconStrong: baseColor with { A = 0xF2 },
        PinGlyph: baseColor with { A = 0x8C },
        CloseGlyph: baseColor with { A = 0xB3 },
        ProgressFill: baseColor,
        ProgressTrack: baseColor with { A = 0x33 });
}
