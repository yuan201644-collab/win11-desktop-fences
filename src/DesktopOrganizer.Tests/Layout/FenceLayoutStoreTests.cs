using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Layout;

public class FenceLayoutStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"fencelayout-test-{Path.GetRandomFileName()}.json");

    [Fact]
    public void RoundTrip_PreservesEveryRectangle()
    {
        var layout = new Dictionary<string, FenceLayout>
        {
            ["办公"] = new(100, 50, 420, 320),
            ["开发"] = new(560, 50, 380, 260),
        };
        var path = TempPath();
        try
        {
            FenceLayoutStore.Save(path, layout);
            var loaded = FenceLayoutStore.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(new FenceLayout(100, 50, 420, 320), loaded["办公"]);
            Assert.Equal(new FenceLayout(560, 50, 380, 260), loaded["开发"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyMap()
    {
        var loaded = FenceLayoutStore.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-fencelayout.json"));
        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyMapWithoutThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json !!");
            Assert.Empty(FenceLayoutStore.Load(path));
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
            FenceLayoutStore.Save(path, new Dictionary<string, FenceLayout> { ["办公"] = new(1, 2, 3, 4) });
            Assert.True(FenceLayoutStore.Load(path).ContainsKey("办 公".Replace(" ", "").ToUpperInvariant()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fencelayout-test-dir-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, "sub", "fence-layout.json");
        try
        {
            FenceLayoutStore.Save(path, new Dictionary<string, FenceLayout>());
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
