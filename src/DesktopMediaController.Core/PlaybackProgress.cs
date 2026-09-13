namespace DesktopMediaController.Core;

/// <summary>
/// Which transport buttons to show, and whether each one can actually do anything.
/// </summary>
/// <param name="ShowPause">
/// True when the play/pause button should render as "pause" (i.e. something is playing).
/// </param>
/// <param name="CanToggle">Whether the play/pause button should accept a click.</param>
/// <param name="CanNext">Whether the next-track button should accept a click.</param>
/// <param name="CanPrevious">Whether the previous-track button should accept a click.</param>
public readonly record struct TransportButtons(
    bool ShowPause,
    bool CanToggle,
    bool CanNext,
    bool CanPrevious);

/// <summary>
/// Presentation maths for the progress area. Kept here rather than in the card so the awkward cases
/// (zero-length track, position past the end, a player that reports a bogus duration) are pinned down
/// by tests instead of being discovered as a rendering glitch.
/// </summary>
public static class PlaybackProgress
{
    /// <summary>
    /// Elapsed fraction of the track in <c>[0, 1]</c>. Returns 0 when the duration is unknown or
    /// nonsensical, which is the safe answer: an empty bar is honest, whereas a full or NaN-width bar
    /// is not.
    /// </summary>
    public static double Fraction(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0d;
        if (position <= TimeSpan.Zero) return 0d;
        if (position >= duration) return 1d;
        return position / duration;
    }

    /// <summary>
    /// <c>m:ss</c>, or <c>h:mm:ss</c> once the track is an hour or longer. Negative input reads as
    /// <c>0:00</c> — players do briefly report negative positions mid-seek and a "-0:01" label in the
    /// corner looks like a bug.
    /// </summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;

        var totalMinutes = (int)value.TotalMinutes;
        return value.TotalHours >= 1
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{(int)value.TotalHours}:{totalMinutes % 60:D2}:{value.Seconds:D2}")
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{totalMinutes}:{value.Seconds:D2}");
    }

    /// <summary>
    /// What the transport row should look like for a session. A player that cannot pause must not get
    /// a clickable pause button: clicking one and having nothing happen reads as "the widget is
    /// broken" far more than a greyed-out button does.
    /// </summary>
    public static TransportButtons ButtonsFor(MediaSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var isPlaying = session.Status == MediaPlaybackStatus.Playing;
        return new TransportButtons(
            ShowPause: isPlaying,
            CanToggle: isPlaying ? session.CanPause : session.CanPlay,
            CanNext: session.CanNext,
            CanPrevious: session.CanPrevious);
    }
}
