using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists per-box edge-padding overrides (title → <see cref="FenceInsets"/>). A box with no entry
/// falls back to the global insets (<see cref="FenceInsetStore"/>). Same durability contract as the
/// other stores: atomic write, and a missing/corrupt file quietly yields an empty map. Keys are
/// case-insensitive to match box-title matching everywhere else. Pure I/O, unit-tested.
/// </summary>
public static class FenceBoxInsetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, IReadOnlyDictionary<string, FenceInsets> insets)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(insets, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static IReadOnlyDictionary<string, FenceInsets> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new Dictionary<string, FenceInsets>();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, FenceInsets>>(File.ReadAllText(filePath), Options)
                      ?? new Dictionary<string, FenceInsets>();
            return new Dictionary<string, FenceInsets>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, FenceInsets>();
        }
    }
}
