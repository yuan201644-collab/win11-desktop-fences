using System.Linq;

namespace DesktopMediaController.Core;

/// <summary>
/// Decides which of the machine's media sessions the widget should show.
/// </summary>
/// <remarks>
/// This exists because <c>GetCurrentSession()</c> cannot be trusted. On the machine this was built
/// for, QQ音乐 and 抖音 were both playing and Windows nominated 抖音 as "current" — so the widget
/// would have shown the wrong player's cover art with no way for the user to understand why.
/// <para>
/// The policy is <b>sticky</b>, and that is the important part. Ranking alone (playing beats paused,
/// then most recently updated) would flip between two simultaneously-playing sessions every time one
/// of them ticked its position forward — visibly flapping cover art and transport buttons. So a
/// chosen session is held for as long as it stays usable, and re-ranking only happens when it dies.
/// </para>
/// <para>
/// An optional <see cref="MediaSourceFilter"/> narrows what the <i>automatic</i> choice may land on,
/// and it is applied in exactly one place — <see cref="RankedForAuto"/>. It deliberately does not
/// apply to the held session or to <see cref="ChooseNext"/>: a player the user reached by hand is held
/// even when the filter would never have offered it, otherwise cycling to an excluded app would be
/// undone by the very next poll and the escape hatch would be decorative.
/// </para>
/// <para>
/// Both entry points return indices into the caller's original list and never reorder or compact it.
/// That is a contract, not a detail: the caller uses the index to reach back into its own parallel
/// array of live SMTC sessions, so filtering has to drop candidates without moving the survivors.
/// </para>
/// </remarks>
public sealed class SessionPicker
{
    private readonly MediaSourceFilter? _filter;
    private string? _sticky;

    /// <summary>
    /// Creates a picker. <paramref name="filter"/> <c>null</c> means every usable source is equally
    /// welcome — the behaviour from before tiers existed, and what callers that do not care pass.
    /// </summary>
    public SessionPicker(MediaSourceFilter? filter = null) => _filter = filter;

    /// <summary>The session currently being held, if any. Exposed for diagnostics.</summary>
    public string? StickySourceId => _sticky;

    /// <summary>
    /// Picks the session to display: the held one while it stays usable, otherwise the best available.
    /// Returns an index into <paramref name="sessions"/>, or <c>null</c> when nothing is worth showing.
    /// </summary>
    public int? Choose(IReadOnlyList<MediaSessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        if (_sticky is not null)
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                // Not tier-checked on purpose — see the class remarks. This one line is what makes
                // "manually reachable but never automatic" work instead of snapping back.
                if (sessions[i].SourceId == _sticky && IsUsable(sessions[i])) return i;
            }

            // The held session stopped or went away; fall through and choose a new one.
            _sticky = null;
        }

        var ranked = RankedForAuto(sessions);
        if (ranked.Count == 0) return null;

        var winner = ranked[0];
        _sticky = sessions[winner].SourceId;
        return winner;
    }

    /// <summary>
    /// Moves to the next usable session in rank order (wrapping around). This is the manual escape
    /// hatch for the case the automatic policy cannot solve: two players the user cares about, both
    /// playing, where only the user knows which one they want to see.
    /// </summary>
    /// <remarks>
    /// Ranks <b>every</b> usable session, tiers ignored, so this is also how a source the filter
    /// excludes (<see cref="SourceTier.Excluded"/>) is reached by hand.
    /// </remarks>
    public int? ChooseNext(IReadOnlyList<MediaSessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var ranked = Ranked(sessions);
        if (ranked.Count == 0)
        {
            _sticky = null;
            return null;
        }

        var next = 0;
        if (_sticky is not null)
        {
            var currentAt = ranked.FindIndex(i => sessions[i].SourceId == _sticky);
            // Not found (-1) or the last entry: both wrap to the front.
            next = currentAt + 1 >= ranked.Count ? 0 : currentAt + 1;
        }

        var winner = ranked[next];
        _sticky = sessions[winner].SourceId;
        return winner;
    }

    /// <summary>Drops the held session, so the next <see cref="Choose"/> re-ranks from scratch.</summary>
    public void Forget() => _sticky = null;

    /// <summary>
    /// Whether a session is worth showing at all. <see cref="MediaPlaybackStatus.Stopped"/> and
    /// <see cref="MediaPlaybackStatus.Closed"/> are excluded: a player that has stopped is usually
    /// one the user forgot about, and holding onto it would keep the widget on a dead track while
    /// something else is actually playing.
    /// </summary>
    private static bool IsUsable(MediaSessionInfo session) =>
        !string.IsNullOrEmpty(session.SourceId) &&
        session.Status is MediaPlaybackStatus.Playing
            or MediaPlaybackStatus.Paused
            or MediaPlaybackStatus.Changing
            or MediaPlaybackStatus.Opened;

    /// <summary>
    /// Usable sessions, best first, as indices into the original list. Ordering is
    /// status rank, then most recently updated, then original index — the last term is only there to
    /// make the result fully deterministic when everything else ties.
    /// </summary>
    private static List<int> Ranked(IReadOnlyList<MediaSessionInfo> sessions) =>
        Enumerable.Range(0, sessions.Count)
            .Where(i => IsUsable(sessions[i]))
            .OrderByDescending(i => Rank(sessions[i].Status))
            .ThenByDescending(i => sessions[i].LastUpdated)
            .ThenBy(i => i)
            .ToList();

    /// <summary>
    /// Usable sessions the automatic choice may land on, best first, as indices into the original list.
    /// </summary>
    /// <remarks>
    /// Tier comes <b>before</b> playback status, which is the whole point of the filter: with a browser
    /// and a player both playing, status alone would tie and hand the card to whichever ticked its
    /// clock last. The consequence to accept is that a paused player still outranks a playing browser —
    /// right for a music controller, and rarely visible anyway because stickiness keeps the player on
    /// screen through a pause rather than letting the browser take over mid-song.
    /// </remarks>
    private List<int> RankedForAuto(IReadOnlyList<MediaSessionInfo> sessions) =>
        Enumerable.Range(0, sessions.Count)
            .Where(i => IsUsable(sessions[i]) && IsAutoEligible(sessions[i]))
            .OrderByDescending(i => TierRank(TierOf(sessions[i])))
            .ThenByDescending(i => Rank(sessions[i].Status))
            .ThenByDescending(i => sessions[i].LastUpdated)
            .ThenBy(i => i)
            .ToList();

    /// <summary>The source's tier under the filter; everything is <see cref="SourceTier.Preferred"/> when there is no filter.</summary>
    private SourceTier TierOf(MediaSessionInfo session) =>
        _filter?.TierOf(session.SourceId) ?? SourceTier.Preferred;

    private bool IsAutoEligible(MediaSessionInfo session) =>
        _filter?.IsAutoEligible(session.SourceId) ?? true;

    private static int TierRank(SourceTier tier) => tier switch
    {
        SourceTier.Preferred => 1,
        SourceTier.Fallback => 0,
        _ => -1,
    };

    private static int Rank(MediaPlaybackStatus status) => status switch
    {
        MediaPlaybackStatus.Playing => 4,
        MediaPlaybackStatus.Paused => 3,
        MediaPlaybackStatus.Changing => 2,
        MediaPlaybackStatus.Opened => 1,
        _ => 0,
    };
}
