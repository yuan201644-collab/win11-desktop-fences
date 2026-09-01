using System;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceSortStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotestSort", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "fence-sort.json");
    }

    [Theory]
    [InlineData(FenceSortMode.Name)]
    [InlineData(FenceSortMode.Type)]
    [InlineData(FenceSortMode.Modified)]
    public void SaveLoad_RoundTrips(FenceSortMode mode)
    {
        var path = TempPath();
        FenceSortStore.Save(path, mode);
        Assert.Equal(mode, FenceSortStore.Load(path));
    }

    [Fact]
    public void Load_MissingFile_DefaultsToName()
        => Assert.Equal(FenceSortMode.Name, FenceSortStore.Load(TempPath()));

    [Fact]
    public void Load_CorruptFile_DefaultsToName()
    {
        var path = TempPath();
        File.WriteAllText(path, "not-an-enum");
        Assert.Equal(FenceSortMode.Name, FenceSortStore.Load(path));
    }
}