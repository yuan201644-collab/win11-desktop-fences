using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class PlaybackProgressTests
{
    private static MediaSessionInfo Session(
        MediaPlaybackStatus status,
        bool canPlay = true,
        bool canPause = true,
        bool canNext = true,
        bool canPrevious = true) => new()
        {
            SourceId = "test.exe",
            Title = "Track",
            Status = status,
            CanPlay = canPlay,
            CanPause = canPause,
            CanNext = canNext,
            CanPrevious = canPrevious,
        };

    [Theory]
    [InlineData(0, 0, 0d)]
    [InlineData(-5, 0, 0d)]
    [InlineData(-5, 200, 0d)]
    [InlineData(0, 200, 0d)]
    public void Fraction_UnknownOrNonsensicalTimeline_DrawsAnEmptyBar(int positionSeconds, int durationSeconds, double expected)
    {
        Assert.Equal(expected, PlaybackProgress.Fraction(
            TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(durationSeconds)));
    }

    [Fact]
    public void Fraction_MidwayThroughTheTrack_IsHalf()
    {
        Assert.Equal(0.5, PlaybackProgress.Fraction(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)), 9);
    }

    [Fact]
    public void Fraction_PositionPastTheEnd_IsClampedToFullInsteadOfOverflowingTheBar()
    {
        Assert.Equal(1d, PlaybackProgress.Fraction(TimeSpan.FromSeconds(999), TimeSpan.FromSeconds(120)));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(259, "4:19")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    public void Format_RendersMinutesAndSecondsWithAnHoursFieldOnlyWhenNeeded(int seconds, string expected)
    {
        Assert.Equal(expected, PlaybackProgress.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Format_NegativePosition_ReadsAsZeroRatherThanMinusZero()
    {
        Assert.Equal("0:00", PlaybackProgress.Format(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Format_SubSecondPosition_DoesNotRoundUpToTheNextSecond()
    {
        Assert.Equal("0:59", PlaybackProgress.Format(TimeSpan.FromSeconds(59.9)));
    }

    [Fact]
    public void ButtonsFor_PlayingSession_ShowsPauseAndGatesOnThePlayersPauseSupport()
    {
        Assert.Equal(
            new TransportButtons(ShowPause: true, CanToggle: true, CanNext: true, CanPrevious: true),
            PlaybackProgress.ButtonsFor(Session(MediaPlaybackStatus.Playing)));
    }

    [Fact]
    public void ButtonsFor_PlayingSessionThatCannotPause_LeavesTheToggleUnclickableInsteadOfSilentlyFailing()
    {
        var buttons = PlaybackProgress.ButtonsFor(Session(MediaPlaybackStatus.Playing, canPause: false));

        Assert.True(buttons.ShowPause);
        Assert.False(buttons.CanToggle);
    }

    [Fact]
    public void ButtonsFor_PausedSession_ShowsPlayAndGatesOnPlaySupport()
    {
        Assert.Equal(
            new TransportButtons(ShowPause: false, CanToggle: true, CanNext: true, CanPrevious: true),
            PlaybackProgress.ButtonsFor(Session(MediaPlaybackStatus.Paused)));

        Assert.False(PlaybackProgress.ButtonsFor(
            Session(MediaPlaybackStatus.Paused, canPlay: false)).CanToggle);
    }

    [Fact]
    public void ButtonsFor_AStoppedSession_OffersPlayRatherThanPause()
    {
        Assert.False(PlaybackProgress.ButtonsFor(Session(MediaPlaybackStatus.Stopped)).ShowPause);
    }

    [Fact]
    public void ButtonsFor_PlayerWithoutSkipSupport_DisablesThoseButtons()
    {
        var buttons = PlaybackProgress.ButtonsFor(
            Session(MediaPlaybackStatus.Playing, canNext: false, canPrevious: false));

        Assert.False(buttons.CanNext);
        Assert.False(buttons.CanPrevious);
    }

    [Fact]
    public void ButtonsFor_NullSession_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PlaybackProgress.ButtonsFor(null!));
    }
}
