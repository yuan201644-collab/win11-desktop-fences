using DesktopMediaController.Core;
using static DesktopMediaController.Core.CardVisibilityAction;
using static DesktopMediaController.Core.MediaAvailability;

namespace DesktopMediaController.Tests;

/// <summary>
/// The card's self-hiding rules. Time is injected, so the graces are walked through second by second
/// instead of being waited out — which is what makes the awkward cases (music returning mid-grace, a
/// broken media layer, the user's own dismissal) cheap enough to actually test.
/// </summary>
public sealed class CardVisibilityPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 9, 0, 0, TimeSpan.FromHours(8));

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Observe_QuietDesktop_LeavesOnlyAfterTheGraceHasElapsed()
    {
        // The grace is what stops a track change — where players report Stopped for a moment — from
        // flashing the card off and on.
        var policy = new CardVisibilityPolicy();

        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(0)));
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(4.9)));
        Assert.Equal(Hide, policy.Observe(hasMusic: false, Available, At(5)));
        Assert.Equal(CardPhase.HiddenAuto, policy.Phase);
    }

    [Fact]
    public void Observe_MusicReturningDuringTheGrace_KeepsTheCardAndRestartsTheClock()
    {
        var policy = new CardVisibilityPolicy();
        policy.Observe(hasMusic: false, Available, At(0));

        Assert.Equal(None, policy.Observe(hasMusic: true, Available, At(3)));

        // The near-miss absence must not carry over: the clock starts again from the new silence.
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(6)));
        Assert.Equal(Hide, policy.Observe(hasMusic: false, Available, At(11)));
    }

    [Fact]
    public void Observe_CardThatLeftForSilence_ComesBackOnItsOwn()
    {
        var policy = new CardVisibilityPolicy();
        policy.Observe(hasMusic: false, Available, At(0));
        Assert.Equal(Hide, policy.Observe(hasMusic: false, Available, At(5)));

        // Nothing happens while it keeps being quiet...
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(300)));

        // ...and then the music starting is enough. No user action anywhere in this test.
        Assert.Equal(Show, policy.Observe(hasMusic: true, Available, At(301)));
        Assert.Equal(CardPhase.Visible, policy.Phase);
    }

    [Fact]
    public void Observe_TheShowIsAskedForOnce_NotOnEveryTickAfterwards()
    {
        // The caller shows the window on Show; repeating it three times a second would be a repaint
        // storm for no reason.
        var policy = new CardVisibilityPolicy(CardPhase.HiddenAuto);

        Assert.Equal(Show, policy.Observe(hasMusic: true, Available, At(0)));
        Assert.Equal(None, policy.Observe(hasMusic: true, Available, At(1)));
        Assert.Equal(None, policy.Observe(hasMusic: true, Available, At(2)));
    }

    [Fact]
    public void Observe_HideIsAskedForOnce_NotOnEveryTickWhileTheMusicStaysAway()
    {
        var policy = new CardVisibilityPolicy();
        policy.Observe(hasMusic: false, Available, At(0));
        Assert.Equal(Hide, policy.Observe(hasMusic: false, Available, At(5)));

        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(6)));
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(7)));
    }

    [Fact]
    public void Observe_CardTheUserDismissed_StaysDismissedEvenWhenMusicStarts()
    {
        // Closing the widget means closing it. Re-opening it when a song starts is the behaviour that
        // makes people stop using the close button.
        var policy = new CardVisibilityPolicy();
        policy.UserHid();

        Assert.Equal(None, policy.Observe(hasMusic: true, Available, At(60)));
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(600)));
        Assert.Equal(CardPhase.HiddenUser, policy.Phase);
        Assert.False(policy.IsVisibleIntent);
    }

    [Fact]
    public void UserShowed_FromDismissed_PutsTheCardBackAndBuysItTheGrace()
    {
        var policy = new CardVisibilityPolicy();
        policy.UserHid();

        policy.UserShowed(At(0));

        Assert.Equal(CardPhase.Visible, policy.Phase);
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(29)));
    }

    [Fact]
    public void UserShowed_OnAQuietDesktop_ThenStillHidesOnceTheGraceRunsOut()
    {
        // The grace protects the user's click from vanishing under their hand, it does not pin a
        // contentless card to the desktop forever.
        var policy = new CardVisibilityPolicy();
        policy.UserShowed(At(0));

        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(30)));

        // The absence grace is separate and starts after the manual one, so hiding is 30 + 5, not 30.
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(34)));
        Assert.Equal(Hide, policy.Observe(hasMusic: false, Available, At(35)));
    }

    [Fact]
    public void Observe_SilentLaunch_StaysParkedUntilMusicPlays()
    {
        var policy = new CardVisibilityPolicy(CardPhase.HiddenAuto);

        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(0)));
        Assert.Equal(None, policy.Observe(hasMusic: false, Available, At(600)));
        Assert.Equal(Show, policy.Observe(hasMusic: true, Available, At(601)));
    }

    [Fact]
    public void Observe_BeforeSmtcHasAnswered_DecidesNothing()
    {
        // The dangerous case: at startup an empty reading is indistinguishable from "nothing is
        // playing", and acting on it would hide the card before the first session ever arrives.
        var policy = new CardVisibilityPolicy();

        Assert.Equal(None, policy.Observe(hasMusic: false, Starting, At(600)));
        Assert.Equal(CardPhase.Visible, policy.Phase);
    }

    [Fact]
    public void Observe_SmtcBroken_LeavesTheVisibleCardAlone()
    {
        // An unreadable sensor is not evidence that the music stopped. Hiding here would leave an
        // invisible widget reachable only through the tray, which is indistinguishable from a crash.
        var policy = new CardVisibilityPolicy();

        Assert.Equal(None, policy.Observe(hasMusic: false, Unavailable, At(600)));
        Assert.Equal(CardPhase.Visible, policy.Phase);
    }

    [Fact]
    public void Observe_SmtcBreakingWhileHidden_ShowsTheCardAgain()
    {
        var policy = new CardVisibilityPolicy(CardPhase.HiddenAuto);

        Assert.Equal(Show, policy.Observe(hasMusic: false, Unavailable, At(0)));
        Assert.Equal(CardPhase.Visible, policy.Phase);
    }

    [Fact]
    public void Observe_SmtcBroken_StillDoesNotOverrideTheUser()
    {
        // The one rule that beats everything else, because it is the only one the user stated in person.
        var policy = new CardVisibilityPolicy();
        policy.UserHid();

        Assert.Equal(None, policy.Observe(hasMusic: false, Unavailable, At(600)));
        Assert.Equal(CardPhase.HiddenUser, policy.Phase);
    }

    [Fact]
    public void UserShowed_WhileAlreadyHiddenForSilence_DoesNotAskForASecondShow()
    {
        var policy = new CardVisibilityPolicy(CardPhase.HiddenAuto);

        policy.UserShowed(At(0));

        Assert.Equal(None, policy.Observe(hasMusic: true, Available, At(1)));
    }
}
