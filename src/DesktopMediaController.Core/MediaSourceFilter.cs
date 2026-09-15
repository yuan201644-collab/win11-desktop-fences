namespace DesktopMediaController.Core;

/// <summary>
/// Decides which tiers the machine's media sessions belong to, so the card can be limited to music
/// players without losing the ability to reach anything else by hand.
/// </summary>
/// <remarks>
/// <para>
/// This is an allow-list, not a deny-list: a source is only <see cref="SourceTier.Preferred"/> or
/// <see cref="SourceTier.Fallback"/> because the user or the defaults said so, and everything else —
/// including a music player released next month — lands in <see cref="SourceTier.Excluded"/>. The cost
/// is that a brand-new app shows nothing until its id is added to <c>sources.json</c>; the mitigation
/// is that cycling the source label by hand still reaches it.
/// </para>
/// <para>
/// Matching is an exact, case-insensitive comparison against the whole id rather than a substring
/// search. Names contain words like "player" and "music" that mean the opposite of what they say
/// ("PotPlayer" plays video, "媒体播放器" plays both), so guessing from the text would misfile exactly
/// the apps this exists to keep off the card.
/// </para>
/// </remarks>
public sealed class MediaSourceFilter
{
    /// <summary>
    /// The music players, in the shipped order. Anything listed here takes the card ahead of a browser.
    /// </summary>
    /// <remarks>
    /// Both Spotify ids are here because the desktop build and the Store build publish different ones.
    /// 媒体播放器 (<c>music.ui.exe</c>) is kept deliberately: it plays video as well, but when it is
    /// playing a song it publishes a complete music session, and dropping it would look like a bug.
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultPreferred =
    [
        "qqmusic.exe",
        "cloudmusic.exe",
        "kugou.exe",
        "kuwo.exe",
        "spotify.exe",
        "spotify",
        "appleinc.applemusic_nzyj5cx40ttqa!app",
        "aimp.exe",
        "foobar2000.exe",
        "music.ui.exe",
    ];

    /// <summary>
    /// Sources allowed to fill the card but never to take it from a player: the browsers.
    /// </summary>
    /// <remarks>
    /// Leaving these in means listening through a web player still works. Leaving them out means
    /// B站 and YouTube are invisible to the controller, which is a defensible choice the user can make
    /// by emptying this list in <c>sources.json</c>.
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultFallback =
    [
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
    ];

    /// <summary>The filter a fresh install gets: the shipped players, with browsers demoted.</summary>
    public static MediaSourceFilter Default { get; } = new(DefaultPreferred, DefaultFallback);

    private readonly HashSet<string> _preferred;
    private readonly HashSet<string> _fallback;

    public MediaSourceFilter(IEnumerable<string>? preferred, IEnumerable<string>? fallback)
    {
        _preferred = Normalize(preferred);
        _fallback = Normalize(fallback);

        // A source named in both lists means the user was editing and changed their mind about which
        // line it belongs on. Preferring it is the interpretation that keeps the card working.
        _fallback.ExceptWith(_preferred);
    }

    /// <summary>The ids that win the card. Exposed so the store can write them back out.</summary>
    public IReadOnlyCollection<string> Preferred => _preferred;

    /// <summary>The ids that are only used when nothing in <see cref="Preferred"/> is playing.</summary>
    public IReadOnlyCollection<string> Fallback => _fallback;

    /// <summary>
    /// The tier <paramref name="sourceId"/> belongs to. Blank ids and ids in neither list are
    /// <see cref="SourceTier.Excluded"/> — the "nobody vouched for this" case.
    /// </summary>
    public SourceTier TierOf(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return SourceTier.Excluded;

        var id = sourceId.Trim();
        if (_preferred.Contains(id)) return SourceTier.Preferred;
        if (_fallback.Contains(id)) return SourceTier.Fallback;
        return SourceTier.Excluded;
    }

    /// <summary>Whether the automatic choice is allowed to land on this source.</summary>
    public bool IsAutoEligible(string? sourceId) => TierOf(sourceId) != SourceTier.Excluded;

    private static HashSet<string> Normalize(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids is null) return set;

        foreach (var id in ids)
        {
            // A hand-edited file tends to grow stray spaces and blank lines; neither should turn into
            // a source id that can never match.
            if (!string.IsNullOrWhiteSpace(id)) set.Add(id.Trim());
        }

        return set;
    }
}
