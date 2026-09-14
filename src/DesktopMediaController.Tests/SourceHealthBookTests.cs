using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

/// <summary>
/// The policy these tests pin down exists because the lyric sources <b>throttle silently</b>: under
/// repeated querying they answer HTTP 200 with a success code and an empty result set. To a caller
/// that is indistinguishable from "this track is not in the catalogue" — which is how a rate limit
/// turns into a permanent "no lyrics" that nobody can explain.
/// </summary>
public sealed class SourceHealthBookTests
{
    private const string Qq = "QQMusic.exe";

    [Fact]
    public void ShouldSkip_ASourceNobodyHasJudgedYet_IsAskedImmediately()
    {
        Assert.False(new SourceHealthBook().ShouldSkip(Qq, 0));
    }

    [Fact]
    public void Record_ATransportFailure_BenchesTheSourceForThirtySeconds()
    {
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.Failed, nowMs: 1_000);

        var health = book.HealthOf(Qq, 1_000);
        Assert.True(health.IsCoolingDown);
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.Equal(SourceHealthBook.FirstFailureCooldownMs, health.CooldownRemainingMs);
    }

    [Fact]
    public void ShouldSkip_OnceTheCooldownHasElapsed_IsHappyToTryAgain()
    {
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.Failed, nowMs: 0);
        var until = SourceHealthBook.FirstFailureCooldownMs;

        Assert.True(book.ShouldSkip(Qq, until - 1));
        Assert.False(book.ShouldSkip(Qq, until));
    }

    [Fact]
    public void Record_RepeatedFailures_BackOffFurtherEachTime()
    {
        var book = new SourceHealthBook();

        book.Record(Qq, SourceOutcome.Failed, nowMs: 0);
        Assert.Equal(30_000, book.HealthOf(Qq, 0).CooldownRemainingMs);

        book.Record(Qq, SourceOutcome.Failed, nowMs: 1);
        Assert.Equal(60_000, book.HealthOf(Qq, 1).CooldownRemainingMs);

        book.Record(Qq, SourceOutcome.Failed, nowMs: 2);
        Assert.Equal(120_000, book.HealthOf(Qq, 2).CooldownRemainingMs);
    }

    [Fact]
    public void Record_ManyFailures_StopGrowingAtTheCeiling()
    {
        var book = new SourceHealthBook();
        for (var i = 0; i < 20; i++) book.Record(Qq, SourceOutcome.Failed, nowMs: i);

        Assert.Equal(SourceHealthBook.MaxFailureCooldownMs, book.HealthOf(Qq, 19).CooldownRemainingMs);
    }

    [Fact]
    public void Record_ASuccess_ClearsEverything()
    {
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.Failed, nowMs: 0);
        book.Record(Qq, SourceOutcome.Failed, nowMs: 1);

        book.Record(Qq, SourceOutcome.Found, nowMs: 2);

        var health = book.HealthOf(Qq, 2);
        Assert.False(health.IsCoolingDown);
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Equal(0, health.ConsecutiveEmptyAnswers);
    }

    [Fact]
    public void Record_EmptyAnswersNobodyContradicted_NeverBenchASource()
    {
        // The whole point. A source that has genuinely never heard of the last ten tracks the user
        // played is not broken, and benching it would take away the one source that might know the
        // eleventh.
        var book = new SourceHealthBook();
        for (var i = 0; i < 10; i++)
        {
            book.Record(Qq, SourceOutcome.NotFound, nowMs: i * 1_000, anotherSourceFound: false);
        }

        Assert.False(book.ShouldSkip(Qq, 20_000));
    }

    [Fact]
    public void Record_AnEmptyAnswerARivalContradicted_CountsTowardsTheCooldown()
    {
        var book = new SourceHealthBook();

        for (var i = 0; i < SourceHealthBook.EmptyAnswersBeforeCooldown - 1; i++)
        {
            book.Record(Qq, SourceOutcome.NotFound, nowMs: i, anotherSourceFound: true);
        }

        Assert.False(book.ShouldSkip(Qq, 10));

        book.Record(Qq, SourceOutcome.NotFound, nowMs: 10, anotherSourceFound: true);
        Assert.True(book.ShouldSkip(Qq, 10));
        Assert.Equal(SourceHealthBook.NotFoundCooldownMs, book.HealthOf(Qq, 10).CooldownRemainingMs);
    }

    [Fact]
    public void Record_AnUncontradictedMiss_IsNotEvidenceButDoesNotEraseTheEvidenceEither()
    {
        // "Nobody else found it either" means this observation says nothing about the source — so it is
        // not counted, and it does not undo what the contradictions before it already established.
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.NotFound, nowMs: 0, anotherSourceFound: true);
        book.Record(Qq, SourceOutcome.NotFound, nowMs: 1, anotherSourceFound: true);

        Assert.Equal(2, book.HealthOf(Qq, 1).ConsecutiveEmptyAnswers);

        book.Record(Qq, SourceOutcome.NotFound, nowMs: 2, anotherSourceFound: false);

        // The uncontradicted miss said nothing about the source, so the two contradictions that came
        // before it still stand: one more completes the count.
        book.Record(Qq, SourceOutcome.NotFound, nowMs: 3, anotherSourceFound: true);
        Assert.True(book.ShouldSkip(Qq, 3));
    }

    [Fact]
    public void Record_AnAnswerOfAnyKindMeansTheSourceIsReachableAgain()
    {
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.Failed, nowMs: 0);
        book.Record(Qq, SourceOutcome.Failed, nowMs: 1);
        Assert.Equal(2, book.HealthOf(Qq, 1).ConsecutiveFailures);

        book.Record(Qq, SourceOutcome.NotFound, nowMs: 2, anotherSourceFound: false);

        Assert.Equal(0, book.HealthOf(Qq, 2).ConsecutiveFailures);
    }

    [Fact]
    public void Record_OneSourcesTrouble_DoesNotBenchAnother()
    {
        var book = new SourceHealthBook();
        book.Record(Qq, SourceOutcome.Failed, nowMs: 0);

        Assert.True(book.ShouldSkip(Qq, 0));
        Assert.False(book.ShouldSkip("Netease", 0));
    }

    [Fact]
    public void ForgetAll_LetsTheUserDropTheWholeHistory()
    {
        var book = new SourceHealthBook();
        for (var i = 0; i < 5; i++) book.Record(Qq, SourceOutcome.Failed, nowMs: i);

        book.ForgetAll();

        Assert.False(book.ShouldSkip(Qq, 5));
        Assert.Equal(default, book.HealthOf(Qq, 5));
    }

    [Fact]
    public void HealthOf_ASourceNeverSeen_IsTheDefaultRatherThanAThrow()
    {
        Assert.Equal(default, new SourceHealthBook().HealthOf("Netease", 0));
    }
}
