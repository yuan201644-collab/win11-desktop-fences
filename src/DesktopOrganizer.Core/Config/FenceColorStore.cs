using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists per-box color overrides (title → <see cref="OverlayAppearance"/>). A box with no entry
/// falls back to the global palette (<see cref="OverlayAppearanceStore"/>). Same durability contract
/// as the other stores: atomic write, and a missing/corrupt file quietly yields an empty map.
/// Keys are case-insensitive to match box-title matching everywhere else. Pure I/O, unit-tested.
/// </summary>
public static class FenceColorStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, IReadOnlyDictionary<string, OverlayAppearance> colors)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(colors, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static IReadOnlyDictionary<string, OverlayAppearance> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new Dictionary<string, OverlayAppearance>();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, OverlayAppearance>>(File.ReadAllText(filePath), Options)
                      ?? new Dictionary<string, OverlayAppearance>();
            return new Dictionary<string, OverlayAppearance>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, OverlayAppearance>();
        }
    }
}
