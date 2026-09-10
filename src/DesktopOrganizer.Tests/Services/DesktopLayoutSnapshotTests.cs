using System;
using System.Collections.Generic;
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

/// <summary>
/// Whole-desktop snapshot tests: a realistic icon set plus a set of remembered (fixed) rectangles,
/// run through ONE <see cref="DesktopLayoutService.ArrangeAll"/>. These are the guard rails the
/// layout rewrite lacked — every previous defect (icons piled onto one cell, auto boxes covering a
/// box the user had positioned, half-cell snap drift) was only ever caught by hand on the real
/// desktop, and one self-inflicted regression slipped through except for a lucky unrelated test.
///
/// Invariants asserted together: no two icons share a cell, every placement is inside the screen
/// AND on the lattice, no icon is lost, and no auto-packed icon lands inside a remembered box.
/// </summary>
public class DesktopLayoutSnapshotTests
{
    private const int CellW = 76;
    private const int CellH = 82;
    private const int GridOx = 22;
    private const int GridOy = 2;

    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fence-snapshot-" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed record Fixture(FakeDesktopIconProvider Provider, DesktopLayoutService Service);

    /// <summary>~120 icons across the folder / file / software boxes, on a real lattice pitch+phase
    /// (76×82 with the measured 22/2 offset), so the packing really goes through the lattice path.</summary>
    private static Fixture Build()
    {
        var provider = new FakeDesktopIconProvider
        {
            IconSpacingX = CellW,
            IconSpacingY = CellH,
            FakeGridCx = CellW,
            FakeGridCy = CellH,
            FakeGridOx = GridOx,
            FakeGridOy = GridOy,
        };

        // One real directory stands in for every folder icon: classification only asks whether the
        // path IS a directory, and 24 real subdirectories would just slow the fixture down.
        var dir = TempDir();
        var icons = new List<DesktopIcon>();
        var index = 0;
        for (var i = 0; i < 24; i++)
            icons.Add(new DesktopIcon(index++, $"资料{i:00}", dir, new PointI(400, 300)));
        for (var i = 0; i < 60; i++)
            icons.Add(new DesktopIcon(index++, $"文档{i:00}", $"C:\\fake\\文档{i:00}.txt", new PointI(400, 300)));
        for (var i = 0; i < 36; i++)
            icons.Add(new DesktopIcon(index++, $"工具{i:00}.exe", $"C:\\fake\\工具{i:00}.exe", new PointI(400, 300)));

        foreach (var ic in icons) provider.Icons.Add(ic);
        foreach (var ic in icons) provider.SetPosition(ic.Index, new PointI(400 + ic.Index * 10, 300));

        return new Fixture(provider, new DesktopLayoutService(provider, new ClassifierEngine(), new ClassifierConfig()));
    }

    private static readonly IReadOnlyDictionary<string, RectI> NoPins = new Dictionary<string, RectI>();

    private static bool Intersects(RectI a, PointI target)
        => a.Left < target.X + CellW && target.X < a.Right
           && a.Top < target.Y + CellH && target.Y < a.Bottom;

    [Fact]
    public void ArrangeAll_Snapshot_NoPileUp_NoOverlap_NoLostIcon()
    {
        var f = Build();
        var screen = new RectI(0, 0, 1920, 1080);
        var layout = new RectI(32, 32, 1920 - 64, 1080 - 64);
        var pinned = new Dictionary<string, RectI>(StringComparer.OrdinalIgnoreCase)
        {
            // Two boxes the user dragged out, sized and left in the middle of the desktop.
            ["文件夹"] = new RectI(64, 64, 520, 420),
            ["文件"] = new RectI(700, 64, 1150, 420),
        };

        var outcome = f.Service.ArrangeAll(layout, 12, FenceSortMode.Name, pinned, screen);

        // Every icon is accounted for — placed OR explicitly reported as unplaced (never dropped).
        Assert.Equal(f.Provider.Icons.Count, outcome.Entries.Count + outcome.Unplaced.Count);
        Assert.NotEmpty(outcome.Entries);

        var points = outcome.Entries.Select(e => e.Target).ToList();

        // No two icons share a lattice cell.
        Assert.Equal(points.Count, points.Distinct().Count());

        // Everything the arrange placed is inside the screen.
        foreach (var p in points)
        {
            Assert.InRange(p.X, screen.Left, screen.Right - CellW);
            Assert.InRange(p.Y, screen.Top, screen.Bottom - CellH);
        }

        // …and every AUTO-packed icon is exactly on the lattice (Explorer's per-write snapping must
        // be the identity, or boxes drift against their icons). Icons inside a remembered box are
        // laid out by PackInRect, which anchors to the rectangle's edge padding, not the grid — that
        // is a separate (pre-existing) path and is asserted only for containment.
        foreach (var e in outcome.Entries.Where(e => !pinned.ContainsKey(e.Title)))
        {
            Assert.Equal(0, (e.Target.X - GridOx) % CellW);
            Assert.Equal(0, (e.Target.Y - GridOy) % CellH);
        }

        // Icons inside a remembered box stay inside it.
        foreach (var e in outcome.Entries.Where(e => pinned.ContainsKey(e.Title)))
        {
            var rect = pinned[e.Title];
            Assert.InRange(e.Target.X, rect.Left, rect.Right - CellW);
            Assert.InRange(e.Target.Y, rect.Top, rect.Bottom - CellH);
        }

        // No auto-packed icon lands inside a remembered box.
        foreach (var e in outcome.Entries.Where(e => !pinned.ContainsKey(e.Title)))
            foreach (var (title, rect) in pinned)
                Assert.False(Intersects(rect, e.Target),
                    $"auto box '{e.Title}' icon at ({e.Target.X},{e.Target.Y}) landed inside remembered box '{title}'");
    }

