using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopMediaController.Core;
using DesktopMediaController.Services;
using DesktopMediaController.Services.Lyrics;
using DesktopMediaController.Win32;

namespace DesktopMediaController.Widget;

/// <summary>
/// The controller's top-level window: a borderless, always-on-top, per-pixel-translucent card the
/// user drags and resizes anywhere on any monitor. It is built on a raw <see cref="HwndSource"/>
/// rather than a WPF <see cref="Window"/> so its styles, size and lifetime are all under our control.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> reparented into the shell (<c>SetParent</c>). The organizer learned this
/// the hard way: reparenting a WPF transparency-enabled window hangs the UI thread in a shell
/// handshake. Top-level only, forever.
/// </remarks>
internal sealed class WidgetWindow
{
    /// <summary>
    /// FROZEN IDENTITY CONTRACT.
    /// <para>
    /// The desktop-icon packer decides whether the controller is on screen by <i>looking for this
    /// window</i>, and the title is the only self-chosen handle it has. WPF gives a managed window
    /// no way to pick its class name (<see cref="HwndSourceParameters"/> has no WindowClass member —
    /// verified by the compiler), and the class WPF does register is
    /// <c>HwndWrapper[&lt;process&gt;;;&lt;guid&gt;]</c>, which differs on every run.
    /// </para>
    /// <para>
    /// So: do not rename this, and never derive it from user data. Callers must also require
    /// <c>IsWindowVisible</c> — a window hidden to the tray is still enumerable.
    /// </para>
    /// </summary>
    public const string WindowTitle = "DesktopMediaController";

    /// <summary>
    /// FROZEN PROTOCOL CONTRACT.
    /// <para>
    /// A second launch does not build a rival widget; it posts this message to the running one, which
    /// then puts its card back on screen. Deliberately the same window-message channel the rest of the
    /// project uses — no pipe, no heartbeat file, and it reaches a card that is currently hidden.
    /// </para>
    /// </summary>
    public const int ShowCardMessage = 0x8000 + 1;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_SIZE = 0x0005;
    private const int WM_SHOWWINDOW = 0x0018;

    /// <summary>
    /// How often the widget re-reads the players. Fast enough that a track change feels immediate,
    /// slow enough that the per-tick cost is irrelevant. See <see cref="SmtcMediaSource"/> for why
    /// this is a poll rather than a subscription.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How often the lyric position is recomputed.
    /// </summary>
    /// <remarks>
    /// A timer rather than <c>CompositionTarget.Rendering</c>, which would recompute at 60 Hz and — far
    /// worse — keep the compositor rendering frames all day for a widget that is usually just sitting
    /// there. 20 Hz is chosen against what the display actually does: syllables change at most a few
    /// times a second, and the highlight only moves when one does, so the worst error is 50 ms on the
    /// moment a syllable lights up. Between changes a tick is one integer comparison and nothing else.
    /// </remarks>
    private static readonly TimeSpan LyricInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Width, in DIPs, of the band along each edge that resizes instead of moving the card.
    /// </summary>
    /// <remarks>
    /// Has to stay inside the card's outer margin (10–12 DIPs), because the bands and the buttons would
    /// otherwise compete for the same pixels — the close button is 10 DIPs from the right edge and the
    /// transport row 10 from the bottom, so 6 leaves both alone. Mouse-down still gives way to a button
    /// regardless, so the worst case is a resize that will not start rather than a button that will not
    /// press.
    /// </remarks>
    private const double ResizeBandDip = 6;

    private readonly HwndSource _source;
    private readonly WidgetCard _card;
    private readonly IntPtr _hwnd;
    private readonly SmtcMediaSource _media = new();
    private readonly LyricsService _lyrics = new();
    private readonly PlaybackClock _clock = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _lyricTimer;

    private bool _dragging;
    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private ResizeEdge _hoverEdge;
    private WidgetSize _grabSize;
    private (int X, int Y) _grabCursor;
    private ScreenRect _grabBounds;

    // ---- lyric state --------------------------------------------------------------------------

    /// <summary>Identifies which track the current document belongs to; empty when there is none.</summary>
    private string _trackKey = string.Empty;

    private LyricsDocument _document = LyricsDocument.Empty;
    private LyricsState _lyricsState = LyricsState.Idle;

    /// <summary>Last painted position, so a frame that changes nothing does no work at all.</summary>
    private LyricCursor? _cursor;

