using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The card's single-primary color derivation: presets stay distinct, the derived channels keep their
/// relationship to the primary, and the hex parser accepts exactly the forms the menu produces.
/// </summary>
public sealed class CardPaletteTests
{
    [Fact]
    public void Presets_AreTenDistinctPrimaries()
    {
        Assert.Equal(10, CardPalette.Presets.Length);
        var accents = CardPalette.Presets.Select(p => p.Accent).ToArray();
        Assert.Equal(accents.Distinct().Count(), accents.Length);
    }

    [Fact]
    public void Presets_FirstIsTheShippedDefaultBlue()
    {
        Assert.Equal(CardTheme.Default, CardPalette.Presets[0]);
    }

    [Fact]
    public void FromPrimary_BorderKeepsThePrimaryHueAt30PercentAlpha()
    {
        var primary = ArgbColor.FromArgb(0xFF, 0x2E, 0x7D, 0x5B); // green
        var theme = CardPalette.FromPrimary(primary);

        Assert.Equal(0x4D, theme.Border.A);
        Assert.Equal(0x2E, theme.Border.R);
        Assert.Equal(0x7D, theme.Border.G);
        Assert.Equal(0x5B, theme.Border.B);
    }

    [Fact]
    public void FromPrimary_ProgressIsThePrimaryItself()
    {
        var primary = ArgbColor.FromArgb(0xFF, 0x9C, 0x3B, 0x3B); // brick red
        var theme = CardPalette.FromPrimary(primary);

        Assert.Equal(primary, theme.Progress);
    }

    [Fact]
    public void FromPrimary_LabelIsLighterThanThePrimary()
    {
        var primary = ArgbColor.FromArgb(0xFF, 0x2E, 0x8B, 0x8B); // teal
        var theme = CardPalette.FromPrimary(primary);

        Assert.True(theme.Label.R >= primary.R);
        Assert.True(theme.Label.G >= primary.G);
        Assert.True(theme.Label.B >= primary.B);
    }

    [Fact]
    public void FromPrimary_SungIsAtLeastAsBrightAsProgress()
    {
        var primary = ArgbColor.FromArgb(0xFF, 0x6A, 0x4E, 0x9C); // violet
        var theme = CardPalette.FromPrimary(primary);

        Assert.True(theme.Sung.R >= theme.Progress.R);
    }

    [Fact]
    public void Default_BackgroundIsTheDarkFill()
    {
        Assert.Equal(CardTheme.DarkBackground, CardTheme.Default.Background);
    }

    [Fact]
    public void FromPrimary_BackgroundIsIndependentOfTheAccent()
    {
        var primary = ArgbColor.FromArgb(0xFF, 0x2E, 0x7D, 0x5B); // green
        var theme = CardPalette.FromPrimary(primary);

        // The background is a fixed dark fill, never derived from the picked accent.
        Assert.Equal(CardTheme.DarkBackground, theme.Background);
        Assert.NotEqual(primary.R, theme.Background.R);
    }

    [Theory]
    [InlineData("#87C5FF", 0x87, 0xC5, 0xFF)]
    [InlineData("87C5FF", 0x87, 0xC5, 0xFF)]
    [InlineData("#C99A3E", 0xC9, 0x9A, 0x3E)]
    public void Parse_AcceptsHex(string input, byte r, byte g, byte b)
    {
        var color = ArgbColor.Parse(input);

        Assert.True(color.HasValue);
        Assert.Equal(0xFF, color!.Value.A);
        Assert.Equal(r, color.Value.R);
        Assert.Equal(g, color.Value.G);
        Assert.Equal(b, color.Value.B);
    }

    [Theory]
    [InlineData("#1A1A1A20", 0x20, 0x1A, 0x1A, 0x1A)]
    [InlineData("1A1A1A20", 0x20, 0x1A, 0x1A, 0x1A)]
    public void Parse_AcceptsHexWithAlpha(string input, byte a, byte r, byte g, byte b)
    {
        var color = ArgbColor.Parse(input);

        Assert.True(color.HasValue);
        Assert.Equal(a, color!.Value.A);
        Assert.Equal(r, color.Value.R);
        Assert.Equal(g, color.Value.G);
        Assert.Equal(b, color.Value.B);
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData(null)]
    public void Parse_RejectsMalformed(string? input)
    {
        Assert.False(ArgbColor.Parse(input).HasValue);
    }

    [Fact]
    public void Darken_ScalesRgbTowardBlackAndKeepsAlpha()
    {
        var color = ArgbColor.FromArgb(0xE8, 0x2A, 0x2A, 0x2C);

        var half = CardPalette.Darken(color, 0.5);
        Assert.Equal(0xE8, half.A);
        Assert.Equal(0x15, half.R);
        Assert.Equal(0x15, half.G);
        Assert.Equal(0x16, half.B);

        Assert.Equal(color, CardPalette.Darken(color, 1.0));
        var black = CardPalette.Darken(color, 0.0);
        Assert.Equal(0xE8, black.A);
        Assert.Equal(0, black.R);
        Assert.Equal(0, black.G);
        Assert.Equal(0, black.B);
    }

    [Fact]
    public void Darken_ClampsAnOutOfRangeFactor()
    {
        var color = ArgbColor.FromArgb(0xFF, 0x10, 0x20, 0x30);

        // Anything below 0 clamps to black — the factor is a fraction, not an offset.
        var clampedLow = CardPalette.Darken(color, -1.0);
        Assert.Equal(0, clampedLow.R);
        Assert.Equal(0, clampedLow.G);
        Assert.Equal(0, clampedLow.B);

        var clampedHigh = CardPalette.Darken(color, 5.0);
        Assert.Equal(color, clampedHigh);
    }

    [Fact]
    public void PanelFromBackground_IsDarkerThanTheBackgroundItself()
    {
        // The whole point of the lyric panel: an inset that reads as deeper than the card while
        // staying in the same colour family.
        foreach (var background in CardPalette.BackgroundPresets)
        {
            var opaque = background with { A = 0xFF };
            var panel = CardPalette.PanelFromBackground(opaque);

            // Pure black has nothing left to darken to; every other hue must lose some channel.
            if (opaque.R == 0 && opaque.G == 0 && opaque.B == 0) continue;

            Assert.True(panel.R < opaque.R || panel.G < opaque.G || panel.B < opaque.B,
                $"panel {panel} not darker than background {opaque}");
        }
    }
}
