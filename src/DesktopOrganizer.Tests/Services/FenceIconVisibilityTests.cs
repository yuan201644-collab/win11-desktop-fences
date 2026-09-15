using System;
using System.IO;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// The 查看 → 显示桌面图标 seam: unchecking it makes Explorer HIDE the desktop listview itself
/// while the window (and its icon data) stays alive. The fences are independent top-level windows,
/// so before the <see cref="IDesktopIconProvider.AreDesktopIconsVisible"/> gate they kept floating
/// as empty frames over an empty desktop (2026-09-15 user report「不能隐藏」). These tests pin the
/// follow behavior: hide with the icons, come back when the icons do — without a re-arrange.
///
/// The injected fake foreground HWND routes <c>ShouldShowFences</c> down its "foreign window
/// without WS_EX_APPWINDOW → shown" branch deterministically (native lookups fail safe on a
/// non-window handle), so the gate is the ONLY thing that can flip the verdict — a regression that
/// removes the gate turns <see cref="HideFollowsIcons_MeshHidesWhileDataStaysReadable"/> red.
/// </summary>
public class FenceIconVisibilityTests
{
    private const string BoxTitle = "文件夹";
    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    // Not a real window: GetWindowThreadProcessId fails (pid 0), IsShellProcess(false) via the
    // ArgumentException guard, GetWindowLongPtr yields no WS_EX_APPWINDOW → verdict SHOWN.
    private static readonly IntPtr FakeForeground = new(0x1234);

    private static (FenceOverlayController controller, FakeDesktopIconProvider provider, NullOverlayHost host)
        Build(bool iconsVisible = true)
    {
        var provider = new FakeDesktopIconProvider { AreDesktopIconsVisible = iconsVisible };
        for (int i = 0; i < 6; i++)
        {
            var pos = new PointI(80 + i * 120, 80);
            provider.Icons.Add(new DesktopIcon(i, $"图标{i}", $@"C:\fake\icon{i}.lnk", pos));
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
            liveSortFilePath: Path.Combine(scratch, "live-sort.json"),
            foregroundWindowProvider: () => FakeForeground);
        return (controller, provider, host);
    }

    [Fact]
    public void IconsVisible_BenignForeignForeground_MeshShown()
    {
        // Baseline sanity: with icons drawn and a benign foreground the mesh shows — proving the
        // hide test below is discriminating the icons gate, not merely the foreground rule.
        var (controller, _, host) = Build(iconsVisible: true);
        controller.ArrangeAndShow();
        Assert.True(host.Visible);
    }

    [Fact]
    public void HideFollowsIcons_MeshHidesWhileDataStaysReadable()
    {
        var (controller, provider, host) = Build(iconsVisible: true);
        controller.ArrangeAndShow();

        // The user unchecks 显示桌面图标: only the VISIBILITY seam flips — the window and its icon
        // data stay alive (IsAvailable true, GetIcons keeps returning the icons).
        provider.AreDesktopIconsVisible = false;
        controller.ForceRefresh();
        Assert.False(host.Visible);

        // The data path must NOT have been suppressed along with the drawing: the icon set is
        // still fully readable (LiveSort / rescue keep running off it).
        Assert.Equal(6, provider.Count);
    }

    [Fact]
    public void RestoreIcons_MeshReturns_WithoutRearrange()
    {
        var (controller, provider, host) = Build(iconsVisible: true);
        controller.ArrangeAndShow();
        provider.AreDesktopIconsVisible = false;
        controller.ForceRefresh();
        Assert.False(host.Visible);

        // Re-checking 显示桌面图标 brings the mesh back on the next tick, with the SAME arranged
        // clusters — no manual 整理 needed, no empty mesh flash.
        provider.AreDesktopIconsVisible = true;
        controller.ForceRefresh();
        Assert.True(host.Visible);
        Assert.NotEmpty(host.LastClusters);
    }
}
