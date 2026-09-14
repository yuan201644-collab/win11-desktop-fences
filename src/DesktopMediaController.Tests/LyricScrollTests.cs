using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The four-row scroll: the geometry of one line change, and the property that makes it look like a
/// scroll rather than like a swap.
/// </summary>
/// <remarks>
/// This is the second time the lyric block's motion has had to be rewritten, and the first rewrite
/// failed because the problem was mistaken for a tuning problem — a curve to soften, a duration to
/// shorten — when the animation was structurally unable to be one continuous movement. So the cases
/// below are mostly about structure: where each slot ends up, that one frame does not overlap another,
/// and above all that the frame the motion ends on is the resting frame shifted by one slot, which is
/// what makes the reset at the end invisible.
/// </remarks>
public sealed class LyricScrollTests
{
    /// <summary>The default card's block: an 18 DIP context row above and below a 31 DIP middle row.</summary>
    private static LyricScroll.Plan Default => LyricScroll.For(18d, 31d, 13d, 22d, 22d);

    /// <summary>
    /// Every geometry the card can produce: the design size, the floor and ceiling scales, a card that
    /// was only made taller, and a long incoming line whose type had to be set smaller to fit.
    /// </summary>
    public static TheoryData<double, double, double, double, double> Geometries() => new()
    {
        { 18d, 31d, 13d, 22d, 22d },
        { 12.96d, 22.32d, 9.36d, 15.84d, 15.84d },
        { 46.8d, 80.6d, 33.8d, 57.2d, 57.2d },
        { 38d, 65.5d, 27.5d, 46.5d, 21.3d },
        { 18d, 31d, 13d, 13d, 13d },
        { 18d, 31d, 13d, 21.3d, 15.2d },
    };

    [Fact]
    public void Plan_AtTheDesignSize_ReproducesTheDesignGeometry()
    {
        var plan = Default;

        Assert.Equal(67d, plan.BlockHeight, 1e-9);

        // Slot 3 rests exactly on the clip's bottom edge, which is what makes it invisible — and what
        // makes filling it in before a scroll unobservable.
        Assert.Equal(0d, plan.RestTop(0), 1e-9);
        Assert.Equal(18d, plan.RestTop(1), 1e-9);
        Assert.Equal(49d, plan.RestTop(2), 1e-9);
        Assert.Equal(67d, plan.RestTop(3), 1e-9);
        Assert.Equal(plan.BlockHeight, plan.RestTop(3), 1e-9);

        // Slot 0 travels one context row further up than the block's own top edge.
        Assert.Equal(-18d, plan.OffTop, 1e-9);
    }

    /// <summary>
    /// The property the whole design rests on: the frame the motion ends on is the resting frame shifted
    /// up by one slot, so the reset that has to follow cannot be seen.
    /// </summary>
    /// <remarks>
    /// The reset is not optional. Slot offsets are functions of the row heights, and the row heights the
    /// layout needs are the resting ones — a slot whose height changed mid-motion cannot stay that way.
    /// So the reset happens immediately after the motion, in the same callback, and it is only invisible
    /// because every pixel it redraws was already there: same top, same type size, same opacity.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Geometries))]
    public void End_IsTheRestingFrameShiftedUpOneSlot(
        double contextRow, double currentRow, double contextFont, double outgoingFont, double incomingFont)
    {
        var plan = LyricScroll.For(contextRow, currentRow, contextFont, outgoingFont, incomingFont);
        var end = plan.End;
        var rest = plan.Rest;

        for (var slot = 0; slot < LyricScroll.RowCount - 1; slot++)
        {
            Assert.Equal(rest.Top(slot), end.Top(slot + 1), 1e-9);
            Assert.Equal(rest.FontSize(slot), end.FontSize(slot + 1), 1e-9);
            Assert.Equal(rest.Opacity(slot), end.Opacity(slot + 1), 1e-9);
        }
    }

    [Fact]
    public void Start_IsTheRestingFrameTheSongWasOnBeforeTheLineChanged()
    {
        var plan = Default;
        var start = plan.Start;
        var rest = plan.Rest;

        // Same geometry, same context rows — and the middle row still carrying the outgoing line's own
        // size, which is the one thing that differs from the frame that follows.
        for (var slot = 0; slot < LyricScroll.RowCount; slot++)
        {
            Assert.Equal(rest.Top(slot), start.Top(slot), 1e-9);
            Assert.Equal(rest.Opacity(slot), start.Opacity(slot), 1e-9);
        }

        Assert.Equal(13d, start.FontSize(0), 1e-9);
        Assert.Equal(22d, start.FontSize(1), 1e-9);
        Assert.Equal(13d, start.FontSize(2), 1e-9);
        Assert.Equal(13d, start.FontSize(3), 1e-9);
    }

