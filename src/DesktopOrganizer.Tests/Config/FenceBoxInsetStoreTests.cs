using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceBoxInsetStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"fenceboxinset-test-{Path.GetRandomFileName()}.json");

    [Fact]
    public void RoundTrip_PreservesPerBoxOverrides()
    {
        var insets = new Dictionary<string, FenceInsets>
        {
            ["办公"] = new(Left: 40, Right: 12, Top: 2, Bottom: 6),
            ["开发"] = new(Left: 4, Right: 60, Top: 10, Bottom: 0),
        };
        var path = TempPath();
        try
        {
            FenceBoxInsetStore.Save(path, insets);
            var loaded = FenceBoxInsetStore.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(new FenceInsets(40, 12, 2, 6), loaded["办公"]);
            Assert.Equal(new FenceInsets(4, 60, 10, 0), loaded["开发"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyMap()
    {
        Assert.Empty(FenceBoxInsetStore.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-fenceboxinset.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyMapWithoutThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, ":::nope:::");
            Assert.Empty(FenceBoxInsetStore.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_KeysAreCaseInsensitive()
    {
        var path = TempPath();
        try
        {
            FenceBoxInsetStore.Save(path, new Dictionary<string, FenceInsets> { ["办公"] = new(20, 20, 20, 20) });
            Assert.True(FenceBoxInsetStore.Load(path).ContainsKey("办公".ToUpperInvariant()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
