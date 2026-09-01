using System;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists the user's per-side fence box insets as JSON. Mirrors
/// <see cref="OverlayAppearanceStore"/>: atomic temp-file+move and a quiet fallback to the default
/// on a missing/corrupt file, so the tool never fails on a bad settings file.
/// </summary>
public static class FenceInsetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, FenceInsets insets)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(insets, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static FenceInsets Load(string filePath)
    {
        if (!File.Exists(filePath)) return FenceInsets.Default;
        try
        {
            return JsonSerializer.Deserialize<FenceInsets>(File.ReadAllText(filePath), Options)
                   ?? FenceInsets.Default;
        }
        catch (Exception)
        {
            return FenceInsets.Default;
        }
    }
}