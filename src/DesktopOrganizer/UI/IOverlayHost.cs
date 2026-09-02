using System;
using System.Collections.Generic;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;

namespace DesktopOrganizer.UI;

/// <summary>
/// The overlay surface the <see cref="FenceOverlayController"/> drives. Extracted from
/// <see cref="FenceHost"/> so the controller can be exercised against a headless double in unit
/// tests — <see cref="NullOverlayHost"/> implements this without ever creating a WPF window.
/// </summary>
public interface IOverlayHost
{
    /// <summary>Raised when a fence's header is double-clicked — flip its collapsed state.</summary>
    event Action<string>? CollapseToggled;

    /// <summary>Raised when a fence's header (incl. collapsed tab) is right-clicked — show its context menu.</summary>
    event Action<string, int, int>? ContextMenuRequested;

    /// <summary>Raised when a drag begins — snapshot the cluster's icon positions here.</summary>
    event Action<string>? DragStarted;

    /// <summary>Raised per mouse move with cumulative pixel deltas from drag start.</summary>
    event Action<string, int, int>? DragMoved;

    /// <summary>Raised once when a drag ends so the controller can finalize and persist.</summary>
    event Action<string>? DragEnded;

    /// <summary>Raised live while the user drags a box edge, with the candidate screen-px rectangle.
    /// The controller re-lays-out that box's icons and persists the pinned layout (throttled).</summary>
    event Action<string, RectI>? ResizeMoved;

    /// <summary>Raised once when an edge drag ends (mouse up) — the controller finalizes the layout.</summary>
    event Action<string>? ResizeEnded;

    /// <summary>Palette used to recolor every visible fence live.</summary>
    OverlayAppearance Appearance { get; set; }

    /// <summary>Shows or hides the whole overlay mesh.</summary>
    void SetVisible(bool visible);

    /// <summary>Resizes the fence mesh to match a fresh cluster layout.</summary>
    void Sync(IReadOnlyList<FenceCluster> clusters, int headerPx);

    /// <summary>Moves/resizes one fence window live (no icon moves) — used while the user drags a box edge.</summary>
    void SetFenceBounds(string title, RectI bounds);

    /// <summary>The fence window's current screen rectangle, or null when the overlay never drew
    /// this box (window absent or not laid out). The settings layout editor uses this as the X/Y
    /// anchor for a box that auto-packs, so typing a width/height pins it in place.</summary>
    RectI? GetFenceBounds(string title);

    /// <summary>Overrides one fence's palette; null clears the override back to <see cref="Appearance"/>.</summary>
    void SetFenceAppearance(string title, OverlayAppearance? appearance);

    /// <summary>Seeds the remembered collapsed set (loaded from disk) at startup.</summary>
    void SetInitialCollapsed(IEnumerable<string> titles);

    /// <summary>The titles currently drawn as collapsed tabs.</summary>
    IReadOnlyList<string> CollapsedTitles { get; }

    /// <summary>True when <paramref name="title"/> is currently drawn as a collapsed tab.</summary>
    bool IsCollapsed(string title);

    /// <summary>Flips a fence's collapsed state and returns the new value.</summary>
    bool ToggleCollapse(string title);

    /// <summary>Releases any native/UI resources the host holds.</summary>
    void Dispose();
}
