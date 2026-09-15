using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopMediaController.Core;

/// <summary>
/// Reads the source allow-list from JSON, in the controller's own data folder (next to placement.json
/// and theme.json), and writes a commented default file on first run so the list is discoverable.
/// </summary>
/// <remarks>
/// Same durability contract as the other stores: a missing, half-written or hand-mangled file quietly
/// yields <see cref="MediaSourceFilter.Default"/> rather than stopping the widget. The file is read
/// once at startup — there is no file watcher, so an edit takes effect on the next launch. Watching it
/// would mean either a hot path of file checks per poll or a change notification that fires while the
/// user is still typing, and neither buys anything on a list that changes a few times a year.
/// </remarks>
public static class SourceFilterStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // The default file ships with comments explaining itself, and a hand-edited one will pick up
        // trailing commas from whoever wrote it. Both are read rather than rejected.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary><c>%LOCALAPPDATA%\DesktopMediaController\sources.json</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopMediaController",
        "sources.json");

    /// <summary>
    /// Loads the filter, creating the commented default file first if there is none. This is what
    /// startup calls: a fresh install ends up with an editable list instead of an invisible rule.
    /// </summary>
    public static MediaSourceFilter LoadOrCreate(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        try
        {
            if (!File.Exists(filePath)) WriteDefaultFile(filePath);
        }
        catch (Exception)
        {
            // Not being able to leave a file behind (read-only folder, no permission) is no reason to
            // refuse to run; the in-memory defaults still apply.
        }

        return Load(filePath);
    }

    /// <summary>
    /// Reads the allow-list, or <see cref="MediaSourceFilter.Default"/> when there is nothing to trust.
    /// </summary>
    public static MediaSourceFilter Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        try
        {
            if (!File.Exists(filePath)) return MediaSourceFilter.Default;

            var document = JsonSerializer.Deserialize<SourceFilterDocument>(
                File.ReadAllText(filePath), Options);

            if (document is null) return MediaSourceFilter.Default;

            // An absent or empty "preferred" is a broken edit, not an instruction to stop showing
            // music — it would leave the card permanently blank with no clue why. Keep the defaults
            // and honour the other half of the file. An empty "fallback", by contrast, is a perfectly
            // reasonable thing to mean: the user does not want browsers on the card at all.
            var preferred = document.Preferred is { Count: > 0 }
                ? document.Preferred
                : MediaSourceFilter.DefaultPreferred;

            var fallback = document.Fallback ?? MediaSourceFilter.DefaultFallback;

            return new MediaSourceFilter(preferred, fallback);
        }
        catch (Exception)
        {
            return MediaSourceFilter.Default;
        }
    }

    /// <summary>Writes a filter out as plain JSON, atomically (temp file + move).</summary>
    public static void Save(string filePath, MediaSourceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var document = new SourceFilterDocument
        {
            Preferred = [.. filter.Preferred],
            Fallback = [.. filter.Fallback],
        };

        WriteAtomic(filePath, JsonSerializer.Serialize(document, Options) + Environment.NewLine);
    }

    /// <summary>
    /// Writes the shipped lists, with the comments that explain what they are for. Order is taken from
    /// the defaults rather than from the filter's sets, so the file a user opens for the first time
    /// reads in the order it was designed in instead of hash order.
    /// </summary>
    private static void WriteDefaultFile(string filePath)
    {
        var document = new SourceFilterDocument
        {
            Preferred = [.. MediaSourceFilter.DefaultPreferred],
            Fallback = [.. MediaSourceFilter.DefaultFallback],
        };

        WriteAtomic(filePath, Header + JsonSerializer.Serialize(document, Options) + Environment.NewLine);
    }

    private static void WriteAtomic(string filePath, string contents)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>
    /// The comment block the default file opens with. Written by hand rather than generated because it
    /// is documentation for a human, and it is the only place the rules are stated in the file itself.
    /// </summary>
    private const string Header = """
// 控制器只自动控制下面两栏里的软件，其他媒体一律不自动控制（但可以点卡片上的来源标签手动切过去）。
//
//   preferred  音乐软件：有它们在播时优先显示。
//   fallback   降权软件（浏览器）：只有在 preferred 一个都没在播时，才轮到它们。
//   两栏之外的软件（抖音、视频播放器、任何新装的软件）永远不会被自动选中。
//
// 改完保存，重启控制器生效（不会自动重新读取）。
//
// 每项是软件的完整 ID，大小写不敏感，不做关键词匹配：
//   · 普通桌面软件写进程名，如 qqmusic.exe、foobar2000.exe；
//   · 应用商店（UWP）软件写 SourceAppUserModelId，可能长这样：
//     electron.app.douyin、appleinc.applemusic_nzyj5cx40ttqa!app。
// 想知道某个软件叫什么，把它切到卡片上，来源标签显示的名字就是从 ID 推出来的。

""";

    /// <summary>
    /// The on-disk shape. Nullable members on purpose: "this key is absent" and "this list is empty"
    /// are different answers, and <see cref="Load"/> treats them differently.
    /// </summary>
    private sealed record SourceFilterDocument
    {
        public List<string>? Preferred { get; init; }

        public List<string>? Fallback { get; init; }
    }
}
