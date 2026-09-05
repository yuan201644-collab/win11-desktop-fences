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
/// Headless regression tests for the box drag gesture. Two independent movers (the WPF frame and
/// the cross-process icons) used to advance at different speeds, which read as a rubber band
/// ("果冻感"); a same-frame submission scheme tamed it but kept every frame paying cross-process
/// writes. The final design removes the per-frame traffic entirely: on drag start the controller
/// PARKS the box's icons off-screen (the collapse trick), the window glides with the cursor on its
/// own, and on release every icon reappears once, already at its final spot. These tests pin that
/// contract: park on start, silence during the move, rigid restore + pin + clamp on release.
///
/// Like the other controller tests, box membership comes from the REAL classification and every
/// persistence path is routed to a scratch dir.
/// </summary>
public class FenceDragTests
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
            Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Drag", Guid.NewGuid().ToString("N"))).FullName;

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
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Drag", Guid.NewGuid().ToString("N"));
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
    public void DragStarted_ParksTheBoxIconsOffScreenAndLeavesTheRestAlone()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        var before = IconsIn(f, BoxA);
        // Fixture sanity: three icons really live in the dragged box and three in the other one —
        // otherwise "nothing moved" would pass over an empty set (a false green).
        Assert.Equal(3, before.Count);
        Assert.Equal(3, IconsIn(f, BoxB).Count);
        var othersBefore = IconsIn(f, BoxB).ToDictionary(ic => ic.Index, ic => ic.Position);

        f.Host.RaiseDragStarted(BoxA);

        // Every icon of the dragged box vanished into the park pocket (far off-screen, same slot
        // space a collapse uses) — that is what makes the drag frame free of cross-process writes.
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
    public void DragMoved_WritesNothingCrossProcess()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        f.Host.FenceBoundsOverride = new RectI(300, 350, 420, 300);
        f.Host.RaiseDragStarted(BoxA);
        var parked = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => f.Provider.GetPosition(ic.Index));
        // Fixture sanity: the snapshot really is parked state, not accidentally the original spots.
        Assert.All(parked.Values, p => Assert.True(p.X < -30000, $"not parked: {p}"));

        for (var i = 1; i <= 20; i++) f.Host.RaiseDragMoved(BoxA, i * 10, i * 5);

        // The whole point of hiding: a move costs zero SetPosition (icons stay parked) and zero
        // SetFenceBounds (the window glides itself). Nothing can lag behind anything.
        foreach (var ic in IconsIn(f, BoxA))
            Assert.Equal(parked[ic.Index], f.Provider.GetPosition(ic.Index));
        Assert.Empty(f.Host.MovedBounds);
    }

    [Fact]
    public void DragEnded_PlacesEveryIconAtTheDroppedRectRigidly()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // Emulate the window the user actually grabbed…
        f.Host.FenceBoundsOverride = new RectI(300, 350, 420, 300);
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);
        var spreadBefore = before.Values.Max(p => p.X) - before.Values.Min(p => p.X);
        Assert.True(spreadBefore > 0); // fixture sanity: icons are not stacked on one point
        var othersBefore = IconsIn(f, BoxB).ToDictionary(ic => ic.Index, ic => ic.Position);
        f.Host.RaiseDragStarted(BoxA);
        // …the window glides itself to the drop spot, then the gesture ends.
        f.Host.FenceBoundsOverride = new RectI(440, 440, 420, 300);
        f.Host.RaiseDragMoved(BoxA, 140, 90);
        f.Host.RaiseDragEnded(BoxA);

        // Rigid restore: every icon got the SAME delta and is back on screen.
        var after = IconsIn(f, BoxA);
        Assert.NotEmpty(after);
        foreach (var ic in after)
        {
            Assert.Equal(before[ic.Index].X + 140, ic.Position.X);
            Assert.Equal(before[ic.Index].Y + 90, ic.Position.Y);
        }
        Assert.Equal(spreadBefore, after.Max(ic => ic.Position.X) - after.Min(ic => ic.Position.X));
        // The window was already where the cursor left it — no corrective snap was needed…
        Assert.Empty(f.Host.MovedBounds);
        // …and the drop rect is pinned, so the next refresh cannot auto-pack the box away.
        Assert.Equal(new FenceLayout(440, 440, 420, 300), f.Controller.GetFenceLayout(BoxA));
        // Other boxes untouched.
        foreach (var ic in IconsIn(f, BoxB))
            Assert.Equal(othersBefore[ic.Index], ic.Position);
    }

    [Fact]
    public void DragEnded_ClampsAnOffScreenDropAndSnapsTheWindowBack()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // A box whose right edge sits 200px from the (injected) screen edge.
        f.Host.FenceBoundsOverride = new RectI(3400, 400, 400, 300);
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);
        f.Host.RaiseDragStarted(BoxA);
        // Yank it far past the edge: the window reports a drop spot off-screen.
        f.Host.FenceBoundsOverride = new RectI(3900, 400, 400, 300);
        f.Host.RaiseDragMoved(BoxA, 5000, 0);
        f.Host.RaiseDragEnded(BoxA);

        // The drop is clamped back onto the screen: right edge exactly at the screen edge…
        var pinned = f.Controller.GetFenceLayout(BoxA);
        Assert.NotNull(pinned);
        Assert.Equal(new FenceLayout(3600, 400, 400, 300), pinned);
        // …every icon got the SAME clamped delta (rigid body, no deformation)…
        foreach (var ic in IconsIn(f, BoxA))
        {
            Assert.Equal(before[ic.Index].X + 200, ic.Position.X);
            Assert.Equal(before[ic.Index].Y, ic.Position.Y);
        }
        // …and the window was snapped from its off-screen drop spot to the clamped one.
        var snap = Assert.Single(f.Host.MovedBounds);
        Assert.Equal(BoxA, snap.Title);
        Assert.Equal(new RectI(3600, 400, 400, 300), snap.Bounds);
    }

    [Fact]
    public void BareClick_RestoresIconsWithoutPinningANewLayout()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);

        // The park now happens at press time (the fix for the start-of-drag jolt), so a bare
        // click — press then release, dead-zone never crossed — still opens a gesture that the
        // release MUST close, or the icons would be left hidden on the desktop.
        f.Host.RaiseDragStarted(BoxA);
        Assert.All(IconsIn(f, BoxA), ic => Assert.True(ic.Position.X < -30000, "not parked at press"));
        f.Host.RaiseDragEnded(BoxA);

        // Zero-delta restore: every icon is exactly where it was, and clicking a title does not
        // turn into a layout decision (no pin is created for the previously unpinned box).
        foreach (var ic in IconsIn(f, BoxA))
            Assert.Equal(before[ic.Index], ic.Position);
        Assert.Null(f.Controller.GetFenceLayout(BoxA));
    }

    [Fact]
    public void DragEnded_WithoutLiveGeometry_FallsBackToTheLastCumulativeDelta()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // No FenceBoundsOverride: the host "never drew a window", so the drag anchors on the
        // icon-derived rect and the release must fall back to the last reported delta.
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);
        f.Host.RaiseDragStarted(BoxA);
        f.Host.RaiseDragMoved(BoxA, 140, 90);
        f.Host.RaiseDragEnded(BoxA);

        foreach (var ic in IconsIn(f, BoxA))
        {
            Assert.Equal(before[ic.Index].X + 140, ic.Position.X);
            Assert.Equal(before[ic.Index].Y + 90, ic.Position.Y);
        }
        Assert.NotNull(f.Controller.GetFenceLayout(BoxA));
    }
}
