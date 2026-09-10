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
    public void Load_FileWrittenBeforeLockingExisted_ComesBackUnlocked()
    {
        // Files written by older builds have no "locked" property. They must still load — as
        // UNLOCKED pins. A deserialize failure here is swallowed by Load's catch-all and would
        // silently wipe every box's remembered position, so this is worth pinning down.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"办公\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4}}");

            var loaded = FenceLayoutStore.Load(path);

            var (_, layout) = Assert.Single(loaded);
            Assert.Equal(new FenceLayout(1, 2, 3, 4), layout);
            Assert.False(layout.Locked);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_SkipsTransientPlacements_ButKeepsRealPins()
    {
        // A drag only REMEMBERS a box for the session (transient); clicking the badge is what makes
        // it a real pin. Only the real pin may survive a restart — otherwise a stray drag would
        // petrify the layout and 整理 would stop being able to re-pack the box.
        var layout = new Dictionary<string, FenceLayout>
        {
            ["固定框"] = new(100, 50, 420, 320),
            ["临时框"] = new(600, 50, 420, 320, Transient: true),
        };
        var path = TempPath();
        try
        {
            FenceLayoutStore.Save(path, layout);
            var loaded = FenceLayoutStore.Load(path);

            var only = Assert.Single(loaded);
            Assert.Equal("固定框", only.Key);
            Assert.False(only.Value.Transient);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_FileWrittenBeforeTransientExisted_ComesBackAsARealPin()
    {
        // Older files have neither "locked" nor "transient". Loading must not invent a transient
        // placement — that would make the next 整理 silently re-pack a box the user had pinned.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"办公\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4}}");

            var (_, layout) = Assert.Single(FenceLayoutStore.Load(path));

            Assert.False(layout.Transient);
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
