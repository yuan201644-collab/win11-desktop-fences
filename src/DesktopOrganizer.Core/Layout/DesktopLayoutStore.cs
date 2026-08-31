using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Layout;

/// <summary>
/// Persists a desktop-icon layout: a map of icon display-name → top-left screen position.
/// Used so a freely-adjusted arrangement (category clusters, then any manual tweaks) survives
/// restarts. Keys are matched case-insensitively because desktop item names are case-insensitive.
/// Pure I/O, no P/Invoke — unit-tested.
/// </summary>
public static class DesktopLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, IReadOnlyDictionary<string, PointI> layout)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file then move so a crash mid-write never corrupts the last good layout.
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(layout, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static IReadOnlyDictionary<string, PointI> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new Dictionary<string, PointI>();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, PointI>>(File.ReadAllText(filePath), Options)
                      ?? new Dictionary<string, PointI>();
            return new Dictionary<string, PointI>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Missing/corrupt file should never stop the tool — treat as "no saved layout".
            return new Dictionary<string, PointI>();
        }
    }
}