using System.IO;
using DesktopMediaController.Core;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;

namespace DesktopMediaController.Services.Lyrics;

/// <summary>
/// Finds the lyrics for whatever is playing, across several catalogues, without ever hammering them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why several sources, in a fixed order.</b> Measured over 100 tracks from a real chart, no single
/// source is enough: 网易云 answered for 99 %, LRCLIB for 78 %, and roughly a fifth of the misses were
/// tracks LRCLIB had simply never heard of — well-known ones, with no pattern to them. So multi-source
/// is not a nicety, it is the only way to reach the coverage a user expects. The order is not
/// arbitrary either: the same measurement showed 网易云 confidently returning the <i>wrong</i> track
/// where QQ音乐 returned the right one, so the sources are ranked, not treated as equals.
/// </para>
/// <para>
/// <b>Why it prefers syllable timing over availability.</b> Only some sources publish per-syllable
/// timings (QQ音乐's QRC, 网易云's YRC). A line-level answer from the best-matching source is worth
/// less than a syllable-level answer from the second-best, so a line-level hit is held back while the
/// remaining syllable-capable sources are still asked — and is used if none of them can do better.
/// </para>
/// <para>
/// <b>Why it is careful about asking.</b> These services throttle silently: past some rate they answer
/// with a success code and an empty result, which reads exactly like "not in the catalogue". So
/// requests are spaced out, results are cached per track, and a source that has been contradicted
/// several times is benched for a while (see <see cref="SourceHealthBook"/>).
/// </para>
/// <para>
/// Live on the UI thread throughout. The work is network-bound, the caller is a timer, and being
/// single-flight means a burst of track changes cannot pile up requests behind each other.
/// </para>
/// </remarks>
internal sealed class LyricsService : IDisposable
{
    /// <summary>
    /// Best match quality first. Fixed rather than configurable: the ranking came from measurement, and
    /// a user-facing knob here would only ever be set wrong.
    /// </summary>
    private static readonly Searchers[] SourceOrder =
    {
        Searchers.QQMusic,
        Searchers.Netease,
        Searchers.LRCLIB,
    };

    /// <summary>
    /// The minimum match score worth accepting. The threshold sits deliberately below
    /// <c>PrettyHigh</c>: a faint match that is the right song beats no lyrics at all, and the measured
    /// penalty for an ambiguous artist string was 70 against a gate of 70, which is exactly why the
    /// artist is split before searching.
    /// </summary>
    private const CompareHelper.MatchType Gate = CompareHelper.MatchType.Medium;

    /// <summary>
    /// Minimum spacing between two requests to the providers. Even in normal use — one search per
    /// track change — a user skipping through an album can fire a dozen in a few seconds, and that is
    /// the shape of traffic that starts the silent throttling.
    /// </summary>
    private const int MinGapBetweenRequestsMs = 800;

    private readonly LyricsCache _cache;
    private readonly SourceHealthBook _health = new();

    /// <summary>
    /// Everything already answered for this session, including the negatives. A definitive "no lyrics"
    /// is worth remembering because it costs three network round trips to re-establish; it is kept in
    /// memory rather than on disk because it is also the answer most likely to change when a provider
    /// comes back.
    /// </summary>
    private readonly Dictionary<string, LyricsDocument> _answered = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _oneLookupAtATime = new(1, 1);

    private long _lastRequestMs;
    private bool _disposed;

