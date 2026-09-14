namespace DesktopMediaController.Core;

/// <summary>
/// A single 32-bit ARGB color. Plain bytes so Core stays free of UI/OS color types
/// (System.Drawing / System.Windows.Media); the widget converts these to a WPF brush.
/// Mirrors the ArgbColor the fence overlay uses in DesktopOrganizer.Core.
/// </summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    /// <summary>
    /// Parses #RRGGBB or RRGGBB (alpha defaults to 0xFF). Returns null when the text is not exactly
    /// six hex digits, optionally led by a #. Surrounding whitespace is tolerated.
    /// </summary>
    public static ArgbColor? Parse(string? text)
    {
        if (text is null) return null;
        var s = text.Trim();
        if (s.Length == 7 && s[0] == '#') s = s.Substring(1);
        else if (s.Length == 9 && s[0] == '#') s = s.Substring(1);
        if (s.Length != 6 && s.Length != 8) return null;
        if (!TryHex(s.Substring(0, 2), out var r)) return null;
        if (!TryHex(s.Substring(2, 2), out var g)) return null;
        if (!TryHex(s.Substring(4, 2), out var b)) return null;
        // #RRGGBB -> opaque; #RRGGBBAA -> honour the supplied alpha (used by the background entry).
        byte a = 0xFF;
        if (s.Length == 8 && !TryHex(s.Substring(6, 2), out a)) return null;
        return new ArgbColor(a, r, g, b);
    }

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    private static bool TryHex(string two, out byte value)
    {
        value = 0;
        foreach (var c in two)
        {
            var d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (d < 0) return false;
            value = (byte)(value * 16 + d);
        }
        return true;
    }
}

/// <summary>
/// The controller card's accent colors. The user ever picks a single primary; the rest are derived from
/// it so one click is always coherent. Only the accent-aware channels live here — the neutral white text
/// tiers and the translucent dark background are fixed and stay in XAML.
/// Pure data, persisted as JSON, unit-tested.
/// </summary>
public sealed record CardTheme(
    /// <summary>The primary the user picked (so the UI can re-highlight it).</summary>
    ArgbColor Accent,
    /// <summary>Already-sung lyrics and the active line's sung prefix.</summary>
    ArgbColor Sung,
    /// <summary>Progress-bar fill.</summary>
    ArgbColor Progress,
    /// <summary>Card border stroke.</summary>
    ArgbColor Border,
    /// <summary>Source chip and the pin button when pinned.</summary>
    ArgbColor Label,
    /// <summary>Card fill. Its alpha is the card's transparency: 0 = see-through, 0xFF = opaque.</summary>
    ArgbColor Background)
{
    /// <summary>The shipping default background: a near-black at ~10% alpha, byte-for-byte the old XAML fill.</summary>
    public static readonly ArgbColor DarkBackground = ArgbColor.FromArgb(0x1A, 0x1A, 0x1A, 0x20);

    /// <summary>The shipping default: the blue the card has always used, byte-for-byte.</summary>
    public static CardTheme Default => new(
        Accent:   ArgbColor.FromArgb(0xFF, 0x87, 0xC5, 0xFF),
        Sung:     ArgbColor.FromArgb(0xFF, 0x8F, 0xC5, 0xFF),
        Progress: ArgbColor.FromArgb(0xFF, 0x87, 0xC5, 0xFF),
        Border:   ArgbColor.FromArgb(0x4D, 0x87, 0xC5, 0xFF),
        Label:    ArgbColor.FromArgb(0xFF, 0xB3, 0xD6, 0xFF),
        Background: DarkBackground);
}
