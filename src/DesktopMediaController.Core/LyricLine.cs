namespace DesktopMediaController.Core;

/// <summary>
/// One line of lyrics, together with its syllables when the provider published them.
/// </summary>
/// <remarks>
/// <para>
/// Syllable timing is a property of <i>the line</i>, not of the document. A real QRC answer routinely
/// mixes the two — the library calls that <c>MixedSynced</c>: an instrumental intro or a spoken
/// interlude has no syllables while the sung lines either side of it do. Deciding the render mode per
/// line is what stops a handful of lines from mysteriously refusing to animate.
/// </para>
/// <para>
/// Deliberately a class rather than a record: <see cref="Syllables"/> is a list, and a record's
/// generated equality would compare it by reference while looking like it compared by value.
/// </para>
/// </remarks>
public sealed class LyricLine
{
    public LyricLine(int startMs, int endMs, string? text, IReadOnlyList<LyricSyllable>? syllables = null)
    {
        StartMs = startMs;
        EndMs = endMs;
        Text = text ?? string.Empty;
        Syllables = syllables ?? Array.Empty<LyricSyllable>();
    }

    /// <summary>Milliseconds from the start of the track.</summary>
    public int StartMs { get; }

    /// <summary>
    /// Milliseconds from the start of the track. Best-effort: providers report it for the last line
    /// only implicitly, so callers must not depend on it being the start of the next line.
    /// </summary>
    public int EndMs { get; }

    /// <summary>
    /// The whole line — always populated, whether or not syllables exist, so the fallback "highlight
    /// the line" path and any text search work unchanged on syllable-timed lyrics.
    /// </summary>
    public string Text { get; }

    /// <summary>The syllables in reading order; empty when the provider only timed the line.</summary>
    public IReadOnlyList<LyricSyllable> Syllables { get; }

    /// <summary>Whether this line can be drawn syllable by syllable.</summary>
    public bool IsSyllabic => Syllables.Count > 0;
}
