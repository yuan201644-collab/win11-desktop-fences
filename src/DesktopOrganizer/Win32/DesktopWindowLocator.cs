using System;

namespace DesktopOrganizer.Win32;

public static class DesktopWindowLocator
{
    public static IntPtr FindDesktopListView() => FindDesktopHost().ListView;

    /// <summary>
    /// Resolves the desktop shell windows needed to embed a fence behind the icons:
    /// the SHELLDLL_DefView (the desktop's icon container, itself inside a WorkerW/Progman)
    /// and the SysListView32 (the icon view). Reparenting a fence window under <see cref="DesktopHost.DefView"/>
    /// and inserting it before <see cref="DesktopHost.ListView"/> (z-order) puts the fence behind the icons.
    /// </summary>
    public static DesktopHost FindDesktopHost()
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
        return new DesktopHost(defView, listView);
    }

    public readonly record struct DesktopHost(IntPtr DefView, IntPtr ListView);
}
