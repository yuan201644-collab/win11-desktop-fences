using System.Linq;
using System.Windows;
using DesktopMediaController.Services;
using DesktopMediaController.Widget;

namespace DesktopMediaController;

/// <summary>Entry point. There is no App.xaml: the widget is the whole application, and it is built
/// in code so nothing depends on XAML resource lookup on the startup path.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Before anything else, because the damage is done at construction time: two copies would
        // share one placement file (each overwriting the other's saved position) and the packer, which
        // finds the controller by window title, would only ever see one of them. A second launch hands
        // off to the first and exits without building a widget.
        using var instance = SingleInstance.Acquire();
        if (instance is null)
        {
            SingleInstance.SignalRunningInstance();
            return;
        }

        // Passed by the sign-in entry only: come up parked in the tray rather than throwing a card
        // onto the desktop every boot.
        var startHidden = args.Any(argument =>
            string.Equals(argument, Autostart.SilentArgument, StringComparison.OrdinalIgnoreCase));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("Dispatcher", e.Exception);
            e.Handled = true;
        };

        TrayIconHost? tray = null;
        try
        {
            // Never built visible and then hidden: that ordering leaves a layered WPF window with an
            // uninitialised render target, so a later show reports success and draws nothing.
            var widget = new WidgetWindow(startHidden);

            // Once the card is hidden the shortcut is the only route back, so make sure it exists and
            // still points at this executable — it moves whenever the widget is redeployed.
            _ = DesktopShortcut.Ensure(Environment.ProcessPath);

            widget.ExitRequested += (_, _) =>
            {
                tray?.Dispose();
                app.Shutdown();
            };

            // The card's own close button hides; quitting for real goes through the tray.
            tray = new TrayIconHost(widget, () => widget.Shutdown());
        }
        catch (Exception ex)
        {
            // Without this the process would die silently and the widget would just never appear.
            CrashLog.Write("startup", ex);
            throw;
        }

        app.Run();
    }
}
