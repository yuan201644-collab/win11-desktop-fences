using System.Text.Json;

namespace DesktopMediaController.Core;

/// <summary>
/// Persists the widget's position as JSON and keeps it reachable on the current desktop.
/// </summary>
/// <remarks>
/// Pure logic: no Win32, no WPF. The caller passes the current virtual screen rectangle, which is
/// what lets a position saved on a monitor that is no longer attached still be pulled back into
/// view instead of leaving the widget invisible off-desktop.
/// </remarks>
public static class PlacementStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// <c>%LOCALAPPDATA%\DesktopMediaController\placement.json</c>. The controller keeps its own
    /// folder rather than reusing DesktopOrganizer's: it is a separate tool with a separate
    /// lifetime, and the organizer must not have to know about it.
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopMediaController",
        "placement.json");

    /// <summary>
    /// Reads the saved position, or <c>null</c> when there is none to trust (missing file,
    /// unreadable file, malformed JSON).
    /// </summary>
    public static WidgetPosition? Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        try
        {
            if (!File.Exists(filePath)) return null;
            return JsonSerializer.Deserialize<WidgetPosition>(File.ReadAllText(filePath), Options);
        }
        catch (Exception)
        {
            // A half-written or hand-edited file must never stop the widget from starting;
            // falling back to the first-run spot is always safe.
            return null;
        }
    }

    /// <summary>Writes the position atomically (temp file + move), creating the folder as needed.</summary>
    public static void Save(string filePath, WidgetPosition position)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(position, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>
    /// Pulls <paramref name="position"/> back so the whole window fits inside
    /// <paramref name="screen"/>. A window that is larger than the screen wins over the screen's
    /// far edge (the top-left corner stays visible), and a position whose monitor is gone lands at
    /// the screen's origin rather than off-desktop.
    /// </summary>
    public static WidgetPosition Clamp(WidgetPosition position, ScreenRect screen, int widthPx, int heightPx)
    {
        var maxX = screen.Right - Math.Max(1, widthPx);
        var maxY = screen.Bottom - Math.Max(1, heightPx);
        return new WidgetPosition(
            Math.Clamp(position.X, screen.X, Math.Max(screen.X, maxX)),
            Math.Clamp(position.Y, screen.Y, Math.Max(screen.Y, maxY)));
    }
}
