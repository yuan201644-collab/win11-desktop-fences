using System;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists the user's fence-overlay colors as JSON. Mirrors <see cref="DesktopOrganizer.Core.Layout.DesktopLayoutStore"/>:
/// atomic write via temp-file+move, and a missing/corrupt file quietly yields the default palette
/// instead of ever failing the tool. Pure I/O, no P/Invoke — unit-tested.
/// </summary>
public static class OverlayAppearanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, OverlayAppearance appearance)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(appearance, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static OverlayAppearance Load(string filePath)
    {
        if (!File.Exists(filePath)) return OverlayAppearance.Default;
        try
        {
            return JsonSerializer.Deserialize<OverlayAppearance>(File.ReadAllText(filePath), Options)
                   ?? OverlayAppearance.Default;
        }
        catch (Exception)
        {
            // Missing/corrupt file should never stop the tool — fall back to the default palette.
            return OverlayAppearance.Default;
        }
    }
}