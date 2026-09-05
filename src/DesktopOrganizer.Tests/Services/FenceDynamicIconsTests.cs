using System;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Headless tests for the controller under RUNTIME desktop churn — the user dragging a new icon onto
/// the desktop, deleting one, or nudging one to a new spot. The overlay must re-cluster on the next
/// refresh without throwing and must keep every on-screen icon represented (none silently dropped,
/// none duplicated). These are the churn cases a static arrange never exercises.
///
/// All icons use real directory paths so they classify into the same "文件夹" box (the real
/// classification drives grouping, not the test resolver), keeping the assertions deterministic.
/// Virtual-screen rect and collapse path are injected.
/// </summary>
public class FenceDynamicIconsTests
{
    private const string BoxTitle = "文件夹";
    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    private static (FenceOverlayController controller, FakeDesktopIconProvider provider, NullOverlayHost host)
        Build(int iconCount)
    {
        var provider = new FakeDesktopIconProvider();
        for (int i = 0; i < iconCount; i++)
        {
            var pos = new PointI(80 + i * 120, 80);
            provider.Icons.Add(new DesktopIcon(i, $"资料{i}", $@"C:\fake\dir{i}", pos));
            provider.SetPosition(i, pos);
        }
        var host = new NullOverlayHost();
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var controller = new FenceOverlayController(
            provider, host, _ => BoxTitle,
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"),
            layoutFilePath: Path.Combine(scratch, "fence-layout.json"),
            colorFilePath: Path.Combine(scratch, "fence-colors.json"),
            boxInsetFilePath: Path.Combine(scratch, "fence-box-insets.json"),
            fenceInsetFilePath: Path.Combine(scratch, "fence-inset.json"),
            desktopLayoutFilePath: Path.Combine(scratch, "layout.json"),
            liveSortFilePath: Path.Combine(scratch, "live-sort.json"));
        return (controller, provider, host);
    }

    private static int TotalIcons(NullOverlayHost host) => host.LastClusters.Sum(c => c.IconCount);

    [Fact]
    public void LiveSort_NewIcon_FiledBelowItsBoxBounds()
    {
        // Enable BEFORE arranging: icons present at enable-time are the baseline and are never
        // filed — only icons that appear afterwards are.
        var (controller, provider, host) = Build(3);
        controller.LiveSortEnabled = true;
        controller.ArrangeAndShow();
        Assert.Equal(3, TotalIcons(host));

        // A brand-new icon appears somewhere far from its box.
        provider.Icons.Add(new DesktopIcon(42, "资料新", @"C:\fake\dir-new3", new PointI(1500, 700)));
        provider.SetPosition(42, new PointI(1500, 700));

        controller.ForceRefresh();

        // Filed: just below the box's other members, in their column — not where it appeared.
        var filed = provider.GetPosition(42);
        var members = provider.GetIcons().Where(ic => ic.Index != 42).ToList();
        Assert.NotEqual(new PointI(1500, 700), filed);
        Assert.True(filed.Y > members.Max(ic => ic.Position.Y),
            $"filed y={filed.Y} should be below every member (max {members.Max(ic => ic.Position.Y)})");
        Assert.True(filed.X <= members.Min(ic => ic.Position.X) + provider.IconSpacingX,
            $"filed x={filed.X} should be in the box's left column");
        // Filed at most once: a second refresh must not move it again.
        var after = filed;
        controller.ForceRefresh();
        Assert.Equal(after, provider.GetPosition(42));
    }

    [Fact]
    public void LiveSort_Disabled_NewIconLeftAlone()
    {
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();
        Assert.False(controller.LiveSortEnabled); // default OFF — never move icons without consent

        provider.Icons.Add(new DesktopIcon(42, "资料新", @"C:\fake\dir-new4", new PointI(1500, 700)));
        provider.SetPosition(42, new PointI(1500, 700));

        controller.ForceRefresh();
        Assert.Equal(new PointI(1500, 700), provider.GetPosition(42));
    }

