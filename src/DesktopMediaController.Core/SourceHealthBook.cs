namespace DesktopMediaController.Core;

/// <summary>What came back the last time a lyric source was asked for a track.</summary>
public enum SourceOutcome
{
    /// <summary>The source answered and had the track.</summary>
    Found,

    /// <summary>
    /// The source answered, and had nothing. <b>Ambiguous on purpose</b> — see
    /// <see cref="SourceHealthBook.Record"/> for why this is not by itself evidence of anything.
    /// </summary>
    NotFound,

    /// <summary>The source did not answer at all: a timeout, a DNS failure, a transport error.</summary>
    Failed,
}

/// <summary>How a source is currently behaving.</summary>
/// <param name="ConsecutiveFailures">Unbroken run of transport failures.</param>
/// <param name="ConsecutiveEmptyAnswers">Unbroken run of empty answers that a rival source contradicted.</param>
/// <param name="CooldownRemainingMs">Milliseconds until the source is worth asking again; 0 when it is not benched.</param>
public readonly record struct SourceHealth(
    int ConsecutiveFailures,
    int ConsecutiveEmptyAnswers,
    long CooldownRemainingMs)
{
    public bool IsCoolingDown => CooldownRemainingMs > 0;
}

/// <summary>
/// Remembers how each lyric source has been behaving, so the widget stops hammering one that has
/// quietly stopped answering.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of the single most expensive discovery made while measuring these sources: they
/// <b>throttle silently</b>. Under repeated querying, QQ音乐 and 酷狗 answer <c>HTTP 200</c> with a
/// success code and an <i>empty result set</i> — no error, no 429, no exception. To any caller that is
/// indistinguishable from "this track is not in the catalogue", which is how a rate limit becomes a
/// permanent "no lyrics" that nobody can explain.
/// </para>
/// <para>
/// So an empty answer is only treated as evidence when it is <b>contradicted</b>: if another source
/// found the very same track, then this one's silence is about this source, not about the track. Three
/// such contradictions in a row bench it for a while. That asymmetry is what keeps the policy from
/// punishing a source merely for being asked about obscure music.
/// </para>
/// <para>
/// Being benched is never permanent and never fatal — the remaining sources carry the load, and the
/// cooldown expires on its own.
/// </para>
/// </remarks>
public sealed class SourceHealthBook
{
    /// <summary>Contradicted empty answers before a source is benched.</summary>
    public const int EmptyAnswersBeforeCooldown = 3;

    /// <summary>How long a source sits out after <see cref="EmptyAnswersBeforeCooldown"/> contradictions.</summary>
    public const long NotFoundCooldownMs = 10 * 60 * 1000;

    /// <summary>Cooldown after the first transport failure; doubles with each further one.</summary>
    public const long FirstFailureCooldownMs = 30 * 1000;

    /// <summary>Ceiling on the transport-failure backoff.</summary>
    public const long MaxFailureCooldownMs = 5 * 60 * 1000;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="source"/> is currently benched and should not be asked.</summary>
    public bool ShouldSkip(string source, long nowMs) => HealthOf(source, nowMs).IsCoolingDown;

    /// <summary>The source's current state, for logging and for tests.</summary>
    public SourceHealth HealthOf(string source, long nowMs)
    {
        if (source is null || !_entries.TryGetValue(source, out var entry)) return default;

        var remaining = entry.SkipUntilMs - nowMs;
        return new SourceHealth(
            entry.ConsecutiveFailures,
            entry.ConsecutiveEmptyAnswers,
            remaining > 0 ? remaining : 0);
    }

    /// <summary>Records what happened, updating the source's backoff.</summary>
    /// <param name="source">The source's identifier.</param>
    /// <param name="outcome">What came back.</param>
    /// <param name="nowMs">The current monotonic tick, in milliseconds.</param>
    /// <param name="anotherSourceFound">
    /// Whether a different source found lyrics for the same track. Only consulted for
    /// <see cref="SourceOutcome.NotFound"/>, and only this flag makes an empty answer count.
    /// </param>
    public void Record(string source, SourceOutcome outcome, long nowMs, bool anotherSourceFound = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_entries.TryGetValue(source, out var entry))
        {
            entry = new Entry();
            _entries[source] = entry;
        }

        switch (outcome)
        {
            case SourceOutcome.Found:
                entry.ConsecutiveFailures = 0;
                entry.ConsecutiveEmptyAnswers = 0;
                entry.SkipUntilMs = 0;
                break;

            case SourceOutcome.Failed:
                entry.ConsecutiveEmptyAnswers = 0;
                entry.ConsecutiveFailures++;
                entry.SkipUntilMs = nowMs + FailureCooldown(entry.ConsecutiveFailures);
                break;

            case SourceOutcome.NotFound:
                // It answered, so it is reachable — whatever was wrong, it was not the network.
                entry.ConsecutiveFailures = 0;
                if (!anotherSourceFound) break;

                entry.ConsecutiveEmptyAnswers++;
                if (entry.ConsecutiveEmptyAnswers >= EmptyAnswersBeforeCooldown)
                {
                    entry.ConsecutiveEmptyAnswers = 0;
                    entry.SkipUntilMs = nowMs + NotFoundCooldownMs;
                }
                break;
        }
    }

    /// <summary>Clears every source's history — used when the user explicitly asks for a retry.</summary>
    public void ForgetAll() => _entries.Clear();

    /// <summary>
    /// 30 s, 60 s, 2 min, 4 min, then capped. Doubling rather than a flat penalty because the two
    /// causes look identical from here — a transient blip and a source that has gone down — and the
    /// cost of guessing wrong is asymmetric: waiting too long means lyrics that do not appear.
    /// </summary>
    private static long FailureCooldown(int consecutiveFailures)
    {
        var steps = Math.Min(consecutiveFailures - 1, 8);
        var cooldown = FirstFailureCooldownMs << steps;
        return Math.Min(cooldown, MaxFailureCooldownMs);
    }

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public int ConsecutiveEmptyAnswers;
        public long SkipUntilMs;
    }
}
