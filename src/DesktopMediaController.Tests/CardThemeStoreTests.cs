using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The theme store: a missing or corrupt file falls back to the default, and a saved theme — preset or
/// custom — round-trips through JSON exactly. This is also what proves the ArgbColor record struct and
/// CardTheme record survive System.Text.Json in both directions.
/// </summary>
public sealed class CardThemeStoreTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "dmc-tests", Guid.NewGuid().ToString("N"), "theme.json");

    [Fact]
    public void DefaultFilePath_LivesInTheControllerDataFolder()
    {
        Assert.EndsWith("theme.json", CardThemeStore.DefaultFilePath);
        Assert.Contains("DesktopMediaController", CardThemeStore.DefaultFilePath);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        Assert.Equal(CardTheme.Default, CardThemeStore.Load(TempFile()));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultInsteadOfThrowing()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.Equal(CardTheme.Default, CardThemeStore.Load(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAPresetTheme()
    {
        var path = TempFile();
        var saved = CardPalette.Presets[3]; // brick red

        CardThemeStore.Save(path, saved);

        Assert.Equal(saved, CardThemeStore.Load(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsACustomTheme()
    {
        var path = TempFile();
        var saved = CardPalette.FromPrimary(ArgbColor.FromArgb(0xFF, 0x12, 0x34, 0x56));

        CardThemeStore.Save(path, saved);

        Assert.Equal(saved, CardThemeStore.Load(path));
    }
}
