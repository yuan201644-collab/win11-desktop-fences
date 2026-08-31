using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// One software "收纳框": a display title plus the keywords (matched case-insensitively
/// against the shortcut's display name and resolved target exe) that put software into it.
/// Kept in a user-editable JSON file, so the box layout is data, not code.
/// </summary>
/// <summary>
/// A mutable class (not a record) so System.Text.Json can round-trip it: serialization
/// holds the seeded exact list, and a hand-edited JSON file deserializes back into the
/// concrete <see cref="List{T}"/> with no interface-typed constructor parameter in the way.
/// </summary>
public sealed class SoftwareGroup
{
    public string Title { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();

    public SoftwareGroup() { }

    /// <summary>Seed convenience — accepts any sequence so callers can pass a string[] literal.</summary>
    public SoftwareGroup(string title, IEnumerable<string> keywords)
    {
        Title = title;
        Keywords = keywords.ToList();
    }
}

public sealed class SoftwareGroupingConfig
{
    /// <summary>Ordered software boxes. Earlier groups win when a keyword matches more than one.</summary>
    public List<SoftwareGroup> Groups { get; set; } = new();
}

/// <summary>
/// Classifies a software shortcut into one of the configured purpose boxes (or the fallback
/// "其他软件"). Pure and unit-tested; the boxes come from <see cref="SoftwareGroupingConfig"/>.
/// </summary>
public static class SoftwarePurposeClassifier
{
    public const string FallbackTitle = "其他软件";

    public static string Classify(SoftwareGroupingConfig config, string? name, string? linkTarget)
    {
        var hay = string.Concat(name ?? string.Empty, " ", linkTarget ?? string.Empty).ToLowerInvariant();
        foreach (var group in config.Groups)
            foreach (var kw in group.Keywords)
                if (hay.Contains(kw, StringComparison.Ordinal)) return group.Title;
        return FallbackTitle;
    }

    /// <summary>Display order index for a box title; the fallback sits after all configured groups.</summary>
    public static int OrderOf(SoftwareGroupingConfig config, string title)
    {
        var i = config.Groups.FindIndex(g => g.Title == title);
        return i < 0 ? config.Groups.Count : i;
    }
}