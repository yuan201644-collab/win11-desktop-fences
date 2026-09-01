using System;
using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Config;
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

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = TempPath();
        FenceCollapseStore.Save(path, new[] { "办公软件", "文件夹" });
        Assert.Equal(new[] { "办公软件", "文件夹" }, FenceCollapseStore.Load(path));
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
    public void Load_MatchingIsCaseInsensitive()
    {
        var path = TempPath();
        FenceCollapseStore.Save(path, new[] { "开发" });

        // The reloaded list is a plain array; title matching up the stack uses OrdinalIgnoreCase.
        var loaded = FenceCollapseStore.Load(path);
        Assert.Single(loaded);
        Assert.Equal("开发", loaded[0]);
    }

    [Fact]
    public void Save_Null_CleansFile()
    {
        var path = TempPath();
        FenceCollapseStore.Save(path, new[] { "影音娱乐" });
        FenceCollapseStore.Save(path, null);
        Assert.Empty(FenceCollapseStore.Load(path));
    }
}