using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopMediaController.Core;
using DesktopMediaController.Services;
using DesktopMediaController.Win32;

namespace DesktopMediaController.Widget;

/// <summary>
/// The controller's top-level window: a borderless, always-on-top, per-pixel-translucent card the
/// user drags anywhere on any monitor. It is built on a raw <see cref="HwndSource"/> rather than a
/// WPF <see cref="Window"/> so its styles, size and lifetime are all under our control.
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

    private readonly HwndSource _source;
    private readonly WidgetCard _card;
    private readonly IntPtr _hwnd;
    private readonly SmtcMediaSource _media = new();
    private readonly DispatcherTimer _pollTimer;

    private bool _dragging;
    private (int X, int Y) _grabCursor;
    private ScreenRect _grabBounds;

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
        _card = new WidgetCard();
        _card.CloseRequested += OnCloseRequested;

        var screen = WidgetNative.VirtualScreen();
        var start = PlacementStore.Load(PlacementStore.DefaultFilePath) ?? WidgetPosition.FirstRun(screen);

        // HwndSourceParameters' size is resolved against the SYSTEM dpi, not the target monitor's, so
        // a window restored onto a 125% monitor is born 320x112 and only becomes 400x140 once WPF
        // sees the WM_DPICHANGED for the monitor it landed on. SizeToContent is what makes that
        // follow-through automatic, in both directions, for the life of the window: the card's
        // DESIGN size (in DIPs) is the single source of truth and WPF owns the physical pixels.
        //
        // Do not go back to hand-rolling this (GetDpiForWindow + SetWindowPos on WM_DPICHANGED).
        // That was tried: SetWindowPos' x/y arguments are literal unless SWP_NOMOVE is passed, so
        // each resize also teleported the window to (0,0), which re-triggered a DPI change, which
        // resized again - a 320x112 card inflated to 1906x670 inside one drag.
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
            Width = (int)Math.Ceiling(WidgetCard.DesignWidthDip),
            Height = (int)Math.Ceiling(WidgetCard.DesignHeightDip),
        });
        _hwnd = _source.Handle;
        _source.RootVisual = _card;
        _source.SizeToContent = SizeToContent.WidthAndHeight;

        _source.AddHook(WndProc);
        MoveTo(PlacementStore.Clamp(start, screen, WidthOf(), HeightOf()));

        _card.MouseLeftButtonDown += OnCardMouseDown;
        _card.MouseMove += OnCardMouseMove;
        _card.MouseLeftButtonUp += OnCardMouseUp;
        _card.ToggleRequested += OnToggleRequested;
        _card.NextRequested += OnNextRequested;
        _card.PreviousRequested += OnPreviousRequested;
        _card.SourceSwitchRequested += OnSourceSwitchRequested;

        // Low priority: repainting the card must never compete with a drag. Ticks that land while a
        // refresh is still in flight are dropped rather than queued (see SmtcMediaSource).
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        // A silent start is a card nobody can see, so it does not poll at all - same reasoning as
        // HideCard. ShowCard starts it again, and refreshes immediately so the first frame is live.
        if (!startHidden) _pollTimer.Start();

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
        _card.Apply(_media.Snapshot);
    }

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
    private void RepaintNow() => _card.Apply(_media.Snapshot);

    private int WidthOf() => WidgetNative.BoundsOf(_hwnd).Width;

    private int HeightOf() => WidgetNative.BoundsOf(_hwnd).Height;

    private void MoveTo(WidgetPosition position) => WidgetNative.MoveTo(_hwnd, position.X, position.Y);

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Anything that wants the click for itself (the close button) gets it.
        if (e.OriginalSource is ButtonBase) return;

        _grabCursor = WidgetNative.CursorPosition();
        _grabBounds = WidgetNative.BoundsOf(_hwnd);
        _dragging = true;
        _card.CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var (x, y) = WidgetNative.CursorPosition();
        // Absolute tracking, not accumulated deltas: every frame recomputes from the grab point, so
        // the window cannot creep away from the cursor the way delta accumulation does.
        WidgetNative.MoveTo(_hwnd, _grabBounds.X + (x - _grabCursor.X), _grabBounds.Y + (y - _grabCursor.Y));
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _card.ReleaseMouseCapture();
        SettleAndSave();
    }

    /// <summary>Pulls the window back onto a real monitor and persists where it ended up.</summary>
    private void SettleAndSave()
    {
        var clamped = ClampToScreen();
        MoveTo(clamped);
        PlacementStore.Save(PlacementStore.DefaultFilePath, clamped);
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
        // second. The session itself stays open: showing it again is instant.
        _pollTimer.Stop();
    }

    /// <summary>
    /// Persists where the card ended up and releases the media session, ready for the process to end.
    /// Only an explicit quit reaches here.
    /// </summary>
    public void Shutdown()
    {
        _pollTimer.Stop();
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
        // the size changes for any reason - but never mid-drag, where it would fight the cursor.
        if (msg == WM_SIZE && !_dragging)
        {
            MoveTo(ClampToScreen());
        }
        return IntPtr.Zero;
    }
}
