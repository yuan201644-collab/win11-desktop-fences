using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The clock's job is to turn SMTC's sparse, sometimes-stale snapshots into a position that moves
/// smoothly, without ever mistaking a seek for drift or a pause for a stall.
/// </summary>
/// <remarks>
/// The numbers here mirror what was actually measured off QQ音乐: snapshots arrive a few hundred
/// milliseconds stale, and one reading in five repeats the previous position verbatim.
/// </remarks>
public sealed class PlaybackClockTests
{
    [Fact]
    public void EstimateAt_BeforeAnyReading_IsZeroRatherThanNonsense()
    {
        Assert.Equal(0, new PlaybackClock().EstimateAt(12_345));
    }

    [Fact]
    public void EstimateAt_WhilePlaying_AdvancesWithTheTick()
    {
        var clock = new PlaybackClock();
        clock.Sync(positionMs: 30_000, lagMs: 0, playing: true, nowTickMs: 1_000);

        Assert.Equal(30_000, clock.EstimateAt(1_000));
        Assert.Equal(31_500, clock.EstimateAt(2_500));
    }

    [Fact]
    public void EstimateAt_WhilePaused_HoldsStill()
    {
        // A paused player is not a stopped clock, it is an unplugged one: letting the estimate run on
        // would walk the lyrics away from the music by exactly the length of the pause.
        var clock = new PlaybackClock();
        clock.Sync(positionMs: 30_000, lagMs: 0, playing: true, nowTickMs: 1_000);
        clock.Sync(positionMs: 31_000, lagMs: 0, playing: false, nowTickMs: 2_000);

        Assert.Equal(31_000, clock.EstimateAt(2_000));
        Assert.Equal(31_000, clock.EstimateAt(60_000));
    }

    [Fact]
    public void EstimateAt_FoldsInHowStaleTheSnapshotAlreadyWas()
    {
        // This is the whole reason the reconstruction is accurate. The snapshot says "position was
        // 30 s", but it was recorded 2 s before we read it, so the playhead is really at 32 s now.
        // Reading Position alone would put the lyrics a whole line behind.
        var clock = new PlaybackClock();
        clock.Sync(positionMs: 30_000, lagMs: 2_000, playing: true, nowTickMs: 100_000);

        Assert.Equal(32_000, clock.EstimateAt(100_000));
    }

    [Fact]
    public void Sync_FirstReading_IsInitialRatherThanAJump()
    {
        var clock = new PlaybackClock();

        Assert.Equal(ClockSync.Initial, clock.Sync(30_000, 0, playing: true, nowTickMs: 0));
        Assert.True(clock.HasAnchor);
    }

    [Fact]
    public void Sync_AReadingThatMatchesElapsedTime_IsFollow()
    {
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 0);

