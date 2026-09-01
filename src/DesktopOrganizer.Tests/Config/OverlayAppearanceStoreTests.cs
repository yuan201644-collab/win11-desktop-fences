using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class OverlayAppearanceStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"overlay-test-{Path.GetRandomFileName()}.json");

    [Fact]
    public void DefaultPalette_HasAllFourColors()
    {
        var a = OverlayAppearance.Default;
        Assert.NotEqual(default, a.Fill);
        Assert.NotEqual(default, a.Border);
        Assert.NotEqual(default, a.Header);
        Assert.Equal(0xFF, a.HeaderText.A);
    }

    [Fact]
    public void RoundTrip_PreservesEveryColorAndAlpha()
    {
        var appearance = new OverlayAppearance(
            ArgbColor.FromArgb(0x20, 0x11, 0x22, 0x33),
            ArgbColor.FromArgb(0x80, 0xAA, 0xBB, 0xCC),
            ArgbColor.FromArgb(0x99, 0x11, 0xBB, 0xEE),
            ArgbColor.FromArgb(0xE0, 0xFF, 0x00, 0x11));
        var path = TempPath();
        try
        {
            OverlayAppearanceStore.Save(path, appearance);
            var loaded = OverlayAppearanceStore.Load(path);

            Assert.Equal(appearance.Fill, loaded.Fill);
            Assert.Equal(appearance.Border, loaded.Border);
            Assert.Equal(appearance.Header, loaded.Header);
            Assert.Equal(appearance.HeaderText, loaded.HeaderText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        Assert.Equal(OverlayAppearance.Default, OverlayAppearanceStore.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-overlay.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultWithoutThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json !!");
            var loaded = OverlayAppearanceStore.Load(path);
            Assert.Equal(OverlayAppearance.Default, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "overlay-test-dir-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, "sub", "overlay.json");
        try
        {
            OverlayAppearanceStore.Save(path, OverlayAppearance.Default);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}