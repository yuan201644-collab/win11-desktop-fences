namespace DesktopMediaController.Core;

/// <summary>
/// The four rows the lyric block keeps on screen, and the single motion they run when the song moves
/// on to the next line.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a two-part animation — slide the whole block up and away, swap the text at the
/// furthest point, slide it back — which read as stiff for three separate reasons, all of them
/// structural rather than a matter of tuning:
/// </para>
/// <list type="number">
/// <item>
/// <b>It is not a scroll.</b> The slots are not the same height (the emphasised row is 31 DIP against
/// a context row's 18), so "move everything up by one line" is not one distance and cannot be one
/// translation of a container. Animating a height per row is what turns it into a scroll: each row's
/// top is the sum of the heights above it, so the rows move continuously.
/// </item>
/// <item>
/// <b>The two halves do not join.</b> Each half eases out to zero velocity, and the second is started
/// from the callback that ends the first, so the joint is a visible beat.
/// </item>
/// <item>
/// <b>There was nothing below to scroll in.</b> Three rows can only ever be swapped; a fourth row,
/// staged off the bottom edge, is what lets the next line actually arrive.
/// </item>
/// </list>
/// <para>
/// The four rows are indexed by <i>slot</i> — the position they occupy, not the line they show, since
/// which line is in which slot is exactly what changes. Slot 1 is the middle (the emphasised one) at
/// rest; slot 0 is the line above it, slot 2 the line below, and slot 3 sits entirely below the
/// clipped block, invisible, staged.
/// </para>
/// <para>
/// The motion is one interpolation, parameterised by progress, shared by every row and by the type
/// size — see <see cref="Plan.At"/>. Because one parameter drives all of them, the frames are
/// continuous by construction: there is no second animation to hand over to and no instant at which
/// two of them can disagree.
/// </para>
/// <para>
/// The reason a four-row carousel is worth the extra row is <see cref="Plan.End"/>: the frame at the
/// end of the motion is <i>pixel-identical</i> to the resting frame that follows the slot reset, so
/// the reset — which has to happen, because the row heights the layout needs are the resting ones —
/// cannot be seen. That is a property to be tested rather than tuned, which is why this lives here
/// and not inside the WPF control.
/// </para>
/// </remarks>
public static class LyricScroll
{
    /// <summary>Rows kept on screen: the visible three, plus one staged below the clip.</summary>
    public const int RowCount = 4;

    /// <summary>
    /// How far a row dims on its way out of the middle slot, over the dark card.
    /// </summary>
    /// <remarks>
    /// The line that has just been sung keeps its highlight as it leaves, so this is the whole
    /// difference between "the line being sung" and "the line before it" — and animating it, rather
    /// than restyling the text at the end of the motion, is what stops the previous line from changing
    /// colour in a single frame at the exact moment the eye has followed it to the top row.
    /// </remarks>
    public const double PastOpacity = 0.5;

