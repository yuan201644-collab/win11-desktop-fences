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
}
