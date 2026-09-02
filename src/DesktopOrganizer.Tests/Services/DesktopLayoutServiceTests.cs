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

    [Fact]
    public void ArrangeOneFence_UnknownTitle_ReturnsEmptyWithoutMovingAnything()
    {
        var (provider, service) = Build();
        var before = provider.GetIcons().ToDictionary(i => i.Index, i => i.Position);

        Assert.Empty(service.ArrangeOneFence("不存在的框", new RectI(100, 100, 400, 300)));
        foreach (var (i, p) in before) Assert.Equal(p, provider.GetPosition(i));
    }
}
