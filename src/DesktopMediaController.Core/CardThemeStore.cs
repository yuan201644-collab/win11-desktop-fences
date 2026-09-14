using System.Text.Json;

namespace DesktopMediaController.Core;

/// <summary>
/// Persists the card's chosen color theme as JSON, in the controller's own data folder (next to
/// placement.json). Same durability contract as the placement store: atomic write, and a
/// missing/corrupt file quietly yields the default theme. Pure I/O, unit-tested.
/// </summary>
public static class CardThemeStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary><c>%LOCALAPPDATA%\DesktopMediaController\theme.json</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopMediaController",
        "theme.json");

    public static void Save(string filePath, CardTheme theme)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(theme, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static CardTheme Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        try
        {
            if (!File.Exists(filePath)) return CardTheme.Default;
            var theme = JsonSerializer.Deserialize<CardTheme>(File.ReadAllText(filePath), Options);
            return theme ?? CardTheme.Default;
        }
        catch (Exception)
        {
            // A half-written or hand-edited file must never stop the widget from starting.
            return CardTheme.Default;
        }
    }
}
