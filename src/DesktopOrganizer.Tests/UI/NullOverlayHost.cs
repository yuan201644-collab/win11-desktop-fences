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
    public event Action<string>? PinCycled;
    public event Action<string, int, int>? ContextMenuRequested;
    public event Action<string>? DragStarted;
    public event Action<string, int, int>? DragMoved;
    public event Action<string>? DragEnded;
    public event Action<string>? ResizeStarted;
    public event Action<string, RectI>? ResizeMoved;
    public event Action<string>? ResizeEnded;

    public OverlayAppearance Appearance { get; set; } = OverlayAppearance.Default;

    /// <summary>Per-fence overrides, if the controller set any (observable by tests).</summary>
    public Dictionary<string, OverlayAppearance> FenceColors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clusters from the most recent <see cref="Sync"/> call, so tests can assert the
    /// geometry the controller actually drew (per-box insets reshape these bounds).</summary>
    public IReadOnlyList<FenceCluster> LastClusters { get; private set; } = Array.Empty<FenceCluster>();

    /// <summary>The value from the most recent <see cref="SetVisible"/> call (false before the
    /// first call), so tests can assert the controller's show/hide verdict without a real window.</summary>
    public bool Visible { get; private set; }

    /// <summary>How many times <see cref="SetVisible"/> was called — lets a test prove the
    /// controller did NOT spam redundant show/hide requests.</summary>
    public int SetVisibleCalls { get; private set; }

    public void SetVisible(bool visible)
    {
        Visible = visible;
        SetVisibleCalls++;
    }

    public void Sync(IReadOnlyList<FenceCluster> clusters, int headerPx, IReadOnlyCollection<string>? pinnedTitles = null,
        IReadOnlyCollection<string>? lockedTitles = null)
    {
        LastClusters = clusters.ToList();
        LastPinnedTitles = pinnedTitles;
        LastLockedTitles = lockedTitles;
    }

    /// <summary>The pinned-titles set from the most recent <see cref="Sync"/> call (null when the
    /// controller passed none), so tests can assert which boxes were drawn as pinned.</summary>
    public IReadOnlyCollection<string>? LastPinnedTitles { get; private set; }

    /// <summary>The locked-titles set from the most recent <see cref="Sync"/> call — a locked box is
    /// pinned AND refuses drag/resize, so tests can tell the two badge states apart.</summary>
    public IReadOnlyCollection<string>? LastLockedTitles { get; private set; }

    /// <summary>Every single-box move the controller asked for (title → rect), newest last. The
    /// drag path only writes here as a CORRECTIVE snap (when the clamped drop spot differs from
    /// what the window reported); tests assert against it to verify that clamp.</summary>
    public List<(string Title, RectI Bounds)> MovedBounds { get; } = new();

    public void SetFenceBounds(string title, RectI bounds) => MovedBounds.Add((title, bounds));

    /// <summary>Every animated single-box move (title → rect → glide ms), newest last. The drag-end
    /// magnetic snap writes here; the target rect is mirrored into <see cref="MovedBounds"/> too, so
    /// "where did the controller put the box" assertions stay uniform across both paths.</summary>
    public List<(string Title, RectI Bounds, int GlideMs)> MovedBoundsAnimated { get; } = new();

    public void SetFenceBoundsAnimated(string title, RectI bounds, int glideMilliseconds)
    {
        MovedBounds.Add((title, bounds));
        MovedBoundsAnimated.Add((title, bounds, glideMilliseconds));
    }

    /// <summary>The drag snap preview the controller last asked for (null = hidden). Tests assert
    /// the previewed drop spot against the lattice math and that release clears it.</summary>
    public RectI? LastPreviewBounds { get; private set; }

    public void SetFencePreview(string title, RectI? bounds) => LastPreviewBounds = bounds;

    /// <summary>Every badge repaint the controller asked for (title → mode), newest last. The badge
    /// is a cycle button, so a pin-mode change must repaint it without moving the box.</summary>
    public List<(string Title, FencePinMode Mode)> PinModeToggles { get; } = new();

    public void SetFencePinMode(string title, FencePinMode mode) => PinModeToggles.Add((title, mode));

    // Test-side triggers for the drag gesture (a real FenceWindow raises these from mouse events).
    public void RaiseDragStarted(string title) => DragStarted?.Invoke(title);
    public void RaiseDragMoved(string title, int dx, int dy) => DragMoved?.Invoke(title, dx, dy);
    public void RaiseDragEnded(string title) => DragEnded?.Invoke(title);

    /// <summary>Test-side trigger for the pin badge click (a real FenceWindow raises this from the
    /// badge's mouse-down).</summary>
    public void RaisePinCycled(string title) => PinCycled?.Invoke(title);

    /// <summary>Test-side trigger for the resize gesture (a real FenceWindow raises this from the
    /// edge-grab mouse-down).</summary>
    public void RaiseResizeStarted(string title) => ResizeStarted?.Invoke(title);
    public void RaiseResizeMoved(string title, RectI bounds) => ResizeMoved?.Invoke(title, bounds);
    public void RaiseResizeEnded(string title) => ResizeEnded?.Invoke(title);

    /// <summary>Set by tests to emulate a real window the user grabbed (the drag path anchors on the
    /// rendered rect so a resized box keeps its size); null by default = "never drawn".</summary>
    public RectI? FenceBoundsOverride { get; set; }

    /// <summary>Headless host has no windows — the settings editor must not rely on live geometry
    /// in tests; the pinned-layout path (which needs no window) is the one under test.</summary>
    public RectI? GetFenceBounds(string title) => FenceBoundsOverride;

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
