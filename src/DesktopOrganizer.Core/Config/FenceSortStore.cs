using System;
using System.IO;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists the chosen <see cref="FenceSortMode"/>. Mirrors the other *Store types: atomic
/// temp-file+move and a quiet fallback to <see cref="FenceSortMode.Name"/> on a missing/corrupt file.
/// </summary>
public static class FenceSortStore
{
    public static void Save(string filePath, FenceSortMode mode)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, mode.ToString());
        File.Move(tmp, filePath, overwrite: true);
    }

    public static FenceSortMode Load(string filePath)
    {
        if (!File.Exists(filePath)) return FenceSortMode.Name;
        try
        {
            var raw = File.ReadAllText(filePath).Trim();
            return Enum.TryParse(raw, out FenceSortMode mode) ? mode : FenceSortMode.Name;
        }
        catch (Exception)
        {
            return FenceSortMode.Name;
        }
    }
}