    /// <summary>
    /// One frame of the motion: where each slot is and how big its type is.
    /// </summary>
    /// <param name="Opacity1">
    /// The opacity of slot 1 only — the row that is leaving the middle and dimming to
    /// <see cref="PastOpacity"/>. Slot 0 is a past row and already at that opacity; slots 2 and 3 are
    /// upcoming lines, which are not dimmed at all.
    /// </param>
    public readonly record struct Frame(
        double Top0, double Top1, double Top2, double Top3,
        double Font0, double Font1, double Font2, double Font3,
        double Opacity1)
    {
        public double Top(int slot) => slot switch
        {
            0 => Top0,
            1 => Top1,
            2 => Top2,
            3 => Top3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        public double FontSize(int slot) => slot switch
        {
            0 => Font0,
            1 => Font1,
            2 => Font2,
            3 => Font3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        public double Opacity(int slot) => slot switch
        {
            0 => PastOpacity,
            1 => Opacity1,
            2 or 3 => 1d,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
    }

    /// <summary>
    /// Where the rows sit at rest, and how to interpolate them to the next line's rest.
    /// </summary>
    /// <param name="OutgoingFont">Type size of the line being left, which is the one now in slot 1.</param>
    /// <param name="IncomingFont">
    /// Type size the arriving line will settle at in slot 1. Its own length decides that — a long line
    /// is set smaller so that it is shown whole — and it is deliberately not the same as the block
    /// scale, which is fixed.
    /// </param>
    public readonly record struct Plan(
        double ContextRow,
        double CurrentRow,
        double ContextFont,
        double OutgoingFont,
        double IncomingFont)
    {
        /// <summary>Height of the clipped window: the three visible rows, and no more.</summary>
        public double BlockHeight => (ContextRow * 2) + CurrentRow;

        /// <summary>
        /// The row immediately above the window, which slot 0 travels into. A row moving up by one slot
        /// is a row moving to the position of the slot above it, and for slot 0 that position is off the
        /// top edge — which is what makes the outgoing line leave rather than stop at the top.
        /// </summary>
        public double OffTop => RestingTop(ContextRow, CurrentRow, -1);

        /// <summary>Top of a slot at rest, in DIP from the top of the clipped block.</summary>
        public double RestTop(int slot) => RestingTop(ContextRow, CurrentRow, slot);

        private static double RestingTop(double contextRow, double currentRow, int slot) => slot switch
        {
            -1 => -contextRow,
            0 => 0d,
            1 => contextRow,
            2 => contextRow + currentRow,
            3 => (contextRow * 2) + currentRow,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        /// <summary>Type size a slot carries at rest: the middle one is emphasised, the rest are not.</summary>
        public double RestFont(int slot) =>
            slot == 1 ? IncomingFont : ContextFont;

        /// <summary>
        /// The resting frame: what the block shows once the song has moved on and the slots have been
        /// reset. Slot 1 is the line that has just started, hence <see cref="IncomingFont"/>.
        /// </summary>
        public Frame Rest => new(
            0d, ContextRow, ContextRow + CurrentRow, BlockHeight,
            ContextFont, IncomingFont, ContextFont, ContextFont,
            1d);

        /// <summary>
        /// The frame the motion <i>starts</i> from: the resting frame the song was on before the line
        /// changed, so slot 1 is still the outgoing line at its own size.
        /// </summary>
        public Frame Start => At(0d);

        /// <summary>
        /// The frame the motion <i>ends</i> at — and it is the whole point of the fourth row that this is
        /// the resting frame shifted up by one slot, exactly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Slot <c>k</c> ends where slot <c>k − 1</c> rests: same top, same type size, same opacity. So
        /// the reset that follows — shifting the text up one slot and putting the rows back on their
        /// resting offsets — redraws every pixel that was already there. No crossfade, no hold frame, no
        /// easing curve that has to hide a seam: the seam does not exist.
        /// </para>
        /// <para>
        /// Deliberately a reading of <see cref="At"/> rather than a second table of numbers. Written out
        /// by hand it would be an independent definition of the same frame, free to drift from the one the
        /// card actually animates to — and the property stated here would then still pass while the card
        /// moved somewhere else. It was written out by hand first, and that is exactly what happened.
        /// </para>
        /// </remarks>
        public Frame End => At(1d);

        /// <summary>The frame at <paramref name="progress" />, clamped to the motion's bounds.</summary>
        public Frame At(double progress)
        {
            var t = double.IsNaN(progress) ? 0d : Math.Clamp(progress, 0d, 1d);

            // A struct's local functions cannot capture `this`, so the four numbers they need are copied
            // out first. Everything they read is a constant for the length of the motion anyway — that is
            // precisely what makes the interpolation below well-defined.
            var contextRow = ContextRow;
            var currentRow = CurrentRow;
            var contextFont = ContextFont;
            var outgoingFont = OutgoingFont;
            var incomingFont = IncomingFont;

            double Top(int slot) =>
                Lerp(RestingTop(contextRow, currentRow, slot), RestingTop(contextRow, currentRow, slot - 1), t);

            // Slot 1 gives its size up as it leaves and slot 2 takes that size on as it arrives, so the
            // emphasised type is handed over rather than restored at the end.
            double Font(int slot) => Lerp(
                slot == 1 ? outgoingFont : contextFont,
                slot == 2 ? incomingFont : contextFont,
                t);

            return new Frame(
                Top(0), Top(1), Top(2), Top(3),
                Font(0), Font(1), Font(2), Font(3),
                Lerp(1d, PastOpacity, t));
        }

        private static double Lerp(double from, double to, double t) => from + ((to - from) * t);
    }

    /// <summary>Describes the motion between two lines of the same song.</summary>
    public static Plan For(
        double contextRow,
        double currentRow,
        double contextFont,
        double outgoingFont,
        double incomingFont) => new(contextRow, currentRow, contextFont, outgoingFont, incomingFont);
}
