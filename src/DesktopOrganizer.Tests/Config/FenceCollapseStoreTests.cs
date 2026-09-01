using System;
using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceCollapseStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotestCollapse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "fence-collapse.json");
    }

    private static CollapsedFenceRecord Record(string title, RectI tab)
        => new(title, tab, new Dictionary<string, PointI>
        {
            ["Visual Studio.lnk"] = new(120, 80),
            ["Git Bash.lnk"] = new(200, 80),
        });

    [Fact]
    public void SaveLoad_RoundTrips_TabAndIconPositions()
    {
        var path = TempPath();
        var tab = new RectI(100, 50, 240, 34);
        FenceCollapseStore.Save(path, new[] { Record("办公软件", tab), Record("文件夹", new RectI(100, 200, 120, 34)) });

        var loaded = FenceCollapseStore.Load(path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("办公软件", loaded[0].Title);
        Assert.Equal(tab, loaded[0].Tab);
        Assert.Equal(new PointI(120, 80), loaded[0].Icons["Visual Studio.lnk"]);
        Assert.Equal(new PointI(200, 80), loaded[0].Icons["Git Bash.lnk"]);
        Assert.Equal("文件夹", loaded[1].Title);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
        => Assert.Empty(FenceCollapseStore.Load(TempPath()));

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "not json {{{");
        Assert.Empty(FenceCollapseStore.Load(path));
    }

    [Fact]
    public void Load_LegacyStringArray_ReturnsRecordsWithoutIcons()
    {
        // v1.1 wrote a plain array of titles; those entries must come back as records the caller
        // can recognize as "never actually hidden" (default Tab, no icons).
        var path = TempPath();
        File.WriteAllText(path, "[\"开发\",\"影音娱乐\"]");

        var loaded = FenceCollapseStore.Load(path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("开发", loaded[0].Title);
        Assert.Equal(default, loaded[0].Tab);
        Assert.Empty(loaded[0].Icons);
    }

    [Fact]
    public void Save_Null_CleansFile()
    {
        var path = TempPath();
        FenceCollapseStore.Save(path, new[] { Record("影音娱乐", new RectI(10, 10, 80, 34)) });
        FenceCollapseStore.Save(path, null);
        Assert.Empty(FenceCollapseStore.Load(path));
    }
}