    [Fact]
    public void End_HandsTheEmphasisedTypeOverRatherThanRestoringIt()
    {
        var plan = LyricScroll.For(18d, 31d, 13d, 21.3d, 15.2d);
        var end = plan.End;

        // The departing line arrives in the top slot as a context row...
        Assert.Equal(13d, end.FontSize(1), 1e-9);

        // ...while the line that was below is already at its own final size in the middle slot, rather
        // than growing into it after the motion has stopped.
        Assert.Equal(15.2d, end.FontSize(2), 1e-9);
    }

    /// <summary>
    /// The emphasised type is handed over during the motion, not restored when it stops: the middle
    /// row's size falls as the row below rises to meet it, and the two cross somewhere in between.
    /// </summary>
    /// <remarks>
    /// Both rows being in motion is what lets the block keep its fixed heights: the middle slot's height
    /// never changes, so a scroll always covers the same distance, and the size travels through the rows
    /// instead of the layout changing underneath them.
    /// </remarks>
    [Fact]
    public void At_TheEmphasisIsHandedOver_RatherThanSwapped()
    {
        var plan = LyricScroll.For(18d, 31d, 13d, 21.3d, 22d);
        var leaving = double.MaxValue;
        var arriving = double.MinValue;

        for (var step = 0; step <= 20; step++)
        {
            var frame = plan.At(step / 20d);

            Assert.True(frame.FontSize(1) <= leaving + 1e-9, "the leaving line grew");
            Assert.True(frame.FontSize(2) >= arriving - 1e-9, "the arriving line shrank");
            leaving = frame.FontSize(1);
            arriving = frame.FontSize(2);
        }

        Assert.Equal(21.3d, plan.At(0d).FontSize(1), 1e-9);
        Assert.Equal(13d, plan.At(0d).FontSize(2), 1e-9);
        Assert.Equal(13d, plan.At(1d).FontSize(1), 1e-9);
        Assert.Equal(22d, plan.At(1d).FontSize(2), 1e-9);

        // They must cross: the line being read is the bigger of the two at the start and the smaller at
        // the end, and both move monotonically, so there is no frame at which neither row is emphasised.
        Assert.True(plan.At(0d).FontSize(1) > plan.At(0d).FontSize(2));
        Assert.True(plan.At(1d).FontSize(1) < plan.At(1d).FontSize(2));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(2d)]
    [InlineData(double.NaN)]
    public void At_ProgressOutsideTheUnitRange_IsClampedNotExtrapolated(double progress)
    {
        var plan = Default;
        var frame = plan.At(progress);
        var expected = progress > 1d ? plan.End : plan.Start;

        for (var slot = 0; slot < LyricScroll.RowCount; slot++)
        {
            Assert.Equal(expected.Top(slot), frame.Top(slot), 1e-9);
            Assert.Equal(expected.FontSize(slot), frame.FontSize(slot), 1e-9);
        }
    }

