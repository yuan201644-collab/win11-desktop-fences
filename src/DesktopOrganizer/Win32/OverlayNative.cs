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
    /// Styles for a draggable fence that sits <em>behind</em> the desktop icons: layered and
    /// non-activating, but deliberately <b>not</b> mouse-transparent — the fence's blank areas
    /// must still receive clicks so it can be grabbed and dragged.
    /// </summary>
    public static void ApplyFenceStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        ex &= ~(WS_EX_APPWINDOW | WS_EX_TRANSPARENT);
        NativeMethods.SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }
}