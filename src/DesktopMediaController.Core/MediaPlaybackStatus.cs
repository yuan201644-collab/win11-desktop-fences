namespace DesktopMediaController.Core;

/// <summary>
/// What a media session is currently doing. Mirrors WinRT's
/// <c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c> numerically so the bridge layer can
/// cast straight across, while keeping this assembly free of any WinRT reference — the selection
/// policy below is the part most likely to be wrong, and it stays unit-testable only if it never
/// needs a real media session to run.
/// </summary>
public enum MediaPlaybackStatus
{
    /// <summary>The session went away (player closed, or the source stopped offering it).</summary>
    Closed = 0,

    /// <summary>Alive, but nothing loaded yet.</summary>
    Opened = 1,

    /// <summary>Mid-transition (player is switching tracks).</summary>
    Changing = 2,

    /// <summary>Stopped. Treated as "nothing worth showing".</summary>
    Stopped = 3,

    Playing = 4,

    Paused = 5,
}