    /// <summary>Cancels the in-flight lookup when the track changes; the old answer is worthless.</summary>
    private CancellationTokenSource? _lookup;

    /// <summary>
    /// The user's intent, which is not the same as <c>IsWindowVisible</c>: WPF re-shows the window
    /// whenever <see cref="HwndSource.SizeToContent"/> re-measures it, so intent is the thing that has
    /// to be defended rather than merely observed.
    /// </summary>
    private bool _cardHidden;

    /// <param name="startHidden">
    /// Create the card already off screen, for the sign-in entry point. This is a constructor
    /// parameter rather than a <see cref="HideCard"/> call right after construction on purpose:
    /// creating the window visible and hiding it again in the same breath — before WPF has ever
    /// composited a frame — leaves the render target of this per-pixel-translucent (i.e. layered)
    /// window uninitialised, and a later <c>SW_SHOWNOACTIVATE</c> never wakes it. The window then
    /// reports <c>IsWindowVisible == true</c> while drawing nothing at all. Measured: the exact same
    /// hide/show cycle performed after the first frame is fine, so the damage is specifically the
    /// hide-before-first-render, and never creating the window visible is what avoids it.
    /// </param>
    public WidgetWindow(bool startHidden = false)
    {
        _card = new WidgetCard(WidgetSize.Default);
        _card.CloseRequested += OnCloseRequested;

        var screen = WidgetNative.VirtualScreen();
        var start = PlacementStore.Load(PlacementStore.DefaultFilePath) ?? WidgetPlacement.FirstRun(screen);
        _card.SetCardSize(start.Size);

        // HwndSourceParameters' size is resolved against the SYSTEM dpi, not the target monitor's, so
        // a window restored onto a 125% monitor is born at the 100% size and only reaches its real one
        // once WPF sees the WM_DPICHANGED for the monitor it landed on. SizeToContent is what makes
        // that follow-through automatic, in both directions, for the life of the window: the card's
        // DESIGN size (in DIPs) is the single source of truth and WPF owns the physical pixels.
        //
        // Do not go back to hand-rolling this (GetDpiForWindow + SetWindowPos on WM_DPICHANGED).
        // That was tried: SetWindowPos' x/y arguments are literal unless SWP_NOMOVE is passed, so
        // each resize also teleported the window to (0,0), which re-triggered a DPI change, which
        // resized again - a 360x112 card inflated to 1906x670 inside one drag.
        _cardHidden = startHidden;

        _source = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            // WS_VISIBLE is not implied: HwndSourceParameters builds exactly the style it is given,
            // so a WS_POPUP-only window is created and stays invisible. That is precisely what a
            // silent start wants - see the startHidden remark above.
            WindowStyle = startHidden ? WS_POPUP : WS_POPUP | WS_VISIBLE,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            // Per-pixel alpha: what lets the card be a translucent, rounded shape instead of a
            // rectangle. Turns the window into a layered window under the hood.
            UsesPerPixelOpacity = true,
            PositionX = start.X,
            PositionY = start.Y,
            Width = (int)Math.Ceiling(start.WidthDip),
            Height = (int)Math.Ceiling(start.HeightDip),
        });
        _hwnd = _source.Handle;
        _source.RootVisual = _card;
        _source.SizeToContent = SizeToContent.WidthAndHeight;

        _source.AddHook(WndProc);
        MoveTo(PlacementStore.Clamp(start.Position, screen, WidthOf(), HeightOf()));

        _card.MouseLeftButtonDown += OnCardMouseDown;
        _card.MouseMove += OnCardMouseMove;
        _card.MouseLeftButtonUp += OnCardMouseUp;
        _card.MouseLeave += OnCardMouseLeave;
        _card.ToggleRequested += OnToggleRequested;
        _card.NextRequested += OnNextRequested;
        _card.PreviousRequested += OnPreviousRequested;
        _card.SourceSwitchRequested += OnSourceSwitchRequested;

        // Low priority: repainting the card must never compete with a drag or a resize. Ticks that
        // land while a refresh is still in flight are dropped rather than queued (see SmtcMediaSource).
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;

        _lyricTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LyricInterval };
        _lyricTimer.Tick += OnLyricTick;

        // A silent start is a card nobody can see, so it does not poll at all - same reasoning as
        // HideCard. ShowCard starts it again, and refreshes immediately so the first frame is live.
        if (!startHidden)
        {
            _pollTimer.Start();
            _lyricTimer.Start();
        }

        // Fire-and-forget on purpose: it swallows its own failures (a machine with SMTC unavailable
        // still gets a draggable widget) and every tick before it completes is a harmless no-op.
        _ = _media.InitializeAsync();
    }

    private async void OnPollTick(object? sender, EventArgs e) => await RefreshAndApplyAsync();

    /// <summary>
    /// Reads the players and repaints. Shared by the timer and by <see cref="ShowCard"/>, so a card
    /// returning from the tray is never left a frame behind.
    /// </summary>
    private async Task RefreshAndApplyAsync()
    {
        await _media.RefreshAsync();

        var snapshot = _media.Snapshot;
        _card.Apply(snapshot);

        if (snapshot.Session is { } session)
        {
            BeginTrack(session);
            SyncClock(session);
            return;
        }

        ClearTrack();
    }

    // ---- lyrics -------------------------------------------------------------------------------

    /// <summary>
    /// Starts a lyric lookup when — and only when — the displayed track actually changed.
    /// </summary>
    /// <remarks>
    /// Driven from the poll rather than from a "track changed" event because there is no such event:
    /// the whole media layer is polled on purpose. Keying on title and artist means a position tick, a
    /// pause, or a re-read that returns the same song all cost nothing.
    /// </remarks>
    private void BeginTrack(MediaSessionInfo session)
    {
        var key = LyricsCacheKey.For(session.Title, session.Artist);
        if (key == _trackKey) return;

        _trackKey = key;
        _clock.Reset();
        CancelLookup();

        if (!session.HasTrack)
        {
            // Nothing identifiable: not "no lyrics", just nothing to look up yet.
            SetLyrics(LyricsState.Idle, LyricsDocument.Empty);
            return;
        }

        var token = (_lookup = new CancellationTokenSource()).Token;
        SetLyrics(LyricsState.Searching, LyricsDocument.Empty);
        _ = LoadLyricsAsync(session, token);
    }

    private async Task LoadLyricsAsync(MediaSessionInfo session, CancellationToken token)
    {
        try
        {
            var lookup = await _lyrics.LookupAsync(session, token);
            if (token.IsCancellationRequested) return;
            SetLyrics(lookup.State, lookup.Document);
        }
        catch (OperationCanceledException)
        {
            // The user moved to another track before this answer arrived. That is the normal way this
            // lookup ends, not a failure.
        }
        catch (Exception ex)
        {
            CrashLog.Write("lyrics-lookup", ex);
            SetLyrics(LyricsState.Unavailable, LyricsDocument.Empty);
        }
    }

    /// <summary>Stops looking and forgets the track, for when there is no session at all.</summary>
    private void ClearTrack()
    {
        if (_trackKey.Length == 0 && _lyricsState == LyricsState.Idle) return;

        _trackKey = string.Empty;
        _clock.Reset();
        CancelLookup();
        SetLyrics(LyricsState.Idle, LyricsDocument.Empty);
    }

    private void CancelLookup()
    {
        _lookup?.Cancel();
        _lookup?.Dispose();
        _lookup = null;
    }

    private void SetLyrics(LyricsState state, LyricsDocument document)
    {
        _lyricsState = state;
        _document = document;
        RepaintLyrics();
    }

    /// <summary>Paints the lyric area from the current position, unconditionally.</summary>
    private void RepaintLyrics()
    {
        if (_document.IsEmpty)
        {
            _cursor = null;
            _card.ApplyLyrics(null, _lyricsState);
            return;
        }

        var position = _clock.EstimateAt(Environment.TickCount64);
        _cursor = LyricTimeline.Locate(_document.Lines, position);
        _card.ApplyLyrics(LyricTimeline.WindowAt(_document, position), _lyricsState);
    }

    /// <summary>
    /// The per-frame lyric step: recompute where the playhead is, and repaint only when the answer
    /// changed.
    /// </summary>
    /// <remarks>
    /// The cursor is compared rather than the window, because a cursor is integers: comparing windows
    /// would mean building six strings per frame, sixty times a second, to discover they were the same
    /// ones as last time. Nothing here touches the network, the players, or the window manager — it is
    /// arithmetic on a clock that was already anchored.
    /// </remarks>
    private void OnLyricTick(object? sender, EventArgs e)
    {
        if (_document.IsEmpty) return;

        var position = _clock.EstimateAt(Environment.TickCount64);
        var cursor = LyricTimeline.Locate(_document.Lines, position);
        if (_cursor == cursor) return;

        _cursor = cursor;
        _card.ApplyLyrics(LyricTimeline.WindowAt(_document, position), _lyricsState);
    }

    /// <summary>
    /// Hands the player's latest reading to the clock, which extrapolates between readings.
    /// </summary>
    private void SyncClock(MediaSessionInfo session)
    {
        var result = _clock.Sync(
            positionMs: (int)Math.Clamp(session.Position.TotalMilliseconds, 0, int.MaxValue),
            lagMs: SnapshotLagMs(session),
            playing: session.Status == MediaPlaybackStatus.Playing,
            nowTickMs: Environment.TickCount64,
            positionKnown: session.HasTimeline);

        // A seek makes the reconstruction discontinuous — subtracting timestamps cannot know the
        // playhead was moved by hand — so repaint on the spot rather than letting the highlight sit on
        // a stale line until the next syllable boundary happens to notice.
        if (result == ClockSync.Jumped) RepaintLyrics();
    }

    /// <summary>
    /// How stale the timeline reading already was when we read it.
    /// </summary>
    /// <remarks>
    /// The single most important number in the whole lyric path. Measured on QQ音乐, a snapshot was up
    /// to 1.1 s old by the time it was available; treating its position as "now" puts the lyrics a
    /// whole line behind. Folding the lag into the anchor is what makes the reconstruction accurate
    /// rather than merely smooth.
    /// </remarks>
    private static int SnapshotLagMs(MediaSessionInfo session)
    {
        var lag = DateTimeOffset.UtcNow - session.LastUpdated;
        if (lag <= TimeSpan.Zero) return 0;
        return lag.TotalMilliseconds >= int.MaxValue ? int.MaxValue : (int)lag.TotalMilliseconds;
    }

    // ---- transport ----------------------------------------------------------------------------

    private async void OnToggleRequested(object? sender, EventArgs e)
    {
        await _media.TogglePlayPauseAsync();
        RepaintNow();
    }

    private async void OnNextRequested(object? sender, EventArgs e)
    {
        await _media.NextAsync();
        RepaintNow();
    }

    private async void OnPreviousRequested(object? sender, EventArgs e)
    {
        await _media.PreviousAsync();
        RepaintNow();
    }

    private void OnSourceSwitchRequested(object? sender, EventArgs e)
    {
        _media.CycleSession();
        RepaintNow();
    }

    /// <summary>
    /// Repaints straight away after a command instead of waiting for the next tick, so a button press
    /// looks like it did something even when the player is slow to publish its new state.
    /// </summary>
    private void RepaintNow()
    {
        _card.Apply(_media.Snapshot);
        RepaintLyrics();
    }

    // ---- geometry -----------------------------------------------------------------------------

    private int WidthOf() => WidgetNative.BoundsOf(_hwnd).Width;

    private int HeightOf() => WidgetNative.BoundsOf(_hwnd).Height;

    private void MoveTo(WidgetPosition position) => WidgetNative.MoveTo(_hwnd, position.X, position.Y);

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Anything that wants the click for itself (the close button, the transport row) gets it.
        if (e.OriginalSource is ButtonBase) return;

        var edge = HitTestEdge(e.GetPosition(_card));

        _grabCursor = WidgetNative.CursorPosition();
        _grabBounds = WidgetNative.BoundsOf(_hwnd);

        if (edge == ResizeEdge.None)
        {
            _dragging = true;
        }
        else
        {
            // The size is captured in DIPs alongside the physical bounds, because the cursor moves in
            // physical pixels while the card is sized in DIPs and the two only agree at 100%.
            _resizing = true;
            _resizeEdge = edge;
            _grabSize = _card.CardSize;
        }

        _card.CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging && !_resizing)
        {
            UpdateHoverCursor(e.GetPosition(_card));
            return;
        }

        var (x, y) = WidgetNative.CursorPosition();

        if (_dragging)
        {
            // Absolute tracking, not accumulated deltas: every frame recomputes from the grab point, so
            // the window cannot creep away from the cursor the way delta accumulation does.
            WidgetNative.MoveTo(_hwnd, _grabBounds.X + (x - _grabCursor.X), _grabBounds.Y + (y - _grabCursor.Y));
            return;
        }

        ResizeTo(x, y);
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && !_resizing) return;

        _dragging = false;
        _resizing = false;
        _resizeEdge = ResizeEdge.None;
        _card.ReleaseMouseCapture();
        SettleAndSave();
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging || _resizing || _hoverEdge == ResizeEdge.None) return;
        _hoverEdge = ResizeEdge.None;
        _card.Cursor = Cursors.Arrow;
    }

    /// <summary>
    /// Resizes the card to follow the cursor, holding the edge opposite the one being dragged still.
    /// </summary>
    /// <remarks>
    /// Only the card's <i>design</i> size is changed; the window follows through <c>SizeToContent</c>.
    /// The origin then has to be moved for the west and north edges, because WPF grows a window from its
    /// top-left: without that, dragging the left edge leftwards would leave the edge behind and instead
    /// stretch the card to the right. Moving the window is a position-only operation and is exactly
    /// what the drag path already does — it is the <i>size</i> that must never be set directly.
    /// </remarks>
    private void ResizeTo(int cursorX, int cursorY)
    {
        // Queried per move rather than cached: the card can be dragged across the boundary between the
        // 100% and 125% monitors mid-gesture, and a cached scale would then be wrong by 25%.
        var dpi = VisualTreeHelper.GetDpi(_card);

        var size = WidgetSize.Coerce(
            HorizontalSize(cursorX, dpi.DpiScaleX),
            VerticalSize(cursorY, dpi.DpiScaleY));

        _card.SetCardSize(size);

        var x = _grabBounds.X;
        var y = _grabBounds.Y;
        if (_resizeEdge.HasFlag(ResizeEdge.West)) x = _grabBounds.Right - Physical(size.WidthDip, dpi.DpiScaleX);
        if (_resizeEdge.HasFlag(ResizeEdge.North)) y = _grabBounds.Bottom - Physical(size.HeightDip, dpi.DpiScaleY);

        WidgetNative.MoveTo(_hwnd, x, y);
    }

    private double HorizontalSize(int cursorX, double scale)
    {
        if (!_resizeEdge.HasFlag(ResizeEdge.West) && !_resizeEdge.HasFlag(ResizeEdge.East))
        {
            return _grabSize.WidthDip;
        }

        var delta = (cursorX - _grabCursor.X) / scale;
        return _resizeEdge.HasFlag(ResizeEdge.West) ? _grabSize.WidthDip - delta : _grabSize.WidthDip + delta;
    }

    private double VerticalSize(int cursorY, double scale)
    {
        if (!_resizeEdge.HasFlag(ResizeEdge.North) && !_resizeEdge.HasFlag(ResizeEdge.South))
        {
            return _grabSize.HeightDip;
        }

        var delta = (cursorY - _grabCursor.Y) / scale;
        return _resizeEdge.HasFlag(ResizeEdge.North) ? _grabSize.HeightDip - delta : _grabSize.HeightDip + delta;
    }

    private static int Physical(double dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>Which edges the point is within <see cref="ResizeBandDip"/> of, if any.</summary>
    private ResizeEdge HitTestEdge(Point point)
    {
        var width = _card.ActualWidth;
        var height = _card.ActualHeight;
        if (width <= 0 || height <= 0) return ResizeEdge.None;

        var edge = ResizeEdge.None;
        if (point.X <= ResizeBandDip) edge |= ResizeEdge.West;
        else if (point.X >= width - ResizeBandDip) edge |= ResizeEdge.East;

        if (point.Y <= ResizeBandDip) edge |= ResizeEdge.North;
        else if (point.Y >= height - ResizeBandDip) edge |= ResizeEdge.South;

        return edge;
    }

    /// <summary>
    /// Shows the resize cursor over an edge. Without it the ability is undiscoverable — nothing else on
    /// the card advertises that its border can be dragged.
    /// </summary>
    private void UpdateHoverCursor(Point point)
    {
        var edge = HitTestEdge(point);
        if (edge == _hoverEdge) return;

        _hoverEdge = edge;
        _card.Cursor = edge switch
        {
            ResizeEdge.West or ResizeEdge.East => Cursors.SizeWE,
            ResizeEdge.North or ResizeEdge.South => Cursors.SizeNS,
            ResizeEdge.North | ResizeEdge.West => Cursors.SizeNWSE,
            ResizeEdge.South | ResizeEdge.East => Cursors.SizeNWSE,
            ResizeEdge.North | ResizeEdge.East => Cursors.SizeNESW,
            ResizeEdge.South | ResizeEdge.West => Cursors.SizeNESW,
            _ => Cursors.Arrow,
        };
    }

    /// <summary>Pulls the window back onto a real monitor and persists where and how big it ended up.</summary>
    private void SettleAndSave()
    {
        var bounds = WidgetNative.BoundsOf(_hwnd);
        var position = PlacementStore.Clamp(
            new WidgetPosition(bounds.X, bounds.Y),
            WidgetNative.VirtualScreen(),
            bounds.Width,
            bounds.Height);

        MoveTo(position);

        // The size is read back from the card rather than from the window: the card holds DIPs, which
        // is the form that survives a monitor scale change, and the window's pixels are derived from it.
        var size = _card.CardSize;
        PlacementStore.Save(
            PlacementStore.DefaultFilePath,
            new WidgetPlacement(position.X, position.Y, size.WidthDip, size.HeightDip));
    }

    private WidgetPosition ClampToScreen()
    {
        var bounds = WidgetNative.BoundsOf(_hwnd);
        return PlacementStore.Clamp(
            new WidgetPosition(bounds.X, bounds.Y), WidgetNative.VirtualScreen(), bounds.Width, bounds.Height);
    }

    /// <summary>The card's close button. Hides rather than quits — see <see cref="HideCard"/>.</summary>
    private void OnCloseRequested(object? sender, EventArgs e) => HideCard();

    /// <summary>
    /// Whether the card is on screen right now. Read back from the window rather than from our own
    /// flag, so the tray menu cannot claim "visible" while nothing is actually drawn.
    /// </summary>
    public bool IsCardVisible => WidgetNative.IsVisible(_hwnd);

    /// <summary>
    /// Raised once <see cref="Shutdown"/> has persisted everything and the process is free to end.
    /// The widget outlives the card — that is the point of the tray — so the card's close button is
    /// deliberately not what ends the process.
    /// </summary>
    public event EventHandler? ExitRequested;

    /// <summary>Puts the card back on screen without activating it, and resumes live updates.</summary>
    public void ShowCard()
    {
        _cardHidden = false;
        WidgetNative.SetVisible(_hwnd, true);

        // Polling is suspended while hidden, so without this the card would reappear still showing
        // whatever was playing when it was dismissed.
        _pollTimer.Start();
        _lyricTimer.Start();
        _ = RefreshAndApplyAsync();
    }

    /// <summary>
    /// Takes the card off screen while keeping the process — and the SMTC poll — alive, which is the
    /// entire difference between hiding and closing. The window handle survives, so a later launch can
    /// still find this instance, and the packer correctly stops reserving desktop space for it.
    /// </summary>
    public void HideCard()
    {
        _cardHidden = true;
        WidgetNative.SetVisible(_hwnd, false);

        // Nobody can see the card, so there is no reason to keep reading the players three times a
        // second or stepping the lyric clock twenty times a second. The session itself stays open:
        // showing it again is instant.
        _pollTimer.Stop();
        _lyricTimer.Stop();
    }

    /// <summary>
    /// Persists where the card ended up and releases the media session, ready for the process to end.
    /// Only an explicit quit reaches here.
    /// </summary>
    public void Shutdown()
    {
        _pollTimer.Stop();
        _lyricTimer.Stop();
        CancelLookup();
        _lyrics.Dispose();
        _media.Dispose();
        SettleAndSave();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A second launch asking the running instance to surface its card.
        if (msg == ShowCardMessage)
        {
            ShowCard();
            handled = true;
            return IntPtr.Zero;
        }

        // WPF re-shows the window whenever SizeToContent re-measures it, which would silently undo a
        // deliberate hide. Veto the show — but only when wParam says the window is being shown: hiding
        // raises this message again with wParam == FALSE, and reacting to that would recurse forever.
        if (msg == WM_SHOWWINDOW && _cardHidden && wParam != IntPtr.Zero)
        {
            WidgetNative.SetVisible(hwnd, false);
            handled = true;
            return IntPtr.Zero;
        }

        // Crossing a monitor boundary changes the physical size, and a window that was flush against
        // the right edge before the growth can end up poking off-screen after it. Re-clamp whenever
        // the size changes for any reason - but never mid-gesture, where it would fight the cursor.
        if (msg == WM_SIZE && !_dragging && !_resizing)
        {
            MoveTo(ClampToScreen());
        }
        return IntPtr.Zero;
    }

    /// <summary>Which edges of the card a gesture is anchored to.</summary>
    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        North = 1,
        South = 2,
        West = 4,
        East = 8,
    }
}
