using System;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

public class DesktopLayoutServiceTests
{
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fence-layout-test-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <param name="folder">The REAL directory this icon's path resolves to — must exist, or
    /// ItemKindClassifier resolves it as "其他" and the icon leaves the 文件夹 box.</param>
    private static DesktopIcon FolderIcon(int index, string name, string folder)
        => new(index, name, folder, new PointI(0, 0));

    private static DesktopIcon FileIcon(int index, string name)
        => new(index, name, "C:\\fake\\" + name + ".txt", new PointI(0, 0));

    /// <summary>4 folder icons (each pointing at a real subdirectory) + 2 file icons; folder icons
    /// start on the left, file icons to the right.</summary>
    private static (FakeDesktopIconProvider provider, DesktopLayoutService service) Build()
    {
        var provider = new FakeDesktopIconProvider();
        var dir = TempDir(); // root kept alive for the whole test run
        var icons = new System.Collections.Generic.List<DesktopIcon>
        {
            FolderIcon(0, "资料A", CreateSubdir(dir, "资料A")),
            FolderIcon(1, "资料B", CreateSubdir(dir, "资料B")),
            FolderIcon(2, "资料C", CreateSubdir(dir, "资料C")),
            FolderIcon(3, "资料D", CreateSubdir(dir, "资料D")),
            FileIcon(4, "报告"),
            FileIcon(5, "清单"),
        };
        for (var i = 0; i < icons.Count; i++) provider.Icons.Add(icons[i]);
        foreach (var ic in icons) provider.SetPosition(ic.Index, new PointI(200 + ic.Index * 110, 120));

        var engine = new ClassifierEngine();
        var config = new ClassifierConfig();
        return (provider, new DesktopLayoutService(provider, engine, config));
    }

    private static string CreateSubdir(string root, string name)
        => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    [Fact]
    public void ArrangeOneFence_PacksOnlyTheTargetBox()
    {
        var (provider, service) = Build();
        var before = provider.GetIcons().ToDictionary(i => i.Index, i => i.Position);

        // Fixture sanity: the folder icons really classify into the 文件夹 box, or every assertion
        // below would silently pass over an empty set (a false green).
        var folders = provider.GetIcons().Where(ic =>
            BoxGrouping.FromEntry(new SoftwareGroupingConfig(), ic.Name, ic.Path, null).Title == "文件夹").ToList();
        Assert.Equal(4, folders.Count);

        var report = service.ArrangeOneFence("文件夹", new RectI(100, 100, 320, 220));

        // 4 folder icons must be placed inside the rectangle…
        Assert.Equal(4, report.Count);
        foreach (var (icon, target) in report)
        {
            Assert.Equal("文件夹", BoxGrouping.FromEntry(new SoftwareGroupingConfig(), icon.Name, icon.Path, null).Title);
            Assert.True(target.X >= 100 && target.X < 420, $"x={target.X} outside rect");
            Assert.True(target.Y >= 100 && target.Y < 320, $"y={target.Y} outside rect");
        }

        // …and the file box must be untouched.
        foreach (var i in new[] { 4, 5 })
            Assert.Equal(before[i], provider.GetPosition(i));
    }

    [Fact]
    public void ArrangeOneFence_WidthDrivesColumnCount()
    {
        var (provider, service) = Build();

        var wide = service.ArrangeOneFence("文件夹", new RectI(100, 100, 640, 220));
        var narrow = service.ArrangeOneFence("文件夹", new RectI(100, 400, 160, 220));

        var wideXs = wide.Select(t => t.Target.X).Distinct().Count();
        var narrowXs = narrow.Select(t => t.Target.X).Distinct().Count();
        Assert.True(wideXs > narrowXs, $"wide box should use more columns (wide={wideXs}, narrow={narrowXs})");
    }

    [Fact]
    public void ArrangeOneFence_TinyBox_ClampsInsideWithoutOverflow()
    {
        var (provider, service) = Build();

        // A rectangle barely big enough for one cell must never push an icon past its right edge.
        var report = service.ArrangeOneFence("文件夹", new RectI(100, 100, 60, 60));
        Assert.Equal(4, report.Count);
        foreach (var (_, target) in report)
        {
            Assert.True(target.X >= 100 && target.X + 96 <= 160 + 96, $"x={target.X} escaped");
            Assert.True(target.Y >= 100, $"y={target.Y} above the box");
        }
    }

    [Fact]
    public void ArrangeIntoFence_SkipTitles_LeavesThoseBoxesAlone()
    {
        var (provider, service) = Build();
        var before = provider.GetIcons().ToDictionary(i => i.Index, i => i.Position);

        service.ArrangeIntoFence(new RectI(0, 0, 1000, 600), 5, FenceSortMode.Name, skipTitles: new[] { "文件夹" });

        // Folder icons are untouched (their layout is pinned); file icons moved into the fence.
        foreach (var i in new[] { 0, 1, 2, 3 })
            Assert.Equal(before[i], provider.GetPosition(i));
        foreach (var i in new[] { 4, 5 })
            Assert.NotEqual(before[i], provider.GetPosition(i));
    }

