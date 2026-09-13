namespace DesktopMediaController.Core;

/// <summary>
/// Turns an SMTC <c>SourceAppUserModelId</c> into something a human can read.
/// </summary>
/// <remarks>
/// The widget deliberately shows which player it is reading from, because the automatic session
/// choice cannot always be what the user wants (two players can be playing at once). Displaying
/// <c>electron.app.douyin</c> would make that useless, so the common ids get a real name and
/// anything unknown is at least stripped down to a recognisable word.
/// </remarks>
public static class SourceAppName
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qqmusic.exe"] = "QQ音乐",
        ["cloudmusic.exe"] = "网易云音乐",
        ["kugou.exe"] = "酷狗音乐",
        ["kuwo.exe"] = "酷我音乐",
        ["electron.app.douyin"] = "抖音",
        ["spotify.exe"] = "Spotify",
        ["spotify"] = "Spotify",
        ["appleinc.applemusic_nzyj5cx40ttqa!app"] = "Apple Music",
        ["music.ui.exe"] = "媒体播放器",
        ["aimp.exe"] = "AIMP",
        ["foobar2000.exe"] = "foobar2000",
        ["potplayermini64.exe"] = "PotPlayer",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["firefox.exe"] = "Firefox",
    };

    /// <summary>
    /// A short, human-facing name for the player. Unknown ids lose their <c>.exe</c> and any
    /// package-style prefix (<c>electron.app.douyin</c> → <c>douyin</c>) rather than being shown raw;
    /// an empty or whitespace id yields an empty string, never the placeholder text.
    /// </summary>
    public static string Describe(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return string.Empty;
        if (Known.TryGetValue(sourceId.Trim(), out var known)) return known;

        var name = sourceId.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < name.Length - 1) name = name[(lastDot + 1)..];

        return name;
    }
}
