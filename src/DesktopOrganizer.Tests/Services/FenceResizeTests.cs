using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Headless regression tests for the box resize gesture. The old live path re-laid-out the icons
/// (throttled cross-process writes) on every mouse move, which read as icon jank while the frame
/// grew. The new contract mirrors the drag: on edge-grab the controller PARKS the box's icons
/// off-screen, the frame resizes itself with zero icon traffic per frame, and on release the icons
/// reappear ONCE, re-packed into the final rect in the adjusted order. These tests pin that
/// contract: park on start, silence during the move, single re-pack + pin on release.
///
/// Like the other controller tests, box membership comes from the REAL classification and every
/// persistence path is routed to a scratch dir.
/// </summary>
public class FenceResizeTests
{
    private const string BoxA = "文件夹";
    private const string BoxB = "文件";

    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    private sealed record Fixture(
        FenceOverlayController Controller,
        FakeDesktopIconProvider Provider,
        NullOverlayHost Host,
        string Scratch);

    private static Fixture Build()
    {
        var provider = new FakeDesktopIconProvider();
        var realDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Resize", Guid.NewGuid().ToString("N"))).FullName;

        var icons = new List<DesktopIcon>();
        for (var i = 0; i < 3; i++)
        {
            var sub = Directory.CreateDirectory(Path.Combine(realDir, $"资料{i}")).FullName;
            icons.Add(new DesktopIcon(i, $"资料{i}", sub, new PointI(400 + i * 120, 400)));
        }
        for (var i = 0; i < 3; i++)
            icons.Add(new DesktopIcon(3 + i, $"文档{i}", $@"C:\fake\doc{i}.txt", new PointI(400 + (3 + i) * 120, 700)));
        foreach (var ic in icons)
        {
            provider.Icons.Add(ic);
            provider.SetPosition(ic.Index, ic.Position);
        }

        var host = new NullOverlayHost();
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Resize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var controller = new FenceOverlayController(
            provider, host,
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"),
            layoutFilePath: Path.Combine(scratch, "fence-layout.json"),
            colorFilePath: Path.Combine(scratch, "fence-colors.json"),
            boxInsetFilePath: Path.Combine(scratch, "fence-box-insets.json"),
            fenceInsetFilePath: Path.Combine(scratch, "fence-inset.json"),
            desktopLayoutFilePath: Path.Combine(scratch, "layout.json"),
            liveSortFilePath: Path.Combine(scratch, "live-sort.json"));
        return new Fixture(controller, provider, host, scratch);
    }

    private static string BoxOf(DesktopIcon ic)
        => BoxGrouping.FromEntry(new SoftwareGroupingConfig(), ic.Name, ic.Path, null).Title;

    private static List<DesktopIcon> IconsIn(Fixture f, string box)
        => f.Provider.GetIcons().Where(ic => BoxOf(ic) == box).ToList();

    [Fact]
    public void ResizeStarted_ParksTheBoxIconsOffScreen()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        var before = IconsIn(f, BoxA);
        Assert.Equal(3, before.Count); // fixture sanity: the resized box really has icons
        var othersBefore = IconsIn(f, BoxB).ToDictionary(ic => ic.Index, ic => ic.Position);

        f.Host.RaiseResizeStarted(BoxA);

        // Every icon of the resized box vanished into the park pocket (far off-screen, the same
        // slot space a collapse/drag uses)…
        foreach (var ic in before)
        {
            var now = f.Provider.GetPosition(ic.Index);
            Assert.True(now.X < -30000 && now.Y < -30000, $"icon {ic.Index} not parked: {now}");
        }
        // …and not one icon of any other box was disturbed.
        foreach (var ic in IconsIn(f, BoxB))
            Assert.Equal(othersBefore[ic.Index], ic.Position);
    }

    [Fact]
    public void ResizeMoved_WritesOnlyWindowGeometryAndSurvivesARefresh()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        f.Host.RaiseResizeStarted(BoxA);
        var parked = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => f.Provider.GetPosition(ic.Index));
        Assert.All(parked.Values, p => Assert.True(p.X < -30000, $"not parked: {p}"));

        for (var i = 1; i <= 20; i++)
            f.Host.RaiseResizeMoved(BoxA, new RectI(300, 350, 420 + i * 10, 300));

        // The whole point of hiding: a move costs zero icon SetPosition — the parked coordinates
        // never change — while the window geometry tracks the cursor one-for-one (clamped).
        foreach (var ic in IconsIn(f, BoxA))
            Assert.Equal(parked[ic.Index], f.Provider.GetPosition(ic.Index));
        Assert.Equal(20, f.Host.MovedBounds.Count);
        for (var i = 0; i < 20; i++)
            Assert.Equal(new RectI(300, 350, 430 + i * 10, 300), f.Host.MovedBounds[i].Bounds);

        // The park survives a refresh tick: RescueStrandedIcons must NOT fight the gesture.
        f.Controller.RefreshOverlay();
        foreach (var ic in IconsIn(f, BoxA))
            Assert.Equal(parked[ic.Index], f.Provider.GetPosition(ic.Index));
    }

    [Fact]
    public void ResizeEnded_PacksIconsIntoTheFinalRectOnce()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        var othersBefore = IconsIn(f, BoxB).ToDictionary(ic => ic.Index, ic => ic.Position);
        f.Host.RaiseResizeStarted(BoxA);
        // The window tracked the cursor to a wider final rect, then the gesture ended.
        var final = new RectI(500, 300, 640, 400);
        f.Host.FenceBoundsOverride = final;
        f.Host.RaiseResizeEnded(BoxA);

        // Every icon is back, on screen, inside the final rect…
        var after = IconsIn(f, BoxA).ToList();
        Assert.Equal(3, after.Count);
        foreach (var ic in after)
        {
            var p = ic.Position;
            Assert.True(p.X >= final.X && p.X < final.Right && p.Y >= final.Y && p.Y < final.Bottom,
                $"icon {ic.Index} outside the final rect: {p}");
        }
        // …in the ADJUSTED order: name-sorted icons pack row-major, so the X sequence advances by
        // exactly one cell per icon (single row, no wrap at this width) and Y never varies.
        var ordered = after.OrderBy(ic => ic.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].Position.X + f.Provider.IconSpacingX, ordered[i].Position.X);
            Assert.Equal(ordered[i - 1].Position.Y, ordered[i].Position.Y);
        }
        // The final rect is pinned, so the next refresh cannot auto-pack the box away.
        Assert.Equal(new FenceLayout(final.X, final.Y, final.Width, final.Height),
            f.Controller.GetFenceLayout(BoxA));
        // Other boxes untouched.
        foreach (var ic in IconsIn(f, BoxB))
            Assert.Equal(othersBefore[ic.Index], ic.Position);
    }

    [Fact]
    public void ResizeEnded_WithoutLiveGeometry_RestoresThePreResizePositions()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // No FenceBoundsOverride: the host "never drew a window", so there is no final rect to
        // pack into — the icons must still come back from the park, exactly where they were.
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);
        f.Host.RaiseResizeStarted(BoxA);
        f.Host.RaiseResizeMoved(BoxA, new RectI(300, 350, 800, 500));
        f.Host.RaiseResizeEnded(BoxA);

        foreach (var ic in IconsIn(f, BoxA))
        {
            Assert.Equal(before[ic.Index], ic.Position);
            Assert.True(ic.Position.X > -30000, $"icon {ic.Index} stayed parked: {ic.Position}");
        }
    }
}