    [Fact]
    public void LiveSort_CollapsedBox_NewIconSkipped()
    {
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();
        controller.ToggleFence(BoxTitle); // collapse the box
        controller.LiveSortEnabled = true;

        provider.Icons.Add(new DesktopIcon(42, "资料新", @"C:\fake\dir-new5", new PointI(1500, 700)));
        provider.SetPosition(42, new PointI(1500, 700));

        controller.ForceRefresh();
        Assert.Equal(new PointI(1500, 700), provider.GetPosition(42));
    }

    [Fact]
    public void ExplorerRestart_ProviderRecoversOnNextRefresh_MeshRebuilt()
    {
        // Design gap "Explorer 重启 watcher": after an Explorer restart the provider's cached
        // window handle is dead and RefreshOverlay used to early-return forever — the app stayed
        // broken until manually restarted. The controller now gives the provider one re-acquire
        // attempt per tick and rebuilds the mesh as soon as it succeeds.
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();
        Assert.Equal(3, TotalIcons(host));

        // The shell restarts: the provider goes away, and a new icon exists in the new shell.
        provider.IsAvailable = false;
        provider.Icons.Add(new DesktopIcon(3, "资料新", @"C:\fake\dir-new2", new PointI(1000, 400)));
        provider.SetPosition(3, new PointI(1000, 400));

        // Recovery fails this tick: the mesh must NOT be rebuilt with the dead shell yet.
        provider.TryRecoverHook = () => false;
        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
        Assert.Equal(3, TotalIcons(host));

        // Recovery succeeds on a later tick: one re-acquire attempt per refresh, and the mesh is
        // rebuilt from the fresh shell — the new icon is clustered in.
        provider.TryRecoverHook = () => { provider.IsAvailable = true; return true; };
        controller.ForceRefresh();
        Assert.Equal(4, TotalIcons(host));
    }

    [Fact]
    public void RuntimeIconAdded_RefreshRegroupsNewIconWithoutLoss()
    {
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();
        Assert.Equal(3, TotalIcons(host));

        // User drags a brand-new folder onto the desktop.
        int newIdx = 42;
        provider.Icons.Add(new DesktopIcon(newIdx, "资料新", @"C:\fake\dir-new", new PointI(1000, 400)));
        provider.SetPosition(newIdx, new PointI(1000, 400));

        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
        // The new icon is clustered in — none lost, none duplicated.
        Assert.Equal(4, TotalIcons(host));
        Assert.Contains(host.LastClusters, c => c.Title == BoxTitle && c.IconCount == 4);
    }

    [Fact]
    public void RuntimeIconRemoved_RefreshStaysConsistent()
    {
        var (controller, provider, host) = Build(4);
        controller.ArrangeAndShow();
        Assert.Equal(4, TotalIcons(host));

        // User deletes an icon off the desktop.
        provider.Icons.RemoveAt(0);

        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
        Assert.Equal(3, TotalIcons(host));
        Assert.Contains(host.LastClusters, c => c.Title == BoxTitle && c.IconCount == 3);
    }

    [Fact]
    public void RuntimeIconMoved_RefreshStaysConsistent()
    {
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();
        int before = TotalIcons(host);
        Assert.Equal(3, before);

        // User drags an icon to a new spot (still on-screen).
        provider.SetPosition(1, new PointI(1500, 600));

        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
        // No icon lost or duplicated by the move.
        Assert.Equal(3, TotalIcons(host));
    }

    [Fact]
    public void RuntimeIconMovedOffScreen_IsDroppedNotStretched()
    {
        var (controller, provider, host) = Build(3);
        controller.ArrangeAndShow();

        // An icon somehow ends up far past the virtual desktop (a truncated/bogus coordinate).
        provider.SetPosition(1, new PointI(90000, 600));

        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
        // The stray point is dropped from the box bounds, not used to inflate it.
        Assert.Equal(3, TotalIcons(host));
        var box = Assert.Single(host.LastClusters, c => c.Title == BoxTitle);
        Assert.True(box.Bounds.Right <= TestScreen.Right, $"stray point stretched the box: {box.Bounds}");
    }
}
