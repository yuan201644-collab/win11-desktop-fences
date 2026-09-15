using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The source allow-list: which apps are music, which are merely tolerated, and which are never chosen
/// automatically. The rule that carries the weight is that an unknown id lands in
/// <see cref="SourceTier.Excluded"/> — the filter only ever lets through what someone vouched for.
/// </summary>
public sealed class MediaSourceFilterTests
{
    [Theory]
    [InlineData("qqmusic.exe", SourceTier.Preferred)]
    [InlineData("cloudmusic.exe", SourceTier.Preferred)]
    [InlineData("spotify", SourceTier.Preferred)]
    [InlineData("chrome.exe", SourceTier.Fallback)]
    [InlineData("msedge.exe", SourceTier.Fallback)]
    [InlineData("electron.app.douyin", SourceTier.Excluded)]
    [InlineData("potplayermini64.exe", SourceTier.Excluded)]
    [InlineData("tidal.exe", SourceTier.Excluded)]
    public void Default_TiersTheShippedSources(string sourceId, SourceTier expected)
    {
        Assert.Equal(expected, MediaSourceFilter.Default.TierOf(sourceId));
    }

    [Fact]
    public void TierOf_IgnoresCaseAndSurroundingWhitespace()
    {
        // SMTC reports whatever the player registered, which is not guaranteed to match the casing the
        // user typed into the file.
        Assert.Equal(SourceTier.Preferred, MediaSourceFilter.Default.TierOf("QQMusic.EXE"));
        Assert.Equal(SourceTier.Preferred, MediaSourceFilter.Default.TierOf("  qqmusic.exe  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TierOf_BlankId_IsExcluded(string? sourceId)
    {
        Assert.Equal(SourceTier.Excluded, MediaSourceFilter.Default.TierOf(sourceId));
    }

    [Fact]
    public void TierOf_MatchesTheWholeId_SoALookalikeNameIsNotMistakenForItsTarget()
    {
        // The reason this is an exact match and not a substring search: "PotPlayer" contains "player"
        // and "my-qqmusic-helper.exe" contains a real player's name, yet neither is that player.
        Assert.Equal(SourceTier.Excluded, MediaSourceFilter.Default.TierOf("my-qqmusic-helper.exe"));
        Assert.Equal(SourceTier.Preferred, MediaSourceFilter.Default.TierOf("qqmusic.exe"));
    }

    [Fact]
    public void ListedInBothLists_IsTreatedAsPreferred()
    {
        var filter = new MediaSourceFilter(["chrome.exe"], ["chrome.exe"]);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("chrome.exe"));
    }

    [Fact]
    public void BlankEntriesInTheLists_AreDroppedRatherThanBecomingMatchableIds()
    {
        var filter = new MediaSourceFilter(["qqmusic.exe", "", "   "], ["chrome.exe"]);

        Assert.Equal(2, filter.Preferred.Count + filter.Fallback.Count);
        Assert.Equal(SourceTier.Excluded, filter.TierOf("   "));
    }

    [Fact]
    public void EmptyLists_ExcludeEverything()
    {
        // An emptied "fallback" is a legitimate thing to mean — no browsers on the card at all.
        var filter = new MediaSourceFilter(["qqmusic.exe"], []);

        Assert.Equal(SourceTier.Preferred, filter.TierOf("qqmusic.exe"));
        Assert.False(filter.IsAutoEligible("chrome.exe"));
    }

    [Fact]
    public void NullLists_ExcludeEverything()
    {
        var filter = new MediaSourceFilter(null, null);

        Assert.False(filter.IsAutoEligible("qqmusic.exe"));
        Assert.Equal(SourceTier.Excluded, filter.TierOf("qqmusic.exe"));
    }

    [Fact]
    public void IsAutoEligible_IsTrueForBothChosenTiersAndFalseForTheRest()
    {
        var filter = MediaSourceFilter.Default;

        Assert.True(filter.IsAutoEligible("qqmusic.exe"));
        Assert.True(filter.IsAutoEligible("chrome.exe"));
        Assert.False(filter.IsAutoEligible("electron.app.douyin"));
    }
}
