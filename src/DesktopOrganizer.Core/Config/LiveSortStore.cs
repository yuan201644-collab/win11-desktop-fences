using System;
using System.IO;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists the LiveSort toggle ("new icons auto-filed into their fence"). Mirrors the other
/// *Store types: atomic temp-file+move and a quiet fallback (disabled) on a missing/corrupt file.
/// Defaults to OFF — the tool must not move the user's icons without an explicit opt-in.
/// </summary>
public static class LiveSortStore
{
    public const bool Default = false;

    public static void Save(string filePath, bool enabled)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, enabled ? "true" : "false");
        File.Move(tmp, filePath, overwrite: true);
    }

    public static bool Load(string filePath)
    {
        if (!File.Exists(filePath)) return Default;
        try
        {
            var raw = File.ReadAllText(filePath).Trim();
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return Default;
        }
    }
}