    public LyricsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopMediaController",
            "lyrics"))
    {
    }

    public LyricsService(string cacheDirectory) => _cache = new LyricsCache(cacheDirectory);

    /// <summary>
    /// The lyrics for a track, from cache when possible and from the providers otherwise.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the caller moved on — a new track makes the previous lookup pointless rather than
    /// an error, so this is the one exception deliberately allowed through.
    /// </exception>
    public async Task<LyricsLookup> LookupAsync(MediaSessionInfo session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var title = session.Title?.Trim() ?? string.Empty;

        // Measured: with an empty title the searchers fall through to an artist-only comparison and
        // return a confident, unrelated track — an empty title plus "周杰伦" yields 晴天. Refusing to
        // ask is the only sound answer, because a wrong match is worse than no lyrics.
        if (title.Length == 0) return LyricsLookup.Idle;

        var key = LyricsCacheKey.For(session.Title, session.Artist);
        if (TryAnswer(key, out var memo)) return memo;

        await _oneLookupAtATime.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            // Another lookup may have answered this exact track while we queued — a user double-tapping
            // skip lands here routinely.
            if (TryAnswer(key, out memo)) return memo;

            return await SearchAsync(session, title, key, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _oneLookupAtATime.Release();
        }
    }

    private bool TryAnswer(string key, out LyricsLookup lookup)
    {
        if (_answered.TryGetValue(key, out var memo))
        {
            lookup = memo.IsEmpty
                ? new LyricsLookup(LyricsState.None, memo, string.Empty)
                : new LyricsLookup(LyricsState.Ready, memo, "cache");
            return true;
        }

        if (_cache.TryGet(key, out var cached))
        {
            _answered[key] = cached;
            lookup = new LyricsLookup(LyricsState.Ready, cached, "cache");
            return true;
        }

        lookup = LyricsLookup.Idle;
        return false;
    }

    private async Task<LyricsLookup> SearchAsync(
        MediaSessionInfo session,
        string title,
        string key,
        CancellationToken cancellationToken)
    {
        var track = new TrackMultiArtistMetadata
        {
            Title = title,
            // Split, not passed through: the artist arrives as one string with a slash in it, and
            // leaving the separator in the query costs 20 points of match score.
            Artists = ArtistNames.Split(session.Artist).ToList(),
            AlbumArtists = ArtistNames.Split(session.Artist).ToList(),
            Album = session.Album,
            DurationMs = DurationMsOf(session),
        };

        var notes = new List<string>(SourceOrder.Length);
        var contradicted = new List<Searchers>();
        var bestLineLevel = (LyricsDocument?)null;
        var bestRaw = (RawLyrics?)null;
        var bestSource = string.Empty;
        var attempted = 0;
        var benched = 0;

        foreach (var source in SourceOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nowMs = Environment.TickCount64;
            if (_health.ShouldSkip(source.ToString(), nowMs))
            {
                benched++;
                notes.Add($"{source}=benched({_health.HealthOf(source.ToString(), nowMs).CooldownRemainingMs / 1000}s)");
                continue;
            }

            // Asking a catalogue that cannot publish syllable timing is only worth doing when nothing
            // has answered at all: it cannot improve on a line-level result we already hold.
            if (bestLineLevel is not null && !CanGiveSyllables(source)) break;

            attempted++;
            var attempt = await TrySourceAsync(source, track, cancellationToken).ConfigureAwait(true);
            notes.Add(attempt.Note);

            if (!attempt.Answered)
            {
                _health.Record(source.ToString(), SourceOutcome.Failed, Environment.TickCount64);
                continue;
            }

            if (attempt.Document.IsEmpty)
            {
                // Held back rather than recorded: an empty answer from one source only means something
                // if another source found the same track. That is the whole defence against mistaking a
                // silent rate limit for "not in the catalogue".
                contradicted.Add(source);
                continue;
            }

            _health.Record(source.ToString(), SourceOutcome.Found, Environment.TickCount64);

            if (attempt.Document.HasSyllables)
            {
                return Accept(key, attempt.Document, source.ToString(), title, notes, attempt.Raw);
            }

            if (bestLineLevel is null)
            {
                bestLineLevel = attempt.Document;
                bestRaw = attempt.Raw;
                bestSource = source.ToString();
            }
        }

        foreach (var source in contradicted)
        {
            _health.Record(
                source.ToString(),
                SourceOutcome.NotFound,
                Environment.TickCount64,
                anotherSourceFound: bestLineLevel is not null);
        }

        if (bestLineLevel is not null) return Accept(key, bestLineLevel, bestSource, title, notes, bestRaw);

        // Only claim the track has no lyrics when every source actually answered. If any of them was
        // benched or failed we learned nothing about it, and saying "none" would be a lie that the
        // user cannot tell apart from the truth.
        var state = benched > 0 || attempted < SourceOrder.Length
            ? LyricsState.Unavailable
            : LyricsState.None;

        LyricsLog.Write($"“{title}” / “{session.Artist}” => {state}  [{string.Join(" | ", notes)}]");
        // Remembered in memory only, on purpose: "no lyrics" is the answer most likely to change the
        // moment a provider comes back, and persisting it would outlive the outage that caused it.
        if (state == LyricsState.None) _answered[key] = LyricsDocument.Empty;

        return new LyricsLookup(state, LyricsDocument.Empty, string.Empty);
    }

    private LyricsLookup Accept(
        string key,
        LyricsDocument document,
        string source,
        string title,
        List<string> notes,
        RawLyrics? raw)
    {
        Remember(key, source, document, raw);
        LyricsLog.Write(
            $"“{title}” => {source} {(document.HasSyllables ? "逐字" : "整行")} {document.Lines.Count} 行  "
            + $"[{string.Join(" | ", notes)}]");
        return new LyricsLookup(LyricsState.Ready, document, source);
    }

    /// <summary>
    /// Records a track's lyrics: in memory for this session, and on disk for the next one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the <b>only</b> place a found document is recorded. The disk half of this used to
    /// have no caller at all — <c>LyricsCache.Put</c> existed, was correct, and was never invoked from
    /// anywhere — so every launch re-asked the providers for the same songs. That is precisely the
    /// traffic pattern the cache was written to avoid, and these services answer it by returning a
    /// success code and an empty result, which the widget can only render as "no lyrics". Keeping both
    /// stores behind one method is what stops a later edit from adding a third way to succeed that
    /// forgets one of them.
    /// </para>
    /// <para>
    /// The raw text and its format are what gets written, not the parsed model: an upgrade of the
    /// parsing library then re-reads old entries with the new parser instead of stranding them.
    /// </para>
    /// </remarks>
    private void Remember(string key, string source, LyricsDocument document, RawLyrics? raw)
    {
        _answered[key] = document;

        // Written synchronously, on the UI thread: an entry is a few tens of kilobytes and this happens
        // once per track change, so it costs less than the frame it rides on.
        if (raw is { } value) _cache.Put(key, source, value.RawType, value.Text);
    }

    /// <summary>
    /// One source's whole answer: search, then fetch, then parse. Never throws — a source that fails is
    /// a data point, not an error.
    /// </summary>
    private async Task<SourceAttempt> TrySourceAsync(
        Searchers source,
        TrackMultiArtistMetadata track,
        CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(true);

        ISearchResult? hit;
        try
        {
            hit = await SearchHelper.Search(track, source, Gate).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return SourceAttempt.Failed($"{source}=failed({ex.GetType().Name})");
        }

        if (hit is null) return SourceAttempt.Empty($"{source}=noMatch");

        cancellationToken.ThrowIfCancellationRequested();

        RawLyrics? raw;
        try
        {
            raw = await FetchRawAsync(source, hit).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return SourceAttempt.Failed($"{source}=fetchFailed({ex.GetType().Name})");
        }

        if (raw is null) return SourceAttempt.Empty($"{source}=noLyric");

        var document = LyricifyParser.Parse(raw.Value.Text, raw.Value.RawType);
        if (document.IsEmpty) return SourceAttempt.Empty($"{source}=unparsable");

        return new SourceAttempt(
            document,
            Answered: true,
            Note: $"{source}={(document.HasSyllables ? "syllabic" : "line")}({document.Lines.Count})",
            Raw: raw.Value);
    }

    /// <summary>
    /// Fetches one source's raw lyric text, asking for the syllabic variant first.
    /// </summary>
    /// <remarks>
    /// The upstream library has no common "search result → lyrics" entry point, so this dispatch on the
    /// concrete result type is unavoidable. It is also the right home for the per-source preference for
    /// syllable timing, which is provider-specific knowledge that belongs in exactly one place.
    /// </remarks>
    private static async Task<RawLyrics?> FetchRawAsync(Searchers source, ISearchResult hit)
    {
        switch (source)
        {
            case Searchers.QQMusic when hit is QQMusicSearchResult qq:
            {
                // QRC first — it is the only reason to prefer this catalogue. The line-level LRC is the
                // fallback for the many tracks QQ音乐 has no syllable timing for.
                var qrc = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id).ConfigureAwait(true);
                if (qrc?.Lyrics is { Length: > 0 } qrcText) return new RawLyrics(qrcText, LyricsRawTypes.Qrc);

                var lrc = await ProviderHelper.QQMusicApi.GetLyric(qq.Mid).ConfigureAwait(true);
                return lrc?.Lyric is { Length: > 0 } lrcText ? new RawLyrics(lrcText, LyricsRawTypes.Lrc) : null;
            }

            case Searchers.Netease when hit is NeteaseSearchResult netease:
            {
                var result = await ProviderHelper.NeteaseApi.GetLyricNew(netease.Id).ConfigureAwait(true);
                if (result?.Yrc?.Lyric is { Length: > 0 } yrc) return new RawLyrics(yrc, LyricsRawTypes.Yrc);
                if (result?.Lrc?.Lyric is { Length: > 0 } lrc) return new RawLyrics(lrc, LyricsRawTypes.Lrc);
                return null;
            }

            case Searchers.LRCLIB when hit is LRCLIBSearchResult lrclib:
            {
                var result = await ProviderHelper.LRCLIBApi.GetById(lrclib.Id).ConfigureAwait(true);
                // Only the synced text is usable; the plain text has no timing at all, and pretending
                // otherwise would pin the whole song to line 0.
                return result?.SyncedLyrics is { Length: > 0 } synced
                    ? new RawLyrics(synced, LyricsRawTypes.Lrc)
                    : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a catalogue is able to publish per-syllable timing: QQ音乐 (QRC), 网易云 (YRC),
    /// 酷狗 (KRC). LRCLIB publishes LRC only, so it can never do better than a line.
    /// </summary>
    private static bool CanGiveSyllables(Searchers source) =>
        source is Searchers.QQMusic or Searchers.Netease or Searchers.Kugou;

    private static int? DurationMsOf(MediaSessionInfo session)
    {
        if (session.Duration <= TimeSpan.Zero) return null;
        var milliseconds = session.Duration.TotalMilliseconds;
        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        var waitMs = MinGapBetweenRequestsMs - (now - _lastRequestMs);
        if (waitMs > 0) await Task.Delay((int)waitMs, cancellationToken).ConfigureAwait(true);
        _lastRequestMs = Environment.TickCount64;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _answered.Clear();
        _oneLookupAtATime.Dispose();
    }

    private readonly record struct RawLyrics(string Text, LyricsRawTypes RawType);

    private readonly record struct SourceAttempt(
        LyricsDocument Document,
        bool Answered,
        string Note,
        RawLyrics? Raw)
    {
        public static SourceAttempt Failed(string note) => new(LyricsDocument.Empty, false, note, null);

        public static SourceAttempt Empty(string note) => new(LyricsDocument.Empty, true, note, null);
    }
}
