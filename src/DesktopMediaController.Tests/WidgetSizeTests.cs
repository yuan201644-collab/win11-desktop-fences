using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class WidgetSizeTests
{
    [Fact]
    public void Default_IsBigEnoughForThreeLyricLinesAndTheTransportRow()
    {
        Assert.True(WidgetSize.Default.IsUsable);
        Assert.Equal(440, WidgetSize.Default.WidthDip);
        Assert.Equal(176, WidgetSize.Default.HeightDip);
    }

    [Fact]
    public void Coerce_AnOlderFileWithNoSizeAtAll_YieldsTheDefault()
    {
        // This is the upgrade path: a placement.json from before the card was resizable deserialises
        // its missing width and height as zero.
        Assert.Equal(WidgetSize.Default, WidgetSize.Coerce(0, 0));
    }

    [Theory]
    [InlineData(double.NaN, 176)]
    [InlineData(440, double.NaN)]
    [InlineData(double.PositiveInfinity, 176)]
    [InlineData(double.NegativeInfinity, 176)]
    [InlineData(-10, 300)]
    public void Coerce_ANonsensicalAxis_FallsBackRatherThanProducingARectangleThatCannotBeDrawn(
        double width, double height)
    {
        var size = WidgetSize.Coerce(width, height);

        Assert.True(double.IsFinite(size.WidthDip));
        Assert.True(double.IsFinite(size.HeightDip));
        Assert.True(size.WidthDip > 0);
        Assert.True(size.HeightDip > 0);
    }

    [Fact]
    public void Coerce_ANonsensicalAxis_KeepsTheOtherOneTheFileDidSpecify()
    {
        var size = WidgetSize.Coerce(0, 300);

        Assert.Equal(WidgetSize.Default.WidthDip, size.WidthDip);
        Assert.Equal(300, size.HeightDip);
    }

    [Fact]
    public void Coerce_ASizeBelowTheMinimum_IsPulledUpToIt()
    {
        // Below the minimum the cover, three lyric lines and the transport row start overlapping, so
        // the limit is a rendering constraint rather than a preference.
        Assert.Equal(
            new WidgetSize(WidgetSize.MinWidthDip, WidgetSize.MinHeightDip),
            WidgetSize.Coerce(100, 100));
    }

    [Fact]
    public void Coerce_ASizeAboveTheMaximum_IsPulledDownToIt()
    {
        Assert.Equal(
            new WidgetSize(WidgetSize.MaxWidthDip, WidgetSize.MaxHeightDip),
            WidgetSize.Coerce(9_999, 9_999));
    }

    [Fact]
    public void Coerce_ASensibleSize_IsLeftExactlyAlone()
    {
        Assert.Equal(new WidgetSize(600, 240), WidgetSize.Coerce(600, 240));
    }

    [Fact]
    public void IsUsable_RejectsASizeThatIsOnlyJustTooSmall()
    {
        Assert.False(new WidgetSize(WidgetSize.MinWidthDip - 1, WidgetSize.MinHeightDip).IsUsable);
        Assert.False(new WidgetSize(WidgetSize.MinWidthDip, WidgetSize.MinHeightDip - 1).IsUsable);
        Assert.True(new WidgetSize(WidgetSize.MinWidthDip, WidgetSize.MinHeightDip).IsUsable);
    }

    [Fact]
    public void Placement_FirstRun_UsesTheDefaultSizeAtTheInsetCorner()
    {
        var placement = WidgetPlacement.FirstRun(new ScreenRect(-2560, 0, 4480, 1600));

        Assert.Equal(-2560 + WidgetPlacement.FirstRunMargin, placement.X);
        Assert.Equal(WidgetPlacement.FirstRunMargin, placement.Y);
        Assert.Equal(WidgetSize.Default, placement.Size);
        Assert.Equal(new WidgetPosition(placement.X, placement.Y), placement.Position);
    }
}
