using System;
using System.IO;
using System.Windows;

namespace DesktopOrganizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Crash log lives in %TEMP%\DesktopOrganizer so it survives even when the UI can't show.
    private static readonly string CrashDir = Path.Combine(Path.GetTempPath(), "DesktopOrganizer");
    private static readonly string CrashLog = Path.Combine(CrashDir, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF/UI thread exceptions — handled, app keeps running.
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash("DispatcherUnhandledException", ev.Exception);
            ev.Handled = true;
            ShowCrash(ev.Exception);
        };

        // Non-UI / background thread exceptions.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            LogCrash("AppDomain.UnhandledException", ev.ExceptionObject as Exception);
        };

        // Unobserved Task faults.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("UnobservedTaskException", ev.Exception);
            ev.SetObserved();
        };
    }

    private static void LogCrash(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(CrashDir);
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}:\n{ex}\n\n");
        }
        catch { /* best-effort */ }
    }

    private static void ShowCrash(Exception? ex)
    {
        try
        {
            MessageBox.Show(
                $"未处理的异常（已记录到 {CrashLog}）：\n\n{ex?.GetType().Name}: {ex?.Message}\n\n请把它发给我以便修复。",
                "DesktopOrganizer 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* if even MessageBox fails, the log still has it */ }
    }
}
