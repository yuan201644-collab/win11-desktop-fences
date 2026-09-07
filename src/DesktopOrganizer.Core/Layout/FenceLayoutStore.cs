using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopOrganizer.Core.Layout;

/// <summary>
/// A user-pinned rectangle for one fence box (screen pixels). When present, the layout engine
/// arranges that box's icons inside this rectangle instead of auto-packing them — so a box the
/// user resized or moved keeps its shape across re-arranges and restarts. Absent entries mean
/// "auto pack with the rest". <paramref name="Locked"/> additionally freezes the box: a locked
/// box refuses drag and resize (its icons still re-pack inside it on every arrange).
/// </summary>
/// <remarks>Optional so files written by older builds (no <c>locked</c> property) load as unlocked.</remarks>
public sealed record FenceLayout(int X, int Y, int Width, int Height, bool Locked = false);

/// <summary>
/// The three states a box can be in, cycled by clicking the header badge:
/// <see cref="Auto"/> re-packs with everything else on the next arrange;
/// <see cref="Pinned"/> keeps this rectangle (still draggable and resizable);
/// <see cref="Locked"/> keeps it and refuses to be dragged or resized at all.
/// </summary>
public enum FencePinMode
{
    Auto,
    Pinned,
    Locked,
}

/// <summary>
/// Persists the per-box pinned rectangles as JSON (title → <see cref="FenceLayout"/>). Mirrors
/// <see cref="DesktopLayoutStore"/>: atomic write via temp-file+move, and a missing/corrupt file
/// quietly yields an empty map instead of ever failing the tool. Keys are case-insensitive to
/// match how box titles are matched everywhere else. Pure I/O, no P/Invoke — unit-tested.
/// </summary>
public static class FenceLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string filePath, IReadOnlyDictionary<string, FenceLayout> layout)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file then move so a crash mid-write never corrupts the last good layout.
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(layout, Options));
        File.Move(tmp, filePath, overwrite: true);
    }

    public static IReadOnlyDictionary<string, FenceLayout> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new Dictionary<string, FenceLayout>();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, FenceLayout>>(File.ReadAllText(filePath), Options)
                      ?? new Dictionary<string, FenceLayout>();
            return new Dictionary<string, FenceLayout>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Missing/corrupt file should never stop the tool — treat as "no pinned rectangles".
            return new Dictionary<string, FenceLayout>();
        }
    }
}
