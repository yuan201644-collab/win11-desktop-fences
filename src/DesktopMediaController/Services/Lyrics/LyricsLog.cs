using System.IO;

namespace DesktopMediaController.Services.Lyrics;

/// <summary>
/// A small, self-limiting log of what the lyric lookup did for each track.
/// </summary>
/// <remarks>
/// <para>
/// This is not debugging scaffolding left behind — it is the answer to a specific failure mode these
/// providers exhibit. They <b>throttle silently</b>: instead of an error they return a success code
/// with an empty result, so "no lyrics for this track" and "this source has stopped talking to us for
/// a while" look identical from the outside. A resident widget that stopped showing lyrics would give
/// the user nothing at all to go on.
/// </para>
/// <para>
/// So every lookup writes one line saying which sources were asked and what each of them said. It is
/// capped and truncated rather than rotated, because nobody reads the previous megabytes.
/// </para>
/// </remarks>
internal static class LyricsLog
{
    private const long MaxBytes = 256 * 1024;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopMediaController",
        "lyrics.log");

    internal static void Write(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);

            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A logger that can itself throw is worse than no logger at all.
        }
    }
}
