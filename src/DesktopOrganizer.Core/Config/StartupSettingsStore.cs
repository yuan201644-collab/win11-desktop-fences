using System;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Persists <see cref="StartupSettings"/> (logon auto-start switch + logon delay) as JSON.
/// Mirrors the other *Store types: atomic temp-file+move and a quiet default on a missing/corrupt
/// file — a broken settings file must never stop the app from starting.
/// </summary>
public static class StartupSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "startup.json");

    public static void Save(string filePath, StartupSettings settings)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalized(), Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static StartupSettings Load(string filePath)
    {
        if (!File.Exists(filePath)) return StartupSettings.Default;
        try
        {
            return (JsonSerializer.Deserialize<StartupSettings>(File.ReadAllText(filePath), Options)
                    ?? StartupSettings.Default).Normalized();
        }
        catch (Exception)
        {
            return StartupSettings.Default;
        }
    }
}
