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
public sealed class FenceHost
{
    private readonly Dictionary<string, FenceWindow> _fences = new(StringComparer.OrdinalIgnoreCase);
    private OverlayAppearance _appearance = OverlayAppearance.Default;

    /// <summary>Raised when a drag begins — snapshot the cluster's icon positions here.</summary>
    public event Action<string>? DragStarted;

    /// <summary>Raised per mouse move with cumulative pixel deltas from drag start.</summary>
    public event Action<string, int, int>? DragMoved;

    /// <summary>Raised once when a drag ends so the controller can finalize and persist.</summary>
    public event Action<string>? DragEnded;

    public OverlayAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value ?? OverlayAppearance.Default;
            foreach (var f in _fences.Values) f.SetAppearance(_appearance);
        }
    }

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
        var wanted = clusters.ToDictionary(c => c.Title, StringComparer.OrdinalIgnoreCase);

        foreach (var (title, win) in _fences.ToList())
        {
            if (!wanted.ContainsKey(title)) win.Hide();
        }

        foreach (var cluster in clusters)
        {
            var win = GetWindow(cluster.Title);
            win.SetIconCount(cluster.IconCount);
            win.Render(cluster.Bounds.Left, cluster.Bounds.Top, cluster.Bounds.Width, cluster.Bounds.Height, headerPx);
            if (!win.IsVisible) win.Show();
        }
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

        var win = new FenceWindow(title, _appearance);
        win.ClusterDragStart += (t) => DragStarted?.Invoke(t);
        win.ClusterDrag += (t, dx, dy) => DragMoved?.Invoke(t, dx, dy);
        win.ClusterDragEnd += (t) => DragEnded?.Invoke(t);
        _fences.Add(title, win);
        return win;
    }
}