    /// <summary>When remembered boxes tile the whole desktop, the auto icons must KEEP their current
    /// position rather than be clamped onto a shared cell. This is the second half of the pile-up
    /// fix: the old code clamped every overflow icon into the last cell instead of reporting it.
    /// Reverse-verified: dropping the obstacle input makes this test fail (no unplaced icons, and
    /// the overflow stacked onto one point).</summary>
    [Fact]
    public void ArrangeAll_WhenRememberedBoxesFillTheScreen_AutoIconsStayPut()
    {
        var f = Build();
        var screen = new RectI(0, 0, 1920, 1080);
        var layout = new RectI(32, 32, 1920 - 64, 1080 - 64);
        var pinned = new Dictionary<string, RectI>(StringComparer.OrdinalIgnoreCase)
        {
            ["文件夹"] = new RectI(32, 32, 900, 1016),
            ["文件"] = new RectI(960, 32, 928, 1016),
        };
        // Snapshot the LIVE positions (the icon records' Position field is the construction-time
        // value; only the provider's store tracks writes).
        var before = f.Provider.Icons.ToDictionary(i => i.Index, i => f.Provider.GetPosition(i.Index));

        var outcome = f.Service.ArrangeAll(layout, 12, FenceSortMode.Name, pinned, screen);

        Assert.NotEmpty(outcome.Unplaced);
        foreach (var icon in outcome.Unplaced)
            Assert.Equal(before[icon.Index], f.Provider.GetPosition(icon.Index));
    }

    /// <summary>A remembered box with room to spare must come back unchanged, and the pins must not
    /// be disturbed by the auto pass around them.</summary>
    [Fact]
    public void ArrangeAll_Snapshot_RememberedBoxesKeepTheirRectangle()
    {
        var f = Build();
        var screen = new RectI(0, 0, 1920, 1080);
        var roomy = new RectI(1200, 600, 620, 400);
        var pinned = new Dictionary<string, RectI>(StringComparer.OrdinalIgnoreCase) { ["文件夹"] = roomy };

        var outcome = f.Service.ArrangeAll(new RectI(32, 32, 1856, 1016), 12, FenceSortMode.Name, pinned, screen);

        Assert.Equal(roomy, outcome.FenceRects["文件夹"]);
    }

    /// <summary>Sanity: with nothing remembered the snapshot still satisfies every invariant (the
    /// auto-only path is the common case on a fresh desktop).</summary>
    [Fact]
    public void ArrangeAll_Snapshot_NoPins_StillLandsOnTheLatticeWithoutPileUp()
    {
        var f = Build();
        var screen = new RectI(0, 0, 1920, 1080);

        var outcome = f.Service.ArrangeAll(new RectI(32, 32, 1856, 1016), 12, FenceSortMode.Name, NoPins, screen);

        Assert.Empty(outcome.Unplaced);
        var points = outcome.Entries.Select(e => e.Target).ToList();
        Assert.Equal(points.Count, points.Distinct().Count());
        Assert.All(points, p =>
        {
            Assert.Equal(0, (p.X - GridOx) % CellW);
            Assert.Equal(0, (p.Y - GridOy) % CellH);
        });
    }
}
