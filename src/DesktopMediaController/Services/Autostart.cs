using Microsoft.Win32;

namespace DesktopMediaController.Services;

/// <summary>
/// The "start with Windows" entry.
/// </summary>
/// <remarks>
/// <c>HKCU</c> rather than <c>HKLM</c> for two reasons: it never needs elevation, and an entry in the
/// per-user Run key shows up in Task Manager's Startup tab, so the user can disable it there even if
/// the tray menu is unavailable. The stored command carries <see cref="SilentArgument"/> so that
/// signing in leaves the widget parked in the tray instead of throwing a card onto the desktop.
/// </remarks>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key. Stable — it is what the user sees in Task Manager.</summary>
    private const string ValueName = "DesktopMediaController";

    /// <summary>
    /// Launch flag meaning "come up hidden, tray only". Choosing a command-line flag rather than
    /// inspecting the parent process keeps the two entry points explicit: the sign-in entry passes it,
    /// a double-clicked shortcut does not, and neither has to guess.
    /// </summary>
    public const string SilentArgument = "--silent";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string command && command.Length > 0;
    }

    /// <summary>Registers <paramref name="executablePath"/> to launch hidden at sign-in.</summary>
    public static void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(ValueName, $"\"{executablePath}\" {SilentArgument}", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
