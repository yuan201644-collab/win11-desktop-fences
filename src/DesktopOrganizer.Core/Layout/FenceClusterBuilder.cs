using System.Collections.Generic;
using System.Linq;

namespace DesktopOrganizer.Core.Layout;

/// <summary>
/// A physical group of same-kind desktop icons: the group title shown on the overlay,
/// the icon count, and the on-screen bounding box (screen pixel coordinates) that
/// contains every icon's cell. Built by <see cref="FenceClusterBuilder"/>.
/// </summary>
public sealed record FenceCluster(string Title, int IconCount, RectI Bounds);

/// <summary>
/// Turns a set of placed icon anchors (each icon's top-left screen position), grouped
/// by an arbitrary cluster key (currently the top-level <c>ItemKind</c>), into per-group
/// bounding boxes. Pure and unit-tested.
/// </summary>
public static class FenceClusterBuilder
{
    /// <param name="placed">Icon anchors, each tagged with its cluster group title.</param>
    /// <param name="cellWidth">Icon cell width in px (e.g. <c>IconSpacingX</c>).</param>
    /// <param name="cellHeight">Icon cell height in px (e.g. <c>IconSpacingY</c>).</param>
    /// <param name="pad">Extra px around each cluster so the box doesn't touch icons.</param>
    /// <remarks>Groups keep their first-seen order; pass items grouped by your intended
    /// ordering if a stable layout matters.</remarks>
    public static IReadOnlyList<FenceCluster> Build(
        IEnumerable<(string Group, PointI Position)> placed,
        int cellWidth, int cellHeight, int pad = 8)
    {
        var list = placed.Select(x => (x.Group, x.Position)).ToList();
        var clusters = new List<FenceCluster>();
        if (list.Count == 0) return clusters;

        // Minimum vertical separation between adjacent boxes so they never fuse into one blob,
        // even when the desktop grid packer was forced to drop its kind-gap rows (dense/1080p).
        const int MinGap = 6;

        foreach (var group in list.GroupBy(p => p.Group))
        {
            var pts = group.Select(p => p.Position).ToList();
            if (pts.Count == 0) continue;
            var minX = pts.Min(p => p.X);
            var minY = pts.Min(p => p.Y);
            var maxX = pts.Max(p => p.X);
            var maxY = pts.Max(p => p.Y);
            var bounds = new RectI(
                minX - pad, minY - pad,
                (maxX + cellWidth + pad) - (minX - pad),
                (maxY + cellHeight + pad) - (minY - pad));

            // Bounding boxes can overlap (adjacent kinds sharing an edge, or a gap row sacrificed
            // under height pressure). Keep clusters GroupBy order (软件/文件夹/... first-seen = top),
            // and push this cluster straight down until it is clear of every earlier box.
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

            clusters.Add(new FenceCluster(group.Key, pts.Count, bounds));
        }
        return clusters;
    }

    private static bool Intersects(RectI a, RectI b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}