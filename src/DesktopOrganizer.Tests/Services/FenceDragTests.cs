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
/// Headless regression tests for the box drag gesture. Dragging used to feel rubber-banded
/// ("果冻感") because two things moved independently: the WPF frame slid with the cursor inside the
/// mouse-move handler, while the real desktop icons it holds still had to make a cross-process
/// round trip each — so the contents visibly trailed their own frame. The gesture now pushes both
/// from one delta in one tick, translates them as a rigid body, and coalesces the mouse-move storm
/// into one Win32 burst per frame. These tests pin all four behaviours.
///
/// Like the other controller tests, box membership comes from the REAL classification and every
/// persistence path is routed to a scratch dir; the drag frame interval is injected as 0 so a move
/// is deterministic instead of depending on wall-clock timing.
/// </summary>
public class FenceDragTests
{
    private const string BoxA = "文件夹";
    private const string BoxB = "文件";

    // Margins the drag clamp keeps from the injected screen edges (FenceOverlayController.LayoutMargin).
    private const int Margin = 32;

    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    private sealed record Fixture(
        FenceOverlayController Controller,
        FakeDesktopIconProvider Provider,
        NullOverlayHost Host,
        string Scratch);

    private static Fixture Build(int dragFrameIntervalMs = 0)
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
            dragFrameIntervalMs: dragFrameIntervalMs);
        return new Fixture(controller, provider, host, scratch);
    }

    private static string BoxOf(DesktopIcon ic)
        => BoxGrouping.FromEntry(new SoftwareGroupingConfig(), ic.Name, ic.Path, null).Title;

    private static List<DesktopIcon> IconsIn(Fixture f, string box)
        => f.Provider.GetIcons().Where(ic => BoxOf(ic) == box).ToList();

    [Fact]
    public void DragMoved_TranslatesEveryIconByTheSameDelta()
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
        f.Host.RaiseDragMoved(BoxA, 140, 90);

        // Rigid body: every icon of the dragged box moved by exactly the same delta.
        foreach (var ic in before)
        {
            var now = f.Provider.GetPosition(ic.Index);
            Assert.Equal(ic.Position.X + 140, now.X);
            Assert.Equal(ic.Position.Y + 90, now.Y);
        }
        // …and not one icon of any other box was disturbed.
        foreach (var ic in IconsIn(f, BoxB))
            Assert.Equal(othersBefore[ic.Index], ic.Position);
    }

    [Fact]
    public void DragMoved_RendersTheBoxInTheSameTickAsTheIcons()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // Emulate the window the user actually grabbed: the drag anchors on the rendered rect so a
        // box the user resized keeps its size instead of snapping back to an icon-hugging one.
        f.Host.FenceBoundsOverride = new RectI(300, 350, 420, 300);

        f.Host.RaiseDragStarted(BoxA);
        f.Host.RaiseDragMoved(BoxA, 140, 90);

        // One frame, one move, and it is the grabbed rect translated by the same delta the icons got
        // (this is the anti-rubber-band invariant: the frame never runs ahead of its contents).
        var move = Assert.Single(f.Host.MovedBounds);
        Assert.Equal(BoxA, move.Title);
        Assert.Equal(new RectI(440, 440, 420, 300), move.Bounds);
        Assert.True(f.Provider.GetPosition(IconsIn(f, BoxA)[0].Index).X > 0); // icons really moved too
    }

    [Fact]
    public void DragPastTheScreenEdge_KeepsTheBoxRigidInsteadOfSquishingIt()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // A box whose right edge sits 200px from the (injected) screen edge.
        f.Host.FenceBoundsOverride = new RectI(3400, 400, 400, 300);
        var before = IconsIn(f, BoxA).ToDictionary(ic => ic.Index, ic => ic.Position);
        var spreadBefore = before.Values.Max(p => p.X) - before.Values.Min(p => p.X);
        Assert.True(spreadBefore > 0); // fixture sanity: icons are not stacked on one point

        f.Host.RaiseDragStarted(BoxA);
        f.Host.RaiseDragMoved(BoxA, 5000, 0); // yank it far past the edge

        // The translation is clamped as a whole: the box keeps its exact size and stops at the
        // margin, instead of each icon being dragged back individually (which deformed the box).
        var move = Assert.Single(f.Host.MovedBounds);
        Assert.Equal(400, move.Bounds.Width);
        Assert.Equal(300, move.Bounds.Height);
        Assert.Equal(TestScreen.Right - Margin, move.Bounds.Right);

        // Every icon got the SAME clamped delta — relative spacing is untouched (rigid body).
        var applied = move.Bounds.Left - 3400;
        var after = IconsIn(f, BoxA);
        foreach (var ic in after)
        {
            Assert.Equal(before[ic.Index].X + applied, ic.Position.X);
            Assert.Equal(before[ic.Index].Y, ic.Position.Y);
        }
        Assert.Equal(spreadBefore, after.Max(ic => ic.Position.X) - after.Min(ic => ic.Position.X));
    }

    [Fact]
    public void DragEnded_PinsTheRenderedRectSoRefreshCannotSnapItBack()
    {
        var f = Build();
        f.Controller.ArrangeAndShow();
        // Deliberately a rect that does NOT hug the icons: dragging must pin what the user sees.
        f.Host.FenceBoundsOverride = new RectI(300, 350, 420, 300);

        f.Host.RaiseDragStarted(BoxA);
        f.Host.RaiseDragMoved(BoxA, 140, 90);
        f.Host.RaiseDragEnded(BoxA);

        var pinned = f.Controller.GetFenceLayout(BoxA);
        Assert.NotNull(pinned);
        // Same rectangle that was rendered during the drag → the next refresh re-draws it exactly
        // where the cursor left it (pinning an icon-derived rect here is what made boxes jump).
        Assert.Equal(new FenceLayout(440, 440, 420, 300), pinned);
    }

    [Fact]
    public void RapidMouseMoves_CoalesceIntoOneIconsUpdatePerFrame()
    {
        // A 60s frame interval means nothing can apply on the clock — every move is coalesced until
        // the gesture ends. (Production uses ~12ms; the point is the batching, not the duration.)
        var f = Build(dragFrameIntervalMs: 60_000);
        f.Controller.ArrangeAndShow();
        f.Host.FenceBoundsOverride = new RectI(300, 350, 420, 300);

        f.Host.RaiseDragStarted(BoxA);
        for (var i = 1; i <= 20; i++) f.Host.RaiseDragMoved(BoxA, i * 5, 0);

        // Not twenty bursts of cross-process writes: the storm is swallowed…
        Assert.Empty(f.Host.MovedBounds);
        // …and releasing flushes exactly the final position the cursor reached.
        f.Host.RaiseDragEnded(BoxA);
        var move = Assert.Single(f.Host.MovedBounds);
        Assert.Equal(new RectI(400, 350, 420, 300), move.Bounds);
    }
}
