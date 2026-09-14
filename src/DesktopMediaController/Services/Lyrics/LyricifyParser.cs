using DesktopMediaController.Core;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;

namespace DesktopMediaController.Services.Lyrics;

/// <summary>
/// Turns whatever a lyric provider published into the model this widget renders.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> place that knows the upstream library exists. Everything downstream deals in
/// <see cref="LyricsDocument"/>, which is what keeps the dependency swappable — and the upstream
/// dependency is one that will need bumping, because it talks to services that change.
/// </para>
/// <para>
/// Note that the provider's own format vocabulary (QRC, YRC, KRC, LRC, TTML) stops here. The widget
/// decides how to draw a line by asking whether <i>that line</i> has syllables, never by asking which
/// service it came from.
/// </para>
/// </remarks>
internal static class LyricifyParser
{
    /// <summary>
    /// Parses provider text. Returns <see cref="LyricsDocument.Empty"/> for anything unusable rather
    /// than throwing: the text came off the network, and a malformed answer is a normal outcome.
    /// </summary>
    public static LyricsDocument Parse(string? raw, LyricsRawTypes rawType)
    {
        if (string.IsNullOrWhiteSpace(raw)) return LyricsDocument.Empty;

        LyricsData? data;
        try
        {
            data = ParseHelper.ParseLyrics(raw, rawType);
        }
        catch (Exception ex)
        {
            CrashLog.Write("lyrics-parse", ex);
            return LyricsDocument.Empty;
        }

        // Unsynced lyrics carry no timing at all, so there is nothing to scroll. Accepting them would
        // pile the entire song onto line 0 and hold it there, which looks worse than saying so.
        if (data?.File?.SyncTypes == SyncTypes.Unsynced) return LyricsDocument.Empty;
        if (data?.Lines is not { Count: > 0 }) return LyricsDocument.Empty;

        var lines = new List<LyricLine>(data.Lines.Count);
        for (var i = 0; i < data.Lines.Count; i++)
        {
            var line = data.Lines[i];
            var start = line.StartTime ?? 0;

            // EndTime is whatever the provider happened to publish, and plenty of providers publish
            // none at all. Borrowing the next line's start is the honest reading of "this line lasts
            // until the next one".
            var end = line.EndTime ?? StartOfFollowingLine(data.Lines, i) ?? start;
            if (end < start) end = start;

            lines.Add(new LyricLine(start, end, line.Text, ReadSyllables(line)));
        }

        EnsureAscending(lines);
        return new LyricsDocument(lines);
    }

    private static IReadOnlyList<LyricSyllable>? ReadSyllables(ILineInfo line)
    {
        if (line is not SyllableLineInfo { IsSyllable: true } syllableLine) return null;

        var source = syllableLine.Syllables;
        var syllables = new LyricSyllable[source.Count];
        for (var i = 0; i < syllables.Length; i++)
        {
            var syllable = source[i];
            var start = syllable.StartTime;
            var end = syllable.EndTime;

            // Text is declared non-nullable upstream but is only assigned by the parser, so a syllable
            // that arrived with no text is entirely possible.
            syllables[i] = new LyricSyllable(syllable.Text ?? string.Empty, start, end > start ? end : start);
        }

        return syllables;
    }

    private static int? StartOfFollowingLine(List<ILineInfo> lines, int index)
    {
        for (var i = index + 1; i < lines.Count; i++)
        {
            if (lines[i].StartTime is { } start) return start;
        }

        return null;
    }

    /// <summary>
    /// The timeline scans lines in order and stops at the first one that starts later, so an out-of-order
    /// line would silently hide every line after it.
    /// </summary>
    private static void EnsureAscending(List<LyricLine> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].StartMs >= lines[i - 1].StartMs) continue;
            lines.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
            return;
        }
    }
}
