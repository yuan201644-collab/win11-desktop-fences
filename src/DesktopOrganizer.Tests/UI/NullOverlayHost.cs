using System;
using System.Collections.Generic;
using System.Linq;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.UI;

namespace DesktopOrganizer.Tests.UI;

// The controller subscribes to these events, but a headless double never raises them — so CS0067
// ("event never used") would otherwise fire under TreatWarningsAsErrors. That is intentional here.
#pragma warning disable CS0067

/// <summary>
/// Headless <see cref="IOverlayHost"/> for unit tests. Tracks collapsed state in memory and makes
/// <see cref="Sync"/> / <see cref="SetVisible"/> no-ops, so the controller can be driven without
/// ever creating a WPF <c>FenceWindow</c> (which needs an STA dispatcher the test thread lacks).
/// </summary>
public sealed class NullOverlayHost : IOverlayHost
{
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? CollapseToggled;
    public event Action<string, int, int>? ContextMenuRequested;
    public event Action<string>? DragStarted;
    public event Action<string, int, int>? DragMoved;
    public event Action<string>? DragEnded;
    public event Action<string, RectI>? ResizeMoved;
    public event Action<string>? ResizeEnded;

    public OverlayAppearance Appearance { get; set; } = OverlayAppearance.Default;

    /// <summary>Per-fence overrides, if the controller set any (observable by tests).</summary>
    public Dictionary<string, OverlayAppearance> FenceColors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clusters from the most recent <see cref="Sync"/> call, so tests can assert the
    /// geometry the controller actually drew (per-box insets reshape these bounds).</summary>
    public IReadOnlyList<FenceCluster> LastClusters { get; private set; } = Array.Empty<FenceCluster>();

    public void SetVisible(bool visible) { }

    public void Sync(IReadOnlyList<FenceCluster> clusters, int headerPx)
        => LastClusters = clusters.ToList();

    public void SetFenceBounds(string title, RectI bounds) { }

    /// <summary>Headless host has no windows — the settings editor must not rely on live geometry
    /// in tests; the pinned-layout path (which needs no window) is the one under test.</summary>
    public RectI? GetFenceBounds(string title) => null;

    public void SetFenceAppearance(string title, OverlayAppearance? appearance)
    {
        if (appearance is null) FenceColors.Remove(title);
        else FenceColors[title] = appearance;
    }

    public void SetInitialCollapsed(IEnumerable<string> titles)
    {
        _collapsed.Clear();
        foreach (var t in titles) _collapsed.Add(t);
    }

    public IReadOnlyList<string> CollapsedTitles => _collapsed.ToList();

    public bool IsCollapsed(string title) => _collapsed.Contains(title);

    public bool ToggleCollapse(string title)
    {
        if (!_collapsed.Add(title)) { _collapsed.Remove(title); return false; }
        return true;
    }

    public void Dispose() { }
}

#pragma warning restore CS0067
