namespace DesktopMediaController.Core;

/// <summary>
/// One syllable of a syllable-timed lyric line: the unit QQ音乐's QRC, 网易云's YRC and 酷狗's KRC all
/// publish, and the only thing that makes karaoke highlighting possible at all.
/// </summary>
/// <remarks>
/// Times are milliseconds from the start of the track — which is what every provider reports, so
/// nothing is converted anywhere between the network and the paint.
/// <para>
/// A line-level LRC has no equivalent of this: it timestamps the whole line and nothing smaller. That
/// is a limitation of the format, not of the reader, which is why the card has to be able to render
/// both and decide per line (see <see cref="LyricLine.IsSyllabic"/>).
/// </para>
/// </remarks>
/// <param name="Text">
/// The syllable. Usually a single ideograph, but a Latin run is published as one syllable per piece,
/// so it can be any short string — including a bare space, which is what keeps the rebuilt line's
/// spacing intact.
/// </param>
/// <param name="StartMs">Milliseconds from the start of the track.</param>
/// <param name="EndMs">Milliseconds from the start of the track; always greater than the start.</param>
public readonly record struct LyricSyllable(string Text, int StartMs, int EndMs)
{
    /// <summary>How long the syllable is held, in milliseconds.</summary>
    public int DurationMs => EndMs - StartMs;
}
