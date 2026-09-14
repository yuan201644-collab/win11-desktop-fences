using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class LyricsCacheKeyTests
{
    [Fact]
    public void For_IsCaseInsensitive_SoAPlayerThatShoutsStillHitsTheCache()
    {
        Assert.Equal(
            LyricsCacheKey.For("Blinding Lights", "The Weeknd"),
            LyricsCacheKey.For("BLINDING LIGHTS", "the weeknd"));
    }

    [Fact]
    public void For_CollapsesWhitespace_SoATrailingSpaceDoesNotDuplicateTheEntry()
    {
        Assert.Equal(
            LyricsCacheKey.For("晴天", "周杰伦"),
            LyricsCacheKey.For("  晴天 ", "周杰伦  "));
    }

    [Fact]
    public void For_DoesNotLetTwoFieldsRunIntoEachOther()
    {
        // Without a separator, ("AB", "C") and ("A", "BC") would be the same key and the second track
        // would be served the first one's lyrics.
        Assert.NotEqual(LyricsCacheKey.For("AB", "C"), LyricsCacheKey.For("A", "BC"));
    }

    [Fact]
    public void For_TreatsAMissingArtistAsEmptyRatherThanThrowing()
    {
        Assert.Equal(LyricsCacheKey.For("晴天", string.Empty), LyricsCacheKey.For("晴天", null));
    }

    [Fact]
    public void FileNameFor_IsStableAndEndsInJson()
    {
        var key = LyricsCacheKey.For("晴天", "周杰伦");
        var name = LyricsCacheKey.FileNameFor(key);

        Assert.Equal(name, LyricsCacheKey.FileNameFor(key));
        Assert.EndsWith(".json", name);
        Assert.Equal(69, name.Length); // 64 hex characters plus the extension
    }

    [Fact]
    public void FileNameFor_ProducesSomethingWindowsWillActuallyAccept()
    {
        // Titles really do contain these, and an unescaped name would either fail to save or silently
        // turn one cache entry into a nested directory.
        var name = LyricsCacheKey.FileNameFor(LyricsCacheKey.For("AC/DC: Back in Black?", "A\\B"));

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
    }

    [Fact]
    public void FileNameFor_DifferentTracksGetDifferentFiles()
    {
        Assert.NotEqual(
            LyricsCacheKey.FileNameFor(LyricsCacheKey.For("晴天", "周杰伦")),
            LyricsCacheKey.FileNameFor(LyricsCacheKey.For("演员", "薛之谦")));
    }
}
