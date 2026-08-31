using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly SoftwareGroupingConfig _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
    private readonly DesktopLayoutService _layout;
    private readonly FenceOverlayWindow _window;
    private readonly DispatcherTimer _timer;
    private bool _arranged;
    private IReadOnlyDictionary<string, PointI> _lastSaved = new Dictionary<string, PointI>();

    // Icon display-name → its box title, resolved once at arrange time. RefreshOverlay reuses
    // it every tick so the overlay box matches the placement without re-resolving .lnk targets
    // (COM) dozens of times per refresh.
    private readonly Dictionary<string, string> _groupTitle = new(StringComparer.OrdinalIgnoreCase);

    // Stable on-screen box order: software purpose boxes (in config order) first, then the
    // software fallback (其他软件), then folder / file / other. Boxes with no icons simply
    // produce no clusters, so "empty boxes" are hidden automatically instead of rendering an
    // empty outline.
    private static readonly string[] KindBoxes = { "文件夹", "文件", "其他" };
    private string[] BoxOrder => _grouping.Groups.Select(g => g.Title)
        .Append(SoftwarePurposeClassifier.FallbackTitle)
        .Concat(KindBoxes).ToArray();

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
        RebuildGroupTitles();
        _timer.Start();
        RefreshOverlay();
        SaveLayout();
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

        // The overlay is a non-topmost, click-through window layered over the desktop icons.
        // It stays resident once arranged: foreground apps overlap it naturally (software over
        // icons), and minimizing/returning to the desktop lets it show through again — no
        // foreground-window coupling is needed.
        var icons = _provider.GetIcons();
        // Group by on-screen box (software split by purpose + folder/file/other), stable order.
        var placed = new List<(string Group, PointI Position)>();
        foreach (var title in BoxOrder)
            placed.AddRange(icons.Where(ic => GroupTitle(ic) == title).Select(ic => (title, ic.Position)));
        // Small pad so adjacent boxes stay distinguishable without fusing into one blob.
        // Kept tiny so vertically-adjacent clusters (dense mode) don't overlap much.
        var clusters = FenceClusterBuilder.Build(
            placed, _provider.IconSpacingX, _provider.IconSpacingY,
            pad: 2, headerPx: FenceHeader.HeaderPx);

        var (x, y, w, h) = Primary;
        _window.Render(x, y, w, h, clusters);
        _window.SetVisible(true);
        SaveLayout(); // follow manual drags so the final layout persists
    }

    /// <summary>
    /// Re-applies the last saved layout (category clusters plus any manual tweaks) to the
    /// desktop and redraws the overlay. No-op detection covers the case where nothing was
    /// ever arranged yet.
    /// </summary>
    public void RestoreSavedLayout()
    {
        if (!_provider.IsAvailable) return;
        var saved = DesktopLayoutStore.Load(LayoutFilePath);
        if (saved.Count == 0)
        {
            _window.SetVisible(false);
            MessageBox.Show(
                "还没有保存的布局。\n请先点击「整理并显示分组」生成一组，再在桌面上手动微调当前位置——调整会被自动记住。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_provider.DisableAutoArrange())
        {
            MessageBox.Show("桌面仍处于「自动排列图标」状态，无法恢复位置。\n请手动关闭：桌面右键 → 查看 → 取消勾选后重试。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (sx, sy, sw, sh) = Primary;
        var spacingX = _provider.IconSpacingX;
        var spacingY = _provider.IconSpacingY;
        var byName = _provider.GetIcons()
            .ToDictionary(ic => ic.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in saved)
        {
            if (!byName.TryGetValue(kv.Key, out var icon)) continue;
            // Re-clamp to the primary screen so a monitor change can't push icons off-screen.
            var x = Math.Clamp(kv.Value.X, LayoutMargin, Math.Max(LayoutMargin, sw - spacingX));
            var y = Math.Clamp(kv.Value.Y, LayoutMargin, Math.Max(LayoutMargin, sh - spacingY));
            _provider.SetPosition(icon.Index, new PointI(x, y));
        }

        _arranged = true;
        RebuildGroupTitles();
        _timer.Start();
        RefreshOverlay();
    }

    /// <summary>
    /// Recomputes each icon's box title (software by purpose) so the overlay matches the
    /// placement. Best-effort: if target resolution fails we fall back to kind-only titles.
    /// </summary>
    private void RebuildGroupTitles()
    {
        _groupTitle.Clear();
        try
        {
            foreach (var ic in _provider.GetIcons())
            {
                var link = ic.Path is null ? null : DesktopShellEnumerator.LinkTargetAppFromPath(ic.Path);
                var title = BoxGrouping.FromEntry(_grouping, ic.Name, ic.Path, link).Title;
                _groupTitle[ic.Name] = title;
            }
        }
        catch (Exception)
        {
            // Empty/cached titles only — RefreshOverlay falls back to kind-based labels.
        }
    }

    private string GroupTitle(DesktopIcon ic)
    {
        if (_groupTitle.TryGetValue(ic.Name, out var title)) return title;
        // Cache miss (e.g. an icon the rebuild skipped on a transient error): classify now so a
        // software icon still lands in a purpose box rather than dropping out of the overlay.
        var kind = ItemKindClassifier.FromEntry(ic.Name, ic.Path);
        if (kind == ItemKind.Software)
        {
            var link = ic.Path is null ? null : DesktopShellEnumerator.LinkTargetAppFromPath(ic.Path);
            return SoftwarePurposeClassifier.Classify(_grouping, ic.Name, link);
        }
        return ItemKindClassifier.Title(kind);
    }

    // %LOCALAPPDATA%\DesktopOrganizer\layout.json — small, and survives when the UI can't.
    private static string LayoutFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "layout.json");

    /// <summary>
    /// Persists the desktop's current icon positions. Best-effort: a transient failure is
    /// ignored rather than surfaced. Only writes when the layout actually changed so the
    /// 2s overlay tick doesn't churn the disk every time.
    /// </summary>
    private void SaveLayout()
    {
        try
        {
            var icons = _provider.GetIcons();
            var map = icons.ToDictionary(ic => ic.Name, ic => ic.Position, StringComparer.OrdinalIgnoreCase);
            if (Same(map, _lastSaved)) return;
            DesktopLayoutStore.Save(LayoutFilePath, map);
            _lastSaved = map;
        }
        catch (Exception)
        {
            // Persistence is best-effort — a save failure must never crash the tool.
        }
    }

    private static bool Same(IReadOnlyDictionary<string, PointI> a, IReadOnlyDictionary<string, PointI> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var pos) || pos != kv.Value) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _timer.Stop();
        _provider.Dispose();
    }
}