    /// <summary>
    /// Every row ends exactly one slot further up — the definition of a scroll, and the thing the old
    /// two-part animation could not do, because the slots are not the same height.
    /// </summary>
    [Theory]
    [MemberData(nameof(Geometries))]
    public void At_EveryRowTravelsToTheSlotAboveIt(
        double contextRow, double currentRow, double contextFont, double outgoingFont, double incomingFont)
    {
        var plan = LyricScroll.For(contextRow, currentRow, contextFont, outgoingFont, incomingFont);
        var end = plan.End;

        for (var slot = 0; slot < LyricScroll.RowCount; slot++)
        {
            var above = slot == 0 ? plan.OffTop : plan.RestTop(slot - 1);

            Assert.Equal(above, end.Top(slot), 1e-9);
            Assert.Equal(plan.RestTop(slot) - above, plan.RestTop(slot) - end.Top(slot), 1e-9);
        }

        // A slot's travel is the height of the slot above it, so the row moving into the middle travels
        // the middle slot's height while the others travel a context row. A single translation of a
        // single container cannot express two different distances.
        Assert.Equal(contextRow, plan.RestTop(1) - plan.RestTop(0), 1e-9);
        Assert.Equal(currentRow, plan.RestTop(2) - plan.RestTop(1), 1e-9);
        Assert.True(plan.RestTop(2) - end.Top(2) > plan.RestTop(3) - end.Top(3));
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void At_NoFrameLetsOneLineRunIntoTheNext(
        double contextRow, double currentRow, double contextFont, double outgoingFont, double incomingFont)
    {
        var plan = LyricScroll.For(contextRow, currentRow, contextFont, outgoingFont, incomingFont);

        for (var step = 0; step <= 40; step++)
        {
            var frame = plan.At(step / 40d);

            for (var slot = 0; slot < LyricScroll.RowCount - 1; slot++)
            {
                // A glyph box is at most the font size tall, so comparing against the font size is the
                // conservative form of "the rows do not overlap".
                var bottom = frame.Top(slot) + frame.FontSize(slot);
                Assert.True(
                    bottom <= frame.Top(slot + 1) + 1e-9,
                    $"at {step / 40d:0.###} slot {slot} reaches {bottom:0.#}, past slot "
                    + $"{slot + 1} at {frame.Top(slot + 1):0.#}");
            }
        }
    }

    /// <summary>
    /// The line arriving is readable for the whole of its journey, not only once it has landed: its ink
    /// never leaves the block on its way in.
    /// </summary>
    [Theory]
    [MemberData(nameof(Geometries))]
    public void At_TheArrivingLineIsNeverClipped(
        double contextRow, double currentRow, double contextFont, double outgoingFont, double incomingFont)
    {
        var plan = LyricScroll.For(contextRow, currentRow, contextFont, outgoingFont, incomingFont);

        for (var step = 0; step <= 40; step++)
        {
            var frame = plan.At(step / 40d);

            Assert.True(
                frame.Top(2) + frame.FontSize(2) <= plan.BlockHeight + 1e-9,
                $"the arriving line overflows the block at {step / 40d:0.###}");
        }
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void At_RowsOnlyEverMoveUp(
        double contextRow, double currentRow, double contextFont, double outgoingFont, double incomingFont)
    {
        var plan = LyricScroll.For(contextRow, currentRow, contextFont, outgoingFont, incomingFont);
        var previous = plan.At(0d);

        for (var step = 1; step <= 40; step++)
        {
            var frame = plan.At(step / 40d);

            for (var slot = 0; slot < LyricScroll.RowCount; slot++)
            {
                Assert.True(frame.Top(slot) <= previous.Top(slot) + 1e-9, $"slot {slot} moved down");
            }

            previous = frame;
        }
    }

    /// <summary>
    /// Only the row leaving the middle slot is dimmed, and it is dimmed continuously rather than
    /// restyled in a single frame at the end of the motion.
    /// </summary>
    [Fact]
    public void Opacity_TheRowLeavingTheMiddle_DimsTheWholeWayOut()
    {
        var plan = Default;
        var previous = 1d;

        Assert.Equal(1d, plan.Start.Opacity(1), 1e-9);
        Assert.Equal(LyricScroll.PastOpacity, plan.End.Opacity(1), 1e-9);

        for (var step = 0; step <= 20; step++)
        {
            var opacity = plan.At(step / 20d).Opacity(1);

            Assert.True(opacity <= previous + 1e-9);
            previous = opacity;
        }

        // The rows that are not leaving the middle do not change opacity at all: an upcoming line is not
        // dimmed, and a past line is already at the dimmed value, which is what leaves nothing to change
        // at the reset.
        for (var slot = 0; slot < LyricScroll.RowCount; slot++)
        {
            if (slot == 1) continue;

            Assert.Equal(plan.Start.Opacity(slot), plan.End.Opacity(slot), 1e-9);
        }

        Assert.Equal(LyricScroll.PastOpacity, plan.Rest.Opacity(0), 1e-9);
        Assert.Equal(1d, plan.Rest.Opacity(1), 1e-9);
    }

    /// <summary>
    /// A card whose rows and type are scaled together keeps its proportions — the animation reads the
    /// distances from the geometry, so a scaled card must not change what the motion looks like.
    /// </summary>
    [Fact]
    public void Plan_AScaledCard_KeepsTheSameProportions()
    {
        var design = Default;
        var scaled = LyricScroll.For(36d, 62d, 26d, 44d, 44d);

        Assert.Equal(design.BlockHeight * 2d, scaled.BlockHeight, 1e-9);

        for (var step = 0; step <= 10; step++)
        {
            var t = step / 10d;
            Assert.Equal(design.At(t).Top2 * 2d, scaled.At(t).Top2, 1e-9);
            Assert.Equal(design.At(t).Font2 * 2d, scaled.At(t).Font2, 1e-9);
        }
    }
}
