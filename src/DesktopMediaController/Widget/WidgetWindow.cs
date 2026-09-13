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

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_SIZE = 0x0005;

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

    public WidgetWindow()
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
        _source = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            // WS_VISIBLE is not implied: HwndSourceParameters builds exactly the style it is given,
            // so a WS_POPUP-only window is created and stays invisible.
            WindowStyle = WS_POPUP | WS_VISIBLE,
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
        _pollTimer.Start();

        // Fire-and-forget on purpose: it swallows its own failures (a machine with SMTC unavailable
        // still gets a draggable widget) and every tick before it completes is a harmless no-op.
        _ = _media.InitializeAsync();
    }

    private async void OnPollTick(object? sender, EventArgs e)
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

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _media.Dispose();
        SettleAndSave();
        Application.Current?.Shutdown();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
