using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists the set of fence titles that are collapsed to a thin tab. Mirrors the other *Store
/// types: atomic temp-file+move and a quiet empty fallback on a missing/corrupt file. Note the
/// JSON is a plain array of titles (stored as a List&lt;string&gt;), not an object wrapper.
/// </summary>
public static class FenceCollapseStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Persists which fence titles are collapsed. A null value is treated as "clear".</summary>
    public static void Save(string filePath, IReadOnlyCollection<string>? collapsed)
    {
        var list = collapsed?.ToList() ?? new List<string>();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static IReadOnlyList<string> Load(string filePath)
    {
        if (!File.Exists(filePath)) return Array.Empty<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath), Options);
            return list is null || list.Count == 0 ? Array.Empty<string>() : list;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}