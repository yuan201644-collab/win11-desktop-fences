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
    /// Enumerates BOTH the user desktop and the public desktop (Explorer merges them into
    /// the single desktop the ListView shows). For .lnk files the key is the link name
    /// (without extension); for everything else it's the filename as shown in Explorer.
    /// When two entries share a display name, a shortcut wins over a same-named folder so
    /// an app isn't misread as a folder. Returns an empty map on any failure — never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DisplayNameToPath()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var desktop in roots)
        {
            if (!Directory.Exists(desktop)) continue;

            foreach (var entry in Directory.EnumerateFileSystemEntries(desktop))
            {
                try
                {
                    // Explorer hides known file extensions, so the ListView shows "报告"
                    // for a file named "报告.txt". Key the map by BOTH the full filename
                    // and the extension-stripped name so path lookup succeeds either way.
                    AddName(map, entry, Path.GetFileNameWithoutExtension(entry));
                    AddName(map, entry, Path.GetFileName(entry));
                }
                catch (Exception) { /* skip one entry */ }
            }
        }
        return map;
    }

    private static void AddName(IDictionary<string, string> map, string entry, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!map.TryGetValue(name, out var existing))
        {
            map[name] = entry;
            return;
        }
        // Collision (e.g. a shortcut on the public desktop collides with a same-named folder on
        // the user desktop). A shortcut is an application; a folder is not — prefer the software
        // entry so an app isn't read as a folder.
        if (IsSoftwareEntry(entry) && !IsSoftwareEntry(existing))
            map[name] = entry;
    }

    private static bool IsSoftwareEntry(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "lnk" or "exe" or "url" or "com" or "msi" or "bat" or "cmd" or "appref-ms";
    }

    /// <summary>
    /// Resolves a desktop item path to the program it launches (.exe filename only, or the raw
    /// URL for an internet shortcut). Used by the classifier's LinkTarget rule table. Returns null
    /// when unresolvable — the caller falls back to extension / keyword rules. Never raises a
    /// native exception.
    ///
    /// Results are memoised for the process lifetime and the cache is dropped once per arrange
    /// (see <see cref="ClearLinkTargetCache"/>). Without this, a single arrange resolved the same
    /// shortcuts once per pass — 2 + 2·(pinned boxes) passes, because <c>ArrangeOneFence</c> used to
    /// re-classify the whole desktop for every pinned box — so the UI thread spent tens of seconds
    /// creating a fresh WScript.Shell per call (2026-09-10 "整理卡死" incident: 11 pinned boxes ×
    /// 126 icons × 36 shortcuts ≈ 864 COM resolutions, all synchronous on the UI thread).
    /// </summary>
    public static string? LinkTargetAppFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);
        var resolved = ResolveLinkTargetApp(path);
        Cache[path] = resolved; // cache misses too — a broken link is the most expensive kind
        return resolved;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _cacheHits;
    private static int _cacheMisses;

    /// <summary>Resolution attempts that actually touched the shell/disk (test hook).</summary>
    internal static int CacheMisses => Volatile.Read(ref _cacheMisses);

    /// <summary>Resolutions served from the memo (test hook).</summary>
    internal static int CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>Drops the memo. Called once per arrange so a re-targeted shortcut is re-read, while
    /// every pass inside that arrange still shares one resolution per path.</summary>
    internal static void ClearLinkTargetCache()
    {
        Cache.Clear();
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
    }

    private static string? ResolveLinkTargetApp(string path)
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

            // Internet shortcuts are plain INI files — reading "URL=" costs microseconds and no
            // COM at all. Without this an .url resolves to null, so its only keyword haystack was
            // the display name and every ".url" game/web shortcut fell into the 其他软件 fallback
            // box (2026-09-10: Terraria.url / To the Moon.url).
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return UrlTargetFromFile(path);

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

    /// <summary>Reads the <c>URL=</c> line of an internet shortcut (.url, INI format). Returns the
    /// raw URL so the classifier can keyword-match the site/launcher (steam://, bilibili, ...).</summary>
    private static string? UrlTargetFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return string.IsNullOrWhiteSpace(url) ? null : url;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable/oddly encoded .url — fall back to name-only classification.
        }
        return null;
    }
}
