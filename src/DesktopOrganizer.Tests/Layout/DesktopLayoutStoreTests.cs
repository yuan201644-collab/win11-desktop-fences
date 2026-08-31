using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Layout;

public class DesktopLayoutStoreTests
{
    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), "desktoporganizer-test", $"layout-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var file = TempFile();
        try
        {
            var layout = new Dictionary<string, PointI>
            {
                ["报告.txt"] = new PointI(32, 32),
                ["Word"] = new PointI(224, 32),
                ["文件夹"] = new PointI(32, 416),
            };
            DesktopLayoutStore.Save(file, layout);

            var loaded = DesktopLayoutStore.Load(file);
            Assert.Equal(3, loaded.Count);
            Assert.Equal(new PointI(32, 32), loaded["报告.txt"]);
            Assert.Equal(new PointI(224, 32), loaded["Word"]);
            Assert.Equal(new PointI(32, 416), loaded["文件夹"]);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void Load_KeysAreCaseInsensitive()
    {
        var file = TempFile();
        try
        {
            DesktopLayoutStore.Save(file, new Dictionary<string, PointI> { ["Word"] = new PointI(10, 20) });
            var loaded = DesktopLayoutStore.Load(file);
            Assert.Equal(new PointI(10, 20), loaded["word"]); // different case still resolves
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var loaded = DesktopLayoutStore.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-layout.json"));
        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty_DoesNotThrow()
    {
        var file = TempFile();
        try
        {
            File.WriteAllText(file, "{ this is not valid json");
            var loaded = DesktopLayoutStore.Load(file);
            Assert.Empty(loaded);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}