    /// <summary>
    /// Regression (2026-09-08): with the grid known, EVERY arranged target must sit exactly on
    /// the lattice (origin + k·pitch) — Explorer's per-write snapping then becomes identity, so
    /// no half-cell drift can push edge icons off-screen or tilt one box's phase against
    /// another's (the first-arrange title-overlap bug). Uses a non-zero phase to prove targets
    /// land on origin + k·pitch, not merely on pitch multiples.
    /// </summary>
    [Fact]
    public void ArrangeIntoFence_LatticeAligned_TargetsOnGrid()
    {
        var (provider, service) = Build();
        provider.FakeGridCx = 96;
        provider.FakeGridCy = 96;
        provider.FakeGridOx = 22;
        provider.FakeGridOy = 34;

        var fence = new RectI(0, 0, 1000, 600);
        var report = service.ArrangeIntoFence(fence, 5, FenceSortMode.Name);
        Assert.NotEmpty(report);

        foreach (var (_, _, target) in report)
        {
            Assert.Equal(0, (target.X - 22) % 96);
            Assert.Equal(0, (target.Y - 34) % 96);
            Assert.True(target.X + 96 <= fence.Right, $"x={target.X} cell crosses the right edge");
            Assert.True(target.Y + 96 <= fence.Bottom, $"y={target.Y} cell crosses the bottom edge");
        }
    }

    /// <summary>Row-major shelf: two small boxes with room to spare must sit SIDE BY SIDE in one
    /// row (the old column-major packer stacked them vertically, wasting the wide dimension).</summary>
    [Fact]
    public void ArrangeIntoFence_RowMajor_SideBySide_WhenRoomAllows()
    {
        var (provider, service) = Build();
        var report = service.ArrangeIntoFence(new RectI(0, 0, 1000, 600), 5, FenceSortMode.Name);

        var boxes = BoxesByTitle(report);
        Assert.True(boxes.Count >= 2, $"expected >=2 boxes, got {boxes.Count}");
        var first = boxes[0];
        foreach (var other in boxes.Skip(1))
        {
            Assert.Equal(first.MinY, other.MinY); // same icon row → same row-of-boxes
            Assert.True(other.MinX > first.MaxX, "boxes must not overlap horizontally");
        }
    }

    /// <summary>When the row runs out of width the next box wraps down, and the vertical pitch
    /// between the upper box's last icon row and the lower box's first icon row must be whole
    /// cells with at least one empty grid row between them (rendered title bands then never touch).</summary>
    [Fact]
    public void ArrangeIntoFence_RowWrap_VerticalGapIsWholeCells()
    {
        var (provider, service) = Build();
        var report = service.ArrangeIntoFence(new RectI(0, 0, 300, 600), 5, FenceSortMode.Name);

        var boxes = BoxesByTitle(report);
        Assert.True(boxes.Count >= 2, $"expected >=2 boxes, got {boxes.Count}");
        for (var i = 1; i < boxes.Count; i++)
        {
            var (upper, lower) = (boxes[i - 1], boxes[i]);
            Assert.True(upper.MaxY < lower.MinY, "boxes must not overlap vertically");
            var pitch = lower.MinY - upper.MaxY;
            Assert.Equal(0, pitch % provider.IconSpacingY);
            Assert.True(pitch >= 2 * provider.IconSpacingY,
                $"only {pitch}px below the upper box's last row — title band would overlap");
        }
    }

    private static List<(string Title, int MinX, int MaxX, int MinY, int MaxY)> BoxesByTitle(
        IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> report)
        => report
            .GroupBy(r => BoxGrouping.FromEntry(new SoftwareGroupingConfig(), r.Icon.Name, r.Icon.Path, null).Title)
            .Select(g => (Title: g.Key,
                MinX: g.Min(r => r.Target.X), MaxX: g.Max(r => r.Target.X),
                MinY: g.Min(r => r.Target.Y), MaxY: g.Max(r => r.Target.Y)))
            .OrderBy(b => b.MinY).ThenBy(b => b.MinX)
            .ToList();

    [Fact]
    public void ArrangeOneFence_UnknownTitle_ReturnsEmptyWithoutMovingAnything()
    {
        var (provider, service) = Build();
        var before = provider.GetIcons().ToDictionary(i => i.Index, i => i.Position);

        Assert.Empty(service.ArrangeOneFence("不存在的框", new RectI(100, 100, 400, 300)));
        foreach (var (i, p) in before) Assert.Equal(p, provider.GetPosition(i));
    }
}
