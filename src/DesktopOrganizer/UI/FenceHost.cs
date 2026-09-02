using System;
using System.Collections.Generic;
using System.Linq;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.UI;

/// <summary>
/// Owns the per-cluster <see cref="FenceWindow"/>s. Each cluster is one draggable layered window
/// reparented under the desktop shell (behind the real icons). <see cref="Sync"/> reconciles a set of
/// clusters to the pool — reusing windows by title, repositioning the live ones and hiding the stale
/// ones — so the 2s refresh doesn't destroy/recreate windows every tick.
///
/// The host is deliberately not a Window: it just manages the fence pool and forwards drag events
/// (with cumulative pixel deltas) for the controller to move the real icons.
/// </summary>
public sealed class FenceHost : IOverlayHost
{
    private readonly Dictionary<string, FenceWindow> _fences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlayAppearance> _fenceColors = new(StringComparer.OrdinalIgnoreCase);
    private OverlayAppearance _appearance = OverlayAppearance.Default;

    /// <summary>Raised when a fence's header is double-clicked — flip its collapsed state.</summary>
    public event Action<string>? CollapseToggled;

    /// <summary>Raised when a fence's header (incl. collapsed tab) is right-clicked — show its context menu.</summary>
    public event Action<string, int, int>? ContextMenuRequested;

    /// <summary>Raised when a drag begins — snapshot the cluster's icon positions here.</summary>
    public event Action<string>? DragStarted;

    /// <summary>Raised per mouse move with cumulative pixel deltas from drag start.</summary>
    public event Action<string, int, int>? DragMoved;

    /// <summary>Raised once when a drag ends so the controller can finalize and persist.</summary>
    public event Action<string>? DragEnded;

    /// <summary>Raised once when an edge resize grab starts, before the first move — the
    /// controller parks the box's icons for the gesture (same trick as a drag).</summary>
    public event Action<string>? ResizeStarted;

    /// <summary>Raised live while the user drags a box edge, with the candidate screen-px rectangle.</summary>
    public event Action<string, RectI>? ResizeMoved;

    /// <summary>Raised once when an edge drag ends (mouse up) — the controller finalizes the layout.</summary>
    public event Action<string>? ResizeEnded;

    public OverlayAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value ?? OverlayAppearance.Default;
            foreach (var (title, f) in _fences)
                if (!_fenceColors.ContainsKey(title)) f.SetAppearance(_appearance);
        }
    }

    /// <summary>Overrides one fence's palette; null clears the override back to <see cref="Appearance"/>.</summary>
    public void SetFenceAppearance(string title, OverlayAppearance? appearance)
    {
        if (appearance is null) _fenceColors.Remove(title);
        else _fenceColors[title] = appearance;
        if (_fences.TryGetValue(title, out var win)) win.SetAppearance(EffectiveAppearance(title));
    }

    /// <summary>The current per-fence override, if any (null = falls back to the global palette).</summary>
    public OverlayAppearance? GetFenceAppearance(string title)
        => _fenceColors.TryGetValue(title, out var c) ? c : null;

    private OverlayAppearance EffectiveAppearance(string title)
        => _fenceColors.TryGetValue(title, out var c) ? c : _appearance;

    public void SetVisible(bool visible)
    {
        foreach (var f in _fences.Values)
        {
            if (visible && !f.IsVisible) f.Show();
            else if (!visible && f.IsVisible) f.Hide();
        }
    }

    /// <summary>
    /// Resizes the fence mesh to match a fresh cluster layout. Uses each window's own (reparented)
    /// geometry; windows whose title is gone are hidden, and new titles get a window on demand.
    /// </summary>
    public void Sync(IReadOnlyList<FenceCluster> clusters, int headerPx)
    {
        // First-wins build: two clusters can never share a title by construction, but a malformed
        // grouping config could in theory produce duplicates — fail safe (keep the first) rather than
        // throw ArgumentException and take the whole overlay down.
        var wanted = new Dictionary<string, FenceCluster>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clusters) if (!wanted.ContainsKey(c.Title)) wanted[c.Title] = c;

        foreach (var (title, win) in _fences.ToList())
        {
            if (!wanted.ContainsKey(title)) win.Hide();
        }

        foreach (var cluster in clusters)
        {
            var win = GetWindow(cluster.Title);
            win.SetIconCount(cluster.IconCount);
            win.Render(cluster.Bounds.Left, cluster.Bounds.Top, cluster.Bounds.Width, cluster.Bounds.Height, headerPx,
                IsCollapsed(cluster.Title));
            if (!win.IsVisible) win.Show();
        }
    }

    /// <summary>Seeds the remembered collapsed set (loaded from disk) at startup.</summary>
    public void SetInitialCollapsed(IEnumerable<string> titles)
    {
        _collapsed.Clear();
        foreach (var t in titles) _collapsed.Add(t);
    }

    /// <summary>Moves/resizes one fence window live, without moving icons or re-deriving clusters.
    /// Gives instant feedback while the user drags a box edge; the controller re-lays-out icons on
    /// its own (throttled) schedule and persists the pinned rectangle.</summary>
    public void SetFenceBounds(string title, RectI bounds)
    {
        if (!_fences.TryGetValue(title, out var win)) return;
        win.Render(bounds.Left, bounds.Top, bounds.Width, bounds.Height, FenceHeader.HeaderPx, IsCollapsed(title));
    }

    /// <summary>The fence window's current screen rectangle, or null when the overlay never drew
    /// this box (no window yet, or it was never shown so WPF hasn't assigned geometry).</summary>
    public RectI? GetFenceBounds(string title)
    {
        if (!_fences.TryGetValue(title, out var win)) return null;
        // A Window that has never been shown keeps Left/Top at NaN — treat that as "not drawn".
        if (double.IsNaN(win.Left) || double.IsNaN(win.Top)) return null;
        return new RectI((int)win.Left, (int)win.Top, (int)win.Width, (int)win.Height);
    }

    public IReadOnlyList<string> CollapsedTitles => _collapsed.ToList();

    public bool IsCollapsed(string title) => _collapsed.Contains(title);

    /// <summary>Flips a fence's collapsed state and returns the new value.</summary>
    public bool ToggleCollapse(string title)
    {
        if (!_collapsed.Add(title)) { _collapsed.Remove(title); return false; }
        return true;
    }

    /// <summary>Clears the pool entirely (on large re-allocations). Kept for symmetry / future use.</summary>
    public void Dispose()
    {
        foreach (var f in _fences.Values) f.Close();
        _fences.Clear();
    }

    private FenceWindow GetWindow(string title)
    {
        if (_fences.TryGetValue(title, out var existing)) return existing;

        var win = new FenceWindow(title, EffectiveAppearance(title));
        win.ClusterDragStart += (t) => DragStarted?.Invoke(t);
        win.ClusterDrag += (t, dx, dy) => DragMoved?.Invoke(t, dx, dy);
        win.ClusterDragEnd += (t) => DragEnded?.Invoke(t);
        win.TitleToggleCollapse += (t) => CollapseToggled?.Invoke(t);
        win.ContextMenuRequested += (t, x, y) => ContextMenuRequested?.Invoke(t, x, y);
        win.ResizeStarted += (t) => ResizeStarted?.Invoke(t);
        win.ResizeMoved += (t, r) => ResizeMoved?.Invoke(t, r);
        win.ResizeEnded += (t) => ResizeEnded?.Invoke(t);
        _fences.Add(title, win);
        return win;
    }
}