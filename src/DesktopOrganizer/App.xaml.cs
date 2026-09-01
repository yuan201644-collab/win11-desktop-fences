using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Services;
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
            // A logon-triggered launch must never throw a dialog over the user's desktop — if we are
            // already resident in the tray, the second copy just goes away quietly.
            if (!AutoStartService.HasStartupArg(e.Args))
            {
                MessageBox.Show("「桌面图标整理」已在运行（托盘图标可打开窗口）。",
                    "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
        var settings = StartupSettingsStore.Load(StartupSettingsStore.DefaultFilePath);

        // The Run entry is a cache of the persisted switch: re-syncing it here repairs the entry
        // after the exe was moved to another folder (otherwise auto-start silently dies).
        if (settings.RunAtLogon) AutoStartService.Sync(enabled: true);

        var silent = AutoStartService.HasStartupArg(e.Args);
        if (silent && settings.LogonDelaySeconds > 0)
        {
            // Right after logon the shell's desktop ListView often isn't ready for a few seconds.
            // Waiting is free — nothing is on screen yet — and avoids scanning an empty desktop.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.LogonDelaySeconds) };
            timer.Tick += (_, _) => { timer.Stop(); OpenWindow(silent); };
            timer.Start();
        }
        else
        {
            OpenWindow(silent);
        }
    }

    /// <summary>
    /// Creates the settings window. A logon launch (<paramref name="silent"/>) deliberately does NOT
    /// show it: the tray icon is created inside <see cref="MainWindow"/> either way, so the app is
    /// fully running and reachable while staying out of the user's way.
    /// </summary>
    private void OpenWindow(bool silent)
    {
        var window = new MainWindow();
        MainWindow = window;
        if (!silent) window.Show();
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
