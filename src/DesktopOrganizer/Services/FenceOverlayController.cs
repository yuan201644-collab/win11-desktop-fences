using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
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
    private SoftwareGroupingConfig _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
    private readonly DesktopLayoutService _layout;
    private readonly FenceHost _host;
    private readonly DispatcherTimer _timer;
    private FenceCategoryConfig _categories;
    private bool _arranged;
    private FenceSortMode _sortMode;
    private IReadOnlyDictionary<string, PointI> _lastSaved = new Dictionary<string, PointI>();
    // Last seen icon index→position and shell-foreground state; lets RefreshOverlay skip the
    // re-render when nothing changed, so the layered fence windows don't flash every tick.
    private Dictionary<int, PointI> _lastIcons = new();
    private bool _lastShown;
    private FenceInsets _insets;

    // Drag state. _membership maps each box title → the ListView item indexes in it (rebuilt each
    // RefreshOverlay); _dragStart snapshots those positions on DragStarted so deltas map to absolutes.
    private IReadOnlyDictionary<string, int[]> _membership = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, PointI> _dragStart = new();
    private bool _dragging;

    // Icon display-name → its box title, resolved once at arrange time. RefreshOverlay reuses
    // it every tick so the overlay box matches the placement without re-resolving .lnk targets
    // (COM) dozens of times per refresh.
    private readonly Dictionary<string, string> _groupTitle = new(StringComparer.OrdinalIgnoreCase);

    // Stable on-screen box order: software purpose boxes (in config order) first, then the
    // software fallback (其他软件), then folder / file / other. Boxes with no icons simply
    // produce no clusters, so "empty boxes" are hidden automatically instead of rendering an
    // empty outline.
    private static readonly string[] KindBoxes = { "文件夹", "文件", "其他" };

    /// <summary>Every box title in its natural (un-reordered) sequence.</summary>
    private string[] BaseBoxTitles => _grouping.Groups.Select(g => g.Title)
        .Append(SoftwarePurposeClassifier.FallbackTitle)
        .Concat(KindBoxes).ToArray();

    /// <summary>
    /// All box titles in their current display order — what the settings UI lists. Unlisted-in-config
    /// titles keep their natural place, so a box added later still shows up.
    /// </summary>
    public IReadOnlyList<string> AvailableBoxTitles =>
        FenceCategoryConfig.SortByPreference(BaseBoxTitles, _categories.Order);

    /// <summary>The titles actually drawn: display order minus anything the user hid.</summary>
    private string[] BoxOrder => AvailableBoxTitles.Where(t => !_categories.IsHidden(t)).ToArray();

    public FenceOverlayController()
    {
        _insets = FenceInsetStore.Load(FenceInsetFilePath);
        _sortMode = FenceSortStore.Load(SortFilePath);
        _categories = FenceCategoryStore.Load(CategoryFilePath);
        _provider = new SysListView32Provider();
        _layout = new DesktopLayoutService(_provider, _engine, _config);
        _host = new FenceHost();
        _host.DragStarted += OnDragStarted;
        _host.DragMoved += OnDragMoved;
        _host.DragEnded += OnDragEnded;
        _host.CollapseToggled += OnCollapseToggled;
        _host.SetInitialCollapsed(FenceCollapseStore.Load(CollapseFilePath));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshOverlay();
    }

    /// <summary>
    /// Fence palette forwarded to the host; setting it recolors all visible fences live so the
    /// settings UI can preview changes as the user drags colors/alpha.
    /// </summary>
    public OverlayAppearance Appearance
    {
        get => _host.Appearance;
        set => _host.Appearance = value;
    }

    /// <summary>
    /// Per-edge box padding. Setting it repersists and redraws every box live so the settings UI can
    /// preview box width as the user slides one of the inset sliders.
    /// </summary>
    public FenceInsets BoxInsets
    {
        get => _insets;
        set
        {
            _insets = value ?? FenceInsets.Default;
            try { FenceInsetStore.Save(FenceInsetFilePath, _insets); } catch (Exception) { /* best-effort */ }
            ForceRefresh();
        }
    }

    /// <summary>In-fence ordering. Persisted; a re-arrange picks it up via <see cref="ArrangeAndShow"/>.</summary>
    public FenceSortMode SortMode
    {
        get => _sortMode;
        set
        {
            _sortMode = value;
            try { FenceSortStore.Save(SortFilePath, _sortMode); } catch (Exception) { /* best-effort */ }
        }
    }

    /// <summary>
    /// A copy of the live grouping rules for the rule editor to mutate. It is a copy on purpose:
    /// typing in a title/keyword box must not change the running overlay until the user presses
    /// 保存并应用, otherwise a half-typed edit would silently reclassify the whole desktop.
    /// </summary>
    public SoftwareGroupingConfig Grouping => new()
    {
        Groups = _grouping.Groups.Select(g => new SoftwareGroup(g.Title, g.Keywords)).ToList(),
    };

    /// <summary>A copy of the current box visibility/order settings (what the category manager edits).</summary>
    public FenceCategoryConfig Categories => new()
    {
        Order = new List<string>(_categories.Order),
        Hidden = new List<string>(_categories.Hidden),
    };

    /// <summary>
    /// Swaps in a user-edited grouping config, persists it, and redraws the box titles immediately.
    /// Does NOT re-arrange icon positions — call <see cref="ArrangeAndShow"/> first for new boxes to land.
    /// </summary>
    public void SetGrouping(SoftwareGroupingConfig cfg)
    {
        _grouping = cfg ?? SoftwareGroupStore.Default();
        try { SoftwareGroupStore.Save(SoftwareGroupStore.DefaultFilePath, _grouping); }
        catch (Exception) { /* best-effort */ }
        _layout.SetGrouping(_grouping);
        // The box list just changed — drop any hidden/ordered titles that no longer exist.
        SetCategories(_categories);
        RebuildGroupTitles();
        ForceRefresh();
    }

    /// <summary>
    /// Applies the category manager's choices: which boxes are drawn and in what order. Titles that
    /// no longer exist (a deleted group) are dropped so the saved file can't accumulate ghosts.
    /// Hidden boxes simply aren't drawn — their icons stay exactly where the user left them.
    /// </summary>
    public void SetCategories(FenceCategoryConfig cfg)
    {
        var incoming = cfg ?? FenceCategoryConfig.Default;
        var known = new HashSet<string>(BaseBoxTitles, StringComparer.OrdinalIgnoreCase);
        _categories = new FenceCategoryConfig
        {
            Order = incoming.Order.Where(known.Contains).ToList(),
            Hidden = incoming.Hidden.Where(known.Contains).ToList(),
        };
        try { FenceCategoryStore.Save(CategoryFilePath, _categories); }
        catch (Exception) { /* best-effort */ }
        ForceRefresh();
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
            _host.SetVisible(false);
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
            _layout.ArrangeIntoFence(new RectI(x + LayoutMargin, y + LayoutMargin, w - LayoutMargin * 2, h - LayoutMargin * 2), maxRows, _sortMode);
        }
        catch (DesktopAutoArrangeException)
        {
            // Re-enabled between Disable and here (or the style clear didn't stick).
            _host.SetVisible(false);
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
            _host.SetVisible(false);
            return;
        }

        // Don't disturb the mesh mid-drag — the user's hand is on the icons.
        if (_dragging) return;

        // Idempotent: if neither the icon positions nor the shell-foreground visibility changed,
        // re-rendering the layered windows would just make them flash — skip it entirely.
        var icons = _provider.GetIcons();
        var positions = IconPositions(icons);
        bool shown = ShouldShowFences();

        if (SamePos(positions, _lastIcons) && shown == _lastShown) return;
        _lastIcons = positions;
        _lastShown = shown;

        // Group by on-screen box (software split by purpose + folder/file/other), stable order,
        // and remember which ListView indexes each box holds (for dragging).
        var placed = new List<(string Group, PointI Position)>();
        var membership = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in BoxOrder)
        {
            foreach (var ic in icons.Where(x => GroupTitle(x) == title))
            {
                placed.Add((title, ic.Position));
                if (!membership.TryGetValue(title, out var list)) { list = new List<int>(); membership[title] = list; }
                list.Add(ic.Index);
            }
        }
        _membership = membership.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        // Small pad so adjacent boxes stay distinguishable without fusing into one blob.
        // Kept tiny so vertically-adjacent clusters (dense mode) don't overlap much.
        var clusters = FenceClusterBuilder.Build(
            placed, _provider.IconSpacingX, _provider.IconSpacingY,
            pad: 2, headerPx: FenceHeader.HeaderPx,
            padLeft: _insets.Left, padRight: _insets.Right,
            padTop: _insets.Top, padBottom: _insets.Bottom,
            separateOverlaps: false); // boxes must stay on their icons, never pushed away from them

        _host.Sync(clusters, FenceHeader.HeaderPx);
        _host.SetVisible(shown);
        SaveLayout(); // follow manual drags so the final layout persists
    }

    /// <summary>
    /// Fences are only shown while the desktop itself (or this app's own settings window) is
    /// foreground; when a real app takes the foreground the fences hide so they never float over it,
    /// and they return the instant the user comes back to the desktop.
    /// </summary>
    private static bool ShouldShowFences()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return true; // our own settings window — keep boxes visible after "整理"
        if (IsShellProcess(pid)) return true;           // desktop, taskbar, Start menu — all explorer.exe
        // A different process's window: only treat it as "an app covering the desktop" if it is
        // actually a taskbar-app window (WS_EX_APPWINDOW). Transient/helper windows without that style
        // must NOT hide the fences, otherwise they vanish for no reason a user can see.
        long ex = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (ex & NativeMethods.WS_EX_APPWINDOW) == 0;
    }

    private static bool IsShellProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Forces a full redraw on the next refresh, bypassing the idle-skip, so live settings edits
    /// (box insets) immediately reshape the boxes even though no icon moved.
    /// </summary>
    public void ForceRefresh()
    {
        _lastIcons = new Dictionary<int, PointI>();
        _lastShown = !_lastShown;
        RefreshOverlay();
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
            _host.SetVisible(false);
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

    /// <summary>Flips a fence's collapsed state, persists the set, and redraws so the box shrinks to a tab.</summary>
    private void OnCollapseToggled(string title)
    {
        _host.ToggleCollapse(title);
        try { FenceCollapseStore.Save(CollapseFilePath, _host.CollapsedTitles); }
        catch (Exception) { /* best-effort */ }
        ForceRefresh();
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

    private static string FenceInsetFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-inset.json");

    private static string CollapseFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-collapse.json");

    private static string SortFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-sort.json");

    private static string CategoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-category.json");

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

    private static Dictionary<int, PointI> IconPositions(IEnumerable<DesktopIcon> icons)
    {
        var m = new Dictionary<int, PointI>();
        foreach (var i in icons) m[i.Index] = i.Position;
        return m;
    }

    private static bool SamePos(IReadOnlyDictionary<int, PointI> a, IReadOnlyDictionary<int, PointI> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var pos) || pos != kv.Value) return false;
        }
        return true;
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

    // --- fence dragging: translate the cluster's real icons with the box ---

    private void OnDragStarted(string title)
    {
        if (!_membership.TryGetValue(title, out var indexes)) return;
        _dragging = true;
        try
        {
            var byIndex = _provider.GetIcons().ToDictionary(ic => ic.Index);
            _dragStart = new Dictionary<int, PointI>();
            foreach (var idx in indexes)
                if (byIndex.TryGetValue(idx, out var ic)) _dragStart[idx] = ic.Position;
        }
        catch (Exception)
        {
            _dragStart = new Dictionary<int, PointI>();
        }
    }

    private void OnDragMoved(string title, int dx, int dy)
    {
        if (_dragStart.Count == 0) return;
        var (_, _, sw, sh) = Primary;
        var spacingX = _provider.IconSpacingX;
        var spacingY = _provider.IconSpacingY;
        foreach (var (idx, start) in _dragStart)
        {
            var x = Math.Clamp(start.X + dx, LayoutMargin, Math.Max(LayoutMargin, sw - spacingX));
            var y = Math.Clamp(start.Y + dy, LayoutMargin, Math.Max(LayoutMargin, sh - spacingY));
            _provider.SetPosition(idx, new PointI(x, y));
        }
    }

    private void OnDragEnded(string title)
    {
        _dragging = false;
        _dragStart = new Dictionary<int, PointI>();
        SaveLayout();
        // The box already followed the icons during the drag, so don't re-derive (and possibly
        // re-push) every box from scratch here — that's what moved the OTHER fences around. Record
        // the post-drag positions so the 2s tick sees no change and leaves every box alone.
        _lastIcons = IconPositions(_provider.GetIcons());
    }

    public void Dispose()
    {
        _timer.Stop();
        _host.Dispose();
        _provider.Dispose();
    }
}