using System.Windows.Media.Imaging;
using DesktopMediaController.Core;

namespace DesktopMediaController.Services;

/// <summary>
/// Everything the card needs for one repaint, in a form that is safe to read from the UI thread.
/// </summary>
/// <param name="Session">The session being displayed, or <c>null</c> when nothing is worth showing.</param>
/// <param name="Cover">
/// Frozen album art, so it can be handed to the UI thread from wherever it was decoded. Never
/// mutated after construction.
/// </param>
internal sealed record MediaSnapshot(MediaSessionInfo? Session, BitmapImage? Cover)
{
    /// <summary>The "nothing is playing" state the widget rests in.</summary>
    public static readonly MediaSnapshot Idle = new(null, null);

    public bool HasSession => Session is not null;
}
