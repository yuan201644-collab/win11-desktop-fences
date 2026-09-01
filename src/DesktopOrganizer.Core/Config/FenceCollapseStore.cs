using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// One collapsed fence. Collapsing now means the cluster's *real icons* are parked off-screen
/// (only a thin tab remains on the desktop), so the record carries the tab rectangle plus each
/// icon's pre-collapse position (keyed by display name) so expanding restores the desktop exactly.
/// </summary>
public sealed record CollapsedFenceRecord(
    string Title,
    RectI Tab,
    IReadOnlyDictionary<string, PointI> Icons);

/// <summary>
/// Persists which fences are collapsed to a thin tab. Mirrors the other *Store types:
/// atomic temp-file+move and a quiet empty fallback on a missing/corrupt file.
/// The JSON is a plain array of <see cref="CollapsedFenceRecord"/> (not an object wrapper).
/// </summary>
public static class FenceCollapseStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Persists collapsed fences (tab rect + hidden icon positions). A null value is treated as "clear".</summary>
    public static void Save(string filePath, IReadOnlyCollection<CollapsedFenceRecord>? records)
    {
        var list = records?.ToList() ?? new List<CollapsedFenceRecord>();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>
    /// Loads collapsed fences. Tolerates the pre-v1.2 plain string-array format: those entries
    /// come back as records with a default <see cref="CollapsedFenceRecord.Tab"/> and empty
    /// <see cref="CollapsedFenceRecord.Icons"/> — callers treat them as "not collapsed" and drop them.
    /// </summary>
    public static IReadOnlyList<CollapsedFenceRecord> Load(string filePath)
    {
        if (!File.Exists(filePath)) return Array.Empty<CollapsedFenceRecord>();
        var json = SafeRead(filePath);
        if (json is null) return Array.Empty<CollapsedFenceRecord>();

        try
        {
            var records = JsonSerializer.Deserialize<List<CollapsedFenceRecord>>(json, Options);
            if (records is not null) return records;
        }
        catch (Exception)
        {
            // not the record format — try the legacy string-array below
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<string>>(json, Options);
            if (legacy is null) return Array.Empty<CollapsedFenceRecord>();
            return legacy
                .Select(t => new CollapsedFenceRecord(t, default, new Dictionary<string, PointI>()))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<CollapsedFenceRecord>();
        }
    }

    private static string? SafeRead(string filePath)
    {
        try { return File.ReadAllText(filePath); }
        catch (Exception) { return null; }
    }
}
