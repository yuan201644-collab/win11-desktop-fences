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
/// One lyric line reduced to what the karaoke renderer needs: the text, and where the playhead sits
/// inside it — both measured in <i>text elements</i>, not UTF-16 code units.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the whole line plus two counts, rather than the sung / current / remaining substrings
/// this used to carry. The substrings existed so the card could hand each piece to its own
/// <c>Run</c>; but assigning to a <c>Run</c>'s text invalidates the measure, so the line re-flowed
/// sideways every time the boundary moved by one character — the "characters jumping" the arrangement
/// was meant to avoid. Counts let the card build one inline per character up front and afterwards
/// touch nothing but their colour, which invalidates the render only.
/// </para>
/// <para>
/// The three substrings are still derivable — slice <see cref="Text"/> at the character offsets the
/// syllables give — so nothing that needs the old view has lost it.
/// </para>
/// </remarks>
/// <param name="Text">The whole line, exactly as the provider published it.</param>
/// <param name="ElementCount">How many reader-visible characters <paramref name="Text"/> has.</param>
/// <param name="SungElements">How many of them the playhead has already passed.</param>
/// <param name="CurrentElements">
/// How many are being sung right now. The whole line when the provider only timed the line, which is
/// how "highlight the whole line" is expressed without a second code path anywhere else.
/// </param>
public readonly record struct LyricLinePaint(
    string Text,
    int ElementCount,
    int SungElements,
    int CurrentElements)
{
    /// <summary>Nothing to draw — used for blank and instrumental lines.</summary>
    public static readonly LyricLinePaint Empty = new(string.Empty, 0, 0, 0);

    /// <summary>Whether the playhead has moved past the whole line.</summary>
    public bool IsFullySung => SungElements + CurrentElements >= ElementCount;
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
    /// Resolves a line and a cursor into the counts the renderer paints from.
    /// </summary>
    /// <remarks>
    /// A line with no syllables reports the whole line as current and nothing as sung. That is the
    /// "highlight the whole line" degradation, and expressing it through the same two counters is what
    /// lets the card run one code path: it colours what is sung one way, what is current another, and a
    /// line-level source simply ends up with all of it in the second bucket.
    /// </remarks>
    public static LyricLinePaint Split(LyricLine line, LyricCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(line);

        var text = line.Text;
        var total = TextElements.Count(text);

        if (!line.IsSyllabic) return new LyricLinePaint(text, total, 0, total);

        var count = Math.Clamp(cursor.SungSyllableCount, 0, line.Syllables.Count);
        if (count == 0) return new LyricLinePaint(text, total, 0, 0);

        // Syllable lengths are UTF-16 lengths because that is the coordinate system the provider cut
        // its own text in; they are converted to text elements on the way out.
        var sungEnd = 0;
        for (var i = 0; i < count - 1; i++) sungEnd += line.Syllables[i].Text.Length;

        // Clamped rather than trusted, because "the syllables reproduce the text exactly" is what
        // providers are supposed to guarantee, not what they do — and this runs inside a paint
        // callback, where a provider contradicting itself must not be able to take the card down.
        var start = Math.Clamp(sungEnd, 0, text.Length);
        var end = Math.Clamp(start + line.Syllables[count - 1].Text.Length, start, text.Length);

        var (sung, current) = TextElements.CountAt(text, start, end);
        return new LyricLinePaint(text, total, sung, current);
    }
}
