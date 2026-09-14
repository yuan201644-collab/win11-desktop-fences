using DesktopMediaController.Core;

namespace DesktopMediaController.Services.Lyrics;

/// <summary>What the card's lyric area should be showing.</summary>
internal enum LyricsState
{
    /// <summary>Nothing to show and nothing worth saying — no track at all.</summary>
    Idle,

    /// <summary>A lookup is in flight for the current track.</summary>
    Searching,

    /// <summary>Lyrics are ready to scroll.</summary>
    Ready,

    /// <summary>
    /// Every source answered and none of them had this track. A definite answer, safe to cache for
    /// the session.
    /// </summary>
    None,

    /// <summary>
    /// At least one source never got to answer — it is benched after throttling us, or the request
    /// failed. Deliberately distinct from <see cref="None"/>: the honest statement is "we do not know
    /// yet", not "there are no lyrics", and only this one is worth retrying.
    /// </summary>
    Unavailable,
}

/// <summary>
/// The outcome of a lyric lookup, in the only terms the card needs.
/// </summary>
/// <param name="State">What to show.</param>
/// <param name="Document">The lyrics, empty unless <see cref="State"/> is <see cref="LyricsState.Ready"/>.</param>
/// <param name="SourceId">Which source supplied them, for the log and the tooltip.</param>
internal sealed record LyricsLookup(LyricsState State, LyricsDocument Document, string SourceId)
{
    public static readonly LyricsLookup Idle = new(LyricsState.Idle, LyricsDocument.Empty, string.Empty);

    public static readonly LyricsLookup Searching = new(LyricsState.Searching, LyricsDocument.Empty, string.Empty);
}
