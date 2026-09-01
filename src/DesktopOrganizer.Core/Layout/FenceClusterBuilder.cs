using System.Collections.Generic;
using System.Linq;

namespace DesktopOrganizer.Core.Layout;

/// <summary>
/// A physical group of same-kind desktop icons: the group title shown on the overlay,
/// the icon count, and the on-screen bounding box (screen pixel coordinates) that
/// contains every icon's cell. Built by <see cref="FenceClusterBuilder"/>.
/// </summary>
public sealed record FenceCluster(string Title, int IconCount, RectI Bounds)
{
    /// <summary>Collapsed to a thin tab (title band only); icons stay in place, the box is not drawn.</summary>
    public bool IsCollapsed { get; init; }
}

public static class FenceHeader
{
    /// <summary>
    /// Height (px) reserved at the top of every fence for its title bar. Icons are laid out
    /// below it, so the title never overlaps the first icon. Shared by the layout (which
    /// offsets icons down) and the overlay (which extends each box up by this much).
    /// </summary>
    public const int HeaderPx = 34;
}

/// <summary>
/// Turns a set of placed icon anchors (each icon's top-left screen position), grouped
/// by an arbitrary cluster key (currently the top-level <c>ItemKind</c>), into per-group
/// bounding boxes. Pure and unit-tested.
/// </summary>
public static class FenceClusterBuilder
{
    /// <summary>
    /// Positions more negative than this on either axis are treated as parked/stranded
    /// (fold parks icons at a -32000 base) and excluded from bounding-box math.
    /// Shared threshold with the controller's RescueStrandedIcons.
    /// </summary>
    public const int ParkedThreshold = -10000;

    /// <param name="placed">Icon anchors, each tagged with its cluster group title.</param>
    /// <param name="cellWidth">Icon cell width in px (e.g. <c>IconSpacingX</c>).</param>
    /// <param name="cellHeight">Icon cell height in px (e.g. <c>IconSpacingY</c>).</param>
    /// <param name="pad">Extra px around each cluster so the box doesn't touch icons.</param>
    /// <remarks>Groups keep their first-seen order; pass items grouped by your intended
    /// ordering if a stable layout matters.</remarks>
    /// <param name="screen">When given, the virtual desktop rect: icons lying entirely outside it
    /// (fold-parked at -32000, or stranded at any bogus coordinate) are dropped before the box is
    /// computed, so one stray point can never stretch a box — and its title bar — off-screen.</param>
    public static IReadOnlyList<FenceCluster> Build(
        IEnumerable<(string Group, PointI Position)> placed,
        int cellWidth, int cellHeight, int pad = 8, int headerPx = 0,
        int padLeft = int.MinValue, int padRight = int.MinValue, int padTop = int.MinValue, int padBottom = int.MinValue,
        bool separateOverlaps = true, RectI? screen = null)
    {
        // Per-side padding: int.MinValue is the sentinel meaning "use pad". Any real value — including
        // negatives (shrink that edge of the box) — is honored as-is on that edge without touching the others.
        int left = padLeft == int.MinValue ? pad : padLeft;
        int right = padRight == int.MinValue ? pad : padRight;
        int top = padTop == int.MinValue ? pad : padTop;
        int bottom = padBottom == int.MinValue ? pad : padBottom;

        var list = placed.Select(x => (x.Group, x.Position)).ToList();
        var clusters = new List<FenceCluster>();
        if (list.Count == 0) return clusters;

        // Parked/stranded icons sit far off-screen (fold base -32000, detection threshold -10000,
        // matching RescueStrandedIcons). One such point in a group would stretch its bounding box
        // to ~32k px wide — the "title bar extends forever" bug. Skip them; the next refresh,
        // after explorer applies the restored positions, draws them back into the box.
        list.RemoveAll(p => p.Position.X < ParkedThreshold || p.Position.Y < ParkedThreshold);
        if (list.Count == 0) return clusters;

        // Second guard, screen-relative: catches stray coordinates in BOTH directions — a parked
        // icon at -32000 as well as anything dumped far to the right/below the monitors. Only a
        // cell with no overlap at all with the virtual desktop is dropped, so icons sitting on a
        // secondary monitor (legitimately negative, e.g. x=-1920) always survive.
        if (screen is { } sc)
        {
            list.RemoveAll(p => !IsOnScreen(p.Position, sc, cellWidth, cellHeight));
            if (list.Count == 0) return clusters;
        }

        // Minimum vertical separation between adjacent boxes so they never fuse into one blob,
        // even when the desktop grid packer was forced to drop its kind-gap rows (dense/1080p).
        const int MinGap = 6;

        foreach (var group in list.GroupBy(p => p.Group))
        {
            var pts = group.Select(p => p.Position).ToList();
            if (pts.Count == 0) continue;
            var bounds = BoxBounds(pts, cellWidth, cellHeight, left, top, right, bottom, headerPx);

            // Bounding boxes can overlap (adjacent kinds sharing an edge, or a gap row sacrificed
            // under height pressure). Keep clusters GroupBy order (软件/文件夹/... first-seen = top),
            // and push this cluster straight down until it is clear of every earlier box. This ONLY
            // makes sense for a packer; when boxes are derived from already-placed icons (our render
            // path) pushing the box away dislocates it from its own icons, so it switches off.
            if (separateOverlaps)
            {
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var prior in clusters)
                    {
                        if (Intersects(bounds, prior.Bounds))
                        {
                            bounds = new RectI(bounds.X, prior.Bounds.Bottom + MinGap, bounds.Width, bounds.Height);
                            changed = true;
                        }
                    }
                }
            }

