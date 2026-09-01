using System;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists <see cref="FenceCategoryConfig"/> (fence box visibility + ordering) as JSON. Mirrors the
/// other *Store types: atomic temp-file+move and a quiet fallback to
/// <see cref="FenceCategoryConfig.Default"/> on a missing/corrupt file, so a bad settings file can
/// never stop the tool from starting.
/// </summary>
public static class FenceCategoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, FenceCategoryConfig config)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config ?? FenceCategoryConfig.Default, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static FenceCategoryConfig Load(string filePath)
    {
        if (!File.Exists(filePath)) return FenceCategoryConfig.Default;
        try
        {
            return JsonSerializer.Deserialize<FenceCategoryConfig>(File.ReadAllText(filePath), Options)
                   ?? FenceCategoryConfig.Default;
        }
        catch (Exception)
        {
            return FenceCategoryConfig.Default;
        }
    }
}
