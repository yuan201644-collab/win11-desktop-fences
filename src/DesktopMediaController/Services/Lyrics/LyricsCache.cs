using System.IO;
using System.Text.Json;
using DesktopMediaController.Core;
using Lyricify.Lyrics.Models;

namespace DesktopMediaController.Services.Lyrics;

/// <summary>
/// Keeps fetched lyrics on disk, keyed by track, so a song is fetched once and not once per play.
/// </summary>
/// <remarks>
/// <para>
/// The cache is not an optimisation, it is part of staying inside the providers' tolerance. They
/// throttle under repeated querying — silently, with a success code and an empty result — and a widget
/// that re-asked for the same song every time the user skipped back to it would be a reliable way to
/// trigger that.
/// </para>
/// <para>
/// What is stored is the provider's <b>raw text</b> plus the format to read it as, not the parsed
/// model. That keeps the file tiny, and it means an upgrade of the parsing library re-interprets the
/// cache with the new parser instead of leaving old entries stranded in an old shape.
/// </para>
/// </remarks>
internal sealed class LyricsCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _directory;

    public LyricsCache(string directory) => _directory = directory;

    /// <summary>
    /// Reads and parses a cached entry. False for a miss, an unreadable file, or an entry that no
    /// longer parses into anything — all of which simply mean "go and ask".
    /// </summary>
    public bool TryGet(string key, out LyricsDocument document)
    {
        document = LyricsDocument.Empty;

        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return false;

            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Options);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Raw)) return false;
            if (!Enum.TryParse<LyricsRawTypes>(entry.RawType, out var rawType)) return false;

            document = LyricifyParser.Parse(entry.Raw, rawType);
            return !document.IsEmpty;
        }
        catch (Exception)
        {
            // A corrupt cache entry must never be more than a slow path.
            return false;
        }
    }

    /// <summary>Stores an entry. Failures are swallowed: a cache that cannot write is still a working widget.</summary>
    public void Put(string key, string source, LyricsRawTypes rawType, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            Directory.CreateDirectory(_directory);
            var entry = new Entry(source, rawType.ToString(), raw);
            File.WriteAllText(PathFor(key), JsonSerializer.Serialize(entry, Options));
        }
        catch (Exception)
        {
            // Ignored on purpose.
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, LyricsCacheKey.FileNameFor(key));

    private sealed record Entry(string Source, string RawType, string Raw);
}
