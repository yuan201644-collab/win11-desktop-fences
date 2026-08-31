using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Win32;

/// <summary>
/// Resolves desktop icon display-names to file paths and (.lnk) target apps.
///
/// Implementation: direct filesystem enumeration of the Desktop folder.
/// This avoids the fragile Shell COM path (SHCreateItemFromIDList / IShellItem /
/// STRRET / PIDL) which silently fails on many Windows 11 configurations and
/// can raise native AccessViolations on virtual items like Recycle Bin.
///
/// The Desktop folder is just a regular directory — .lnk files, .exe shortcuts,
/// documents, and folders all live there as normal filesystem entries. We enumerate
/// them with System.IO, match display names against ListView item text, and resolve
/// .lnk targets via late-bound WScript.Shell (managed COM — never a native AV).
/// </summary>
internal static class DesktopShellEnumerator
{
    /// <summary>
    /// Maps each desktop icon's display name to its full file-system path.
    /// For .lnk files the key is the link name (without extension); for everything
    /// else it's the filename as shown in Explorer.
    /// Returns an empty map on any failure — never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DisplayNameToPath()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) return map;

            foreach (var entry in Directory.EnumerateFileSystemEntries(desktop))
            {
                try
                {
                    // Explorer hides known file extensions, so the ListView shows "报告"
                    // for a file named "报告.txt". Key the map by BOTH the full filename
                    // and the extension-stripped name so path lookup succeeds either way.
                    var withExt = Path.GetFileName(entry);
                    var withoutExt = Path.GetFileNameWithoutExtension(withExt);
                    if (!string.IsNullOrEmpty(withoutExt) && !map.ContainsKey(withoutExt))
                        map[withoutExt] = entry;
                    if (!string.IsNullOrEmpty(withExt) && !map.ContainsKey(withExt))
                        map[withExt] = entry;
                }
                catch (Exception) { /* skip one entry */ }
            }
        }
        catch (Exception)
        {
            // Desktop folder inaccessible — degrade gracefully.
        }
        return map;
    }

    /// <summary>
    /// Resolves a desktop item path to the program it launches (.exe filename only).
    /// Used by the classifier's LinkTarget rule table. Returns null when unresolvable
    /// — the caller falls back to extension / keyword rules. Never raises a native exception.
    /// </summary>
    public static string? LinkTargetAppFromPath(string path)
    {
        try
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Late-bound WScript.Shell — the most stable shortcut resolver on Windows.
                // At worst throws a managed COMException; never a native AV.
                var wsType = Type.GetTypeFromProgID("WScript.Shell");
                if (wsType is null) return null;
                object? shell = null;
                try
                {
                    shell = Activator.CreateInstance(wsType);
                    if (shell is null) return null;
                    dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
                    if (shortcut is null) return null;
                    string? target = shortcut.TargetPath as string;
                    return string.IsNullOrWhiteSpace(target) ? null : Path.GetFileName(target);
                }
                finally
                {
                    // Release RCWs promptly — these hold COM references.
                    if (shell is IDisposable d) d.Dispose();
                    else if (shell is not null) Marshal.ReleaseComObject(shell);
                }
            }

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(path);

            // Directories: check for a known executable inside (e.g., game launchers).
            if (Directory.Exists(path))
            {
                // Common single-exe launcher patterns (Steam games, etc.)
                foreach (var exe in new[] { "game.exe", "app.exe", Path.GetFileName(path) + ".exe" })
                {
                    var candidate = Path.Combine(path, exe);
                    if (File.Exists(candidate)) return exe;
                }
            }
        }
        catch (Exception)
        {
            // Broken link, UWP app, permission error — just skip.
        }
        return null;
    }
}
