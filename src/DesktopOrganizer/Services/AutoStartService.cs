using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace DesktopOrganizer.Services;

/// <summary>
/// Windows "run at logon" registration for this app, via HKCU\Software\Microsoft\Windows\
/// CurrentVersion\Run. HKCU (not HKLM) is deliberate: it needs no elevation, and it is the
/// per-user location Windows itself shows in Task Manager → Startup apps.
///
/// The registered command always carries <see cref="StartupArg"/>. Without it a logon launch would
/// open the settings window right on top of the user's desktop; with it the app boots straight into
/// the tray, which is where a background desktop tool belongs.
///
/// The registry is treated as a *cache* of the persisted <see cref="StartupSettings.RunAtLogon"/>
/// switch, not as the source of truth: every launch re-syncs it, so moving the exe to a new folder
/// repairs itself instead of silently stopping the auto-start.
/// </summary>
public static class AutoStartService
{
    public const string StartupArg = "--startup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopOrganizer";

    /// <summary>The command line stored in the Run key: the quoted exe path plus the tray flag.</summary>
    internal static string BuildCommand(string exePath) => $"\"{exePath}\" {StartupArg}";

    /// <summary>True when this launch came from the Run key (i.e. should stay out of the way).</summary>
    internal static bool HasStartupArg(IReadOnlyList<string> args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, StartupArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "/" + StartupArg.TrimStart('-'), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The currently registered command, or null when nothing is registered.</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            // A locked/absent registry hive must never stop the app — report "not registered".
            return null;
        }
    }

    public static bool IsEnabled() => CurrentCommand() is not null;

    /// <summary>
    /// Makes the Run key match <paramref name="enabled"/>. When enabling it also refreshes a stale
    /// path (exe moved / renamed), so the entry always points at the copy that is running.
    /// Returns false if the registry could not be written (the caller surfaces that to the user).
    /// </summary>
    public static bool Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var exe = CurrentExePath();
            if (string.IsNullOrEmpty(exe)) return false;

            var want = BuildCommand(exe);
            if (key.GetValue(ValueName) as string == want) return true;
            key.SetValue(ValueName, want, RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Path of the running executable. <see cref="Environment.ProcessPath"/> is used first because it
    /// stays correct for single-file publishes; the module lookup is the pre-.NET-5 fallback.
    /// </summary>
    public static string? CurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p)) return p;
        try { return Process.GetCurrentProcess().MainModule?.FileName; }
        catch (Exception) { return null; }
    }
}
