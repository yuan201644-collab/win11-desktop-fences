using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class SessionPickerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private static MediaSessionInfo Session(
        string sourceId,
        MediaPlaybackStatus status,
        DateTimeOffset? lastUpdated = null,
        string title = "Track",
        bool canPlay = true,
        bool canPause = true,
        bool canNext = true,
        bool canPrevious = true) => new()
        {
            SourceId = sourceId,
            Title = title,
            Artist = "Artist",
            Status = status,
            CanPlay = canPlay,
            CanPause = canPause,
            CanNext = canNext,
            CanPrevious = canPrevious,
            LastUpdated = lastUpdated ?? T0,
        };

    [Fact]
    public void Choose_NoSessionsAtAll_ReturnsNothingToShow()
    {
        Assert.Null(new SessionPicker().Choose([]));
    }

    [Fact]
    public void Choose_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionPicker().Choose(null!));
    }

    [Fact]
    public void Choose_OnlyStoppedOrClosedSessions_ReturnsNothingSoTheWidgetCanShowItsIdleState()
    {
        var picker = new SessionPicker();

        Assert.Null(picker.Choose([Session("QQMusic.exe", MediaPlaybackStatus.Stopped)]));
        Assert.Null(picker.Choose([Session("QQMusic.exe", MediaPlaybackStatus.Closed)]));
    }

    [Fact]
    public void Choose_APlayingSessionBeatsAPausedOneRegardlessOfOrder()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session("CloudMusic.exe", MediaPlaybackStatus.Paused),
            Session("QQMusic.exe", MediaPlaybackStatus.Playing),
        };

        Assert.Equal(1, picker.Choose(sessions));
    }

    [Fact]
    public void Choose_TwoPlayingSessions_PrefersTheOneThatUpdatedItsTimelineMostRecently()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, T0),
            Session("QQMusic.exe", MediaPlaybackStatus.Playing, T0.AddSeconds(4)),
        };

        Assert.Equal(1, picker.Choose(sessions));
    }

    [Fact]
    public void Choose_FullyTiedSessions_AreBrokenByListOrderSoTheChoiceIsNeverArbitrary()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session("a.exe", MediaPlaybackStatus.Playing, T0),
            Session("b.exe", MediaPlaybackStatus.Playing, T0),
        };

        Assert.Equal(0, picker.Choose(sessions));
    }

    /// <summary>
    /// The behaviour the whole class exists for. Two players are genuinely playing at once here and
    /// the held one has become the *worse* candidate on paper; it must still be kept, because
    /// re-ranking would swap the cover art and transport buttons back and forth every time either
    /// player ticked its position forward.
    /// </summary>
    [Fact]
    public void Choose_HoldsTheChosenSessionEvenWhenAnotherOneLooksLikeABetterCandidate()
    {
        var picker = new SessionPicker();
        var first = new[]
        {
            Session("QQMusic.exe", MediaPlaybackStatus.Playing, T0),
            Session("electron.app.douyin", MediaPlaybackStatus.Paused, T0),
        };
        Assert.Equal(0, picker.Choose(first));

        // QQ音乐 pauses; 抖音 starts playing and is now the obviously better candidate.
        var later = new[]
        {
            Session("QQMusic.exe", MediaPlaybackStatus.Paused, T0),
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, T0.AddMinutes(1)),
        };

        Assert.Equal(0, picker.Choose(later));
        Assert.Equal("QQMusic.exe", picker.StickySourceId);
    }

    [Fact]
    public void Choose_ReRanksOnceTheHeldSessionStops()
    {
        var picker = new SessionPicker();
        Assert.Equal(0, picker.Choose([
            Session("QQMusic.exe", MediaPlaybackStatus.Playing, T0),
            Session("electron.app.douyin", MediaPlaybackStatus.Paused, T0),
        ]));

        var afterQqStopped = new[]
        {
            Session("QQMusic.exe", MediaPlaybackStatus.Stopped, T0),
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, T0.AddMinutes(2)),
        };

        Assert.Equal(1, picker.Choose(afterQqStopped));
        Assert.Equal("electron.app.douyin", picker.StickySourceId);
    }

    [Fact]
    public void Choose_ForgetsTheHeldSessionWhenNothingIsUsableAnyMore()
    {
        var picker = new SessionPicker();
        Assert.Equal(0, picker.Choose([Session("QQMusic.exe", MediaPlaybackStatus.Playing)]));

        Assert.Null(picker.Choose([]));
        Assert.Null(picker.StickySourceId);
    }

    [Fact]
    public void Choose_SkipsASessionWithNoSourceIdBecauseItCannotBeReidentified()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session(string.Empty, MediaPlaybackStatus.Playing, T0.AddHours(1)),
            Session("QQMusic.exe", MediaPlaybackStatus.Paused, T0),
        };

        Assert.Equal(1, picker.Choose(sessions));
    }

    [Fact]
    public void Forget_DropsTheHeldSessionSoTheNextPickIsAFreshRanking()
    {
        var picker = new SessionPicker();
        var qqBest = new[]
        {
            Session("QQMusic.exe", MediaPlaybackStatus.Playing, T0.AddMinutes(2)),
            Session("electron.app.douyin", MediaPlaybackStatus.Paused, T0),
        };
        Assert.Equal(0, picker.Choose(qqBest));
        Assert.Equal("QQMusic.exe", picker.StickySourceId);

        // 抖音 takes over as the better candidate, but the held session keeps winning...
        var douyinBest = new[]
        {
            Session("QQMusic.exe", MediaPlaybackStatus.Paused, T0),
            Session("electron.app.douyin", MediaPlaybackStatus.Playing, T0.AddMinutes(3)),
        };
        Assert.Equal(0, picker.Choose(douyinBest));

        // ...until the user says otherwise.
        picker.Forget();

        Assert.Equal(1, picker.Choose(douyinBest));
        Assert.Equal("electron.app.douyin", picker.StickySourceId);
    }

    [Fact]
    public void ChooseNext_WalksThroughTheUsableSessionsInRankOrderAndWrapsAround()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session("a.exe", MediaPlaybackStatus.Playing, T0.AddMinutes(2)),
            Session("b.exe", MediaPlaybackStatus.Playing, T0.AddMinutes(1)),
            Session("c.exe", MediaPlaybackStatus.Paused, T0),
        };

        // Rank order is a, b, c.
        Assert.Equal(0, picker.Choose(sessions));
        Assert.Equal(1, picker.ChooseNext(sessions));
        Assert.Equal(2, picker.ChooseNext(sessions));
        Assert.Equal(0, picker.ChooseNext(sessions));
    }

    [Fact]
    public void ChooseNext_WithNothingHeld_StartsFromTheBestCandidate()
    {
        var picker = new SessionPicker();
        var sessions = new[]
        {
            Session("a.exe", MediaPlaybackStatus.Paused, T0),
            Session("b.exe", MediaPlaybackStatus.Playing, T0),
        };

        Assert.Equal(1, picker.ChooseNext(sessions));
    }

    [Fact]
    public void ChooseNext_WhenNothingIsUsable_ReturnsNothingRatherThanThrowing()
    {
        var picker = new SessionPicker();

        Assert.Null(picker.ChooseNext([Session("a.exe", MediaPlaybackStatus.Closed)]));
        Assert.Null(picker.ChooseNext([]));
    }

    [Fact]
    public void ChooseNext_FallsBackToTheFrontWhenTheHeldSessionIsNoLongerListed()
    {
        var picker = new SessionPicker();
        Assert.Equal(0, picker.Choose([
            Session("a.exe", MediaPlaybackStatus.Playing, T0.AddMinutes(2)),
            Session("b.exe", MediaPlaybackStatus.Playing, T0),
        ]));

        var onlyB = new[] { Session("b.exe", MediaPlaybackStatus.Playing, T0) };

        Assert.Equal(0, picker.ChooseNext(onlyB));
        Assert.Equal("b.exe", picker.StickySourceId);
    }
}
