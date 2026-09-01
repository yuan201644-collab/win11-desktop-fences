using System;
using System.Linq;

namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// Splits a user typed keyword line into the individual keywords matched by
/// <see cref="SoftwarePurposeClassifier"/>. Accepts a mix of ASCII commas/spaces/semicolons,
/// full-width comma/period, and · as separators; strips empty entries and de-duplicates.
/// </summary>
public static class KeywordsParser
{
    private static readonly char[] Separators = { ',', '、', '，', ';', '；', ' ', '\t', '·' };

    public static string[] Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToArray();
    }
}