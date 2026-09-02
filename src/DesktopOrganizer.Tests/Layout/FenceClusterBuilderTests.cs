using DesktopOrganizer.Core.Config;
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

    [Fact]
    public void CollapsedTab_SitsExactlyOnTheExpandedBoxTitleBar()
    {
        // Regression: the tab used its own ad-hoc formula (no padding, no header lift), so folding
        // kicked the title right/down and narrowed it, and expanding snapped it back.
        var pts = new[] { new PointI(846, 28), new PointI(942, 28), new PointI(846, 124) };
        var box = Assert.Single(FenceClusterBuilder.Build(
            pts.Select(p => ("文件夹", p)).ToArray(), 96, 96, pad: 2, headerPx: 34)).Bounds;

        var tab = FenceClusterBuilder.CollapsedTabBounds(pts, 96, 96, 2, 2, 2, 2, 34);

        Assert.Equal(box.Left, tab.Left);   // no sideways jump when folding
        Assert.Equal(box.Top, tab.Top);     // no downward jump (the old bug dropped it by pad+34)
        Assert.Equal(box.Width, tab.Width); // no narrowing
        Assert.Equal(34, tab.Height);       // title band only
    }

    [Fact]
    public void CollapsedTab_HonorsPerSidePaddingLikeTheBox()
    {
        // Asymmetric insets (the user can drag each edge) must move the tab and the box together.
        var pts = new[] { new PointI(500, 200), new PointI(700, 300) };
        var box = FenceClusterBuilder.Build(
            pts.Select(p => ("软件", p)).ToArray(), 96, 96,
            padLeft: 10, padRight: 30, padTop: 5, padBottom: 15, headerPx: 34).Single().Bounds;

        var tab = FenceClusterBuilder.CollapsedTabBounds(pts, 96, 96, 10, 5, 30, 15, 34);

        Assert.Equal(box.Left, tab.Left);
        Assert.Equal(box.Top, tab.Top);
        Assert.Equal(box.Width, tab.Width);
    }

    // -----------------------------------------------------------------------------------------
    // Parking pockets (LVM_SETITEMPOSITION packs coordinates into 16-bit words — see ParkSlot).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ParkSlot_StaysInside16BitRange_EvenForAHugeBox()
    {
        // The old `-32000 - spacing * i` marched past short.MinValue and got truncated by Explorer
        // into a bogus positive coordinate. Any icon count must stay representable.
        for (int slot = 0; slot < 5000; slot++)
        {
            var p = FenceClusterBuilder.ParkSlot(slot, 96, 96);
            Assert.InRange(p.X, short.MinValue, short.MaxValue);
            Assert.InRange(p.Y, short.MinValue, short.MaxValue);
        }
    }

    [Fact]
    public void ParkSlot_AlwaysFarOffScreen()
    {
        // Parked icons must be invisible AND detectable by the stranded-icon rescue.
        for (int slot = 0; slot < 500; slot++)
        {
            var p = FenceClusterBuilder.ParkSlot(slot, 76, 84);
            Assert.True(p.X < FenceClusterBuilder.ParkedThreshold, $"slot {slot}: x={p.X} not far off-screen");
            Assert.True(p.Y < FenceClusterBuilder.ParkedThreshold, $"slot {slot}: y={p.Y} not far off-screen");
        }
    }

    [Fact]
    public void ParkSlot_Regression_ThirtyIconFolderBoxNeverWraps()
    {
        // The exact repro: a 30-icon 文件夹 box parked with the live 76x84 icon grid. The old scheme
        // reached y = -32000 - 84*28 = -34352, which Explorer read back as +31184 (0x79D0) —
        // log line: "dropped 1 off-screen icon(s): 文件夹@-34128,31184".
        for (int slot = 0; slot < 30; slot++)
        {
            var p = FenceClusterBuilder.ParkSlot(slot, 76, 84);
            Assert.InRange(p.X, short.MinValue, short.MaxValue);
            Assert.InRange(p.Y, short.MinValue, short.MaxValue);
            Assert.True(p.Y < FenceClusterBuilder.ParkedThreshold);
            // The old formula, for contrast, is what broke:
            var oldY = -32000 - 84 * slot;
            if (slot >= 28) Assert.True(oldY < short.MinValue, "the old scheme did overflow — regression guard");
        }
    }

    [Fact]
    public void ParkSlot_SpreadsSmallBoxesOverDistinctCells()
    {
        var cells = new HashSet<(int, int)>();
        for (int slot = 0; slot < 12; slot++) cells.Add((FenceClusterBuilder.ParkSlot(slot, 96, 96).X,
                                                        FenceClusterBuilder.ParkSlot(slot, 96, 96).Y));
        Assert.Equal(12, cells.Count); // no two parked icons share a cell in the normal case
    }

    private static bool Overlaps(RectI x, RectI y)
        => x.Left < y.Right && x.Right > y.Left && x.Top < y.Bottom && x.Bottom > y.Top;

    [Fact]
    public void PerBoxInsets_OverrideOnlyThatBox_OthersKeepGlobalPads()
    {
        // Box 办公 at (0,0), box 开发 at (500,0); global pad=2, but 办公 overrides its own
        // left/top/right/bottom. The override must shape 办公's box only — 开发 keeps global 2.
        var clusters = FenceClusterBuilder.Build(
            new[]
            {
                ("办公", new PointI(0, 0)),
                ("开发", new PointI(500, 0)),
            }, 96, 96, pad: 2,
            perBoxInsets: title => title == "办公" ? new FenceInsets(40, 20, 10, 30) : null);

        var office = Assert.Single(clusters, c => c.Title == "办公");
        var dev = Assert.Single(clusters, c => c.Title == "开发");

        // 办公: left = 0-40 = -40; top = 0-10 = -10; width = (0+96+20)-(-40) = 156;
        //       height = (0+96+30)-(-10) = 136.
        Assert.Equal(new RectI(-40, -10, 156, 136), office.Bounds);
        // 开发 unchanged: left = 500-2; top = -2; width = 96+4 = 100; height = 100.
        Assert.Equal(new RectI(498, -2, 100, 100), dev.Bounds);
    }

    [Fact]
    public void PerBoxInsets_BoxWithNoOverride_StillHonorsGlobalPerSidePads()
    {
        // Only 开发 has no override; 办公 overrides. 开发 must keep the global per-side pads
        // (padLeft=20 …), proving a null lookup falls through to the caller's global values.
        var cl = Assert.Single(FenceClusterBuilder.Build(
            new[] { ("开发", new PointI(200, 300)) }, 96, 96, pad: 2,
            padLeft: 20, padRight: 10, padTop: 4, padBottom: 8,
            perBoxInsets: _ => null));
        Assert.Equal(new RectI(180, 296, 126, 108), cl.Bounds);
    }

    // --- dual-monitor (主屏 + 副屏) integration: the virtual desktop is the UNION of all monitors,
    //     so a secondary monitor at x>=1920 (or at negative x when it sits left) must keep its icons
    //     in their own box instead of being dropped by ScreenGuard or stretched across screens. ---

    [Fact]
    public void DualMonitor_RightSecondary_GroupsStayOnTheirOwnScreen()
    {
        // 3840x1080: primary [0..1919] + secondary on the right [1920..3839].
        var screen = new RectI(0, 0, 3840, 1080);
        var clusters = FenceClusterBuilder.Build(
            new[] {
                ("文件夹", new PointI(80, 100)),
                ("文件夹", new PointI(176, 100)),
                ("文件",   new PointI(2000, 100)),
                ("文件",   new PointI(2096, 100)),
            }, 96, 96, pad: 2, headerPx: 34,
            separateOverlaps: false, screen: screen).ToList();

        // Two groups, each icon kept (ScreenGuard must NOT drop the secondary-monitor icons).
        Assert.Equal(2, clusters.Count);
        var primary = clusters.Single(c => c.Title == "文件夹");
        var secondary = clusters.Single(c => c.Title == "文件");
        Assert.Equal(2, primary.IconCount);
        Assert.Equal(2, secondary.IconCount);
        // The primary box stays on the primary screen; the secondary box stays on the secondary screen.
        Assert.True(primary.Bounds.Right <= 1920, $"primary box crossed into the secondary: {primary.Bounds}");
        Assert.True(secondary.Bounds.Left >= 1920, $"secondary box spilled onto the primary: {secondary.Bounds}");
    }

    [Fact]
    public void DualMonitor_LeftSecondary_NegativeCoordsStayOnTheirOwnBox()
    {
        // 3840x1080 with the secondary monitor on the LEFT: virtual desktop is x ∈ [-1920, 1919].
        var screen = new RectI(-1920, 0, 3840, 1080);
        var clusters = FenceClusterBuilder.Build(
            new[] {
                ("文件夹", new PointI(-1880, 100)),
                ("文件夹", new PointI(-1784, 100)),
                ("文件",   new PointI(80, 100)),
                ("文件",   new PointI(176, 100)),
            }, 96, 96, pad: 2, headerPx: 34,
            separateOverlaps: false, screen: screen).ToList();

        Assert.Equal(2, clusters.Count);
        var left = clusters.Single(c => c.Title == "文件夹");
        var right = clusters.Single(c => c.Title == "文件");
        Assert.Equal(2, left.IconCount);
        Assert.Equal(2, right.IconCount);
        // The left-monitor box keeps a NEGATIVE left edge (not clamped up to 0) and stays on the left.
        Assert.True(left.Bounds.Left < 0, $"left-monitor box lost its negative origin: {left.Bounds}");
        Assert.True(left.Bounds.Right <= 0, $"left box crossed to the primary: {left.Bounds}");
        Assert.True(right.Bounds.Left >= 0, $"primary box spilled left: {right.Bounds}");
    }

    [Fact]
    public void DualMonitor_IconPastVirtualScreenEdge_IsDroppedNotStretched()
    {
        // A coordinate outside the union of monitors (far right of the 3840-wide virtual desktop)
        // is genuinely stray and must be dropped — the other icon still defines a correct box.
        var screen = new RectI(0, 0, 3840, 1080);
        var cl = Assert.Single(FenceClusterBuilder.Build(
            new[] {
                ("文件夹", new PointI(80, 100)),
                ("文件夹", new PointI(176, 100)),
                ("文件夹", new PointI(90000, 100)), // beyond the virtual desktop
            }, 96, 96, pad: 2, headerPx: 34, separateOverlaps: false, screen: screen));
        Assert.Equal(2, cl.IconCount);
        Assert.True(cl.Bounds.Right <= 1920, $"stray point stretched the box: {cl.Bounds}");
    }
}