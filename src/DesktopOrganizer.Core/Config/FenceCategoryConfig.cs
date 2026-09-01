using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopOrganizer.Core.Config;

/// <summary>
/// Per-box display settings for the fence overlay: which boxes are drawn at all
/// (<see cref="Hidden"/>) and in what on-screen order they are laid out (<see cref="Order"/>).
///
/// Ordering is a preference, not a whitelist: a title missing from <see cref="Order"/> keeps its
/// natural position and is appended after the prioritized ones. A box the user never touched (or one
/// a later version introduces) therefore still shows up instead of silently vanishing.
/// </summary>
public sealed class FenceCategoryConfig
{
    /// <summary>Box titles in the order they should be laid out; unlisted titles trail behind.</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>Box titles the overlay must not draw (their icons are left where they are).</summary>
    public List<string> Hidden { get; set; } = new();

    public static FenceCategoryConfig Default => new();

    /// <summary>Case-insensitive membership test against <see cref="Hidden"/>.</summary>
    public bool IsHidden(string title) => Hidden.Contains(title, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stable-sorts <paramref name="titles"/> so the ones named in <paramref name="preferred"/> come
    /// first in that exact order; everything else follows in its original relative order. Repeated
    /// entries in <paramref name="preferred"/> count once (first occurrence wins), and blank entries
    /// are ignored so a half-finished edit can't reorder the whole desktop.
    /// </summary>
    public static IReadOnlyList<string> SortByPreference(IEnumerable<string> titles, IReadOnlyList<string>? preferred)
    {
        var list = titles.ToList();
        if (preferred is null || preferred.Count == 0) return list;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < preferred.Count; i++)
        {
            var t = preferred[i];
            if (string.IsNullOrWhiteSpace(t) || rank.ContainsKey(t)) continue;
            rank[t] = rank.Count;
        }

        // OrderBy is a stable sort, so equally-ranked titles keep their original relative order.
        return list.Where(t => rank.ContainsKey(t)).OrderBy(t => rank[t])
            .Concat(list.Where(t => !rank.ContainsKey(t)))
            .ToList();
    }
}
