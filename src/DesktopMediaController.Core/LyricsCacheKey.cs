using System.Security.Cryptography;
using System.Text;

namespace DesktopMediaController.Core;

/// <summary>
/// A stable identity for "the lyrics of this track", used to cache them between plays.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on title and artist, and deliberately <i>not</i> on duration: the same recording is routinely
/// reported with a slightly different length by different players, and including it would store the
/// same lyrics twice under two keys while never reusing either.
/// </para>
/// <para>
/// The cache is what makes the network traffic acceptable. A track the user plays repeatedly is
/// fetched once, and — just as important — a track is never re-fetched just because the widget was
/// restarted, which is the difference between a few requests an hour and a few hundred.
/// </para>
/// </remarks>
public static class LyricsCacheKey
{
    /// <summary>Joined with a character no real title contains, so (A+B, C) cannot collide with (A, B+C).</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>The cache identity for a track: case-folded, whitespace-collapsed title and artist.</summary>
    public static string For(string? title, string? artist) =>
        Normalize(title) + FieldSeparator + Normalize(artist);

    /// <summary>
    /// A file name for the key. Hashed rather than escaped because titles legitimately contain slashes,
    /// colons and question marks — all of which are either illegal in a Windows file name or would
    /// quietly turn one cache entry into a nested directory.
    /// </summary>
    public static string FileNameFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash) + ".json";
    }

    /// <summary>
    /// Trims, collapses internal whitespace and case-folds. Full-width and half-width punctuation are
    /// left alone: the searchers treat them as the same thing anyway, and folding them here would make
    /// the key disagree with the text it was derived from.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
