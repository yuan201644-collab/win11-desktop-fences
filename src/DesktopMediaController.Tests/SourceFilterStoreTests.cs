using System.IO;
using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The allow-list store: a missing file is created (the list has to be discoverable), a corrupt one
/// falls back to the defaults instead of stopping the widget, and an empty "preferred" is read as a
/// broken edit rather than as an instruction to show nothing ever again.
/// </summary>
public sealed class SourceFilterStoreTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "dmc-tests", Guid.NewGuid().ToString("N"), "sources.json");

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void DefaultFilePath_LivesInTheControllerDataFolder()
    {
        Assert.EndsWith("sources.json", SourceFilterStore.DefaultFilePath);
        Assert.Contains("DesktopMediaController", SourceFilterStore.DefaultFilePath);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultAndLeavesNoFileBehind()
    {
        var path = TempFile();

        var filter = SourceFilterStore.Load(path);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("qqmusic.exe"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultInsteadOfThrowing()
    {
        var path = TempFile();
        Write(path, "{ not json");

        Assert.Equal(SourceTier.Preferred, SourceFilterStore.Load(path).TierOf("qqmusic.exe"));
    }

    [Fact]
    public void LoadOrCreate_WritesTheDefaultFileWithItsExplanatoryComments()
    {
        var path = TempFile();

        var filter = SourceFilterStore.LoadOrCreate(path);

        var text = File.ReadAllText(path);
        Assert.StartsWith("//", text);
        Assert.Contains("preferred", text);

        // The comments must not stop the file from being read back as JSON.
        Assert.Equal(SourceTier.Preferred, filter.TierOf("qqmusic.exe"));
        Assert.Equal(SourceTier.Fallback, filter.TierOf("chrome.exe"));
        Assert.Equal(SourceTier.Excluded, filter.TierOf("electron.app.douyin"));
    }

    [Fact]
    public void LoadOrCreate_DoesNotOverwriteAnExistingFile()
    {
        var path = TempFile();
        Write(path, """{ "preferred": ["tidal.exe"], "fallback": [] }""");

        var filter = SourceFilterStore.LoadOrCreate(path);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("tidal.exe"));
    }

    [Fact]
    public void Load_EmptyPreferred_FallsBackToTheShippedPlayersButKeepsTheEditedFallback()
    {
        // The half-broken edit: the user cleared one list. Showing nothing at all would look like the
        // controller is broken, so the defaults stand in — while the list they did edit is honoured.
        var path = TempFile();
        Write(path, """{ "preferred": [], "fallback": ["vivaldi.exe"] }""");

        var filter = SourceFilterStore.Load(path);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("qqmusic.exe"));
        Assert.Equal(SourceTier.Fallback, filter.TierOf("vivaldi.exe"));
    }

    [Fact]
    public void Load_AbsentPreferredKey_IsTreatedTheSameWay()
    {
        var path = TempFile();
        Write(path, """{ "fallback": ["vivaldi.exe"] }""");

        var filter = SourceFilterStore.Load(path);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("qqmusic.exe"));
        Assert.Equal(SourceTier.Fallback, filter.TierOf("vivaldi.exe"));
    }

    [Fact]
    public void Load_EmptyFallback_MeansNoBrowsersRatherThanTheDefaults()
    {
        // The other direction has to stay editable: "do not put my browser on the card" is a real
        // preference, and an empty list is the way to say it.
        var path = TempFile();
        Write(path, """{ "preferred": ["qqmusic.exe"], "fallback": [] }""");

        var filter = SourceFilterStore.Load(path);

        Assert.Equal(SourceTier.Excluded, filter.TierOf("chrome.exe"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBothLists()
    {
        var path = TempFile();
        var saved = new MediaSourceFilter(["qqmusic.exe", "tidal.exe"], ["vivaldi.exe"]);

        SourceFilterStore.Save(path, saved);

        var loaded = SourceFilterStore.Load(path);
        Assert.Equal(SourceTier.Preferred, loaded.TierOf("tidal.exe"));
        Assert.Equal(SourceTier.Fallback, loaded.TierOf("vivaldi.exe"));
        Assert.Equal(SourceTier.Excluded, loaded.TierOf("chrome.exe"));
    }

    [Fact]
    public void Load_ToleratesTrailingCommasAndCommentsFromHandEditing()
    {
        var path = TempFile();
        Write(path, """
            {
              // 我自己加的注释
              "preferred": ["tidal.exe"],
              "fallback": [],
            }
            """);

        Assert.Equal(SourceTier.Preferred, SourceFilterStore.Load(path).TierOf("tidal.exe"));
    }

    [Fact]
    public void Load_NullDocument_ReturnsDefault()
    {
        var path = TempFile();
        Write(path, "null");

        Assert.Equal(SourceTier.Preferred, SourceFilterStore.Load(path).TierOf("qqmusic.exe"));
    }
}
