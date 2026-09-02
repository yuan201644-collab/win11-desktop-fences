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
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"));
        return (controller, provider, host);
    }

    private static int TotalIcons(NullOverlayHost host) => host.LastClusters.Sum(c => c.IconCount);

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
