namespace DesktopMediaController.Core;

/// <summary>
/// Where the playhead sits inside a lyric document, expressed as the two indices the card paints
/// from. Integers only, so it can be recomputed every rendered frame without allocating anything.
/// </summary>
/// <param name="LineIndex">Index into <see cref="LyricsDocument.Lines"/>, or −1 before the first line.</param>
/// <param name="SungSyllableCount">
/// How many syllables of that line have already started. Meaningless when <see cref="LineIndex"/> is
/// −1 or the line is not syllabic.
/// </param>
public readonly record struct LyricCursor(int LineIndex, int SungSyllableCount)
{
    /// <summary>
    /// Before the first line has started — an instrumental intro, or the last few seconds of the
    /// previous track. Nothing is highlighted.
    /// </summary>
    public static readonly LyricCursor BeforeFirstLine = new(-1, 0);

    public bool HasLine => LineIndex >= 0;
}

/// <summary>
/// One lyric line split into the three pieces the karaoke renderer needs: everything the playhead has
/// already passed, the syllable it is on now, and everything still to come.
/// </summary>
/// <param name="Sung">Text before the current syllable.</param>
/// <param name="Current">
/// The syllable being sung, or the whole line when the provider only timed the line.
/// </param>
/// <param name="Remaining">Text after the current syllable.</param>
public readonly record struct LyricLinePaint(string Sung, string Current, string Remaining)
{
    /// <summary>Nothing to draw — used for blank and instrumental lines.</summary>
    public static readonly LyricLinePaint Empty = new(string.Empty, string.Empty, string.Empty);

    /// <summary>Whether the playhead has moved past the whole line.</summary>
    public bool IsFullySung => Remaining.Length == 0;
}

/// <summary>
/// The three lines the card shows at once: the one being sung, with its neighbours for context.
/// </summary>
/// <remarks>
/// Produced here rather than assembled in the card so the awkward edges — the intro before the first
/// line, the very last line having no successor, a one-line document — are settled by tests instead of
/// by whichever branch happens to be written first.
/// </remarks>
/// <param name="Previous">The line above, or empty when there is none.</param>
/// <param name="Current">The active line, split into sung / current / remaining.</param>
/// <param name="Next">The line below, or empty when there is none.</param>
/// <param name="CurrentIndex">Index of the active line, or −1 before the first line.</param>
public readonly record struct LyricsWindow(string Previous, LyricLinePaint Current, string Next, int CurrentIndex)
{
    /// <summary>Used when there are no lyrics to show at all.</summary>
    public static readonly LyricsWindow Empty = new(string.Empty, LyricLinePaint.Empty, string.Empty, -1);

    /// <summary>False during an instrumental intro, when the first line is still ahead.</summary>
    public bool HasCurrent => CurrentIndex >= 0;
}

/// <summary>
/// Maps a playhead position onto a lyric document. The only place that knows how lines and syllables
/// turn into "what is highlighted right now".
/// </summary>
/// <remarks>
/// Everything here is a pure function of (document, milliseconds). That keeps the interesting cases —
/// a position before the first line, a line with no syllables, a position past the end of the track —
/// pinned down by tests instead of being discovered as a rendering glitch, and it means the render
/// loop can call straight into it from a frame callback.
/// </remarks>
public static class LyricTimeline
{
    /// <summary>
    /// Index of the line that should be highlighted at <paramref name="positionMs"/>, or −1 when the
    /// position is before the first line starts.
    /// </summary>
    /// <remarks>
    /// Assumes lines are ordered by start time, which every parser we consume guarantees; the scan
    /// stops at the first line that starts later, so a song's worth of lines costs a handful of
    /// comparisons per frame.
    /// </remarks>
    public static int LineIndexAt(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var found = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartMs > positionMs) break;
            found = i;
        }

        return found;
    }

    /// <summary>
    /// How many of <paramref name="line"/>'s syllables have started at <paramref name="positionMs"/>.
    /// A syllable counts as soon as it starts, so the moment a line begins its first syllable counts
    /// as being sung rather than as "nothing yet".
    /// </summary>
    public static int SungSyllableCountAt(LyricLine line, int positionMs)
    {
        ArgumentNullException.ThrowIfNull(line);

        var count = 0;
        for (var i = 0; i < line.Syllables.Count; i++)
        {
            if (line.Syllables[i].StartMs > positionMs) break;
            count++;
        }

        return count;
    }

    /// <summary>Locates the playhead in one pass, for the render loop.</summary>
    public static LyricCursor Locate(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        var index = LineIndexAt(lines, positionMs);
        return index < 0
            ? LyricCursor.BeforeFirstLine
            : new LyricCursor(index, SungSyllableCountAt(lines[index], positionMs));
    }

    /// <summary>
    /// The three lines to show at <paramref name="positionMs"/>, already resolved to text and paint.
    /// </summary>
    /// <remarks>
    /// Before the first line the "next" slot holds line 0, which is what makes an intro read as
    /// "something is coming" rather than as an empty box.
    /// </remarks>
    public static LyricsWindow WindowAt(LyricsDocument document, int positionMs)
    {
        ArgumentNullException.ThrowIfNull(document);

        var lines = document.Lines;
        var index = LineIndexAt(lines, positionMs);

        if (index < 0)
        {
            return new LyricsWindow(
                string.Empty,
                LyricLinePaint.Empty,
                lines.Count > 0 ? lines[0].Text : string.Empty,
                -1);
        }

        var line = lines[index];
        var paint = Split(line, new LyricCursor(index, SungSyllableCountAt(line, positionMs)));

        return new LyricsWindow(
            index > 0 ? lines[index - 1].Text : string.Empty,
            paint,
            index + 1 < lines.Count ? lines[index + 1].Text : string.Empty,
            index);
    }

    /// <summary>
    /// Splits a line into sung / current / remaining.
    /// </summary>
    /// <remarks>
    /// A line with no syllables puts its whole text in <see cref="LyricLinePaint.Current"/> and leaves
    /// the other two empty. That is the "highlight the whole line" degradation, and expressing it as
    /// the same three-field shape is what lets the card run one code path: it colours
    /// <c>Current</c> as active and the others as inactive, and a line-level source simply ends up
    /// with nothing in them.
    /// </remarks>
    public static LyricLinePaint Split(LyricLine line, LyricCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.IsSyllabic) return new LyricLinePaint(string.Empty, line.Text, string.Empty);

        var count = Math.Clamp(cursor.SungSyllableCount, 0, line.Syllables.Count);
        if (count == 0) return new LyricLinePaint(string.Empty, string.Empty, line.Text);

        var sungEnd = 0;
        for (var i = 0; i < count - 1; i++) sungEnd += line.Syllables[i].Text.Length;

        var current = line.Syllables[count - 1].Text;

        // Sliced rather than concatenated, because the line's text is what the provider published and
        // the syllables are supposed to reproduce it exactly; slicing keeps that guarantee visible
        // instead of quietly overwriting it. Clamped because "supposed to" is not "does", and a
        // provider that disagrees with itself must not be able to throw out of a paint callback.
        var start = Math.Clamp(sungEnd, 0, line.Text.Length);
        var end = Math.Clamp(start + current.Length, start, line.Text.Length);

        return new LyricLinePaint(line.Text[..start], current, line.Text[end..]);
    }
}
