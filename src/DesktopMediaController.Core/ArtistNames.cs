namespace DesktopMediaController.Core;

/// <summary>
/// Splits the single artist string the media player hands over into the individual names the lyric
/// searchers expect.
/// </summary>
/// <remarks>
/// <para>
/// SMTC publishes exactly one artist string. QQ音乐 packs collaborations into it with a slash —
/// <c>周杰伦/费玉清</c> — and measured against a real chart, one track in nine is a collaboration.
/// Handing that whole string to a searcher leaves the separator inside the query and drags the match
/// score down to the very edge of the acceptance threshold (measured: 70/100 unsplit against 90/100
/// split, where the gate is 70). So the split is not cosmetic; it is the difference between a
/// borderline match and a comfortable one.
/// </para>
/// <para>
/// The separator set deliberately follows the library we consume — its own LRCLIB reader splits on
/// <c>", "</c>, <c>" &amp; "</c>, <c>" feat. "</c> and <c>" ft. "</c> — because a name that arrives
/// already split must not be split differently by us than by the thing doing the matching.
/// </para>
/// <para>
/// Note what is <i>not</i> a separator: a bare ampersand. It is a divider between artists on Chinese
/// platforms but part of a single band's name in Western music, and the measured traffic includes a
/// meaningful share of English tracks.
/// </para>
/// </remarks>
public static class ArtistNames
{
    /// <summary>Separators that stand on their own, with no surrounding spaces required.</summary>
    private static readonly char[] BareSeparators = { '/', '／', '、', ';', '；', ',', '，', '｜', '|', '×' };

    /// <summary>Separators that only count as dividers when they are set off by spaces.</summary>
    private static readonly string[] SpacedSeparators = { "&", "feat.", "feat", "ft.", "ft", "vs.", "vs" };

    /// <summary>
    /// The individual artist names in <paramref name="artist"/>, trimmed, with blanks dropped. An
    /// artist string with no separators comes back as a single-element list; a blank one comes back
    /// empty.
    /// </summary>
    public static IReadOnlyList<string> Split(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<string>();

        var names = new List<string>(2);
        var start = 0;

        for (var i = 0; i < artist.Length; i++)
        {
            var length = SeparatorLengthAt(artist, i);
            if (length == 0) continue;

            Add(names, artist.AsSpan(start, i - start));
            i += length - 1;
            start = i + 1;
        }

        Add(names, artist.AsSpan(start));
        return names;
    }

    /// <summary>Renders names back into one string for display, using the platform's own divider.</summary>
    public static string Join(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(" / ", names);
    }

    private static void Add(List<string> names, ReadOnlySpan<char> candidate)
    {
        var trimmed = candidate.Trim();
        if (!trimmed.IsEmpty) names.Add(trimmed.ToString());
    }

    /// <summary>
    /// Length of the separator starting at <paramref name="index"/>, or 0 when there is none. Always
    /// consumes any spaces that belong to it, so the surviving name fragments never keep a stray edge.
    /// </summary>
    private static int SeparatorLengthAt(string text, int index)
    {
        if (Array.IndexOf(BareSeparators, text[index]) >= 0) return 1;

        if (text[index] != ' ') return 0;

        // Either " & " — a spaced ampersand — or " feat. ", " ft " and friends. In both cases the
        // divider is the token between two spaces, and matching the trailing space keeps "feat." from
        // swallowing the start of a name that merely begins with those letters.
        var tokenStart = index + 1;
        var tokenEnd = text.IndexOf(' ', tokenStart);
        if (tokenEnd < 0) return 0;

        var token = text.AsSpan(tokenStart, tokenEnd - tokenStart);
        foreach (var separator in SpacedSeparators)
        {
            if (token.Equals(separator, StringComparison.OrdinalIgnoreCase)) return tokenEnd - index + 1;
        }

        return 0;
    }
}
