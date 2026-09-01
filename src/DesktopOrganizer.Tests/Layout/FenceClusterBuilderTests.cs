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
    public void PerSidePadding_ExtendsEachEdgeIndependently()
    {
        // One icon cell [200..296]x[300..396]; default pad=2 is overridden per edge.
        var cl = Assert.Single(FenceClusterBuilder.Build(
            new[] { ("文档", new PointI(200, 300)) }, 96, 96, pad: 2,
            padLeft: 20, padRight: 10, padTop: 4, padBottom: 8));
        // left edge = 200-20=180; top edge = 300-4=296;
        // width = (200+96+10) - 180 = 126; height = (300+96+8) - 296 = 108.
        Assert.Equal(new RectI(180, 296, 126, 108), cl.Bounds);
    }

    [Fact]
    public void WithoutOverlapSeparation_BoxesStayOverTheirOwnIcons()
    {
        // Two overlapping groups. With separateOverlaps:false the later box must NOT be pushed down
        // away from its icons (that dislocates the drawn box from the real icons) — it keeps its own
        // icon-covered bounds. 软件 at (0,0), 文件夹 at (0,96); pad=2.
        var clusters = FenceClusterBuilder.Build(
            new[] {
                ("软件", new PointI(0, 0)),
                ("文件夹", new PointI(0, 96)),
            }, 96, 96, pad: 2, separateOverlaps: false);
        Assert.Equal(2, clusters.Count);
        // 文件夹 box top = icon y(96) - pad(2) = 94, i.e. it hugs its own first icon rather than
        // being shoved below the previous box (which would push its top to ≥ 106).
        Assert.Equal(94, clusters.Single(c => c.Title == "文件夹").Bounds.Top);
        Assert.Equal(-2, clusters.Single(c => c.Title == "软件").Bounds.Top);
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

    [Fact]
    public void ParkedIcon_DoesNotStretchBoundingBox()
    {
        // Repro of the "文件夹 title bar extends forever" bug: after expand, one icon can still
        // report a parked coordinate (fold base -32000) when the refresh reads positions. One
        // such point must NOT inflate the group's bounding box to ~32k px wide.
        var cl = Assert.Single(Build(
            ("文件夹", new PointI(0, 0)),
            ("文件夹", new PointI(96, 0)),
            ("文件夹", new PointI(-32000, -32000))));
        // Bounds computed from the two visible icons only.
        Assert.Equal(new RectI(-8, -8, 208, 112), cl.Bounds);
        Assert.Equal(2, cl.IconCount);
    }

    [Fact]
    public void AllIconsParked_YieldsNoCluster()
    {
        // A group whose every icon is parked off-screen must not produce an (invisible,
        // screen-wide) box at all.
        Assert.Empty(Build(
            ("文件夹", new PointI(-32000, -32000)),
            ("文件夹", new PointI(-32000 - 96, -32000))));
    }

    [Fact]
    public void SecondaryMonitorNegativeCoords_AreNotTreatedAsParked()
    {
        // Dual-monitor: a left-positioned secondary monitor can legitimately sit at e.g. x=-1920,
        // far above the -10000 parked threshold — those icons must keep participating in bounds.
        var cl = Assert.Single(Build(
            ("文件夹", new PointI(-1920, 100)),
            ("文件夹", new PointI(-1824, 100))));
        Assert.Equal(new RectI(-1928, 92, 208, 112), cl.Bounds);
        Assert.Equal(2, cl.IconCount);
    }

    [Fact]
    public void ScreenGuard_DropsFarRightCoordinate_KeepsBoxOnScreen()
    {
        // The real repro: a coordinate dumped far to the right of the monitors (positive, so the
        // negative-only parked filter cannot see it) must not stretch the box.
        var screen = new RectI(0, 0, 1920, 1080);
        var cl = Assert.Single(FenceClusterBuilder.Build(
            new[] {
                ("文件夹", new PointI(846, 28)),
                ("文件夹", new PointI(942, 28)),
                ("文件夹", new PointI(50000, 28)),   // stray, far right
            }, 96, 96, pad: 2, headerPx: 34, separateOverlaps: false, screen: screen));
        Assert.Equal(2, cl.IconCount);
        // width = (942+96+2) - 844 = 196; height = (28+96+2) - (28-2) + 34 = 134.
        Assert.Equal(new RectI(844, -8, 196, 134), cl.Bounds);
    }

    [Fact]
    public void IsOnScreen_KeepsSecondaryMonitorNegativeCoords()
    {
        var screen = new RectI(-1920, 0, 3840, 1080); // second monitor on the left
        Assert.True(FenceClusterBuilder.IsOnScreen(new PointI(-1920, 100), screen, 96, 96));
        Assert.True(FenceClusterBuilder.IsOnScreen(new PointI(-1824, 100), screen, 96, 96));
        Assert.False(FenceClusterBuilder.IsOnScreen(new PointI(-32000, -32000), screen, 96, 96)); // parked
        Assert.False(FenceClusterBuilder.IsOnScreen(new PointI(50000, 100), screen, 96, 96));    // stray right
    }

    [Fact]
    public void ClampBounds_CapsRunawayBox_LeavesMultiMonitorBoxAlone()
    {
        // Dual-monitor virtual desktop: 3840x1080.
        var screen = new RectI(0, 0, 3840, 1080);
        // A legitimate box straddling both monitors still fits inside the virtual screen → untouched.
        var normal = new RectI(846, 28, 1200, 600);
        Assert.Equal(normal, FenceClusterBuilder.ClampBounds(normal, screen));
        // A runaway box (32k px wide) gets capped to the screen rect.
        var runaway = new RectI(846, 28, 32000, 400);
        Assert.Equal(new RectI(846, 28, 2994, 400), FenceClusterBuilder.ClampBounds(runaway, screen));
    }

    private static bool Overlaps(RectI x, RectI y)
        => x.Left < y.Right && x.Right > y.Left && x.Top < y.Bottom && x.Bottom > y.Top;
}