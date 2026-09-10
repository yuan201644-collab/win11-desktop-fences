using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopOrganizer.Core.Layout;

/// <summary>
/// A remembered rectangle for one fence box (screen pixels). When present, the layout engine
/// arranges that box's icons inside this rectangle instead of auto-packing them. Absent entries
/// mean "auto pack with the rest".
///
/// Two orthogonal flags refine what "remembered" means:
/// <list type="bullet">
/// <item><paramref name="Locked"/> freezes the box — it refuses drag and resize (its icons still
/// re-pack inside it on every arrange). A deliberate, hard decision.</item>
/// <item><paramref name="Transient"/> marks an incidental placement: a drag or resize remembers
/// the box only for the rest of the session, so the next 整理 drops it and the box re-joins the
/// auto pack. It is never persisted — <see cref="FenceLayoutStore.Save"/> filters it out. Clicking
/// the pin badge promotes a transient box to a real pin (the user said "keep this").</item>
/// </list>
/// </summary>
/// <remarks>Optional so files written by older builds (no <c>locked</c>/<c>transient</c> property)
/// load as a plain, non-transient pin.</remarks>
public sealed record FenceLayout(int X, int Y, int Width, int Height, bool Locked = false, bool Transient = false);

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

        // Transient rectangles (a drag/resize remembered for this session only) are deliberately NOT
        // persisted: only a real pin — the user clicked the badge — survives a restart. Filtering on
        // write (rather than at the call site) keeps that rule in one place.
        var persistable = layout
            .Where(kv => !kv.Value.Transient)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Write to a temp file then move so a crash mid-write never corrupts the last good layout.
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(persistable, Options));
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
