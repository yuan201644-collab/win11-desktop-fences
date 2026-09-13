using System.IO;

namespace DesktopMediaController.Services;

/// <summary>
/// The desktop shortcut — the only way back to a card the user has hidden, which is why the widget
/// makes sure it exists rather than leaving it to the user to create.
/// </summary>
/// <remarks>
/// Uses Windows Script Host's <c>WScript.Shell</c> through late binding instead of declaring the
/// <c>IShellLink</c> / <c>IPersistFile</c> COM interfaces. It is the same mechanism the shell's own
/// "Create shortcut" verb uses, needs no interop declarations and no elevation, and it can read an
/// existing link back — which the raw interfaces cannot do without far more code.
/// </remarks>
internal static class DesktopShortcut
{
    private const string ShortcutFileName = "媒体控制器.lnk";

    /// <summary>Full path of the shortcut this class owns.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);

    public static bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Points the shortcut at <paramref name="executablePath"/>, creating it if absent and repairing it
    /// if it has drifted (the target moves whenever the widget is redeployed). Returns whether a
    /// shortcut now exists, so the caller can report rather than assume.
    /// </summary>
    public static bool Ensure(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return false;
        if (Exists() && PointsAt(executablePath)) return true;

        dynamic? link = Open(FilePath);
        if (link is null) return false;

        try
        {
            link.TargetPath = executablePath;
            link.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            link.Description = "桌面媒体控制器";
            link.IconLocation = executablePath;
            link.Save();
        }
        catch (Exception)
        {
            // A failed shell hand-off must not take the widget down with it; the tray icon is still a
            // perfectly good way in.
            return false;
        }

        return Exists();
    }

    private static bool PointsAt(string executablePath)
    {
        try
        {
            dynamic? link = Open(FilePath);
            return link is not null && string.Equals(
                (string?)link.TargetPath, executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unreadable link is, for our purposes, the same as a missing one: rewrite it.
            return false;
        }
    }

    /// <summary>Opens a shortcut for editing through Windows Script Host.</summary>
    private static dynamic? Open(string? shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;

        dynamic shell = Activator.CreateInstance(shellType)!;
        return shell.CreateShortcut(shortcutPath);
    }
}
