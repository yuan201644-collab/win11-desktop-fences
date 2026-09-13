using System.Runtime.InteropServices;

namespace DesktopMediaController.Win32;

/// <summary>
/// Raw P/Invoke for the controller widget. Like the organizer, this project keeps <b>every</b>
/// native call inside <c>Win32/</c> and exposes typed wrappers (<see cref="WidgetNative"/>) to the
/// widget layer, so no <c>DllImport</c> ever appears in UI or logic code.
/// </summary>
internal static class NativeMethods
{
    // GetSystemMetrics indices for the virtual screen (spans every monitor; origin may be negative).
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOACTIVATE = 0x0010;

    /// <summary>HWND_TOPMOST for <see cref="SetWindowPos"/>.</summary>
    internal static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);
}
