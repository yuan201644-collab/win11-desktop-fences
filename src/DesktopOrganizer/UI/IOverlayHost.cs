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

    /// <summary>Palette used to recolor every visible fence live.</summary>
    OverlayAppearance Appearance { get; set; }

    /// <summary>Shows or hides the whole overlay mesh.</summary>
    void SetVisible(bool visible);

    /// <summary>Resizes the fence mesh to match a fresh cluster layout.</summary>
    void Sync(IReadOnlyList<FenceCluster> clusters, int headerPx);

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
