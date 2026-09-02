using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FencePaletteTests
{
    [Fact]
    public void Presets_AreTenDistinctColors()
    {
        Assert.Equal(10, FencePalette.Presets.Length);
        for (var i = 0; i < FencePalette.Presets.Length; i++)
        for (var j = i + 1; j < FencePalette.Presets.Length; j++)
            Assert.NotEqual(FencePalette.Presets[i], FencePalette.Presets[j]);
    }

    [Fact]
    public void FromPrimary_DarkColor_KeepsWhiteText()
    {
        var a = FencePalette.FromPrimary(ArgbColor.FromArgb(0xFF, 0x2E, 0x3A, 0x5A));
        Assert.Equal(0xFF, a.HeaderText.A);
        Assert.Equal(0xFF, a.HeaderText.R);
        Assert.Equal(0xFF, a.HeaderText.G);
        Assert.Equal(0xFF, a.HeaderText.B);
    }

    [Fact]
    public void FromPrimary_BrightColor_SwitchesToBlackText()
    {
        var a = FencePalette.FromPrimary(ArgbColor.FromArgb(0xFF, 0xF5, 0xE8, 0x4C)); // bright yellow
        Assert.True(a.HeaderText.R < 0x40, "bright primary must produce dark text");
    }

    [Fact]
    public void FromPrimary_FillIsFainterThanHeader()
    {
        var a = FencePalette.FromPrimary(ArgbColor.FromArgb(0xFF, 0x4E, 0x68, 0x92));
        Assert.True(a.Fill.A < a.Header.A, "box fill should be fainter than the title band");
        Assert.True(a.Border.A > a.Fill.A, "border should carry the color more strongly than the fill");
        // Same RGB base across channels, only alpha differs.
        Assert.Equal(a.Fill.R, a.Border.R);
        Assert.Equal(a.Fill.R, a.Header.R);
        Assert.Equal(a.Fill.G, a.Border.G);
        Assert.Equal(a.Fill.G, a.Header.G);
    }

    [Fact]
    public void FromPrimary_ZeroAlphaPrimary_FallsBackToOpaque()
    {
        var a = FencePalette.FromPrimary(ArgbColor.FromArgb(0x00, 0x4E, 0x68, 0x92));
        // Alpha 0 would make every channel fully transparent — must be normalized to opaque first.
        Assert.True(a.Fill.A > 0);
        Assert.True(a.Header.A > 0);
    }
}
