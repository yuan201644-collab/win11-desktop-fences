using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Services;

/// <summary>
/// Orchestrates desktop-icon grouping: calls the M2 layout to reposition icons into
/// category clusters, then drives the click-through <see cref="FenceOverlayWindow"/> to
/// draw a labeled box around each cluster. Files are never moved — only the desktop
/// ListView layout memory changes, exactly as in M2.
///
/// Before arranging, "Auto arrange" is automatically switched off (clearing the
/// LVS_AUTOARRANGE style) so positions are honored. The whole virtual desktop is used,
/// so multi-monitor layouts are grouped correctly.
///
/// A ticking timer keeps the boxes following manual icon drags and hides the overlay
/// whenever a non-desktop app takes the foreground.
/// </summary>
public sealed class FenceOverlayController : IDisposable
{
    private readonly SysListView32Provider _provider;
    private readonly ClassifierEngine _engine = new();
    private readonly ClassifierConfig _config = new();
    private readonly DesktopLayoutService _layout;
    private readonly FenceOverlayWindow _window;
    private readonly DispatcherTimer _timer;
    private bool _arranged;

    public FenceOverlayController()
    {
        _provider = new SysListView32Provider();
        _layout = new DesktopLayoutService(_provider, _engine, _config);
        _window = new FenceOverlayWindow();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshOverlay();
    }

    // Primary monitor in virtual-screen space. On this machine a secondary monitor sits
    // to the LEFT (x=-2560..0) and the primary is 0..1920 — so packing the primary keeps
    // the grouped icons on the screen the user is actually looking at.
    private (int X, int Y, int Width, int Height) Primary => (
        0, 0, NativeMethods.GetSystemMetrics(0), NativeMethods.GetSystemMetrics(1));

    /// <summary>
    /// Inset kept between the primary-screen edges and the icon grid. Explorer's grid
    /// anchor can render its first column at a slightly negative coordinate when we pack
    /// from x=0, spilling the leftmost icons onto a secondary monitor to the left. Laying
    /// out inside a small margin (but rendering the overlay over the whole screen) keeps
    /// every icon fully on the primary display.
    /// </summary>
    private const int LayoutMargin = 32;

    /// <summary>
    /// Switches off "Auto arrange", repositions the desktop icons into category clusters
    /// across the whole virtual desktop, then draws the overlay over them.
    /// </summary>
    public void ArrangeAndShow()
    {
        if (!_provider.IsAvailable) return;

        // Positions are ignored while "Auto arrange" is on — silently clear it first.
        if (!_provider.DisableAutoArrange())
        {
            _window.SetVisible(false);
            MessageBox.Show(
                "无法关闭桌面的「自动排列图标」。\n请手动关闭后重试：桌面右键 → 查看 → 取消勾选「自动排列图标」。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Allow the shell a moment to apply the style change before we move icons.
        System.Threading.Thread.Sleep(200);

        var (x, y, w, h) = Primary;
        var spacingY = _provider.IconSpacingY;
        // Row budget matches the inset layout rect (minus LayoutMargin top/bottom) so icons
        // never overflow the visible desktop area.
        var maxRows = Math.Max(1, (h - LayoutMargin * 2) / Math.Max(1, spacingY));
        try
        {
            // Layout inside a margin so no icon sits flush against (or over) a screen edge;
            // the overlay below still renders across the full screen.
            _layout.ArrangeIntoFence(new RectI(x + LayoutMargin, y + LayoutMargin, w - LayoutMargin * 2, h - LayoutMargin * 2), maxRows);
        }
        catch (DesktopAutoArrangeException)
        {
            // Re-enabled between Disable and here (or the style clear didn't stick).
            _window.SetVisible(false);
            MessageBox.Show(
                "桌面仍处于「自动排列图标」状态，图标位置已被忽略。\n" +
                "请手动关闭：桌面右键 → 查看 → 取消勾选「自动排列图标」，然后重试。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _arranged = true;
        _timer.Start();
        RefreshOverlay();
    }

    /// <summary>
    /// Rebuilds cluster geometry from the desktop's *current* icon state (so manual
    /// drags are followed) and shows/hides the overlay based on what is foreground.
    /// </summary>
    public void RefreshOverlay()
    {
        if (!_arranged || !_provider.IsAvailable)
        {
            _window.SetVisible(false);
            return;
        }
        if (!DesktopShellStatus.IsOverlayAllowed())
        {
            _window.SetVisible(false);
            return;
        }

        var icons = _provider.GetIcons();
        // Group by top-level item kind (软件/文件夹/文件/其他), keeping a stable order.
        var placed = new List<(string Group, PointI Position)>();
        foreach (var kind in new[] { ItemKind.Software, ItemKind.Folder, ItemKind.File, ItemKind.Other })
            placed.AddRange(
                icons.Where(ic => ItemKindClassifier.FromEntry(ic.Name, ic.Path) == kind)
                     .Select(ic => (ItemKindClassifier.Title(kind), ic.Position)));
        // Small pad so adjacent boxes stay distinguishable without fusing into one blob.
        // Kept tiny so vertically-adjacent clusters (dense mode) don't overlap much.
        var clusters = FenceClusterBuilder.Build(placed, _provider.IconSpacingX, _provider.IconSpacingY, pad: 2);

        var (x, y, w, h) = Primary;
        _window.Render(x, y, w, h, clusters);
        _window.SetVisible(true);
    }

    public void Dispose()
    {
        _timer.Stop();
        _provider.Dispose();
    }
}