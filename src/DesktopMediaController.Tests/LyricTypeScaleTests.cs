using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The lyric block's type sizes, and in particular the width budget that decides them.
/// </summary>
/// <remarks>
/// This exists because getting this arithmetic wrong is invisible in every other kind of test: the app
/// builds, the tests pass, and the only symptom is that the one line the user is trying to read ends in
/// an ellipsis. It happened once — a sung line of thirteen characters was rendered as eight plus "…" on
/// a real 441x255 card — so the cases below pin down both the numbers and the property that failed.
/// </remarks>
public sealed class LyricTypeScaleTests
{
    /// <summary>The default card, and the width its text column gets (440 - 36 chrome - 74 cover).</summary>
    private const double StandardHeight = 176;
    private const double StandardTextWidth = 330;

    /// <summary>The card the ellipsis was seen on: 441x255, cover 107.1, so a 297.9 DIP text column.</summary>
    private const double TallHeight = 255;
    private const double TallTextWidth = 298;

    [Fact]
    public void Measure_AtTheDesignSize_ReproducesTheDesignNumbers()
    {
        var fit = LyricTypeScale.Measure(StandardHeight, StandardTextWidth, 10);

        Assert.Equal(1d, fit.BlockScale, 1e-9);
        Assert.Equal(22d, fit.CurrentFontSize, 1e-9);
        Assert.Equal(13d, fit.ContextFontSize, 1e-9);
        Assert.Equal(31d, fit.CurrentRowHeight, 1e-9);
        Assert.Equal(18d, fit.ContextRowHeight, 1e-9);

        // 18 + 31 + 18 has to fit the slot the card leaves for lyrics, or the rows are clipped.
        Assert.True(fit.AreaHeight <= StandardHeight - LyricTypeScale.ChromeDip);
    }

    /// <summary>
    /// The regression test for the reported defect: a long line arrives whole, not as a fragment.
    /// </summary>
    /// <remarks>
    /// The line that was seen trimmed is "我的心是一个站牌 写着等待" from 手放开 — thirteen characters,
    /// which on this card used to be set at 29.8px and rendered as eight plus an ellipsis.
    /// </remarks>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Measure_ALongLineOnATallCard_LeavesRoomForTheWholeLine(int characters)
    {
        var fit = LyricTypeScale.Measure(TallHeight, TallTextWidth, characters);

        Assert.True(
            characters * fit.CurrentFontSize <= TallTextWidth + 1e-9,
            $"{characters} characters at {fit.CurrentFontSize:0.##}px need "
            + $"{characters * fit.CurrentFontSize:0.#} of {TallTextWidth} DIP, so the line would be trimmed.");
    }

    /// <summary>
    /// ...and leaves room for the ellipsis it will never have to draw.
    /// </summary>
    /// <remarks>
    /// This is the arithmetic the defect actually came down to. Sizing a line to <i>exactly</i> the
    /// column width satisfies "the line fits" and still trims, because the trimming ellipsis and the
    /// last glyph cannot both have the final slot. One character of spare width is what makes "no
    /// ellipsis" a guarantee rather than a coin flip on glyph metrics.
    /// </remarks>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Measure_ALongLineOnATallCard_CouldStillFitOneMoreCharacter(int characters)
    {
        var fit = LyricTypeScale.Measure(TallHeight, TallTextWidth, characters);
        var withSpare = (characters + 1) * fit.CurrentFontSize;

        Assert.True(
            withSpare <= TallTextWidth + 1e-9,
            $"a {characters} character line at {fit.CurrentFontSize:0.##}px leaves only "
            + $"{TallTextWidth - characters * fit.CurrentFontSize:0.#} DIP spare, which is less than the "
            + $"ellipsis needs — {withSpare:0.#} would be required.");
    }

    /// <summary>
    /// Past the length where the type is floored at the context size, a line is allowed to be trimmed —
    /// but not before sixteen characters, which covers the great majority of Chinese lyric lines.
    /// </summary>
    [Fact]
    public void Measure_LinesUpToSixteenCharacters_AreStillShownWholeOnATallCard()
    {
        var fit = LyricTypeScale.Measure(TallHeight, TallTextWidth, 16);

        Assert.True(16 * fit.CurrentFontSize <= TallTextWidth + 1e-9);
        Assert.True(fit.CurrentFontSize >= fit.ContextFontSize - 1e-9);
    }