        // 1 s later the player reports 1 s more; 100 ms of staleness is well inside the noise band.
        Assert.Equal(ClockSync.Follow, clock.Sync(31_000, 100, playing: true, nowTickMs: 1_000));
    }

    [Fact]
    public void Sync_ARepeatedPositionWhilePlaying_IsNotAJump()
    {
        // One reading in five is identical to the previous one. That is the snapshot mechanism, not a
        // seek, and treating it as one would reset the clock several times a second.
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 0);

        Assert.Equal(ClockSync.Follow, clock.Sync(30_000, 250, playing: true, nowTickMs: 250));
    }

    [Fact]
    public void Sync_AForwardDragBiggerThanTheThreshold_IsAJump()
    {
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 0);

        // Dragged forward by a minute while a second of real time passed.
        Assert.Equal(ClockSync.Jumped, clock.Sync(91_000, 0, playing: true, nowTickMs: 1_000));
    }

    [Fact]
    public void Sync_ABackwardDrag_IsAJump()
    {
        var clock = new PlaybackClock();
        clock.Sync(120_000, 0, playing: true, nowTickMs: 0);

        Assert.Equal(ClockSync.Jumped, clock.Sync(10_000, 0, playing: true, nowTickMs: 1_000));
    }

    [Fact]
    public void Sync_CorrectionJustUnderTheThreshold_IsNotAJump()
    {
        // 600 ms is the threshold; the measured steady-state jitter never got past ~250 ms, so
        // anything inside the band has to stay quiet.
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 0);

        Assert.Equal(ClockSync.Follow, clock.Sync(30_600, 0, playing: true, nowTickMs: 1_000));
    }

    [Fact]
    public void Sync_DoesNotLockOutAfterAJump_SoTheDragsSecondCorrectionIsAlsoSeen()
    {
        // Measured: one drag produces two corrections roughly a second apart — the seek itself and the
        // player's own settle afterwards. A detector that ignored everything for N seconds after the
        // first would swallow the second and leave the lyrics wrong for that second.
        var clock = new PlaybackClock();
        clock.Sync(10_000, 0, playing: true, nowTickMs: 0);
        Assert.Equal(ClockSync.Follow, clock.Sync(11_000, 0, playing: true, nowTickMs: 1_000));

        Assert.Equal(ClockSync.Jumped, clock.Sync(90_000, 0, playing: true, nowTickMs: 1_100));
        Assert.Equal(ClockSync.Jumped, clock.Sync(91_050, 0, playing: true, nowTickMs: 1_300));
    }

    [Fact]
    public void Sync_WhilePaused_IsNeverAJump()
    {
        // A paused player keeps the timestamp it had when it stopped, so a paused snapshot's staleness
        // is unbounded. Sizing that up as a seek would fire on every pause.
        var clock = new PlaybackClock();
        clock.Sync(10_000, 0, playing: true, nowTickMs: 0);

        Assert.Equal(ClockSync.Follow, clock.Sync(15_000, 4_000, playing: false, nowTickMs: 5_000));
    }

    [Fact]
    public void Sync_ResumingFromPause_ReAnchorsWhereThePlayheadActuallyIs()
    {
        var clock = new PlaybackClock();
        clock.Sync(10_000, 0, playing: true, nowTickMs: 0);
        clock.Sync(15_000, 0, playing: false, nowTickMs: 5_000);
        clock.Sync(15_000, 0, playing: true, nowTickMs: 30_000);

        Assert.Equal(15_000, clock.EstimateAt(30_000));
        Assert.Equal(16_000, clock.EstimateAt(31_000));
    }

    [Fact]
    public void Sync_WithoutATimeline_SaysNothingRatherThanGuessingZero()
    {
        // A player that publishes no timeline at all (some sources do not) must not be able to yank the
        // playhead back to the start of the track.
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 0);

        Assert.Equal(ClockSync.Follow, clock.Sync(0, 0, playing: true, nowTickMs: 1_000, positionKnown: false));
        Assert.Equal(31_000, clock.EstimateAt(1_000));
    }

    [Fact]
    public void Sync_WithoutATimelineBeforeAnyAnchor_IsStillInitial()
    {
        var clock = new PlaybackClock();

        Assert.Equal(ClockSync.Initial, clock.Sync(0, 0, playing: true, nowTickMs: 0, positionKnown: false));
        Assert.False(clock.HasAnchor);
    }

    [Fact]
    public void Reset_ForgetsEverythingSoANewTrackCannotBeComparedWithTheOldOne()
    {
        var clock = new PlaybackClock();
        clock.Sync(120_000, 0, playing: true, nowTickMs: 0);

        clock.Reset();

        Assert.False(clock.HasAnchor);
        Assert.Equal(0, clock.EstimateAt(1_000));
        // The first reading of the new track is an anchor, not a 100-second backwards seek.
        Assert.Equal(ClockSync.Initial, clock.Sync(1_000, 0, playing: true, nowTickMs: 1_000));
    }

    [Fact]
    public void EstimateAt_ATickFromTheFuture_HoldsInsteadOfGoingBackwards()
    {
        var clock = new PlaybackClock();
        clock.Sync(30_000, 0, playing: true, nowTickMs: 1_000);

        Assert.Equal(30_000, clock.EstimateAt(500));
    }

    [Fact]
    public void ReportedPositionMs_IsTheRawReadingNotTheReconstruction()
    {
        var clock = new PlaybackClock();
        clock.Sync(30_000, 2_000, playing: true, nowTickMs: 100_000);

        Assert.Equal(30_000, clock.ReportedPositionMs);
    }
}
