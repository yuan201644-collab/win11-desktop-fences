using DesktopMediaController.Core;

namespace DesktopMediaController.Win32;

/// <summary>
/// Typed window operations for the widget. This is the only place the widget layer talks to the OS,
/// which keeps the coordinate story in one spot: <b>everything here is physical pixels</b>, the same
/// space <see cref="WidgetPosition"/> is stored in, so no DIP conversion is ever needed to place the
/// window. (DIPs only matter for the card's own layout, which WPF scales from the window's DPI.)
/// </summary>
internal static class WidgetNative
{
    /// <summary>The bounding rectangle of every monitor, in physical pixels.</summary>
    internal static ScreenRect VirtualScreen() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    internal static ScreenRect BoundsOf(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return new ScreenRect(0, 0, 0, 0);
        return new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Moves the window without touching its size.</summary>
    internal static void MoveTo(IntPtr hwnd, int x, int y) =>
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>Current cursor position in physical screen pixels (the drag's source of truth).</summary>
    internal static (int X, int Y) CursorPosition() =>
        NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>
    /// Hides or shows the card. Hiding keeps the window and its handle alive — the process, the SMTC
    /// poll and the frozen title all survive — it only drops out of sight. Returning the card uses
    /// <c>SW_SHOWNOACTIVATE</c> so clicking the tray icon never pulls focus away from the player.
    /// </summary>
    internal static void SetVisible(IntPtr hwnd, bool visible) =>
        _ = NativeMethods.ShowWindow(hwnd, visible
            ? NativeMethods.SW_SHOWNOACTIVATE
            : NativeMethods.SW_HIDE);

    /// <summary>Whether the window is actually on screen right now.</summary>
    internal static bool IsVisible(IntPtr hwnd) => NativeMethods.IsWindowVisible(hwnd);

    /// <summary>
    /// Locates the widget by its frozen title, for the single-instance hand-off. Deliberately
    /// <c>FindWindow</c> rather than an enumeration that filters on visibility: a hidden card must
    /// still be reachable, or the tray icon would be the only way back.
    /// </summary>
    internal static IntPtr FindByTitle(string title) => NativeMethods.FindWindow(null, title);

    /// <summary>Hands a custom message to another window of ours; never blocks.</summary>
    internal static void Post(IntPtr hwnd, int message) =>
        _ = NativeMethods.PostMessage(hwnd, (uint)message, IntPtr.Zero, IntPtr.Zero);
}
