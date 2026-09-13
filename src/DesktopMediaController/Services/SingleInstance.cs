using System.Threading;
using DesktopMediaController.Widget;
using DesktopMediaController.Win32;

namespace DesktopMediaController.Services;

/// <summary>
/// Guarantees a single controller per session.
/// </summary>
/// <remarks>
/// Two running copies are actively harmful rather than merely wasteful, which is why this is not
/// optional: they would share one <c>placement.json</c> and overwrite each other's saved position on
/// every drag, and the desktop packer — which locates the controller by its window title — would only
/// ever see whichever window <c>FindWindow</c> happened to return.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// Session-local, not <c>Global\</c>: one widget per logged-in user is the correct scope, and it
    /// avoids the privilege problems a global mutex has when another user is signed in.
    /// </summary>
    private const string MutexName = @"Local\DesktopMediaController.SingleInstance";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Takes the instance lock. Returns <c>null</c> when another copy already holds it — in which case
    /// the caller must hand off to that copy and exit rather than build a second widget.
    /// </summary>
    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);

        // We did not get ownership, so there is nothing to release; dropping the handle is enough.
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Asks the instance that won the race to put its card on screen.
    /// </summary>
    /// <remarks>
    /// Plain window messaging rather than a pipe or a heartbeat file: the widget's title is already a
    /// frozen cross-process contract, and <c>FindWindow</c> resolves a hidden window just as well as a
    /// visible one, so a widget that is sitting in the tray still answers.
    /// <para>
    /// The retry loop covers the gap where the winner holds the lock but has not created its window
    /// yet — a window that does not exist cannot be posted to, and the user's second double-click
    /// would otherwise vanish silently.
    /// </para>
    /// </remarks>
    public static void SignalRunningInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var hwnd = WidgetNative.FindByTitle(WidgetWindow.WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                WidgetNative.Post(hwnd, WidgetWindow.ShowCardMessage);
                return;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Closing the handle is the release: the OS drops ownership when the last handle to the mutex
    /// goes away, which also covers the case where the process dies without ever shutting down
    /// cleanly.
    /// </summary>
    public void Dispose() => _mutex.Dispose();
}
