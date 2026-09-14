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
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOZORDER = 0x0004;
    internal const int SWP_NOACTIVATE = 0x0010;

    /// <summary>nCmdShow for <see cref="ShowWindow"/>: hide without destroying the window.</summary>
    internal const int SW_HIDE = 0;

    /// <summary>nCmdShow for <see cref="ShowWindow"/>: show without stealing focus from the player.</summary>
    internal const int SW_SHOWNOACTIVATE = 4;

    /// <summary>HWND_TOPMOST for <see cref="SetWindowPos"/>: the band of windows above everything.</summary>
    internal static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// HWND_NOTOPMOST for <see cref="SetWindowPos"/>: leaves the topmost band, clearing
    /// <c>WS_EX_TOPMOST</c> while keeping the window's place among normal windows. This — not
    /// restyling — is how the pin is undone.
    /// </summary>
    internal static readonly IntPtr HwndNotTopmost = new(-2);

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

    /// <summary>
    /// Hides or shows the window without destroying it, so a hidden card keeps its handle — which is
    /// what lets a second launch find it and what tells the packer the card is not on screen.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Ground truth for "is the card on screen". The packer must test this and not mere existence:
    /// a hidden window is still enumerable.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Finds a top-level window by title. Works on a hidden window, which is the point.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>Used to hand a "surface the card" request to the already-running instance.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
