using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceInsetStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"inset-test-{Path.GetRandomFileName()}.json");

    [Fact]
    public void Default_HasSensiblePerEdgePadding()
    {
        var d = FenceInsets.Default;
        Assert.True(d.Left > 0 && d.Right > 0 && d.Top >= 0 && d.Bottom >= 0);
    }

    [Fact]
    public void RoundTrip_PreservesEveryEdge()
    {
        var insets = new FenceInsets(Left: 30, Right: 12, Top: 3, Bottom: 9);
        var path = TempPath();
        try
        {
            FenceInsetStore.Save(path, insets);
            var loaded = FenceInsetStore.Load(path);
            Assert.Equal(insets, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        Assert.Equal(FenceInsets.Default,
            FenceInsetStore.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-inset.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultWithoutThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json !!");
            Assert.Equal(FenceInsets.Default, FenceInsetStore.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inset-test-dir-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, "sub", "fence-inset.json");
        try
        {
            FenceInsetStore.Save(path, FenceInsets.Default);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}