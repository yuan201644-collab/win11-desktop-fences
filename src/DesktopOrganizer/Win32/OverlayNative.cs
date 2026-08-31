using System;

namespace DesktopOrganizer.Win32;

/// <summary>
/// Win32 helpers for the transparent click-through overlay fence window.
/// Lives in the Win32 layer so no raw P/Invoke leaks into UI code.
/// </summary>
internal static class OverlayNative
{
    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_APPWINDOW = 0x00040000;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>
    /// Makes the window both transparent to mouse input (clicks fall through to the
    /// desktop icons below) and non-activating, hidden from Alt-Tab. Render still
    /// covers the screen via WPF's layered/compositor path — not GDI+.
    /// </summary>
    public static void ApplyOverlayStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        ex &= ~WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }
}