using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Layout;

public class FenceClusterBuilderTests
{
    private static IReadOnlyList<FenceCluster> Build(
        params (string Group, PointI Position)[] placed)
        => FenceClusterBuilder.Build(placed, cellWidth: 96, cellHeight: 96);

    [Fact]
    public void Empty_ReturnsNoClusters()
        => Assert.Empty(FenceClusterBuilder.Build(Array.Empty<(string, PointI)>(), 96, 96));

    [Fact]
    public void SingleIcon_BoundsCoverCellPlusPadding()
    {
        var cl = Assert.Single(Build(("文档", new PointI(200, 300))));
        // icon cell [200..296]x[300..396], inflated by pad=8 on each side.
        Assert.Equal(new RectI(192, 292, 112, 112), cl.Bounds);
        Assert.Equal(1, cl.IconCount);
        Assert.Equal("文档", cl.Title);
    }

    [Fact]
    public void TwoSameGroupIcons_MergeIntoOneCluster()
    {
        var cl = Assert.Single(Build(
            ("游戏", new PointI(0, 0)),
            ("游戏", new PointI(96, 0))));
        // x from -8 .. (96+96+8) = width 96+96+16 = 208
        Assert.Equal(new RectI(-8, -8, 208, 112), cl.Bounds);
        Assert.Equal(2, cl.IconCount);
    }

    [Fact]
    public void TwoGroups_YieldTwoClusters()
    {
        var clusters = Build(
            ("软件", new PointI(0, 0)),
            ("文件夹", new PointI(500, 500)));
        Assert.Equal(2, clusters.Count);
        Assert.Equal("软件", clusters[0].Title);
        Assert.Equal("文件夹", clusters[1].Title);
    }

    [Fact]
    public void HeaderPx_ExtendsBoxUpwardByTitleBand()
    {
        // Icon at (0, 34) — i.e. already offset below a 34px title band. With headerPx, the box
        // must extend up to cover that band so the title sits inside the box, not over icons.
        var cl = Assert.Single(FenceClusterBuilder.Build(
            new[] { ("软件", new PointI(0, 34)) }, 96, 96, pad: 2, headerPx: 34));
        // icon at y=34; box top = icon - pad(2) - header(34) = -2, box height = 96 + 2*2 + 34 = 134.
        Assert.Equal(-2, cl.Bounds.Top);     // top reaches above the icon by the full title band
        Assert.Equal(134, cl.Bounds.Height); // pad + icon cell + pad + header band
    }

    [Fact]
    public void OverlappingGroups_ArePushedApartNeverFused()
    {
        // 软件 and 文件夹 land on the same rows (gap row was sacrificed), so raw bounding
        // boxes overlap. The builder must push the later cluster down clear of the first.
        var clusters = Build(
            ("软件", new PointI(0, 0)),
            ("文件夹", new PointI(0, 96))); // adjacent vertically → box vs box intersect
        Assert.Equal(2, clusters.Count);
        var a = clusters.Single(c => c.Title == "软件").Bounds;
        var b = clusters.Single(c => c.Title == "文件夹").Bounds;
        Assert.False(Overlaps(a, b), "overlapping boxes must be pushed apart");
        Assert.True(b.Top > a.Bottom, "later cluster should sit below the first");
    }

    private static bool Overlaps(RectI x, RectI y)
        => x.Left < y.Right && x.Right > y.Left && x.Top < y.Bottom && x.Bottom > y.Top;
}