            clusters.Add(new FenceCluster(group.Key, pts.Count, bounds));
        }
        return clusters;
    }

    /// <summary>
    /// Bounding box of one group's icon cells: padded on every side and lifted by the title band.
    /// Single source of truth for fence geometry — <see cref="Build"/> (the expanded box) and
    /// <see cref="CollapsedTabBounds"/> (the folded tab) both derive from it, so a box stays exactly
    /// where its title bar was when it folds instead of jumping.
    /// </summary>
    public static RectI BoxBounds(IReadOnlyList<PointI> pts, int cellWidth, int cellHeight,
        int left, int top, int right, int bottom, int headerPx)
    {
        var minX = pts.Min(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxX = pts.Max(p => p.X);
        var maxY = pts.Max(p => p.Y);
        return new RectI(
            minX - left, minY - top - headerPx,
            (maxX + cellWidth + right) - (minX - left),
            (maxY + cellHeight + bottom) - (minY - top) + headerPx);
    }

    /// <summary>
    /// The tab that replaces a collapsed box: the <em>same</em> rectangle the expanded box would
    /// occupy, shrunk to title-band height. Because it reuses <see cref="BoxBounds"/>, the tab sits
    /// exactly on the box's title bar — folding no longer shifts it right (padding) or down
    /// (padding + header lift) and no longer narrows it.
    /// </summary>
    public static RectI CollapsedTabBounds(IReadOnlyList<PointI> pts, int cellWidth, int cellHeight,
        int left, int top, int right, int bottom, int headerPx)
    {
        var box = BoxBounds(pts, cellWidth, cellHeight, left, top, right, bottom, headerPx);
        return new RectI(box.X, box.Y, box.Width, Math.Max(1, headerPx));
    }

    /// <summary>True when an icon cell anchored at <paramref name="pos"/> overlaps the virtual
    /// desktop at all. Deliberately generous: a cell is kept if any part of it is on a monitor, so
    /// icons on a secondary screen (which can start at a negative x) are never discarded.</summary>
    public static bool IsOnScreen(PointI pos, RectI screen, int cellWidth, int cellHeight)
        => pos.X + cellWidth >= screen.Left && pos.X <= screen.Right
        && pos.Y + cellHeight >= screen.Top && pos.Y <= screen.Bottom;

    /// <summary>Last-resort cap: shrinks a box that is larger than the whole virtual desktop down
    /// to the screen. Only ever fires for a coordinate we failed to classify; normal boxes — even
    /// ones spanning several monitors — are returned untouched.</summary>
    public static RectI ClampBounds(RectI bounds, RectI screen)
    {
        int l = Math.Max(bounds.Left, screen.Left);
        int t = Math.Max(bounds.Top, screen.Top);
        int r = Math.Min(bounds.Right, screen.Right);
        int b = Math.Min(bounds.Bottom, screen.Bottom);
        return new RectI(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
    }

    private static bool Intersects(RectI a, RectI b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}