namespace DesktopMediaController.Core;

/// <summary>
/// Works out how big the lyric block's type is, on a card of a given size, with a given line being
/// sung.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic, deliberately kept out of the card: the one bug this file exists to prevent was an
/// arithmetic mistake, and arithmetic that lives inside a WPF control cannot be tested. The card asks
/// for a <see cref="Fit"/> and applies the numbers to its text blocks.
/// </para>
/// <para>
/// The shape of the problem is that a desktop widget can be dragged to any size, and the block has to
/// stay legible at all of them. Two independent budgets bound it, and taking only one of them is the
/// mistake the code used to make:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Height</b> — the three rows have to fit the slot the card leaves for lyrics. This is what makes
/// the block grow when the card is made taller.
/// </item>
/// <item>
/// <b>Width</b> — the line being sung has to be readable across the card. Scaling with height alone
/// measured a 46px lyric line on a 441x255 card, which showed six characters of the one line the user
/// is actually trying to read.
/// </item>
/// </list>
/// <para>
/// The width budget has a second, subtler job: it is what stops the emphasised line from being
/// <i>trimmed</i>. A card is a fixed width, so a line longer than the column used to end in an ellipsis
/// — and since a sung line is exactly the one worth reading, "我的心是一个站牌 写着等待" arriving on
/// screen as "我的心是一个站牌…" is a worse failure than slightly smaller type.
/// </para>
/// </remarks>
public static class LyricTypeScale
{
    // ---- the design, measured on a 440x176 card -----------------------------------------------

    /// <summary>Everything on the card that is not lyric block: chrome, the other rows, the margins.</summary>
    public const double ChromeDip = 105;

    /// <summary>Height of the whole three-line block at the default card size.</summary>
    public const double DesignAreaDip = 71;

    /// <summary>
    /// Design type sizes and row heights.
    /// </summary>
    /// <remarks>
    /// The middle row is deliberately 1.69x the outer two. The budget at the default size is
    /// 18 + 31 + 18 = 67 DIP against 71 available, so a four-DIP cushion absorbs rounding. Letting the
    /// emphasised line wrap to two rows instead would need 97 DIP of a 67 DIP budget.
    /// </remarks>
    public const double ContextFont = 13;
    public const double CurrentFont = 22;
    public const double ContextRow = 18;
    public const double CurrentRow = 31;

    /// <summary>
    /// Bounds on how far the block may scale with the card.
    /// </summary>
    /// <remarks>
    /// The floor is legibility: without it a deliberately enormous card turns into four words. The
    /// ceiling is the opposite failure — a card too small for three lines would otherwise shrink its
    /// type past readability, when overflowing the slot symmetrically is the better failure because
    /// the emphasised middle row is the one that survives being clipped.
    /// </remarks>
    public const double MinScale = 0.72;
    public const double MaxScale = 2.6;

    /// <summary>
    /// How many characters the line being sung must still be able to show, whatever the card size.
    /// </summary>
    /// <remarks>
    /// The divisor for short lines, and the reason the width budget does not simply shrink the type to
    /// nothing on a narrow card. Ten is roughly where a Chinese lyric line stops being readable as a
    /// phrase rather than as a fragment.
    /// </remarks>
    public const int MinCharsPerCurrentLine = 10;

    /// <summary>Where the three rows sit and how big their type is, as multiples of the design.</summary>
    public readonly record struct Fit(double BlockScale, double CurrentScale)
    {
        public double AreaHeight => (ContextRow * 2 + CurrentRow) * BlockScale;

        public double ContextFontSize => ContextFont * BlockScale;

        public double ContextRowHeight => ContextRow * BlockScale;

        /// <summary>
        /// The emphasised line's type size. Note that its <i>row</i> is <see cref="CurrentRowHeight"/>,
        /// not this value scaled the same way — see <see cref="Measure"/>.
        /// </summary>
        public double CurrentFontSize => CurrentFont * CurrentScale;

        public double CurrentRowHeight => CurrentRow * BlockScale;
    }

    /// <summary>
    /// Fits the block to a card, given how many characters the line being sung has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The block scale comes from the height and from a fixed ten-character width — fixed meaning it
    /// does not move when the song moves on. That matters: if the width budget followed the current
    /// line, a short line would make the whole block, context rows included, swell and shrink with
    /// every line of the song, which reads as the lyric area pumping.
    /// </para>
    /// <para>
    /// Only the emphasised line's <i>type</i> then moves, and only downwards, so that a long line is
    /// shown whole. Its row height stays on <see cref="Fit.CurrentRowHeight"/>, which is the block
    /// scale and therefore also fixed — the scroll animation measures the distance it travels from that
    /// row height, and a row that grew and shrank with the line would make the scroll jump.
    /// </para>
    /// </remarks>
    public static Fit Measure(double cardHeightDip, double textWidthDip, int currentElementCount)
    {
        var heightScale = Math.Max(0d, cardHeightDip - ChromeDip) / DesignAreaDip;
        var blockScale = Math.Clamp(
            Math.Min(heightScale, WidthScale(textWidthDip, MinCharsPerCurrentLine)),
            MinScale,
            MaxScale);

        return new Fit(blockScale, CurrentScaleFor(blockScale, textWidthDip, currentElementCount));
    }

    /// <summary>
    /// How big the emphasised line's type may be without being trimmed, on a card with a run of
    /// <paramref name="blockScale"/> already chosen for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The divisor is the line's own length, plus one character. That spare character is the whole fix:
    /// sizing a line to exactly the column width and leaving <c>TextTrimming</c> to cope is what
    /// produced the ellipsis in the first place, because the glyphs and the trimming ellipsis cannot
    /// both have the last slot. Reserving the slot up front costs about seven percent of the type size
    /// and removes the failure entirely.
    /// </para>
    /// <para>
    /// The result is floored so that the emphasised line is never smaller than the context rows around
    /// it — a "sung" line smaller than its neighbours would be an odd thing to look at. The floor is
    /// what a line longer than roughly seventeen characters gives up instead: it gets trimmed, which is
    /// the honest trade, since the alternative is type past the point of being read.
    /// </para>
    /// </remarks>
    public static double CurrentScaleFor(double blockScale, double textWidthDip, int currentElementCount)
    {
        var chars = Math.Max(MinCharsPerCurrentLine, currentElementCount);
        var widthScale = WidthScale(textWidthDip, chars + 1);

        // Never smaller than the context rows, never bigger than the rest of the block.
        var floor = Math.Max(MinScale, blockScale * ContextFont / CurrentFont);
        return Math.Clamp(Math.Min(blockScale, widthScale), floor, MaxScale);
    }

    /// <summary>Design-font multiples that fit <paramref name="chars"/> characters across the column.</summary>
    private static double WidthScale(double textWidthDip, double chars) =>
        chars <= 0 ? MaxScale : Math.Max(0d, textWidthDip) / chars / CurrentFont;
}
