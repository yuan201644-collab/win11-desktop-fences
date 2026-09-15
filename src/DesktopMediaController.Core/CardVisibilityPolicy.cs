namespace DesktopMediaController.Core;

/// <summary>
/// Where the card is, and — just as importantly — why.
/// </summary>
public enum CardPhase
{
    /// <summary>On screen.</summary>
    Visible,

    /// <summary>Off screen because nothing was playing. Comes back on its own when music does.</summary>
    HiddenAuto,

    /// <summary>Off screen because the user asked. Only the user brings it back.</summary>
    HiddenUser,
}

/// <summary>What the caller should do about the card after an observation.</summary>
public enum CardVisibilityAction
{
    None,
    Show,
    Hide,
}

/// <summary>
/// How much can be blamed on the media layer. "I cannot read any players" and "there are no players"
/// look identical in a snapshot but mean opposite things for a card that hides itself when the music
/// stops.
/// </summary>
public enum MediaAvailability
{
    /// <summary>SMTC has not answered yet. Say nothing, decide nothing.</summary>
    Starting,

    /// <summary>SMTC is answering; an empty result really does mean nothing is playing.</summary>
    Available,

    /// <summary>SMTC is not answering at all. Nothing can be concluded about the music.</summary>
    Unavailable,
}

/// <summary>
/// Decides when the card should be on screen, so a machine that is not playing music does not have a
/// media controller sitting on the desktop.
/// </summary>
/// <remarks>
/// <para>
/// Three phases rather than one boolean, because "hidden" is two different states. The card that went
/// away because the music stopped has to come back by itself; the card the user dismissed has to stay
/// dismissed. Collapsing them is what produces the classic complaint — you close the widget, a song
/// starts, and it plants itself back on your screen.
/// </para>
/// <para>
/// The absence grace is not an optimisation. Players report <c>Stopped</c> for a moment between tracks
/// and while restarting, so acting on the first quiet observation would flash the card off and on at
/// every track change.
/// </para>
/// <para>
/// Pure and time-injected: the caller passes <c>now</c> rather than the class reading a clock, which is
/// what lets the whole matrix below be tested without waiting five real seconds for anything.
/// </para>
/// </remarks>
public sealed class CardVisibilityPolicy
{
    /// <summary>How long the music has to stay gone before the card leaves.</summary>
    public static readonly TimeSpan AbsenceGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a card the user just asked for stays put even with nothing playing. Without it,
    /// double-clicking the shortcut on a quiet desktop would show a card for five seconds and take it
    /// away again, which reads as "the thing is broken".
    /// </summary>
    public static readonly TimeSpan ManualShowGrace = TimeSpan.FromSeconds(30);

    private DateTimeOffset? _absentSince;
    private DateTimeOffset? _manualGraceUntil;

    /// <param name="initial">
    /// <see cref="CardPhase.HiddenAuto"/> for the sign-in launch, which comes up parked and should
    /// appear as soon as music does; <see cref="CardPhase.Visible"/> otherwise.
    /// </param>
    public CardVisibilityPolicy(CardPhase initial = CardPhase.Visible) => Phase = initial;

    /// <summary>Where the card is now.</summary>
    public CardPhase Phase { get; private set; }

    /// <summary>Whether the card is supposed to be on screen.</summary>
    public bool IsVisibleIntent => Phase == CardPhase.Visible;

    /// <summary>
    /// Folds one observation of the world into at most one instruction.
    /// </summary>
    /// <param name="hasMusic">
    /// Whether there is a session worth showing — a playing <i>or paused</i> player from the allowed
    /// sources. Paused counts: a paused song is still the thing the card exists to show, and treating
    /// it as absence would hide the card every time the user paused.
    /// </param>
    /// <param name="media">Whether SMTC can be trusted to have answered at all.</param>
    /// <param name="now">The current time; injected so the graces are testable.</param>
    public CardVisibilityAction Observe(bool hasMusic, MediaAvailability media, DateTimeOffset now)
    {
        // A player that cannot be read is not a player that has gone away. Going invisible on a broken
        // sensor would leave the widget unreachable except through the tray, which looks exactly like a
        // crash — so the card stays, or is put back.
        if (media == MediaAvailability.Starting) return CardVisibilityAction.None;
        if (media == MediaAvailability.Unavailable)
        {
            return Phase == CardPhase.HiddenAuto
                ? Enter(CardPhase.Visible, CardVisibilityAction.Show)
                : CardVisibilityAction.None;
        }

        // The user's decision outranks ours, including the part where music comes back.
        if (Phase == CardPhase.HiddenUser) return CardVisibilityAction.None;

        if (hasMusic)
        {
            _absentSince = null;

            // This is the whole point of separating the two hidden phases: a card that left because the
            // music stopped is expected back, and nobody has to click anything.
            return Phase == CardPhase.HiddenAuto
                ? Enter(CardPhase.Visible, CardVisibilityAction.Show)
                : CardVisibilityAction.None;
        }

        // Already parked for the same reason; observations now cost nothing until music shows up.
        if (Phase == CardPhase.HiddenAuto) return CardVisibilityAction.None;

        if (_manualGraceUntil is { } until)
        {
            if (now < until) return CardVisibilityAction.None;

            // The grace bought the user a look at the card; it does not buy them a permanent one.
            _manualGraceUntil = null;
        }

        // First quiet observation starts the clock; only a continuous absence ends it. A track that
        // reports Stopped for a moment and comes back never reaches the grace, so it never flickers.
        _absentSince ??= now;
        if (now - _absentSince.Value < AbsenceGrace) return CardVisibilityAction.None;

        return Enter(CardPhase.HiddenAuto, CardVisibilityAction.Hide);
    }

    /// <summary>
    /// The user asked for the card: the tray, the shortcut, or a second launch. Grants the manual grace
    /// so what they asked for does not vanish under their hand.
    /// </summary>
    public void UserShowed(DateTimeOffset now)
    {
        Phase = CardPhase.Visible;
        _absentSince = null;
        _manualGraceUntil = now + ManualShowGrace;
    }

    /// <summary>
    /// The user asked the card away, from its close button or the tray. Remembered as their intent, so
    /// the automatic rules leave it alone until the user says otherwise.
    /// </summary>
    public void UserHid()
    {
        Phase = CardPhase.HiddenUser;
        _absentSince = null;
        _manualGraceUntil = null;
    }

    private CardVisibilityAction Enter(CardPhase phase, CardVisibilityAction action)
    {
        Phase = phase;
        _absentSince = null;
        _manualGraceUntil = null;
        return action;
    }
}
