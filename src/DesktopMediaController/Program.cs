using System.Windows;
using DesktopMediaController.Services;
using DesktopMediaController.Widget;

namespace DesktopMediaController;

/// <summary>Entry point. There is no App.xaml: the widget is the whole application, and it is built
/// in code so nothing depends on XAML resource lookup on the startup path.</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("Dispatcher", e.Exception);
            e.Handled = true;
        };

        try
        {
            _ = new WidgetWindow();
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
