using System;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceCategoryStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DoTestCategory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "fence-category.json");
    }

    [Fact]
    public void SaveLoad_RoundTripsOrderAndHidden()
    {
        var path = TempPath();
        var cfg = new FenceCategoryConfig
        {
            Order = { "游戏", "开发", "文件夹" },
            Hidden = { "其他", "文件" },
        };
        FenceCategoryStore.Save(path, cfg);

        var loaded = FenceCategoryStore.Load(path);
        Assert.Equal(new[] { "游戏", "开发", "文件夹" }, loaded.Order);
        Assert.Equal(new[] { "其他", "文件" }, loaded.Hidden);
        Assert.True(loaded.IsHidden("其他"));
    }

    [Fact]
    public void SaveLoad_EmptyConfig_RoundTripsAsEmpty()
    {
        var path = TempPath();
        FenceCategoryStore.Save(path, FenceCategoryConfig.Default);

        var loaded = FenceCategoryStore.Load(path);
        Assert.Empty(loaded.Order);
        Assert.Empty(loaded.Hidden);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var loaded = FenceCategoryStore.Load(TempPath());
        Assert.Empty(loaded.Order);
        Assert.Empty(loaded.Hidden);
        Assert.False(loaded.IsHidden("其他"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefault()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not json ]");
        Assert.Empty(FenceCategoryStore.Load(path).Hidden);
    }

    [Fact]
    public void Load_WrongShapeFile_ReturnsDefault()
    {
        // Valid JSON, but not the expected object — must not throw or invent boxes.
        var path = TempPath();
        File.WriteAllText(path, "[\"其他\"]");
        Assert.Empty(FenceCategoryStore.Load(path).Hidden);
    }
}
