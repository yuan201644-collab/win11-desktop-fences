using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The picker with a source filter attached: the automatic choice is confined to the allow-list, the
/// manual cycle still reaches everything, and a hand-picked session is held instead of being reverted
/// on the next poll. The last two are the whole reason the filter lives in the picker rather than at
/// the call site.
/// </summary>
public sealed class SessionPickerTierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 9, 0, 0, TimeSpan.FromHours(8));

    /// <summary>A session; <paramref name="updatedSecondsAfterT0"/> just orders recency between them.</summary>
    private static MediaSessionInfo Session(
        string sourceId, MediaPlaybackStatus status, int updatedSecondsAfterT0 = 0) => new()
        {
            SourceId = sourceId,
            Title = "Track",
            Artist = "Artist",
            Status = status,
            CanPlay = true,
            CanPause = true,
            LastUpdated = T0.AddSeconds(updatedSecondsAfterT0),
        };

    private static SessionPicker Filtered() => new(MediaSourceFilter.Default);

    [Fact]
    public void Choose_PlayerAnd抖音BothPlaying_ShowsThePlayer()
    {
        // The case the filter exists for: 抖音 is playing, ticking its clock hardest, and would win on
        // recency alone.
        MediaSessionInfo[] sessions =
        [
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90),
            Session("qqmusic.exe", MediaPlaybackStatus.Playing, 0),
        ];

        var chosen = Filtered().Choose(sessions);

        Assert.Equal(1, chosen);
        Assert.Equal("qqmusic.exe", sessions[chosen!.Value].SourceId);
    }

    [Fact]
    public void Choose_OnlyAnExcludedSessionPlaying_ShowsNothingRatherThanTheWrongThing()
    {
        MediaSessionInfo[] sessions = [Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90)];

        Assert.Null(Filtered().Choose(sessions));
    }

    [Fact]
    public void Choose_OnlyABrowserPlaying_UsesTheBrowserAsTheFallback()
    {
        MediaSessionInfo[] sessions = [Session("chrome.exe", MediaPlaybackStatus.Playing, 10)];

        Assert.Equal(0, Filtered().Choose(sessions));
    }

    [Fact]
    public void Choose_PausedPlayerBeatsAPlayingBrowser_BecauseTierOutranksStatus()
    {
        // The documented price of tier-first ordering: a paused player keeps the card. Acceptable for
        // a music controller, and stickiness means the pause itself rarely even reaches this decision.
        MediaSessionInfo[] sessions =
        [
            Session("chrome.exe", MediaPlaybackStatus.Playing, 90),
            Session("qqmusic.exe", MediaPlaybackStatus.Paused, 0),
        ];

        Assert.Equal(1, Filtered().Choose(sessions));
    }

    [Fact]
    public void Choose_PlayingPlayerBeatsAMoreRecentlyUpdatedPlayingBrowser()
    {
        MediaSessionInfo[] sessions =
        [
            Session("msedge.exe", MediaPlaybackStatus.Playing, 90),
            Session("cloudmusic.exe", MediaPlaybackStatus.Playing, 1),
        ];

        Assert.Equal(1, Filtered().Choose(sessions));
    }

    [Fact]
    public void Choose_IndicesStillPointIntoTheOriginalList_SoTheCallerCanReachItsOwnArray()
    {
        // The filter drops candidates; it must not compact the list. The caller uses the returned index
        // against its parallel array of live SMTC sessions, so a shifted index would command the wrong
        // player — the bug this test exists to prevent.
        MediaSessionInfo[] sessions =
        [
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90),
            Session("chrome.exe", MediaPlaybackStatus.Playing, 60),
            Session("cloudmusic.exe", MediaPlaybackStatus.Playing, 0),
        ];

        var chosen = Filtered().Choose(sessions);

        Assert.Equal(2, chosen);
        Assert.Equal("cloudmusic.exe", sessions[chosen!.Value].SourceId);
    }

    [Fact]
    public void ChooseNext_ReachesASessionTheFilterWouldNeverOffer()
    {
        MediaSessionInfo[] sessions =
        [
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90),
            Session("qqmusic.exe", MediaPlaybackStatus.Playing, 0),
        ];
        var picker = Filtered();
        Assert.Equal(1, picker.Choose(sessions));

        var next = picker.ChooseNext(sessions);

        Assert.Equal(0, next);
        Assert.Equal("electron.app.douyin", sessions[next!.Value].SourceId);
    }

    [Fact]
    public void Choose_HoldsASessionPickedByHand_InsteadOfRevertingItOnTheNextPoll()
    {
        // Without this, "manually reachable but never automatic" would be undone 300 ms later and the
        // escape hatch would be decorative.
        MediaSessionInfo[] sessions =
        [
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90),
            Session("qqmusic.exe", MediaPlaybackStatus.Playing, 0),
        ];
        var picker = Filtered();
        picker.Choose(sessions);
        Assert.Equal(0, picker.ChooseNext(sessions));

        Assert.Equal(0, picker.Choose(sessions));
        Assert.Equal("electron.app.douyin", picker.StickySourceId);
    }

    [Fact]
    public void Choose_AfterTheHandPickedSessionStop_FallsBackToThePlayer()
    {
        MediaSessionInfo[] sessions =
        [
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90),
            Session("qqmusic.exe", MediaPlaybackStatus.Playing, 0),
        ];
        var picker = Filtered();
        picker.Choose(sessions);
        picker.ChooseNext(sessions);

        sessions[0] = Session("electron.app.douyin", MediaPlaybackStatus.Stopped, 90);

        Assert.Equal(1, picker.Choose(sessions));
    }

    [Fact]
    public void Choose_WithoutAFilter_KeepsTreatingEverySourceAlike()
    {
        // The pre-filter behaviour, still the default for callers that pass nothing.
        MediaSessionInfo[] sessions = [Session("electron.app.douyin", MediaPlaybackStatus.Playing, 90)];

        Assert.Equal(0, new SessionPicker().Choose(sessions));
    }
}
