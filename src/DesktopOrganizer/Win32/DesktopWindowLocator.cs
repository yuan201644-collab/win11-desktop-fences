using System;

namespace DesktopOrganizer.Win32;

public static class DesktopWindowLocator
{
    public static IntPtr FindDesktopListView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var defView = progman != IntPtr.Zero
            ? NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;

        IntPtr workerW = IntPtr.Zero;
        if (defView == IntPtr.Zero)
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    workerW = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (workerW != IntPtr.Zero)
                defView = NativeMethods.FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (defView == IntPtr.Zero)
            throw new DesktopWindowNotFoundException("Desktop SHELLDLL_DefView not found (shell not ready?).");

        var listView = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero)
            throw new DesktopWindowNotFoundException("Desktop SysListView32 not found.");
        return listView;
    }
}
