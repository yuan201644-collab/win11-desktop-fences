using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class WidgetSizeTests
{
    [Fact]
    public void Default_IsTheDesignGeometry()
    {
        Assert.Equal(620, WidgetSize.Default.WidthDip);
        Assert.Equal(216, WidgetSize.Default.HeightDip);
        Assert.Equal(WidgetSize.Base, WidgetSize.Default);
    }

    [Fact]
    public void Presets_KeepTheCardsRatioFixed()
    {
        foreach (var scale in WidgetSize.Scales)
        {
            var size = WidgetSize.ForScale(scale);

            // 620/216 = 2.870…; every preset must reproduce it exactly, because the cover column and
            // the lyric panel are laid out against the design geometry and scaled as a whole.
            Assert.Equal(620d / 216d, size.WidthDip / size.HeightDip, 1e-9);
        }
    }

    [Fact]
    public void Coerce_AnOlderFileWithNoSizeAtAll_YieldsTheDefault()
    {
        // This is the upgrade path: a placement.json from before the card had a size deserialises
        // its missing width and height as zero.
        Assert.Equal(WidgetSize.Default, WidgetSize.Coerce(0, 0));
    }

    [Fact]
    public void Coerce_ASizeFromTheFreeResizeEra_IsSnappedToTheNearestPreset()
    {
        // 440x176 was the old default. 176/216 ≈ 0.81 sits between the presets; the smaller one is
        // the honest reading of "the card was this small", and the ratio is restored either way.
        var size = WidgetSize.Coerce(440, 176);

        Assert.Equal(0.75, WidgetSize.NearestScale(440, 176));
        Assert.Equal(WidgetSize.ForScale(0.75), size);
    }

    [Theory]
    [InlineData(465, 162, 0.75)]
    [InlineData(620, 216, 1.0)]
    [InlineData(775, 270, 1.25)]
    public void Coerce_ASizeThatIsAlreadyAPreset_StaysExactlyThere(double width, double height, double expectedScale)
    {
        var size = WidgetSize.Coerce(width, height);

        Assert.Equal(expectedScale, WidgetSize.NearestScale(width, height));
        Assert.Equal(width, size.WidthDip, 3);
        Assert.Equal(height, size.HeightDip, 3);
    }

    [Theory]
    [InlineData(double.NaN, 216)]
    [InlineData(620, double.NaN)]
    [InlineData(double.PositiveInfinity, 216)]
    [InlineData(-10, 300)]
    public void Coerce_ANonsensicalAxis_StillYieldsARenderablePresetSize(double width, double height)
    {
        var size = WidgetSize.Coerce(width, height);

        Assert.True(size.IsUsable);
    }

    [Fact]
    public void Coerce_ANonsensicalHeight_FallsBackToJudgingByWidth()
    {
        // Width is the secondary reference: a file that somehow carries only a sane width still
        // lands on the preset its width describes.
        var size = WidgetSize.Coerce(775, 0);

        Assert.Equal(1.25, WidgetSize.NearestScale(775, 0));
        Assert.Equal(775, size.WidthDip, 3);
    }

    [Fact]
    public void Coerce_SomethingWildlyTooLarge_IsSnappedToTheLargestPreset()
    {
        Assert.Equal(WidgetSize.ForScale(1.25), WidgetSize.Coerce(9_999, 9_999));
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
