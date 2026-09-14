namespace DesktopMediaController.Core;

/// <summary>
/// One media session as the UI needs to see it: enough to draw the card and to decide whether a
/// transport button should be enabled, and nothing else.
/// </summary>
/// <remarks>
/// A snapshot, deliberately. The live WinRT objects are apartment-bound and raise events on threads
/// of their own choosing; copying them into a plain record at the boundary is what lets the UI read
/// state on a timer with no locking and no cross-thread surprises.
/// </remarks>
public sealed record MediaSessionInfo
{
    /// <summary>
    /// The player's identifier (<c>SourceAppUserModelId</c>), e.g. <c>QQMusic.exe</c>. This is the
    /// key the picker remembers, so it must survive between snapshots.
    /// </summary>
    public required string SourceId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    public MediaPlaybackStatus Status { get; init; }

    public bool CanPlay { get; init; }

    public bool CanPause { get; init; }

    public bool CanNext { get; init; }

    public bool CanPrevious { get; init; }

    /// <summary>
    /// Whether the *player* supports seeking. Note QQ音乐 reports <c>false</c>, so this must gate the
    /// UI rather than being assumed true.
    /// </summary>
    public bool CanSeek { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether the player published a timeline at all.
    /// </summary>
    /// <remarks>
    /// Kept separate from "duration is non-zero" because the two mean different things: a player can
    /// report a position with no length, and a player that answered nothing at all must not be read as
    /// "the track is at 0:00", which would drag the lyrics back to the first line.
    /// </remarks>
    public bool HasTimeline { get; init; }

    /// <summary>
    /// When the player last touched its timeline. The only recency signal SMTC offers, and what
    /// breaks ties between two sessions that are both playing.
    /// </summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>True when there is actually something to display.</summary>
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}
