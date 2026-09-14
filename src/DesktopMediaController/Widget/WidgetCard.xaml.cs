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
/// The card the user actually sees: cover art, title, artist, four rows of scrolling lyrics, a
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

    /// <summary>
    /// How long one line change takes to scroll through, for all four rows at once.
    /// </summary>
    /// <remarks>
    /// One duration and one easing curve for the whole motion. The version this replaced animated out
    /// and back as two curves in sequence, and the join — the first decelerating to zero, the second
    /// starting from zero inside the first one's completion callback — is what read as stiffness. A
    /// single curve has no join.
    /// </remarks>
    private static readonly TimeSpan LyricScrollDuration = TimeSpan.FromMilliseconds(260);

    // Frozen statics, because the paint loop compares brushes by reference to decide whether anything
    // changed. Handing WPF a fresh-but-equal brush would invalidate the render on every syllable for
    // no visible reason.
    // Instance fields rather than frozen statics so a theme change can swap in a freshly built frozen
    // brush; the paint loop still compares brushes by reference and early-outs on unchanged runs. Only
    // the two accent-aware brushes move with the theme — the white tiers stay static.
    private Brush SungBrush = FrozenBrush(0x8F, 0xC5, 0xFF);
    private static readonly Brush CurrentBrush = FrozenBrush(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Everything that is not being sung right now: the lines above and below, and the part of the
    /// active line the playhead has not reached yet.
    /// </summary>
    /// <remarks>
    /// One brush for both uses, deliberately. While they differed — the unsung part of the active line
    /// at 20% and the upcoming lines at 35% — a line arriving in the middle slot <i>dimmed</i> as it
    /// arrived, and that step had to be hidden behind a whole-block crossfade. Matching the two means a
    /// line's colour is continuous through the entire motion: the only thing that happens when a line
    /// becomes current is that it grows, which is exactly what the motion is about.
    /// </remarks>
    private static readonly Brush IdleBrush = FrozenBrush(0x59, 0xFF, 0xFF, 0xFF);

    /// <summary>The pin button's accent — the source-label blue, the card's established "interactive" tint.</summary>
    private Brush PinnedBrush = FrozenBrush(0xB3, 0xD6, 0xFF);

    /// <summary>The pin button at rest: the same near-white as its neighbours.</summary>
    private static readonly Brush UnpinnedBrush = FrozenBrush(0x8C, 0xFF, 0xFF, 0xFF);

    private WidgetSize _size;

    /// <summary>The full theme currently shown, so the color menu can tweak one channel (e.g. background)
    /// without losing the others. Kept in step by <see cref="ApplyTheme"/>.</summary>
    private CardTheme _theme = CardTheme.Default;

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

    /// <summary>The block's slot geometry at the current card size, in DIP.</summary>
    private double _contextRowDip = LyricTypeScale.ContextRow;
    private double _currentRowDip = LyricTypeScale.CurrentRow;
    private double _contextFontDip = LyricTypeScale.ContextFont;

    // ---- lyric painting state -----------------------------------------------------------------

    /// <summary>
    /// The four rows, in slot order: 0 is the line above the active one, 1 is the active one, 2 is the
    /// line below, and 3 is parked below the clip holding the line that will arrive next. See
    /// <see cref="LyricScroll"/> for why there are four.
    /// </summary>
    private readonly TextBlock[] _rows;

    /// <summary>
    /// One inline per character, per row — though only the active line's row is populated that way, the
    /// others being a single inline, since nothing ever colours them character by character.
    /// </summary>
    private readonly List<Run>[] _rowRuns;

    /// <summary>The text each row currently shows, so a change of role can be told from a repaint.</summary>
    private readonly string[] _rowTexts = new string[LyricScroll.RowCount];

    /// <summary>Which slot holds the line being sung: 1 at rest, 2 for the length of a scroll.</summary>
    private int _currentRow = 1;

    /// <summary>
    /// How many characters the line in slot 1 has.
    /// </summary>
    /// <remarks>
    /// Its length is what decides its type size — a long line has to be set smaller so that it is shown
    /// whole — so the size is derived from this rather than stored, which keeps it right through a
    /// resize instead of until the next line change.
    /// </remarks>
    private int _row1Elements;

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

    /// <summary>
    /// The highlight most recently handed to the card.
    /// </summary>
    /// <remarks>
    /// Kept because the row that owns the inlines is rebuilt at the end of every scroll, and a rebuild
    /// leaves their colours at the idle one — so the live highlight has to be re-applied on top.
    /// </remarks>
    private LyricLinePaint _paint = LyricLinePaint.Empty;

    /// <summary>The motion in flight, so that a resize or a seek can land it before doing anything else.</summary>
    private LyricScroll.Plan _scrollPlan;

    private bool _scrolling;

    /// <summary>Whether the user has pinned the card, mirrored from the window so a theme change can repaint the pin.</summary>
    private bool _pinned;

    /// <summary>The last lyric window painted, so a theme change can repaint the rows with the new accent.</summary>
    private LyricsWindow? _lastLyrics;

    public WidgetCard(WidgetSize size)
    {
        InitializeComponent();

        // Slot order, not visual order: see the field's comment. The rows are addressed as an array
        // because every animation applies to all four, and four copies of each call would be four
        // places to forget the fourth row in.
        _rows = new[] { LyricRow0, LyricRow1, LyricRow2, LyricRow3 };
        _rowRuns = new List<Run>[LyricScroll.RowCount];
        for (var row = 0; row < _rowRuns.Length; row++) _rowRuns[row] = new List<Run>();

        Apply(MediaSnapshot.Idle);
        ApplyLyrics(null, LyricsState.Idle);
        SetCardSize(size);
        BuildThemeMenu();
    }

    // ---- theme ---------------------------------------------------------------------------------

    /// <summary>Raised after the user picks a color, so the host can persist it.</summary>
    internal event EventHandler<CardTheme>? ThemeChanged;

    /// <summary>
    /// Applies an accent theme: rebuilds the two accent-aware lyric brushes as fresh frozen brushes and
    /// repaints the XAML accents (border, progress fill, source chip, pin). The neutral white tiers and
    /// the dark background are fixed in XAML and untouched.
    /// </summary>
    /// <remarks>
    /// Brushes are replaced wholesale rather than mutated, and never via <c>DynamicResource</c>: the paint
    /// loop compares brushes by reference to skip unchanged runs, and a <c>DynamicResource</c> would defeat
    /// that early-out — re-introducing the per-syllable twitch this card exists to avoid.
    /// </remarks>
    internal void ApplyTheme(CardTheme theme)
    {
        _theme = theme;
        SungBrush = ToFrozenBrush(theme.Sung);
        PinnedBrush = ToFrozenBrush(theme.Label);

        Shell.BorderBrush = ToFrozenBrush(theme.Border);
        Shell.Background = ToFrozenBrush(theme.Background);
        ProgressFill.Background = ToFrozenBrush(theme.Progress);
        SourceButton.Foreground = ToFrozenBrush(theme.Label);

        RefreshLyricColors();
        SetPinnedVisual(_pinned);
    }

    /// <summary>Repaints the lyric rows with the new accent. A scroll in flight is landed first.</summary>
    private void RefreshLyricColors()
    {
        if (_lastLyrics is { } lyrics)
        {
            FinishScroll();
            Paint(lyrics);
        }
    }

    private static Brush ToFrozenBrush(ArgbColor c) => FrozenBrush(c.A, c.R, c.G, c.B);

    // ---- theme menu ----------------------------------------------------------------------------

    private readonly ContextMenu _themeMenu = new();

    /// <summary>Kept so the menu can re-sync its controls to the live theme each time it opens.</summary>
    private TextBox? _bgHexBox;
    private Slider? _transparencySlider;

    /// <summary>
    /// Builds the right-click color picker. Two independent sections — accent (the single primary that
    /// derives the lyric / progress / border tints) and background (its own colour plus a transparency
    /// slider). All interactive controls opt out of the default focus rectangle, which otherwise draws a
    /// blue box around whatever was just clicked and read as a glitch.
    /// </summary>
    private void BuildThemeMenu()
    {
        _themeMenu.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x24));
        _themeMenu.Foreground = new SolidColorBrush(Colors.White);
        // Neutral chrome, not the accent — a blue menu frame read as a glitch.
        _themeMenu.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x42));
        _themeMenu.BorderThickness = new Thickness(1);
        _themeMenu.Padding = new Thickness(8);
        _themeMenu.Opened += (_, _) => SyncMenuControls();

        _themeMenu.Items.Add(HeaderLabel("主题色"));
        _themeMenu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
        _themeMenu.Items.Add(BuildAccentSwatches());

        _themeMenu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
        _themeMenu.Items.Add(BuildAccentCustom());

        _themeMenu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
        _themeMenu.Items.Add(HeaderLabel("背景颜色"));
        _themeMenu.Items.Add(BuildBackgroundSwatches());

        _themeMenu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
        _themeMenu.Items.Add(BuildBackgroundCustom());

        _themeMenu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
        _themeMenu.Items.Add(BuildTransparencySlider());

        var restore = new MenuItem { Header = "恢复默认配色", Foreground = new SolidColorBrush(Colors.White), FocusVisualStyle = null };
        restore.Click += (_, _) => ChooseTheme(CardTheme.Default);
        _themeMenu.Items.Add(restore);

        ContextMenu = _themeMenu;
    }

    /// <summary>Re-syncs the background hex box and transparency slider to the live theme on every open.</summary>
    private void SyncMenuControls()
    {
        if (_bgHexBox is { } box)
            box.Text = $"#{_theme.Background.R:X2}{_theme.Background.G:X2}{_theme.Background.B:X2}";
        if (_transparencySlider is { } slider)
            slider.Value = Math.Round(_theme.Background.A * 100.0 / 255.0);
    }

    private static Button MakeSwatch(ArgbColor fill, string toolTip)
    {
        return new Button
        {
            Width = 22, Height = 22, Margin = new Thickness(2),
            Background = ToFrozenBrush(fill),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = toolTip,
            Focusable = false, FocusVisualStyle = null,
        };
    }

    private UIElement BuildAccentSwatches()
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 162, ItemWidth = 26, ItemHeight = 26 };
        foreach (var theme in CardPalette.Presets)
        {
            var swatch = MakeSwatch(theme.Accent, theme.Accent.ToString());
            // An accent preset changes only the accent; the user's background choice is kept.
            var captured = theme;
            swatch.Click += (_, _) => ChooseTheme(captured with { Background = _theme.Background });
            grid.Children.Add(swatch);
        }
        return grid;
    }

    private UIElement BuildAccentCustom()
    {
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var box = new TextBox
        {
            Width = 92, Height = 22, Text = "#87C5FF",
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x30, 0x30, 0x38)),
            Foreground = new SolidColorBrush(Colors.White),
            // Neutral border, and no focus rectangle.
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x55)),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            FocusVisualStyle = null,
        };
        var apply = new Button
        {
            Content = "自定义", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x6E, 0xA5)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false, FocusVisualStyle = null,
        };
        apply.Click += (_, _) =>
        {
            var parsed = ArgbColor.Parse(box.Text);
            if (parsed is { } color)
            {
                var theme = CardPalette.FromPrimary(color) with { Background = _theme.Background };
                ApplyTheme(theme);
                ThemeChanged?.Invoke(this, theme);
                _themeMenu.IsOpen = false;
            }
            else
            {
                box.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x20, 0x20));
            }
        };
        custom.Children.Add(box);
        custom.Children.Add(apply);
        return custom;
    }

    private UIElement BuildBackgroundSwatches()
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 162, ItemWidth = 26, ItemHeight = 26 };
        foreach (var hue in CardPalette.BackgroundPresets)
        {
            var swatch = MakeSwatch(hue, hue.ToString());
            var captured = hue;
            swatch.Click += (_, _) => ChooseBackground(captured);
            grid.Children.Add(swatch);
        }
        return grid;
    }

    private UIElement BuildBackgroundCustom()
    {
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var box = new TextBox
        {
            Width = 92, Height = 22, Text = "#1A1A20",
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x30, 0x30, 0x38)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x55)),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            FocusVisualStyle = null,
        };
        _bgHexBox = box;
        var apply = new Button
        {
            Content = "自定义", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x6E, 0xA5)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false, FocusVisualStyle = null,
        };
        apply.Click += (_, _) =>
        {
            var parsed = ArgbColor.Parse(box.Text);
            if (parsed is { } color)
            {
                // Honour an explicit alpha (#RRGGBBAA); otherwise keep the current transparency.
                var a = color.A == 0xFF ? _theme.Background.A : color.A;
                var next = _theme with { Background = ArgbColor.FromArgb(a, color.R, color.G, color.B) };
                ApplyTheme(next);
                ThemeChanged?.Invoke(this, next);
                _themeMenu.IsOpen = false;
            }
            else
            {
                box.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x20, 0x20));
            }
        };
        custom.Children.Add(box);
        custom.Children.Add(apply);
        return custom;
    }

    private UIElement BuildTransparencySlider()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var label = new TextBlock
        {
            Text = "透明度", Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA6, 0xA6, 0xB0)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
        };
        var slider = new Slider
        {
            Width = 110, Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10,
            Value = Math.Round(_theme.Background.A * 100.0 / 255.0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false, FocusVisualStyle = null,
        };
        slider.ValueChanged += (_, e) =>
        {
            var a = (byte)Math.Round(e.NewValue * 255.0 / 100.0);
            ApplyTheme(_theme with { Background = _theme.Background with { A = a } });
        };
        // Persist only when the drag ends, not on every tick of the slider.
        slider.PreviewMouseUp += (_, _) => ThemeChanged?.Invoke(this, _theme);
        _transparencySlider = slider;
        panel.Children.Add(label);
        panel.Children.Add(slider);
        return panel;
    }

    /// <summary>Applies a background hue, keeping the card's current transparency.</summary>
    private void ChooseBackground(ArgbColor hue)
    {
        var next = _theme with { Background = ArgbColor.FromArgb(_theme.Background.A, hue.R, hue.G, hue.B) };
        ApplyTheme(next);
        ThemeChanged?.Invoke(this, next);
        _themeMenu.IsOpen = false;
    }

    private void ChooseTheme(CardTheme theme)
    {
        ApplyTheme(theme);
        ThemeChanged?.Invoke(this, theme);
        _themeMenu.IsOpen = false;
    }

    private static UIElement HeaderLabel(string text) =>
        new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA6, 0xA6, 0xB0)),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
        };

    /// <summary>Raised when the user asks the widget to close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user clicks the pin, to ask for the opposite of the current state.</summary>
    public event EventHandler? PinRequested;

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
        // A resize changes the distances a scroll travels, so a motion in flight is landed before
        // anything is recomputed: a drag emits resizes many times a second, and interpolating towards
        // geometry that no longer exists is the one way this could tear.
        FinishScroll();

        _textWidthDip = Math.Max(0d, textWidthDip);

        var fit = LyricTypeScale.Measure(cardHeightDip, _textWidthDip, 0);
        _blockScale = fit.BlockScale;
        _contextRowDip = fit.ContextRowHeight;
        _currentRowDip = fit.CurrentRowHeight;
        _contextFontDip = fit.ContextFontSize;

        LyricStatus.FontSize = fit.ContextFontSize;

        foreach (var row in _rows)
        {
            // Set explicitly rather than letting each row size to its own text. Rows live on a Canvas,
            // which measures its children with infinite width, so a row left to itself would never trim
            // — it would run off the card instead of ending in an ellipsis.
            row.Width = _textWidthDip;
        }

        var activeFont = FontFor(_row1Elements);
        var plan = PlanWith(activeFont, activeFont);
        LyricLines.Height = plan.BlockHeight;
        ApplyFrame(plan.Rest);
    }

    /// <summary>The block's slot geometry, with the two type sizes a line change moves between.</summary>
    private LyricScroll.Plan PlanWith(double outgoingFont, double incomingFont) =>
        LyricScroll.For(_contextRowDip, _currentRowDip, _contextFontDip, outgoingFont, incomingFont);

    /// <summary>
    /// Type size the active line settles at, given how many characters it has.
    /// </summary>
    /// <remarks>
    /// Derived once per line rather than once per syllable: the count cannot change while a line is
    /// being sung, and re-deriving it on every tick would be work inside the one loop that has to stay
    /// cheap. The line's <i>row</i> is deliberately unaffected — it stays on the block scale, so a
    /// scroll always travels the same distance whatever the words are.
    /// </remarks>
    private double FontFor(int elementCount) =>
        _textWidthDip > 0d
            ? LyricTypeScale.CurrentFont * LyricTypeScale.CurrentScaleFor(_blockScale, _textWidthDip, elementCount)
            : LyricTypeScale.CurrentFont * _blockScale;

    /// <summary>Writes one frame of the motion onto the four rows: position, type size, opacity.</summary>
    private void ApplyFrame(LyricScroll.Frame frame)
    {
        for (var row = 0; row < _rows.Length; row++)
        {
            Canvas.SetTop(_rows[row], frame.Top(row));
            _rows[row].FontSize = frame.FontSize(row);
            _rows[row].Opacity = frame.Opacity(row);
        }
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
            FinishScroll();
            _shownIndex = -1;
            _paint = LyricLinePaint.Empty;
            _lastLyrics = null;
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
        _lastLyrics = lyrics;

        if (lyrics.CurrentIndex == _shownIndex)
        {
            // The same line: only the highlight moved. Recolouring inlines whose text never changes
            // invalidates the render and nothing else — and mid-scroll they are the arriving row's
            // inlines, which is what lets the new line light up as it comes in rather than after it
            // lands.
            _paint = lyrics.Current;
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
        FinishScroll();
        Paint(lyrics);
    }

    /// <summary>Puts a window on screen at once, with no animation.</summary>
    private void Paint(LyricsWindow lyrics)
    {
        _shownIndex = lyrics.CurrentIndex;
        _paint = lyrics.Current;
        _currentRow = 1;
        _row1Elements = lyrics.Current.ElementCount;

        SetRow(0, lyrics.Previous, RowStyle.Past);
        SetRow(1, lyrics.Current.Text, RowStyle.Active);
        SetRow(2, lyrics.Next, RowStyle.Upcoming);
        SetRow(3, string.Empty, RowStyle.Upcoming);

        var activeFont = FontFor(_row1Elements);
        ApplyFrame(PlanWith(activeFont, activeFont).Rest);
        PaintHighlight(lyrics.Current);
    }

    /// <summary>How a row that is not the one being sung is painted.</summary>
    private enum RowStyle
    {
        /// <summary>A line below the active one: the idle colour, at full opacity.</summary>
        Upcoming,

        /// <summary>
        /// A line above the active one. It keeps the sung colour and is dimmed instead of being
        /// restyled, which is what makes the handover from "being sung" to "already sung" continuous:
        /// the row leaving the middle slot is dimmed by the motion, not recoloured when it stops. It also
        /// means a previous line looks the same however it got there — scrolled out, or painted after a
        /// seek.
        /// </summary>
        Past,

        /// <summary>The line being sung: one inline per character, so that the highlight can move.</summary>
        Active,
    }

    /// <summary>
    /// Gives a row a new line: one inline per character when it is the active line, one for the whole
    /// line when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole fix for the line that used to twitch sideways on every beat. The old code
    /// rebuilt three runs' text at each syllable boundary, and assigning to a run's text invalidates
    /// the <i>measure</i> — so WPF re-shaped the whole line, and the glyphs after the boundary moved,
    /// once per character. Changing only a run's colour invalidates the <i>render</i>: the run's text is
    /// unchanged, nothing is re-measured, and the positions are therefore frozen by construction rather
    /// than by luck.
    /// </para>
    /// <para>
    /// One inline per <i>text element</i>, not per UTF-16 code unit: see <see cref="TextElements"/> for
    /// what splitting a surrogate pair would do to an emoji.
    /// </para>
    /// <para>
    /// Context rows get a single inline because nothing ever colours them character by character —
    /// giving them that structure would be one more thing to keep in step for no visible gain.
    /// </para>
    /// </remarks>
    private void SetRow(int row, string text, RowStyle style)
    {
        var block = _rows[row];
        var runs = _rowRuns[row];

        block.Inlines.Clear();
        runs.Clear();
        _rowTexts[row] = text;

        if (style == RowStyle.Active)
        {
            foreach (var element in TextElements.Split(text))
            {
                var run = new Run(element) { Foreground = IdleBrush };
                runs.Add(run);
                block.Inlines.Add(run);
            }

            // The runs the last highlight referred to are gone, so it cannot be compared against
            // whatever the next caller paints — a rebuild and an unchanged range must not look alike.
            _paintedSung = -1;
            _paintedEnd = -1;
            return;
        }

        if (text.Length == 0) return;

        block.Inlines.Add(new Run(text)
        {
            Foreground = style == RowStyle.Past ? SungBrush : IdleBrush,
        });
    }

    /// <summary>
    /// Colours the active line's inlines: everything before <c>Sung</c> one way, the syllable being sung
    /// another, the rest a third.
    /// </summary>
    private void PaintHighlight(LyricLinePaint paint)
    {
        // Only reachable if a caller hands over a window whose line is not the one the active row was
        // built from. Rebuilding is the safe answer; colouring the wrong glyphs is not.
        if (!string.Equals(_rowTexts[_currentRow], paint.Text, StringComparison.Ordinal))
            SetRow(_currentRow, paint.Text, RowStyle.Active);

        var runs = _rowRuns[_currentRow];
        var sung = Math.Clamp(paint.SungElements, 0, runs.Count);
        var end = Math.Clamp(paint.SungElements + paint.CurrentElements, sung, runs.Count);
        if (sung == _paintedSung && end == _paintedEnd) return;

        _paintedSung = sung;
        _paintedEnd = end;

        for (var i = 0; i < runs.Count; i++)
        {
            var brush = i < sung ? SungBrush : i < end ? CurrentBrush : IdleBrush;
            if (!ReferenceEquals(runs[i].Foreground, brush)) runs[i].Foreground = brush;
        }
    }

    /// <summary>
    /// Scrolls every row up by one slot, so that the line below arrives in the middle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One curve, one duration, four rows — and a row that is already holding the line after next
    /// before anything starts, which is what makes the arrival an arrival rather than a replacement.
    /// The motion itself is <see cref="LyricScroll"/>'s; that is also where the property that makes the
    /// reset at the end invisible is written down.
    /// </para>
    /// <para>
    /// Nothing here rebuilds the active line's inlines: it is the <i>arriving</i> row that takes them
    /// over, so the highlight on the line being sung is never interrupted and the new line lights up
    /// character by character while it is still moving.
    /// </para>
    /// </remarks>
    private void ScrollToNextLine(LyricsWindow lyrics)
    {
        _scrolling = true;
        _shownIndex = lyrics.CurrentIndex;
        _paint = lyrics.Current;

        var plan = PlanWith(FontFor(_row1Elements), FontFor(lyrics.Current.ElementCount));
        _scrollPlan = plan;

        // Stage the row below the clip. It sits exactly on the clip's bottom edge, where it counts as
        // invisible, so filling it in here cannot be seen — and it has to be filled in before the motion
        // starts, or the last third of the motion would show an empty row rising into place.
        SetRow(3, lyrics.Next, RowStyle.Upcoming);

        // The arriving line takes over the inlines now rather than at the end. A context row is a single
        // inline in the idle colour and the active row's unsung characters are painted in that same
        // colour, so the swap is invisible where it happens.
        SetRow(2, lyrics.Current.Text, RowStyle.Active);
        _currentRow = 2;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var start = plan.Start;
        var end = plan.End;

        // Every row, every property, one duration and one easing: the frames cannot disagree with each
        // other, which is the whole difference between this and two animations handed over in sequence.
        void Animate(int row, double from, double to, DependencyProperty property, bool land)
        {
            var animation = new DoubleAnimation(from, to, LyricScrollDuration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            };

            if (land) animation.Completed += (_, _) => FinishScroll();
            _rows[row].BeginAnimation(property, animation);
        }

        for (var row = 0; row < _rows.Length; row++)
        {
            Animate(row, start.Top(row), end.Top(row), Canvas.TopProperty, land: false);
            Animate(row, start.FontSize(row), end.FontSize(row), TextBlock.FontSizeProperty, land: false);
            Animate(row, start.Opacity(row), end.Opacity(row), OpacityProperty, land: row == 1);
        }
    }

    /// <summary>
    /// Lands a scroll in flight: clears the animations, shifts the text up one slot, and puts the rows
    /// back on their resting offsets.
    /// </summary>
    /// <remarks>
    /// Called when the motion runs out, and — through <see cref="CancelScroll"/> — whenever something
    /// interrupts one. Both want the same thing, because the frame the motion ends on <i>is</i> the
    /// resting frame shifted up one slot: the reset that has to happen here redraws exactly the pixels
    /// that are already on screen, so landing early is as invisible as landing on time. A no-op when
    /// nothing is in flight.
    /// </remarks>
    private void FinishScroll()
    {
        if (!_scrolling) return;

        _scrolling = false;

        foreach (var row in _rows)
        {
            row.BeginAnimation(Canvas.TopProperty, null);
            row.BeginAnimation(TextBlock.FontSizeProperty, null);
            row.BeginAnimation(OpacityProperty, null);
        }

        var plan = _scrollPlan;

        _rowTexts[0] = _rowTexts[1];
        _rowTexts[1] = _rowTexts[2];
        _rowTexts[2] = _rowTexts[3];

        // The line that has just been sung keeps the sung colour as it leaves — it is the row that dims,
        // not the row that changes colour, which is what keeps the previous line from flickering grey at
        // the exact moment the eye has followed it up there. A line changes only once the next one has
        // started, by which point it has been sung through and is entirely in that colour, so rebuilding
        // it here repaints the pixels that are already on screen.
        SetRow(0, _rowTexts[0], RowStyle.Past);
        SetRow(1, _rowTexts[1], RowStyle.Active);
        SetRow(2, _rowTexts[2], RowStyle.Upcoming);
        SetRow(3, string.Empty, RowStyle.Upcoming);

        _currentRow = 1;
        _row1Elements = _paint.ElementCount;

        LyricLines.Height = plan.BlockHeight;
        ApplyFrame(plan.Rest);
        PaintHighlight(_paint);
    }

    /// <summary>Lands a scroll in flight. Deliberately the same thing as letting it finish.</summary>
    private void CancelScroll() => FinishScroll();

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

    private void OnPinClick(object sender, RoutedEventArgs e) =>
        PinRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Reflects the pinned state on the button: an unlit pin invites the click, a lit one says what
    /// state the card is in and offers to end it.
    /// </summary>
    /// <remarks>
    /// The window owns the state — it is the window's ex-style that actually changes — so the card
    /// only ever renders what it is told. Called once with the restored value at startup, so the
    /// button never shows a state the card is not in.
    /// </remarks>
    internal void SetPinnedVisual(bool pinned)
    {
        _pinned = pinned;
        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        PinButton.Foreground = pinned ? PinnedBrush : UnpinnedBrush;
        PinButton.ToolTip = pinned
            ? "取消固定（回到普通窗口，显示在软件之下）"
            : "固定在最上层（让卡片浮在所有软件之上）";
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) =>
        ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e) =>
        NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnPreviousClick(object sender, RoutedEventArgs e) =>
        PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnSourceClick(object sender, RoutedEventArgs e) =>
        SourceSwitchRequested?.Invoke(this, EventArgs.Empty);
}
