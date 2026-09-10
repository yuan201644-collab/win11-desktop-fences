using System;
using System.IO;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Win32;

/// <summary>
/// Target resolution for desktop items. Two behaviours are load-bearing:
///   1. .url internet shortcuts resolve to their URL — previously they resolved to nothing, so the
///      classifier only had the display name to match and every ".url" game fell into 其他软件.
///   2. Resolution is memoised per arrange — the 2026-09-10 freeze was ~860 WScript.Shell creations
///      on the UI thread because the same shortcuts were resolved once per pinned box.
/// </summary>
public class DesktopShellEnumeratorTests
{
    private static string WriteUrlFile(string name, string url)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "shell-enum-test-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(dir, name + ".url");
        File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + url + "\r\nIconIndex=0\r\n");
        return path;
    }

    [Fact]
    public void LinkTargetAppFromPath_InternetShortcut_ResolvesTheUrlLine()
    {
        var path = WriteUrlFile("Terraria", "steam://rungameid/105600");

        var resolved = DesktopShellEnumerator.LinkTargetAppFromPath(path);

        Assert.Equal("steam://rungameid/105600", resolved);
    }

    [Fact]
    public void LinkTargetAppFromPath_InternetShortcut_FeedsTheClassifiersKeywordMatch()
    {
        var path = WriteUrlFile("Terraria", "steam://rungameid/105600");
        var groups = new SoftwareGroupingConfig
        {
            Groups =
            {
                new SoftwareGroup { Title = "影音娱乐/游戏", Keywords = { "steam", "游戏" } },
            },
        };

        var link = DesktopShellEnumerator.LinkTargetAppFromPath(path);
        var box = SoftwarePurposeClassifier.Classify(groups, "Terraria", link);

        Assert.Equal("影音娱乐/游戏", box);
    }

    [Fact]
    public void LinkTargetAppFromPath_SecondLookup_IsServedFromTheMemo()
    {
        var path = WriteUrlFile("To the Moon", "https://store.steampowered.com/app/206440/");

        DesktopShellEnumerator.ClearLinkTargetCache();
        var first = DesktopShellEnumerator.LinkTargetAppFromPath(path);
        var missesAfterFirst = DesktopShellEnumerator.CacheMisses;

        var second = DesktopShellEnumerator.LinkTargetAppFromPath(path);

        Assert.Equal(first, second);
        // The point of the memo: a second pass over the same desktop resolves nothing again.
        Assert.Equal(missesAfterFirst, DesktopShellEnumerator.CacheMisses);
        Assert.True(DesktopShellEnumerator.CacheHits >= 1);
    }

    [Fact]
    public void LinkTargetAppFromPath_MissingShortcut_CachesTheMiss()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".url");

        DesktopShellEnumerator.ClearLinkTargetCache();
        Assert.Null(DesktopShellEnumerator.LinkTargetAppFromPath(missing));
        var misses = DesktopShellEnumerator.CacheMisses;

        Assert.Null(DesktopShellEnumerator.LinkTargetAppFromPath(missing));
        Assert.Equal(misses, DesktopShellEnumerator.CacheMisses); // a broken link is resolved once, not every pass
    }
}