    /// <summary>
    /// The emphasised line is always bigger than the lines around it — that is the whole point of
    /// emphasising it, and it is what the width budget is not allowed to trade away.
    /// </summary>
    [Fact]
    public void Measure_WhateverTheCardAndTheLine_TheEmphasisedLineIsBiggerThanTheContext()
    {
        foreach (var height in new[] { 140d, 176d, 255d, 400d, 900d })
        {
            foreach (var textWidth in new[] { 90d, 150d, 220d, 330d, 600d, 1200d })
            {
                foreach (var characters in new[] { 0, 1, 5, 10, 13, 20, 30, 60 })
                {
                    var fit = LyricTypeScale.Measure(height, textWidth, characters);

                    Assert.True(
                        fit.CurrentFontSize >= fit.ContextFontSize - 1e-9,
                        $"card {height} textWidth {textWidth} line {characters}: current "
                        + $"{fit.CurrentFontSize:0.##} < context {fit.ContextFontSize:0.##}");
                }
            }
        }
    }

    /// <summary>
    /// The emphasised line's <i>row</i> is deliberately independent of the line's length, because the
    /// scroll animation reads its travel distance from that row height.
    /// </summary>
    [Fact]
    public void Measure_TheEmphasisedRowHeight_DoesNotChangeWithTheLine()
    {
        var shortLine = LyricTypeScale.Measure(TallHeight, TallTextWidth, 9);
        var longLine = LyricTypeScale.Measure(TallHeight, TallTextWidth, 18);

        Assert.Equal(shortLine.CurrentRowHeight, longLine.CurrentRowHeight, 1e-9);
        Assert.Equal(shortLine.BlockScale, longLine.BlockScale, 1e-9);

        // The whole block, not only the row, so the context rows stay put as the song moves on.
        Assert.Equal(shortLine.ContextFontSize, longLine.ContextFontSize, 1e-9);
        Assert.Equal(shortLine.AreaHeight, longLine.AreaHeight, 1e-9);

        // Only the type of the line itself moves, and only downwards.
        Assert.True(longLine.CurrentFontSize < shortLine.CurrentFontSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    public void Measure_LinesAtOrBelowTheFloorLength_AllGetTheSameType(int characters)
    {
        var fit = LyricTypeScale.Measure(StandardHeight, StandardTextWidth, characters);
        var atMinimum = LyricTypeScale.Measure(StandardHeight, StandardTextWidth, LyricTypeScale.MinCharsPerCurrentLine);

        Assert.Equal(atMinimum.CurrentFontSize, fit.CurrentFontSize, 1e-9);
    }

    [Fact]
    public void CurrentScaleFor_ALongerLine_IsNeverBigger()
    {
        var previous = double.MaxValue;
        for (var characters = 1; characters <= 40; characters++)
        {
            var scale = LyricTypeScale.CurrentScaleFor(1d, StandardTextWidth, characters);

            Assert.True(scale <= previous + 1e-9, $"line {characters} got bigger than line {characters - 1}");
            previous = scale;
        }
    }

    [Fact]
    public void Measure_ATinyCard_StopsShrinkingAtTheLegibilityFloor()
    {
        var fit = LyricTypeScale.Measure(140, 40, 30);

        Assert.Equal(LyricTypeScale.MinScale, fit.BlockScale, 1e-9);
        Assert.Equal(LyricTypeScale.MinScale, fit.CurrentScale, 1e-9);
    }

    [Fact]
    public void Measure_AnEnormousCard_StopsGrowingAtTheCeiling()
    {
        var fit = LyricTypeScale.Measure(900, 3000, 10);

        Assert.Equal(LyricTypeScale.MaxScale, fit.BlockScale, 1e-9);
        Assert.Equal(LyricTypeScale.MaxScale, fit.CurrentScale, 1e-9);
    }

    /// <summary>
    /// A card narrower than any sensible card still returns numbers rather than throwing or going
    /// negative — the layout passes through sizes the user's drag produced on the way to a clamp.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Measure_ANonPositiveWidth_StillReturnsUsableNumbers(double textWidth)
    {
        var fit = LyricTypeScale.Measure(StandardHeight, textWidth, 13);

        Assert.True(fit.CurrentFontSize > 0);
        Assert.True(fit.ContextFontSize > 0);
        Assert.Equal(fit.CurrentRowHeight, fit.BlockScale * LyricTypeScale.CurrentRow, 1e-9);
    }
}
