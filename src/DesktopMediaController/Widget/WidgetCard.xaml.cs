using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopMediaController.Core;
using DesktopMediaController.Services;
using DesktopMediaController.Services.Lyrics;

namespace DesktopMediaController.Widget;

/// <summary>
/// The card the user actually sees: cover art, title, artist, three lines of scrolling lyrics, a
/// read-only progress bar and transport buttons.
/// </summary>
/// <remarks>
/// Layout and formatting only — it does not know about SMTC, window handles, monitors, dragging or
/// where lyrics come from. It is handed an immutable <see cref="MediaSnapshot"/> plus an already
/// resolved <see cref="LyricsWindow"/> and paints them, which is what keeps the media and lyric layers
/// replaceable and this class testable by eye.
/// </remarks>
public sealed partial class WidgetCard : UserControl
{
    /// <summary>Smallest the cover is allowed to get, so it stays recognisable at the minimum card height.</summary>
    private const double CoverMinDip = 64;

    /// <summary>Largest the cover is allowed to get, so a very tall card does not become all artwork.</summary>
    private const double CoverMaxDip = 160;

    /// <summary>
    /// How much of the card's height the cover takes. A share rather than a fixed size so the artwork
    /// grows with the card: a resizable card whose contents stayed put would just add empty space.
    /// </summary>
    private const double CoverHeightShare = 0.42;

    // Drawn rather than typed: "⏯" and "⏸" are missing from plenty of fonts and would silently render
    // as tofu boxes on a machine that has them missing.
    private static readonly Geometry PlayGeometry = Frozen("M2,1 L11,6 L2,11 Z");
    private static readonly Geometry PauseGeometry = Frozen("M2,1 H5.2 V11 H2 Z M7.4,1 H10.6 V11 H7.4 Z");

    private WidgetSize _size;

    public WidgetCard(WidgetSize size)
    {
        InitializeComponent();
        Apply(MediaSnapshot.Idle);
        ApplyLyrics(null, LyricsState.Idle);
        SetCardSize(size);
    }

    /// <summary>Raised when the user asks the widget to close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user clicks play/pause.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Raised when the user clicks next track.</summary>
    public event EventHandler? NextRequested;

    /// <summary>Raised when the user clicks previous track.</summary>
    public event EventHandler? PreviousRequested;

    /// <summary>Raised when the user clicks the player name to move to the next player.</summary>
    public event EventHandler? SourceSwitchRequested;

    /// <summary>The card's current design size. The window's physical size is derived from it.</summary>
    internal WidgetSize CardSize => _size;

    /// <summary>
    /// Changes the card's design size, which is <b>the only way to resize the window</b>.
    /// </summary>
    /// <remarks>
    /// The window's <c>SizeToContent</c> is <c>WidthAndHeight</c>, so the window follows its content
    /// rather than the other way round: setting the window's size directly is undone on the next layout
    /// pass, and doing it by hand once inflated a 360×112 card to 1906×670 inside a single drag. Going
    /// through the content also means the DPI story stays untouched — WPF recomputes the physical size
    /// from these DIP values whenever the monitor scale changes.
    /// </remarks>
    internal void SetCardSize(WidgetSize size)
    {
        var coerced = WidgetSize.Coerce(size.WidthDip, size.HeightDip);
        _size = coerced;

        Width = coerced.WidthDip;
        Height = coerced.HeightDip;

        var cover = Math.Clamp(coerced.HeightDip * CoverHeightShare, CoverMinDip, CoverMaxDip);
        CoverColumn.Width = new GridLength(cover);
        CoverBorder.Width = cover;
        CoverBorder.Height = cover;
    }

