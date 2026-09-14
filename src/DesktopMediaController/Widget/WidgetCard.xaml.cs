using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // ---- lyric metrics ------------------------------------------------------------------------

    /// <summary>
    /// Everything the text column does not get: shell border, grid margins, cover, its gap.
    /// </summary>
    /// <remarks>
    /// <see cref="LyricTypeScale"/> owns how much of that column the lyric type may use; this is the
    /// part of the sum that only the card's own XAML knows about, so it stays here.
    /// </remarks>
    private const double NonTextChromeDip = 36;

    /// <summary>Length of each half of the scroll: out, then back in.</summary>
    private static readonly TimeSpan LyricScrollHalf = TimeSpan.FromMilliseconds(110);

    /// <summary>How far the block fades while it is off its mark mid-scroll.</summary>
    private const double LyricScrollDim = 0.2;

    // Frozen statics, because the paint loop compares brushes by reference to decide whether anything
    // changed. Handing WPF a fresh-but-equal brush would invalidate the render on every syllable for
    // no visible reason.
    private static readonly Brush SungBrush = FrozenBrush(0x8F, 0xC5, 0xFF);
    private static readonly Brush CurrentBrush = FrozenBrush(0xFF, 0xFF, 0xFF);
    private static readonly Brush RemainingBrush = FrozenBrush(0x32, 0xFF, 0xFF, 0xFF);

    private WidgetSize _size;

    /// <summary>
    /// The type scale the block settled on for the current card size, and the width of the text column.
    /// </summary>
    /// <remarks>
    /// Kept because the emphasised line's size is re-derived every time the song moves on to a new line
    /// — a longer line has to be set smaller to be shown whole — and there is no line to measure at the
    /// moment the card is resized.
    /// </remarks>
    private double _blockScale = 1d;
    private double _textWidthDip;

    // ---- lyric painting state -----------------------------------------------------------------

    /// <summary>One inline per character of the active line. Their text is written once, at build time.</summary>
    private readonly List<Run> _lineRuns = new();

    /// <summary>The text <see cref="_lineRuns"/> was built from, so a rebuild is only done when it changes.</summary>
    private string _runText = string.Empty;

    /// <summary>Boundary of the last painted highlight, as a half-open element range. −1 means "nothing yet".</summary>
    private int _paintedSung = -1;
    private int _paintedEnd = -1;

    /// <summary>
    /// Index of the line whose text is on screen, or −1 when none is.
    /// </summary>
    /// <remarks>
    /// What distinguishes a line the song has just moved on to — worth scrolling for — from a seek, a
    /// track change or the first paint, which land somewhere unrelated and must not animate.
    /// </remarks>
    private int _shownIndex = -1;

    /// <summary>The window most recently handed to the card, so the scroll's swap uses the newest one.</summary>
    private LyricsWindow? _pending;

    private bool _scrolling;

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

        ApplyLyricMetrics(coerced.HeightDip, coerced.WidthDip - NonTextChromeDip - cover);
    }

    /// <summary>
    /// Recomputes the lyric block's type sizes and row heights from the space the card actually has.
    /// </summary>
    /// <remarks>
    /// The block is the one part of the card that has to grow with the card — the star-sized row is
    /// where a resize puts its extra height, and a bigger box with the same size words in it would just
    /// be more empty space. It is bounded from two sides, though: by the height it has to fill, and by
    /// the width a single line has to stay readable in. Both, and the arithmetic behind them, live in
    /// <see cref="LyricTypeScale"/>. Doing it there also means the minimum card cannot end up with a
    /// lyric block taller than its slot, which would have been clipped away.
    /// </remarks>
    private void ApplyLyricMetrics(double cardHeightDip, double textWidthDip)
    {
        _textWidthDip = Math.Max(0d, textWidthDip);

        var fit = LyricTypeScale.Measure(cardHeightDip, _textWidthDip, 0);
        _blockScale = fit.BlockScale;

        LyricCurrent.Height = fit.CurrentRowHeight;

        LyricPrevious.FontSize = fit.ContextFontSize;
        LyricPrevious.Height = fit.ContextRowHeight;

        LyricNext.FontSize = fit.ContextFontSize;
        LyricNext.Height = fit.ContextRowHeight;

        LyricStatus.FontSize = fit.ContextFontSize;

        ApplyCurrentLineFont(0);
    }

    /// <summary>
    /// Sets the emphasised line's type size for a line of <paramref name="elementCount"/> characters.
    /// </summary>
    /// <remarks>
    /// Called once per line rather than once per syllable: the count does not change while a line is
    /// being sung, and re-deriving it on every tick would be a needless assignment inside the one loop
    /// that has to stay cheap. The line's row height is deliberately <i>not</i> touched here — it stays
    /// on the block scale so the scroll always travels the same distance.
    /// </remarks>
    private void ApplyCurrentLineFont(int elementCount)
    {
        var scale = _textWidthDip > 0d
            ? LyricTypeScale.CurrentScaleFor(_blockScale, _textWidthDip, elementCount)
            : _blockScale;

        LyricCurrent.FontSize = LyricTypeScale.CurrentFont * scale;
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
    /// <para>
    /// The active line arrives as text plus two counts, so this method never compares a position
    /// against a timestamp and never needs to know whether the lyrics are syllable-timed or line-timed:
    /// a line-level source simply reports the whole line as current, and the karaoke colours collapse
    /// into whole-line highlighting without a branch.
    /// </para>
    /// <para>
    /// There are three cases, and only the first one animates. A song that has moved on to the next
    /// line scrolls; a highlight that moved inside the line just recolours; and anything else — a seek,
    /// a track change, the first paint — replaces the text outright, because sliding it into place
    /// would misrepresent what happened.
    /// </para>
    /// </remarks>
    internal void ApplyLyrics(LyricsWindow? window, LyricsState state)
    {
        if (window is not { } lyrics)
        {
            CancelScroll();
            _shownIndex = -1;
            _pending = null;
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
            return;
        }

        LyricLines.Visibility = Visibility.Visible;
        LyricStatus.Visibility = Visibility.Collapsed;

        if (lyrics.CurrentIndex == _shownIndex)
        {
            // The same line: only the highlight moved. Recolouring inlines whose text never changes
            // invalidates the render and nothing else, so this is safe to do while a scroll is running
            // — and the newest window is kept so the scroll's swap lands on a current highlight.
            _pending = lyrics;
            PaintHighlight(lyrics.Current);
            return;
        }

        if (!_scrolling && _shownIndex >= 0 && lyrics.CurrentIndex == _shownIndex + 1)
        {
            ScrollToNextLine(lyrics);
            return;
        }

        // A seek, a track change, the first paint, or a second advance inside one scroll. Replacing the
        // text is the honest answer to all four.
        CancelScroll();
        Paint(lyrics);
    }

    /// <summary>Puts a window on screen at once, with no animation.</summary>
    private void Paint(LyricsWindow lyrics)
    {
        LyricPrevious.Text = lyrics.Previous;
        LyricNext.Text = lyrics.Next;

        _pending = lyrics;
        _shownIndex = lyrics.CurrentIndex;

        // Before the inlines are built, so the new line is shaped once at its final size rather than
        // twice. The line's own length is what decides that size: see ApplyCurrentLineFont.
        ApplyCurrentLineFont(lyrics.Current.ElementCount);
        BuildRuns(lyrics.Current.Text);
        PaintHighlight(lyrics.Current);
    }

    /// <summary>
    /// Gives the active line one inline per character, and never touches their text again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole fix for the line that used to twitch sideways on every beat. The old code
    /// rebuilt three runs' text at each syllable boundary, and assigning to a run's text invalidates
    /// the <i>measure</i> — so WPF re-shaped the whole line, and the glyphs after the boundary moved,
    /// once per character. Changing only a run's colour invalidates the <i>render</i>: the run's text
    /// is unchanged, nothing is re-measured, and the positions are therefore frozen by construction
    /// rather than by luck.
    /// </para>
    /// <para>
    /// One inline per <i>text element</i>, not per UTF-16 code unit: see <see cref="TextElements"/> for
    /// what splitting a surrogate pair would do to an emoji.
    /// </para>
    /// </remarks>
    private void BuildRuns(string text)
    {
        var inlines = LyricCurrent.Inlines;
        inlines.Clear();
        _lineRuns.Clear();

        foreach (var element in TextElements.Split(text))
        {
            var run = new Run(element) { Foreground = RemainingBrush };
            _lineRuns.Add(run);
            inlines.Add(run);
        }

        _runText = text;
        _paintedSung = -1;
        _paintedEnd = -1;
    }

    /// <summary>
    /// Colours the inlines the active line already has: everything before <c>Sung</c> one way, the
    /// syllable being sung another, the rest a third.
    /// </summary>
    private void PaintHighlight(LyricLinePaint paint)
    {
        // Only reachable if a caller hands over a window whose line is not the one the inlines were
        // built from. Rebuilding is the safe answer; colouring the wrong glyphs is not.
        if (!string.Equals(paint.Text, _runText, StringComparison.Ordinal)) BuildRuns(paint.Text);

        var sung = Math.Clamp(paint.SungElements, 0, _lineRuns.Count);
        var end = Math.Clamp(paint.SungElements + paint.CurrentElements, sung, _lineRuns.Count);
        if (sung == _paintedSung && end == _paintedEnd) return;

        _paintedSung = sung;
        _paintedEnd = end;

        for (var i = 0; i < _lineRuns.Count; i++)
        {
            var brush = i < sung ? SungBrush : i < end ? CurrentBrush : RemainingBrush;
            if (!ReferenceEquals(_lineRuns[i].Foreground, brush)) _lineRuns[i].Foreground = brush;
        }
    }

    /// <summary>
    /// Scrolls the block up and away, swaps the text at the furthest point, and brings it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Out and back rather than a continuous scroll of the whole document, because the rows are not the
    /// same height — the emphasised line's row is nearly twice a context row's — so "move everything up
    /// by one line" is not a single distance and cannot be a single translation of one container. The
    /// two halves are 110 ms each, which is short enough that the swap is not readable as a swap.
    /// </para>
    /// <para>
    /// Both halves only ever touch <c>RenderTransform</c> and <c>Opacity</c>, so not a single frame of
    /// the animation re-measures anything.
    /// </para>
    /// </remarks>
    private void ScrollToNextLine(LyricsWindow lyrics)
    {
        _scrolling = true;
        _pending = lyrics;

        var lift = LyricCurrent.Height;
        if (double.IsNaN(lift) || lift <= 0) lift = LyricTypeScale.CurrentRow;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Completed is what does the swap. Everything between clearing the first animation and starting
        // the second happens inside this one callback, so no frame is ever drawn showing the new text
        // at the old offset.
        var fadeOut = new DoubleAnimation(1, LyricScrollDim, LyricScrollHalf)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

        fadeOut.Completed += (_, _) =>
        {
            LyricShift.BeginAnimation(TranslateTransform.YProperty, null);
            LyricLines.BeginAnimation(OpacityProperty, null);

            Paint(_pending ?? lyrics);

            // The base values are the resting state, and both animations below stop rather than hold, so
            // the end of the scroll leaves the block exactly where the properties already say it is.
            LyricShift.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(lift, 0, LyricScrollHalf) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            LyricLines.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(LyricScrollDim, 1, LyricScrollHalf) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

            _scrolling = false;
        };

        LyricShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, -lift, LyricScrollHalf) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        LyricLines.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Stops a scroll in flight and puts the block back on its mark.</summary>
    private void CancelScroll()
    {
        if (!_scrolling && LyricShift.Y == 0d && LyricLines.Opacity == 1d) return;

        _scrolling = false;
        LyricShift.BeginAnimation(TranslateTransform.YProperty, null);
        LyricLines.BeginAnimation(OpacityProperty, null);
        LyricShift.Y = 0d;
        LyricLines.Opacity = 1d;
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

    private static Brush FrozenBrush(byte r, byte g, byte b) => FrozenBrush(0xFF, r, g, b);

    private static Brush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
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
