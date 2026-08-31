using System;
using System.Text;

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

internal static class DesktopShellStatus
{
    private const int ClassNameCapacity = 256;

    /// <summary>
    /// True when the overlay may stay visible: the desktop shell is in front, or the
    /// foreground window belongs to our own process (so it stays up right after the
    /// user clicks "整理并显示分组" while our control window is still focused). The
    /// overlay hides only when an actual third-party app takes the foreground.
    /// </summary>
    public static bool IsOverlayAllowed()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;

        // Our own control window being focused must not dismiss the overlay.
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return true;

        var buf = new StringBuilder(ClassNameCapacity);
        if (NativeMethods.GetClassName(fg, buf, ClassNameCapacity) <= 0) return false;
        var cls = buf.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow";
    }
}