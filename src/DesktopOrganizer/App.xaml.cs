using System;
using System.IO;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DesktopOrganizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Crash log lives in %TEMP%\DesktopOrganizer so it survives even when the UI can't show.
    private static readonly string CrashDir = Path.Combine(Path.GetTempPath(), "DesktopOrganizer");
    private static readonly string CrashLog = Path.Combine(CrashDir, "crash.log");

    private const string MutexName = "DesktopOrganizer_SingleInstance";
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one instance may talk to the desktop ListView and draw the overlay. A second
        // launch (double-click / re-running the shortcut) would fight over icon positions and
        // stack two overlays, which made the desktop appear frozen. Detect it via a named mutex.
        // An abandoned mutex (previous instance crashed) still counts as owning it.
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("「桌面图标整理」已在运行。请先关闭已打开的窗口。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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

        // StartupUri was removed so we can enforce single-instance first; build the window here.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        _mutex?.Dispose();
        base.OnExit(e);
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
