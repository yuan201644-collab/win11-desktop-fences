using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopMediaController.Core;
using DesktopMediaController.Services;

namespace DesktopMediaController.Widget;

/// <summary>
/// The card the user actually sees: cover art, title, artist, a read-only progress bar and transport
/// buttons.
/// </summary>
/// <remarks>
/// Layout and formatting only — it does not know about SMTC, window handles, monitors or dragging. It
/// is handed an immutable <see cref="MediaSnapshot"/> and paints it, which is what keeps the media
/// layer replaceable and this class testable by eye.
/// </remarks>
public sealed partial class WidgetCard : UserControl
{
    /// <summary>
    /// Design width in DIPs. The window's <b>physical</b> width is this times the current monitor
    /// scale, so the card keeps its apparent size across the 100 % / 125 % monitor mix.
    /// </summary>
    public const double DesignWidthDip = 360;

    /// <summary>Design height in DIPs. See <see cref="DesignWidthDip"/>.</summary>
    public const double DesignHeightDip = 112;

    // Drawn rather than typed: "⏯" and "⏸" are missing from plenty of fonts and would silently render
    // as tofu boxes on a machine that has them missing.
    private static readonly Geometry PlayGeometry = Frozen("M2,1 L11,6 L2,11 Z");
    private static readonly Geometry PauseGeometry = Frozen("M2,1 H5.2 V11 H2 Z M7.4,1 H10.6 V11 H7.4 Z");

    public WidgetCard()
    {
        InitializeComponent();
        Width = DesignWidthDip;
        Height = DesignHeightDip;
        Apply(MediaSnapshot.Idle);
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
