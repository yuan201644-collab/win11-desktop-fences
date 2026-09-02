using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly IDesktopIconProvider _provider;
    private readonly ClassifierEngine _engine = new();
    private readonly ClassifierConfig _config = new();
    private SoftwareGroupingConfig _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
    private readonly DesktopLayoutService _layout;
    private readonly IOverlayHost _host;
    private readonly Func<DesktopIcon, string>? _titleResolver;
    private readonly Func<RectI?>? _screenProvider;
    private readonly string? _collapseFilePath;
    private DispatcherTimer? _timer;
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

    // Collapse-to-tab state. Collapsing parks the cluster's real icons off-screen (negative
    // coordinates) and leaves a thin tab; these two maps remember the pre-collapse icon positions
    // (by display name) and the tab rectangle so expanding restores the desktop exactly. Persisted
    // through FenceCollapseStore so a restart can bring the same collapsed state back.
    private readonly Dictionary<string, Dictionary<string, PointI>> _hiddenIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectI> _tabBounds = new(StringComparer.OrdinalIgnoreCase);

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
        : this(new SysListView32Provider(), new FenceHost())
    {
    }

    /// <summary>
    /// Injection/test constructor. Accepts a desktop-icon provider and overlay host so the
    /// orchestration logic (collapse → expand → collapse) can be exercised headlessly. The public
    /// parameterless constructor wires the real Win32 provider and WPF host and calls this.
    /// </summary>
    /// <param name="titleResolver">Optional override for box-title resolution in tests; when null the
    /// controller classifies each icon normally (production behavior).</param>
    /// <param name="screenProvider">Optional override for the virtual-desktop rect in tests. The
    /// controller otherwise reads <see cref="SystemParameters"/> directly, which would make every
    /// test depend on the machine it runs on (icons laid out past the real right edge would be
    /// treated as stranded and rescued). Production callers leave this null.</param>
    /// <param name="collapseFilePath">Optional scratch path for the persisted collapse records in
    /// tests; production callers leave this null to use the real %LOCALAPPDATA% file.</param>
    internal FenceOverlayController(
        IDesktopIconProvider provider,
        IOverlayHost host,
        Func<DesktopIcon, string>? titleResolver = null,
        Func<RectI?>? screenProvider = null,
        string? collapseFilePath = null)
    {
        _titleResolver = titleResolver;
        _screenProvider = screenProvider;
        _collapseFilePath = collapseFilePath;
        _provider = provider;
        _host = host;

        _insets = FenceInsetStore.Load(FenceInsetFilePath);
        _sortMode = FenceSortStore.Load(SortFilePath);
        _categories = FenceCategoryStore.Load(CategoryFilePath);
        _layout = new DesktopLayoutService(_provider, _engine, _config);
        _host.DragStarted += OnDragStarted;
        _host.DragMoved += OnDragMoved;
        _host.DragEnded += OnDragEnded;
        _host.CollapseToggled += OnCollapseToggled;
        _host.ContextMenuRequested += (t, x, y) => FenceContextMenu?.Invoke(t, x, y);

        // Restore the persisted collapsed state. Records from the legacy (plain string array)
        // format carry no tab rect / hidden positions — their icons were never parked off-screen,
        // so drop the collapsed flag instead of leaving a title invisible with no way back.
        var collapseRecords = FenceCollapseStore.Load(CollapseFilePath);
        _host.SetInitialCollapsed(collapseRecords.Select(r => r.Title));
        foreach (var rec in collapseRecords)
        {
            if (rec.Icons is { Count: > 0 } && rec.Tab != default)
            {
                _hiddenIcons[rec.Title] = new Dictionary<string, PointI>(rec.Icons, StringComparer.OrdinalIgnoreCase);
                _tabBounds[rec.Title] = rec.Tab;
            }
            else
            {
                _host.ToggleCollapse(rec.Title);
            }
        }


        // If the last session left fences collapsed, their icons are parked off-screen right now —
        // bring the overlay up immediately (without re-arranging anything) so the tabs are visible
        // and the icons aren't simply "missing". Best-effort: a failure here must not kill startup.
        if (collapseRecords.Count > 0)
        {
            try
            {
                _arranged = true;
                RebuildGroupTitles();
                StartOverlayTimer();
                RefreshOverlay();
            }
            catch (Exception)
            {
                // Overlay restore is best-effort; the user can still click 整理 to re-layout.
            }
        }

        // Rescue any icon left parked off-screen by a prior crash whose collapse record was lost
        // (e.g. an expand that threw before it persisted). Without this, such icons stay invisible
        // with no tab to bring them back. Icons that are intentionally collapsed are left alone.
        if (_provider.IsAvailable) RescueStrandedIcons();
    }

    /// <summary>
    /// Lazily creates and starts the 2s refresh timer. Construction is deferred (never done in the
    /// constructor) so the controller can be instantiated on a non-STA thread in unit tests without
    /// <see cref="DispatcherTimer"/> throwing. Production callers (ArrangeAndShow / restore) hit this;
    /// the headless test path never does.
    /// </summary>
    private void StartOverlayTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshOverlay();
        _timer.Start();
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

    /// <summary>Raised when a fence's header (incl. collapsed tab) is right-clicked. The UI layer
    /// shows a context menu of extra actions; the screen-pixel cursor location is included so the
    /// menu can be placed at the click point.</summary>
    public event Action<string, int, int>? FenceContextMenu;

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
        // A collapsed box that just got hidden/removed must expand, or its icons stay parked
        // off-screen with no tab left to restore them.
        foreach (var t in _host.CollapsedTitles.ToList())
        {
            if (!BoxOrder.Contains(t, StringComparer.OrdinalIgnoreCase)) ExpandFence(t);
        }
        PersistCollapsed();
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

        // Positions are ignored while "Auto arrange" is on — silently clear it first. Must come
        // before the collapse-state drop below, so ExpandRestore's own auto-arrange guard can't
        // refuse and leave the fence half-collapsed through a full re-layout.
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

        // A fresh arrange repositions every icon — icons parked off-screen by collapse included —
        // so the collapse state (tab + hidden positions) is dropped outright instead of restoring
        // point-by-point; the layout below re-places the whole desktop anyway.
        foreach (var t in _host.CollapsedTitles.ToList())
        {
            _host.ToggleCollapse(t);
            _hiddenIcons.Remove(t);
            _tabBounds.Remove(t);
        }
        PersistCollapsed();

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
        StartOverlayTimer();
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

        // Safety net: a fence can only be drawn as a collapsed tab when BOTH its parked-icon record
        // and its tab rect exist. If either is missing the tab would be invisible while its icons
        // stay stranded off-screen — the "collapse→expand→collapse makes a box vanish" symptom. Expand
        // any such fence back (rescuing the parked icons) instead of leaving it in that dead state.
        ReconcileCollapsed();

        // Idempotent: if neither the icon positions nor the shell-foreground visibility changed,
        // re-rendering the layered windows would just make them flash — skip it entirely.
        var icons = _provider.GetIcons();
        var positions = IconPositions(icons);
        bool shown = ShouldShowFences();

        if (SamePos(positions, _lastIcons) && shown == _lastShown) return;
        _lastIcons = positions;
        _lastShown = shown;

        // Self-heal: an icon can be left parked with no record able to restore it (older builds
        // keyed restore points by path, so two same-path icons shared one slot and only one of them
        // ever came back). Without this it stays invisible until the next restart, because the
        // constructor is the only other place that rescues stranded icons. RescueStrandedIcons()
        // skips icons owned by a collapsed fence, so this can never fight an in-progress collapse.
        RescueStrandedIcons();

        // Explorer refreshes (F5 / shell restart) can bounce collapsed icons back onto the visible
        // desktop; re-park any that left the off-screen zone so the fence stays collapsed. Only
        // Explorer can move a hidden icon (the user can't see one to drag it), so this is safe.
        ReparkBouncedIcons(icons);
        icons = _provider.GetIcons(); // fresh snapshot after any re-parking
        positions = IconPositions(icons);
        _lastIcons = positions;

        // Virtual desktop rect: lets the cluster builder drop stray coordinates (fold-parked
        // icons at -32000, or any icon parked/stranded beyond the monitors) instead of letting
        // one bad point stretch a box — and the title bar with it — across the whole desktop.
        var screen = VirtualScreen();

        // Group by on-screen box (software split by purpose + folder/file/other), stable order,
        // and remember which ListView indexes each box holds (for dragging). Collapsed boxes are
        // skipped — their icons are parked off-screen; a thin tab is appended below instead.
        var placed = new List<(string Group, PointI Position)>();
        var membership = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in BoxOrder)
        {
            if (_host.IsCollapsed(title)) continue;
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
        // Log (only when it actually happens) any icon dropped for sitting outside the virtual
        // desktop — that is the smoking gun for a box/title bar that stretches off-screen.
        if (screen is { } sc)
        {
            var dropped = placed.Where(p => !FenceClusterBuilder.IsOnScreen(
                p.Position, sc, _provider.IconSpacingX, _provider.IconSpacingY)).ToList();
            if (dropped.Count > 0)
                TraceLog($"[refresh] dropped {dropped.Count} off-screen icon(s): "
                    + string.Join(", ", dropped.Take(6).Select(p => $"{p.Group}@{p.Position.X},{p.Position.Y}")));
        }

        var clusters = FenceClusterBuilder.Build(
            placed, _provider.IconSpacingX, _provider.IconSpacingY,
            pad: 2, headerPx: FenceHeader.HeaderPx,
            padLeft: _insets.Left, padRight: _insets.Right,
            padTop: _insets.Top, padBottom: _insets.Bottom,
            separateOverlaps: false, screen: screen).ToList(); // boxes must stay on their icons, never pushed away from them

        // Last-resort guard: a box larger than the whole virtual desktop can only come from a
        // coordinate we failed to classify. Cap it and record the anomaly instead of letting the
        // title bar run off the screen.
        if (screen is { } sc2)
            for (int i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                if (c.Bounds.Width <= sc2.Width && c.Bounds.Height <= sc2.Height) continue;
                TraceLog($"[refresh] CLAMP '{c.Title}' from {c.Bounds.Width}x{c.Bounds.Height} to screen {sc2.Width}x{sc2.Height}");
                clusters[i] = c with { Bounds = FenceClusterBuilder.ClampBounds(c.Bounds, sc2) };
            }

        // Collapsed boxes produce no clusters above; append one thin tab cluster per collapsed
        // title (drawn at the remembered pre-collapse position) so the tab stays visible/clickable.
        foreach (var title in _host.CollapsedTitles)
        {
            if (!BoxOrder.Contains(title, StringComparer.OrdinalIgnoreCase)) continue;
            if (!_tabBounds.TryGetValue(title, out var tab)) continue;
            // Also guards tab rects restored from disk (a box saved while poisoned by a truncated
            // park coordinate would otherwise come back off-screen and look like it vanished).
            var safeTab = ReachableTab(tab, screen);
            clusters.Add(new FenceCluster(
                title, 0, new RectI(safeTab.X, safeTab.Y, Math.Max(24, safeTab.Width), Math.Max(1, safeTab.Height))));
        }

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
        var byName = new Dictionary<string, DesktopIcon>(StringComparer.OrdinalIgnoreCase);
        foreach (var ic in _provider.GetIcons()) { var k = ic.Name; if (!byName.ContainsKey(k)) byName[k] = ic; }
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
        StartOverlayTimer();
        RefreshOverlay();
    }

    /// <summary>Flips a fence's collapsed state. Collapsing parks the cluster's real icons off-screen
    /// (negative coordinates, far outside the virtual desktop) leaving a thin tab behind;
    /// expanding moves them back to their exact pre-collapse positions. The hidden positions
    /// and tab rect are persisted so a restart can restore the same state.
    /// </summary>
    private void OnCollapseToggled(string title)
    {
        bool collapsed = _host.ToggleCollapse(title);
        TraceLog($"[toggle] '{title}' → {(collapsed ? "collapse" : "expand")}");
        if (collapsed)
        {
            // CollapseHide can fail (no icons matched / provider unavailable). In that case the
            // host's flag must be rolled back — otherwise the box disappears while the icons stay
            // on the desktop, which looks like "collapse did nothing".
            if (!CollapseHide(title))
            {
                _host.ToggleCollapse(title); // roll back to expanded
                ForceRefresh();
                return;
            }
        }
        else if (!ExpandRestore(title))
        {
            // A real restore was impossible (auto-arrange couldn't be cleared / ListView not
            // readable) — roll the flag back to collapsed so the tab stays and no icon is
            // silently stranded off-screen; the user can retry after fixing the cause.
            _host.ToggleCollapse(title);
            ForceRefresh();
            return;
        }
        PersistCollapsed();
        ForceRefresh();
    }

    /// <summary>Public entry used by the right-click menu: flip one fence's collapsed state.</summary>
    public void ToggleFence(string title) => OnCollapseToggled(title);

    /// <summary>True when <paramref name="title"/> is currently drawn as a collapsed tab.</summary>
    public bool IsCollapsed(string title) => _host.IsCollapsed(title);

    /// <summary>Expands every collapsed fence at once (right-click menu: 全部展开).</summary>
    public void ExpandAll()
    {
        foreach (var t in _host.CollapsedTitles.ToList())
            if (_host.IsCollapsed(t)) OnCollapseToggled(t);
    }

    /// <summary>Collapses every visible fence at once (right-click menu: 全部折叠).</summary>
    public void CollapseAll()
    {
        foreach (var t in BoxOrder)
            if (!_host.IsCollapsed(t)) OnCollapseToggled(t);
    }

    /// <summary>Force-expands a collapsed fence whose box is going away (hidden/removed in settings):
    /// flips the host flag, then restores every icon. If the restore itself is refused (auto-arrange
    /// / ListView unreadable) the record is still dropped — a box that no longer exists must not
    /// leave its icons parked with no tab left to bring them back.</summary>
    private void ExpandFence(string title)
    {
        if (_host.IsCollapsed(title)) _host.ToggleCollapse(title);
        if (!ExpandRestore(title))
        {
            // The box is going away, so there's no tab left to expand from. Drop the orphaned record
            // and pull any still-parked icons back to the visible desktop rather than leaving them
            // stranded off-screen with no way back.
            _hiddenIcons.Remove(title);
            _tabBounds.Remove(title);
            RescueStrandedIcons();
        }
    }

    /// <summary>Parks a cluster's icons off-screen. Returns false (leaving everything as-is) when
    /// nothing could be hidden — the caller must roll back the host's collapsed flag then.</summary>
    private bool CollapseHide(string title)
    {
        if (_hiddenIcons.ContainsKey(title)) return true; // already hidden

        // Same auto-arrange defense as expand: while it is on, every SetPosition throws and the
        // icons would stay visible while the box folds away — refuse instead of half-folding.
        if (_provider.IsAutoArrangeOn && !_provider.DisableAutoArrange())
        {
            MessageBox.Show("桌面处于「自动排列图标」状态，无法折叠图标。\n请手动关闭：桌面右键 → 查看 → 取消勾选「自动排列图标」，然后重试。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_provider.IsAutoArrangeOn) System.Threading.Thread.Sleep(200); // let the style change settle

        var icons = _provider.GetIcons().Where(ic => GroupTitle(ic) == title).ToList();
        if (icons.Count == 0) return false;

        int sx = Math.Max(1, _provider.IconSpacingX);
        int sy = Math.Max(1, _provider.IconSpacingY);
        // Snapshot every original position FIRST so a partial SetPosition failure below can never
        // lose an icon's restore point.
        var originals = new Dictionary<string, PointI>(icons.Count, StringComparer.OrdinalIgnoreCase);
        // Keyed by INDEX, not StableKey: two icons can share a path, and a collision made the
        // second overwrite the first's restore point — that icon was then parked but never
        // restored, and stayed invisible off-screen with no record left to bring it back.
        foreach (var ic in icons) originals[IndexKey(ic.Index)] = ic.Position;

        int i = 0;
        foreach (var ic in icons)
        {
            try
            {
                // Park the real icon far off the virtual desktop. Slots come from ParkSlot, which
                // spreads them over a bounded grid INSIDE the signed 16-bit range — the old
                // `-32000 - spacing * i` marched past -32768 once a box held enough icons, and
                // LVM_SETITEMPOSITION truncated the coordinate into a bogus on-screen-looking
                // value that later poisoned the tab's bounding box (box + icons vanished together).
                _provider.SetPosition(ic.Index, FenceClusterBuilder.ParkSlot(i, sx, sy));
            }
            catch (Exception ex)
            {
                // Best-effort park — the original position is recorded regardless, so expanding
                // still restores every icon even if one failed to move.
                TraceLog($"[collapse] '{title}' park FAILED '{ic.Name}': {ex.Message}");
            }
            i++;
        }
        _hiddenIcons[title] = originals;
        _tabBounds[title] = TabBounds(icons, sx, sy);
        TraceLog($"[collapse] '{title}': parked {originals.Count} icon(s) off-screen");
        return true;
    }

    /// <summary>The tab that replaces a collapsed cluster: the icons' bounding box shrunk to title-band height.</summary>
    /// <remarks>
    /// Must reuse <see cref="FenceClusterBuilder.CollapsedTabBounds"/> — i.e. the exact geometry the
    /// expanded box uses. An earlier ad-hoc formula here ignored the per-side padding and the header
    /// lift, so folding kicked the title right by padLeft and down by padTop+HeaderPx (and narrowed
    /// it), and expanding snapped it back: "折叠时标题框会移动，展开之后又会回去".
    /// </remarks>
    private RectI TabBounds(IReadOnlyList<DesktopIcon> icons, int cellW, int cellH)
    {
        var pts = icons.Select(ic => ic.Position).ToList();
        // Only on-screen positions may shape the tab. A single icon left at a bogus coordinate
        // (a truncated 16-bit park position, e.g. y=+31184) would drag the bounding box — and with
        // it the tab, the box's ONLY way back — off the screen. Filter first, then clamp.
        var screen = VirtualScreen();
        var safe = screen is { } sc
            ? pts.Where(p => FenceClusterBuilder.IsOnScreen(p, sc, cellW, cellH)).ToList()
            : pts.Where(p => p.X > FenceClusterBuilder.ParkedThreshold
                             && p.Y > FenceClusterBuilder.ParkedThreshold).ToList();
        if (safe.Count == 0) safe = pts; // nothing on screen to anchor to — keep every point rather than lose the tab
        var tab = FenceClusterBuilder.CollapsedTabBounds(
            safe, cellW, cellH,
            _insets.Left, _insets.Top, _insets.Right, _insets.Bottom, FenceHeader.HeaderPx);
        return ReachableTab(tab, screen);
    }

    /// <summary>Guarantees a collapsed tab the user can actually click. A tab drawn outside the
    /// virtual desktop is indistinguishable from "the box vanished", so any rect that would land
    /// off-screen is pulled back onto it — the box stays reachable and the parked icons can always
    /// be restored. No-op for healthy geometry.</summary>
    private RectI ReachableTab(RectI tab, RectI? screen)
    {
        if (screen is not { } sc) return tab;
        int w = Math.Min(Math.Max(24, tab.Width), Math.Max(24, sc.Width));
        int h = Math.Min(Math.Max(1, tab.Height), Math.Max(1, sc.Height));
        int x = Math.Clamp(tab.Left, sc.Left, Math.Max(sc.Left, sc.Right - w));
        int y = Math.Clamp(tab.Top, sc.Top, Math.Max(sc.Top, sc.Bottom - h));
        if (x == tab.Left && y == tab.Top && w == tab.Width && h == tab.Height) return tab;
        TraceLog($"[collapse] tab @{tab.Left},{tab.Top} {tab.Width}x{tab.Height} would be off-screen → "
                 + $"clamped to @{x},{y} so the box stays reachable");
        return new RectI(x, y, w, h);
    }

    /// <summary>
    /// Re-parks icons of collapsed fences that the shell bounced back into the visible desktop
    /// (e.g. after F5 or an explorer restart). Off-screen cells sit far below -10000 on both axes;
    /// any other coordinate means the icon escaped and must go back to its cell.
    /// </summary>
    private void ReparkBouncedIcons(IReadOnlyList<DesktopIcon> icons)
    {
        if (_host.CollapsedTitles.Count == 0) return;
        int sx = Math.Max(1, _provider.IconSpacingX);
        int sy = Math.Max(1, _provider.IconSpacingY);
        foreach (var title in _host.CollapsedTitles)
        {
            if (!_hiddenIcons.TryGetValue(title, out var originals) || originals.Count == 0) continue;
            int i = 0;
            foreach (var ic in icons)
            {
                // Index keys come from current records; StableKey matches records written by older
                // builds, which keyed restore points by path.
                if (!originals.ContainsKey(IndexKey(ic.Index)) && !originals.ContainsKey(StableKey(ic))) continue;
                if (ic.Position.X > -10000 || ic.Position.Y > -10000)
                {
                    // Same bounded parking pocket as CollapseHide — never `-32000 - spacing * i`.
                    try { _provider.SetPosition(ic.Index, FenceClusterBuilder.ParkSlot(i, sx, sy)); }
                    catch (Exception) { /* best-effort — next tick retries */ }
                }
                i++;
            }
        }
    }

    /// <summary>
    /// Moves a collapsed cluster's icons back to their pre-collapse positions. Returns false when
    /// the desktop made a real restore impossible (auto-arrange could not be cleared, or the
    /// ListView is momentarily unreadable) — the caller must then keep the fence collapsed so no
    /// icon is silently stranded off-screen.
    /// </summary>
    private bool ExpandRestore(string title)
    {
        if (!_hiddenIcons.TryGetValue(title, out var originals)) return true;

        // (H1) Auto-arrange makes every SetPosition throw, which used to leave every icon parked
        // off-screen behind a silent catch — "expanded" but invisible. Same defense as
        // ArrangeAndShow/RestoreSavedLayout: clear the style; if that fails, refuse to expand.
        if (_provider.IsAutoArrangeOn && !_provider.DisableAutoArrange())
        {
            TraceLog($"[expand] '{title}' REFUSED: auto-arrange on and could not be disabled");
            MessageBox.Show("桌面仍处于「自动排列图标」状态，无法恢复图标位置。\n请手动关闭：桌面右键 → 查看 → 取消勾选「自动排列图标」，然后再次点击展开。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_provider.IsAutoArrangeOn)
        {
            TraceLog($"[expand] '{title}': auto-arrange was on — disabled automatically");
            System.Threading.Thread.Sleep(200); // let the style change settle before moving icons
        }

        // Build the live lookup by a STABLE, UNIQUE key (the file path), NOT the display name:
        // two icons can share a display name (e.g. two "健身助手" shortcuts), and ToDictionary on
        // names threw ArgumentException — which used to crash expand before a single icon moved.
        // Retry the restore a few times. GetIcons() can transiently miss icons while Explorer is
        // mid-refresh (e.g. right after the auto-arrange style flip's 200ms sleep): an icon that is
        // momentarily absent would otherwise be counted as "gone" and stranded off-screen — the root
        // cause of "collapse→expand→collapse makes a box vanish". Re-reading catches the flicker so a
        // stable desktop restores everything and the record drops cleanly.
        int restored = 0, missing = 0, failed = 0;
        var pending = new HashSet<string>(originals.Keys, StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3 && pending.Count > 0; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(160);

            // Build the live lookup by a STABLE, UNIQUE key (the file path), NOT the display name:
            // two icons can share a display name (e.g. two "健身助手" shortcuts), and ToDictionary on
            // names threw ArgumentException — which used to crash expand before a single icon moved.
            Dictionary<string, DesktopIcon> byIndex, byKey;
            try
            {
                var live = _provider.GetIcons();
                byIndex = new Dictionary<string, DesktopIcon>(live.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var ic in live) byIndex[IndexKey(ic.Index)] = ic;
                byKey = BuildKeyIndex(live);
            }
            catch (Exception ex)
            {
                TraceLog($"[expand] '{title}' REFUSED: could not read desktop icons (attempt {attempt + 1}): {ex.Message}");
                return false;
            }
            if (byIndex.Count == 0)
            {
                // Explorer likely restarted under us, so GetIcons can't see the desktop yet. Dropping
                // the record here would strand the parked icons off-screen with no way back — stay
                // collapsed and let the user retry once the shell is back.
                TraceLog($"[expand] '{title}' REFUSED: ListView returned 0 icons (explorer restart?)");
                return false;
            }

            foreach (var key in pending.ToList())
            {
                var ic = ResolveParkedIcon(byIndex, byKey, key);
                if (ic is null) continue; // still absent this attempt
                var pos = originals[key];
                try { _provider.SetPosition(ic.Index, pos); restored++; pending.Remove(key); }
                catch (DesktopAutoArrangeException)
                {
                    // Auto-arrange re-engaged mid-restore (rare style reset). Try to clear it once and
                    // retry this icon; keep going so the others still get a chance. If it still fails
                    // the record is kept — never strand icons off-screen with no way back.
                    TraceLog($"[expand] '{title}' auto-arrange re-engaged at '{key}' — re-disabling");
                    if (_provider.DisableAutoArrange())
                    {
                        System.Threading.Thread.Sleep(150);
                        try { _provider.SetPosition(ic.Index, pos); restored++; pending.Remove(key); continue; }
                        catch (Exception ex) { TraceLog($"[expand] '{title}' retry FAILED '{key}' @{pos}: {ex.Message}"); }
                    }
                    failed++;
                    pending.Remove(key);
                }
                catch (Exception ex)
                {
                    // Non-auto-arrange error on this icon — log it but keep going to rescue the rest.
                    failed++;
                    TraceLog($"[expand] '{title}' FAILED '{key}' @{pos}: {ex.Message}");
                    pending.Remove(key);
                }
            }
            missing = pending.Count;
        }

        // Drop the record ONLY when every icon was actually restored to its original position
        // (restored == total). Any unaccounted-for icon keeps the record + returns false so the tab
        // stays collapsed and the user can retry — never strand icons off-screen with a cleared record.
        if (!IsExpandComplete(restored, missing, failed, originals.Count))
        {
            TraceLog($"[expand] '{title}' INCOMPLETE: restored={restored} missing={missing} failed={failed} — record KEPT, retry available");
            return false;
        }
        TraceLog($"[expand] '{title}': restored {restored} icon(s) to original positions");
        _hiddenIcons.Remove(title);
        _tabBounds.Remove(title);
        return true;
    }

    /// <summary>Persists the collapsed set (title → tab rect + hidden icon positions).</summary>
    private void PersistCollapsed()
    {
        try
        {
            var records = _host.CollapsedTitles
                .Where(t => _hiddenIcons.TryGetValue(t, out var icons) && icons.Count > 0
                            && _tabBounds.TryGetValue(t, out var tab))
                .Select(t => new CollapsedFenceRecord(t, _tabBounds[t], _hiddenIcons[t]))
                .ToList();
            FenceCollapseStore.Save(CollapseFilePath, records);
        }
        catch (Exception)
        {
            // Persistence is best-effort — a save failure must never crash the tool.
        }
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
        // Tests may inject a deterministic resolver so a collapse can target a known box title
        // without depending on the real classification pipeline.
        if (_titleResolver is not null) return _titleResolver(ic);
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

    // Collapse records are real user state. Tests inject a scratch path so they neither inherit a
    // previous session's collapsed boxes nor overwrite the user's own collapse file with test data.
    private string CollapseFilePath => _collapseFilePath ?? DefaultCollapseFilePath;

    private static string DefaultCollapseFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-collapse.json");

    // Diagnostic log for collapse/expand failures — the silent catches used to hide exactly why
    // icons stayed off-screen; every failure now lands here so a repro can be read back.
    // When a scratch collapse path is injected (tests) the log follows it, so a test run can never
    // pollute the real diagnostic trail the user reads while debugging an actual incident.
    private string CollapseLogPath => _collapseFilePath is null
        ? DefaultCollapseLogPath
        : Path.Combine(Path.GetDirectoryName(_collapseFilePath)!, "fence-collapse.log");

    private static string DefaultCollapseLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "fence-collapse.log");

    private void TraceLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CollapseLogPath)!);
            File.AppendAllText(CollapseLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* logging must never break the app */ }
    }

    /// <summary>A stable key for an icon: its file path (survives renames, and two icons
    /// with the same display name but different paths stay distinct). Shell items without a path
    /// fall back to their display name.</summary>
    /// <remarks>
    /// <b>Not unique.</b> Two desktop icons can resolve to the same key: the user desktop and the
    /// public desktop may each hold a same-named entry, and <c>DesktopShellEnumerator</c> maps a
    /// display name to a single path (see its collision handling), so both icons report the same
    /// <see cref="DesktopIcon.Path"/>. Two shortcuts pointing at the same target collide the same
    /// way. Restore points are therefore keyed by <see cref="IndexKey"/>, not by this.
    /// </remarks>
    internal static string StableKey(DesktopIcon ic) =>
        string.IsNullOrEmpty(ic.Path) ? "name:" + ic.Name : "path:" + ic.Path;

    /// <summary>The key under which a parked icon's restore point is recorded: its ListView item
    /// index, unique within a desktop snapshot.</summary>
    /// <remarks>
    /// Keying by <see cref="StableKey"/> silently lost icons: when two icons shared a key the
    /// second overwrote the first's restore point, so only one of them was ever moved back and the
    /// other stayed parked off-screen forever — visible in the diagnostics as a permanent
    /// <c>[refresh] dropped 1 off-screen icon(s)</c>. The collapse log counted entries, not icons,
    /// so <c>parked 30</c> / <c>restored 30</c> still looked consistent and hid the loss.
    /// Records written by older builds keep their <see cref="StableKey"/>; expanding matches those
    /// by <see cref="StableKey"/> as a fallback.
    /// </remarks>
    internal static string IndexKey(int index) => "i:" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when the icon's restore point is recorded under any collapsed fence, i.e. it was
    /// parked on purpose and must NOT be rescued back. Matches BOTH key schemes: index keys from
    /// current records, and <see cref="StableKey"/> from records written by older builds. Checking
    /// only one scheme silently re-brought-back every icon of every collapsed fence — the "折叠白折"
    /// symptom (parked 31, then immediately rescued 31).</summary>
    internal static bool IsIntentionallyCollapsed(HashSet<string> intentionalKeys, DesktopIcon ic) =>
        intentionalKeys.Contains(IndexKey(ic.Index)) || intentionalKeys.Contains(StableKey(ic));

    /// <summary>Resolves a restore-point key to a live icon: index keys first (exact, unique), then
    /// <see cref="StableKey"/> (records from older builds, and icons whose index shifted after an
    /// Explorer restart). Returns null when the icon isn't visible to the shell yet.</summary>
    private static DesktopIcon? ResolveParkedIcon(
        Dictionary<string, DesktopIcon> byIndex,
        Dictionary<string, DesktopIcon> byKey,
        string key)
        => byIndex.TryGetValue(key, out var byIdx) ? byIdx
         : byKey.TryGetValue(key, out var byStable) ? byStable
         : null;

    /// <summary>Builds an icon lookup keyed by <see cref="StableKey"/>. Never throws on duplicate
    /// display names — the first occurrence wins — which is exactly what used to crash ExpandRestore
    /// via <c>ToDictionary(ic =&gt; ic.Name)</c>.</summary>
    internal static Dictionary<string, DesktopIcon> BuildKeyIndex(IEnumerable<DesktopIcon> icons)
    {
        var dict = new Dictionary<string, DesktopIcon>(StringComparer.OrdinalIgnoreCase);
        foreach (var ic in icons)
        {
            var key = StableKey(ic);
            if (!dict.ContainsKey(key)) dict[key] = ic;
        }
        return dict;
    }

    /// <summary>An expand is only "complete" — safe to drop its collapse record — when EVERY parked
    /// icon was actually moved back to its original position (<paramref name="restored"/> == total).
    /// A single <paramref name="missing"/> entry (an icon the lookup couldn't match) is treated as
    /// "not yet accounted for", NOT as "genuinely gone": <see cref="GetIcons"/> can transiently miss
    /// icons while Explorer is mid-refresh, and counting those as gone would strand them off-screen
    /// with the record dropped — which is exactly the "collapse→expand→collapse makes a box vanish"
    /// bug (the stranded icons get re-parked by the next collapse, dragging the tab off-screen).
    /// Keeping the record when anything is unaccounted for lets the user retry; the only downside is
    /// a collapsed tab lingering for an icon that was truly deleted, which is far safer than stranding.</summary>
    internal static bool IsExpandComplete(int restored, int missing, int failed, int total) =>
        failed == 0 && restored == total;

    /// <summary>The virtual desktop rect (all monitors) in screen px, or null if the metrics are
    /// unavailable. Used to keep stray icon coordinates out of cluster bounding boxes.
    /// <para>Tests inject a fixed rect via <see cref="_screenProvider"/>; otherwise the metrics come
    /// straight from <see cref="SystemParameters"/>. Returning null is always safe — callers fall back
    /// to the negative-park-zone check alone.</para></summary>
    private RectI? VirtualScreen()
    {
        if (_screenProvider is not null) return _screenProvider();
        try
        {
            int w = (int)SystemParameters.VirtualScreenWidth;
            int h = (int)SystemParameters.VirtualScreenHeight;
            if (w <= 0 || h <= 0) return null;
            return new RectI((int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop, w, h);
        }
        catch { return null; }
    }

    /// <summary>Brings back icons stranded off-screen by a prior crash whose collapse record was
    /// lost (an expand that threw before persisting). Intentionally-collapsed icons (present in
    /// <see cref="_hiddenIcons"/>) are skipped so they stay parked until the user expands them.</summary>
    private void RescueStrandedIcons()
    {
        try
        {
            if (_provider.IsAutoArrangeOn) _provider.DisableAutoArrange();
            var intentional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _hiddenIcons)
                foreach (var key in kv.Value.Keys) intentional.Add(key);

            // Stranded means ANY coordinate off the monitors — not just the classic negative park
            // zone. A coordinate truncated by LVM_SETITEMPOSITION's 16-bit packing can come back as
            // a large POSITIVE value (e.g. y=+31184), which the old negative-only test never caught,
            // leaving that icon invisible forever.
            var screen = VirtualScreen();
            int cw = Math.Max(1, _provider.IconSpacingX);
            int chh = Math.Max(1, _provider.IconSpacingY);
            var parked = _provider.GetIcons()
                .Where(ic => ic.Position.X < FenceClusterBuilder.ParkedThreshold
                          || ic.Position.Y < FenceClusterBuilder.ParkedThreshold
                          || (screen is { } s && !FenceClusterBuilder.IsOnScreen(ic.Position, s, cw, chh)))
                .ToList();
            if (parked.Count == 0) return;

            int slot = 0;
            foreach (var ic in parked)
            {
                if (IsIntentionallyCollapsed(intentional, ic)) continue; // collapsed on purpose
                int x = 80 + (slot % 12) * 90;
                int y = 80 + (slot / 12) * 90;
                try { _provider.SetPosition(ic.Index, new PointI(x, y)); }
                catch (Exception ex) { TraceLog($"[rescue] FAILED '{ic.Name}' @{ic.Position}: {ex.Message}"); }
                slot++;
            }
            if (slot > 0) TraceLog($"[rescue] brought back {slot} stranded icon(s) to a visible cascade");
        }
        catch (Exception ex) { TraceLog($"[rescue] skipped: {ex.Message}"); }
    }

    /// <summary>
    /// Guarantees no fence stays in the dead "collapsed but undrawable" state: a collapsed fence must
    /// have both its parked-icon record (<see cref="_hiddenIcons"/>) and its tab rect (<see cref="_tabBounds"/>),
    /// or its tab can't be rendered and its icons remain stranded off-screen — the box simply vanishes.
    /// Any fence missing either half is expanded back (which rescues the parked icons) so the desktop
    /// can never get stuck invisible. Called every refresh; a healthy state is a no-op.
    /// </summary>
    private void ReconcileCollapsed()
    {
        foreach (var title in _host.CollapsedTitles.ToList())
        {
            if (_tabBounds.ContainsKey(title) && _hiddenIcons.ContainsKey(title)) continue;
            TraceLog($"[reconcile] collapsed '{title}' missing tab/hidden record → restoring to avoid vanishing");
            ExpandFence(title);
        }
    }

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
            var map = new Dictionary<string, PointI>(icons.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var ic in icons)
            {
                // Icons parked off-screen by collapse must not overwrite their pre-collapse
                // position in the saved layout — expanding restores from the collapse store, and
                // the saved layout must keep matching what the user sees when they expand.
                if (_host.IsCollapsed(GroupTitle(ic))) continue;
                map[ic.Name] = ic.Position;
            }
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
        _timer?.Stop();
        _host.Dispose();
        _provider.Dispose();
    }
}