    /// <summary>
    /// Paints a snapshot. Cheap enough to call on every poll tick: the only allocation is the time
    /// string, and WPF no-ops whenever a property is set to the value it already had.
    /// </summary>
    internal void Apply(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var session = snapshot.Session;
        if (session is null)
        {
            ShowIdle();
            return;
        }

        TitleText.Text = session.HasTrack ? session.Title : "未知曲目";
        ArtistText.Text = string.IsNullOrWhiteSpace(session.Artist) ? "未知艺术家" : session.Artist;

        var sourceName = SourceAppName.Describe(session.SourceId);
        SourceText.Text = sourceName;
        var showSource = !string.IsNullOrEmpty(sourceName);
        SourceButton.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        SeparatorText.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;

        TimeText.Text = $"{PlaybackProgress.Format(session.Position)} / "
                      + $"{PlaybackProgress.Format(session.Duration)}";
        SetProgress(PlaybackProgress.Fraction(session.Position, session.Duration));

        var buttons = PlaybackProgress.ButtonsFor(session);
        ToggleIcon.Data = buttons.ShowPause ? PauseGeometry : PlayGeometry;
        ToggleButton.IsEnabled = buttons.CanToggle;
        ToggleButton.ToolTip = buttons.ShowPause ? "暂停" : "播放";
        NextButton.IsEnabled = buttons.CanNext;
        PreviousButton.IsEnabled = buttons.CanPrevious;

        SetCover(snapshot.Cover);
    }

    /// <summary>
    /// Paints the lyric area: three lines when there is something to scroll, otherwise a single status
    /// message. <paramref name="window"/> is <c>null</c> exactly when <paramref name="state"/> is
    /// anything other than "ready".
    /// </summary>
    /// <remarks>
    /// The active line arrives already split into sung / current / remaining, so this method never
    /// compares a position against a timestamp and never needs to know whether the lyrics are
    /// syllable-timed or line-timed. A line-level source simply puts its whole text in
    /// <c>Current</c>, and the karaoke colours collapse into plain whole-line highlighting.
    /// </remarks>
    internal void ApplyLyrics(LyricsWindow? window, LyricsState state)
    {
        if (window is { } lyrics)
        {
            LyricLines.Visibility = Visibility.Visible;
            LyricStatus.Visibility = Visibility.Collapsed;

            LyricPrevious.Text = lyrics.Previous;
            LyricSung.Text = lyrics.Current.Sung;
            LyricNow.Text = lyrics.Current.Current;
            LyricRemaining.Text = lyrics.Current.Remaining;
            LyricNext.Text = lyrics.Next;
            return;
        }

        LyricLines.Visibility = Visibility.Collapsed;
        LyricStatus.Text = state switch
        {
            LyricsState.Searching => "正在搜索歌词…",
            // "Unavailable" reads the same to the user on purpose — they can do nothing different
            // about it — but the two are kept apart internally because only one of them is worth
            // retrying, and the difference is recorded in lyrics.log.
            LyricsState.None or LyricsState.Unavailable => "暂无歌词",
            _ => string.Empty,
        };
        LyricStatus.Visibility = LyricStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowIdle()
    {
        TitleText.Text = "未连接到播放器";
        ArtistText.Text = "打开播放器后自动显示";
        SourceButton.Visibility = Visibility.Collapsed;
        SeparatorText.Visibility = Visibility.Collapsed;
        TimeText.Text = string.Empty;
        SetProgress(0d);

        ToggleIcon.Data = PlayGeometry;
        ToggleButton.IsEnabled = false;
        ToggleButton.ToolTip = "播放";
        NextButton.IsEnabled = false;
        PreviousButton.IsEnabled = false;

        SetCover(null);
    }

    private void SetCover(ImageSource? cover)
    {
        // Hold on to the previous bitmap when the new snapshot has none: a source that briefly fails
        // to hand back its thumbnail should not make the art flicker away.
        if (cover is not null) CoverImage.Source = cover;

        var hasCover = CoverImage.Source is not null;
        CoverImage.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
        CoverPlaceholder.Visibility = hasCover ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetProgress(double fraction)
    {
        // Star widths, so the two columns always add up to the full width: the fill is exactly
        // `fraction` of it and there is no rounding gap at either end.
        ProgressFilledColumn.Width = new GridLength(fraction, GridUnitType.Star);
        ProgressRemainingColumn.Width = new GridLength(1d - fraction, GridUnitType.Star);
    }

    private static Geometry Frozen(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnToggleClick(object sender, RoutedEventArgs e) =>
        ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e) =>
        NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnPreviousClick(object sender, RoutedEventArgs e) =>
        PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnSourceClick(object sender, RoutedEventArgs e) =>
        SourceSwitchRequested?.Invoke(this, EventArgs.Empty);
}
