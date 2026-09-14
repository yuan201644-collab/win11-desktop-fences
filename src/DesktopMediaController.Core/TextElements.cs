using System.Globalization;

namespace DesktopMediaController.Core;

/// <summary>
/// Counts and slices text by <i>text element</i> rather than by UTF-16 code unit.
/// </summary>
/// <remarks>
/// <para>
/// The karaoke renderer gives every character its own inline, writes that inline's text once and then
/// only ever changes its colour. That contract only holds if "one character" means what a reader sees:
/// splitting on <see cref="char"/> cuts a surrogate pair down the middle, so an emoji in a lyric line
/// becomes two replacement glyphs — or a lone half that the text engine refuses to lay out at all.
/// Chinese lyrics rarely contain emoji, but songs whose lines do are not rare, and the failure would
/// be silent and would only show up on the lines that happen to contain one.
/// </para>
/// <para>
/// A text element is a grapheme cluster: one base character plus any combining marks, and a surrogate
/// pair taken whole. <see cref="StringInfo.GetNextTextElementLength(ReadOnlySpan{char})"/> is the
/// framework's own implementation of that rule and works on a span, so measuring costs no allocations.
/// </para>
/// </remarks>
public static class TextElements
{
    /// <summary>How many reader-visible characters <paramref name="text"/> contains.</summary>
    public static int Count(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            text = text[StringInfo.GetNextTextElementLength(text)..];
            count++;
        }

        return count;
    }

    /// <summary>
    /// How many reader-visible characters begin before <paramref name="charStart"/>, and how many
    /// begin in <c>[charStart, charEnd)</c>. The two counts are disjoint, which is what lets them be
    /// used directly as "already sung" and "being sung".
    /// </summary>
    /// <remarks>
    /// The whole line is walked in one pass and each element is classified by where it starts, rather
    /// than measuring the two substrings separately. That keeps the answer exact even when a boundary
    /// falls inside an element — which can only happen when a provider disagrees with itself about its
    /// own text, and which would otherwise leave the counters and the renderer's own split out of step
    /// for the rest of the line.
    /// </remarks>
    public static (int Before, int Within) CountAt(string? text, int charStart, int charEnd)
    {
        var before = 0;
        var within = 0;
        var offset = 0;
        var length = text?.Length ?? 0;

        while (offset < length)
        {
            var element = StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            if (offset < charStart) before++;
            else if (offset < charEnd) within++;
            offset += element;
        }

        return (before, within);
    }

    /// <summary>
    /// The text split into one string per reader-visible character, which is the form the renderer
    /// needs: each of these becomes one inline that is never rewritten.
    /// </summary>
    public static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var elements = new List<string>(text.Length);
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(rest);
            elements.Add(rest[..length].ToString());
            rest = rest[length..];
        }

        return elements.ToArray();
    }
}
