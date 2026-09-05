using System;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class LiveSortStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotestLiveSort", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "live-sort.json");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveLoad_RoundTrips(bool enabled)
    {
        var path = TempPath();
        LiveSortStore.Save(path, enabled);
        Assert.Equal(enabled, LiveSortStore.Load(path));
    }

    [Fact]
    public void Load_MissingFile_DefaultsToOff()
        => Assert.Equal(LiveSortStore.Default, LiveSortStore.Load(TempPath()));

    [Fact]
    public void Load_CorruptFile_DefaultsToOff()
    {
        var path = TempPath();
        File.WriteAllText(path, "maybe");
        Assert.False(LiveSortStore.Load(path));
    }
}
