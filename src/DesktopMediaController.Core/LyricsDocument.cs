namespace DesktopMediaController.Core;

/// <summary>
/// A whole set of lyrics in the only shape the card needs: lines in reading order, and whether any of
/// them carries syllable timing.
/// </summary>
/// <remarks>
/// This is where the provider's vocabulary stops: nothing downstream knows about QRC, YRC, KRC, LRC,
/// <c>MixedSynced</c> or which service the text came from. A document either has lines or it does not.
/// </remarks>
public sealed class LyricsDocument
{
    /// <summary>
    /// A document with no lines. The card renders this as "暂无歌词" — deliberately distinct from
    /// having no document at all, which renders as "searching".
    /// </summary>
    public static readonly LyricsDocument Empty = new(Array.Empty<LyricLine>());

    public LyricsDocument(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Lines = lines;

        foreach (var line in lines)
        {
            if (!line.IsSyllabic) continue;
            HasSyllables = true;
            break;
        }
    }

    /// <summary>The lines, ordered by start time.</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    /// Whether <i>any</i> line can be drawn syllable by syllable. False is not an error: an LRC-only
    /// source is a normal, expected outcome (LRCLIB has no other kind).
    /// </summary>
    public bool HasSyllables { get; }

    public bool IsEmpty => Lines.Count == 0;
}
