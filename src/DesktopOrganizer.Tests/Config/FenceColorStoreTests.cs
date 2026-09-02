using System.Collections.Generic;
using System.IO;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceColorStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"fencecolor-test-{Path.GetRandomFileName()}.json");

    private static OverlayAppearance Make(byte seed)
        => new(
            ArgbColor.FromArgb(seed, (byte)(seed + 1), (byte)(seed + 2), (byte)(seed + 3)),
            ArgbColor.FromArgb((byte)(seed + 4), (byte)(seed + 5), (byte)(seed + 6), (byte)(seed + 7)),
            ArgbColor.FromArgb((byte)(seed + 8), (byte)(seed + 9), (byte)(seed + 10), (byte)(seed + 11)),
            ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    [Fact]
    public void RoundTrip_PreservesPerFenceOverrides()
    {
        var colors = new Dictionary<string, OverlayAppearance>
        {
            ["办公"] = Make(0x10),
            ["开发"] = Make(0x40),
        };
        var path = TempPath();
        try
        {
            FenceColorStore.Save(path, colors);
            var loaded = FenceColorStore.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(Make(0x10), loaded["办公"]);
            Assert.Equal(Make(0x40), loaded["开发"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyMap()
    {
        Assert.Empty(FenceColorStore.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-fencecolor.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyMapWithoutThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, ":::nope:::");
            Assert.Empty(FenceColorStore.Load(path));
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
            FenceColorStore.Save(path, new Dictionary<string, OverlayAppearance> { ["办公"] = Make(0x20) });
            Assert.True(FenceColorStore.Load(path).ContainsKey("办 公".Replace(" ", "").ToUpperInvariant()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
