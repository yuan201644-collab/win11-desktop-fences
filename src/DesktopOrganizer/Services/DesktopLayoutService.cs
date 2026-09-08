using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Services;

public sealed class DesktopLayoutService
{
    private readonly IDesktopIconProvider _provider;
    private readonly ClassifierEngine _engine;
    private readonly ClassifierConfig _config;
    private SoftwareGroupingConfig _grouping;

    public DesktopLayoutService(IDesktopIconProvider provider, ClassifierEngine engine, ClassifierConfig config)
    {
        _provider = provider;
        _engine = engine;
        _config = config;
        _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
    }

    /// <summary>Swaps in a (possibly user-edited) grouping config with no re-layout until ArrangeIntoFence runs.</summary>
    public void SetGrouping(SoftwareGroupingConfig grouping)
        => _grouping = grouping ?? SoftwareGroupStore.Default();

    /// <summary>
    /// Classifies a set of icons without touching their positions. Reused by the
    /// overlay controller so it can rebuild cluster boxes from live icon state
    /// (e.g. after a manual drag) without re-arranging the desktop.
    /// </summary>
    public IReadOnlyList<(DesktopIcon Icon, Category Category)> ClassifyAll(IReadOnlyList<DesktopIcon> icons)
    {
        var result = new List<(DesktopIcon, Category)>(icons.Count);
        foreach (var icon in icons)
        {
            var linkApp = icon.Path is not null ? DesktopShellEnumerator.LinkTargetAppFromPath(icon.Path) : null;
            var entry = new IconEntry(icon.Index, icon.Name, icon.Path ?? string.Empty, linkApp);
            result.Add((icon, _engine.Classify(entry, _config)));
        }
        return result;
    }

    public IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> ArrangeIntoFence(
        RectI fence, int maxRows, FenceSortMode sort = FenceSortMode.Name,
        IReadOnlyCollection<string>? skipTitles = null)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, Category, PointI)>();
        var icons = _provider.GetIcons();
        var classified = ClassifyAll(icons);

        // Group by on-screen box (软件按用途拆成 办公/开发/影音/系统/其他 小框;文件夹/文件/其他各一个),
        // then by the chosen in-fence ordering. Software needs its resolved target exe to tell
        // purpose apart, so we resolve link targets here too — placement and overlay must agree on
        // the same box labels.
        var items = classified
            .Select(c => new
            {
                c.Icon,
                c.Category,
                Link = c.Icon.Path is null ? null : DesktopShellEnumerator.LinkTargetAppFromPath(c.Icon.Path),
            })
            .Select(x =>
            {
                var box = BoxGrouping.FromEntry(_grouping, x.Icon.Name, x.Icon.Path, x.Link);
                return (x.Icon, x.Category, Box: box);
            })
            .Where(x => skipTitles is null || !skipTitles.Contains(x.Box.Title, StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.Box.Order);

        var ordered = sort switch
        {
            FenceSortMode.Type => items.ThenBy(x => x.Category).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            FenceSortMode.Modified => items.ThenBy(x => ModifiedTimeUtc(x.Icon)).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
        };
        var sortedItems = ordered.ToList();

        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var headerPx = FenceHeader.HeaderPx;

        // Lattice-aligned row-major packing. When the grid is known, the whole layout is computed
        // in ICON-CELL coordinates: cursors start on the lattice and every step is a whole number
        // of cells, so every icon target IS a lattice point — Explorer's per-write snapping
        // becomes identity (no half-cell drift, no edge overflow; even the clamp boundaries are
        // lattice points, so clamping can never knock a target off the grid). Boxes flow
        // left→right and wrap to the next row (text-wrap style), separated by exactly one empty
        // grid column / row — which is the smallest lattice-exact gap there is.
        var hasLattice = _provider.TryGetLattice(out var gridCx, out var gridCy, out var gridOx, out var gridOy);
        static int CeilToLattice(int v, int pitch, int origin)
            => pitch > 0 ? origin + (int)Math.Ceiling((v - origin) / (double)pitch) * pitch : v;
        static int FloorToLattice(int v, int pitch, int origin)
            => pitch > 0 ? origin + (int)Math.Floor((v - origin) / (double)pitch) * pitch : v;

        var left = hasLattice ? CeilToLattice(fence.Left, gridCx, gridOx) : fence.Left;
        var maxX = hasLattice ? FloorToLattice(fence.Right - cellW, gridCx, gridOx) : fence.Right - cellW;
        // The first ICON row sits below the reserved title band, and IT is the lattice anchor —
        // the box's own top (iconY − headerPx) then stays inside the layout rect, so the drag
        // release's ClampFenceRect never shifts a bare-click restore (first-arrange regression).
        var top = hasLattice ? CeilToLattice(fence.Top + headerPx, gridCy, gridOy) : fence.Top + headerPx;
        var maxY = hasLattice ? FloorToLattice(fence.Bottom - cellH, gridCy, gridOy) : fence.Bottom - cellH;
        var maxRowsPerFence = Math.Max(3, fence.Height / Math.Max(1, cellH) - 1);

        // items is sorted by box order, so grouping by box title preserves that order and
        // concatenating the groups reproduces `items` — targets line up with `items[i]` below.
        var groups = sortedItems.GroupBy(x => x.Box.Title).Select(g => g.ToList()).ToList();
        var targets = new List<PointI>(sortedItems.Count);

        var cursorX = left;
        var iconY = top;      // y of the current row-of-boxes' first ICON row (lattice point)
        var rowRows = 0;      // tallest box's row count in the current row-of-boxes
        foreach (var group in groups)
        {
            var count = group.Count;
            var cols = PackColumns(count, maxRowsPerFence);
            var rows = Math.Max(1, (int)Math.Ceiling(count / (double)cols));
            var width = cols * cellW;

            // Wrap to the next row when this box would run past the right edge (at least one
            // box per row, even one wider than the layout — its columns then clamp inward).
            if (cursorX > left && cursorX + width > fence.Right)
            {
                cursorX = left;
                iconY += rowRows * cellH + cellH; // one empty grid row between stacked boxes
                rowRows = 0;
            }
            rowRows = Math.Max(rowRows, rows);

            for (var i = 0; i < count; i++)
            {
                var x = Math.Clamp(cursorX + (i % cols) * cellW, left, Math.Max(left, maxX));
                var y = Math.Clamp(iconY + (i / cols) * cellH, top, Math.Max(top, maxY));
                targets.Add(new PointI(x, y));
            }
            cursorX += width + cellW; // one empty grid column between side-by-side boxes
        }

        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < sortedItems.Count && i < targets.Count; i++)
        {
            var (icon, category, _) = sortedItems[i];
            _provider.SetPosition(icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((icon, category, targets[i]));
        }
        return report;
    }

    /// <summary>
    /// Re-arranges ONLY the icons belonging to <paramref name="title"/> into the given
    /// <paramref name="bounds"/> rectangle (a user-pinned fence box). Column count follows the box
    /// width, rows follow the height budget, and every icon stays inside the rectangle — this is
    /// what the resize drag, the settings layout editor, and <c>ArrangeAndShow</c> (for boxes with a
    /// pinned layout) all use. Box membership and ordering match <see cref="ArrangeIntoFence"/> so a
    /// box never changes which icons it owns between auto-pack and manual layouts.
    /// </summary>
    public IReadOnlyList<(DesktopIcon Icon, PointI Target)> ArrangeOneFence(
        string title, RectI bounds, FenceSortMode sort = FenceSortMode.Name)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, PointI)>();
        var icons = _provider.GetIcons();
        var classified = ClassifyAll(icons);

        var items = classified
            .Select(c => new
            {
                c.Icon,
                c.Category,
                Link = c.Icon.Path is null ? null : DesktopShellEnumerator.LinkTargetAppFromPath(c.Icon.Path),
            })
            .Select(x =>
            {
                var box = BoxGrouping.FromEntry(_grouping, x.Icon.Name, x.Icon.Path, x.Link);
                return (x.Icon, x.Category, Box: box);
            })
            .Where(x => string.Equals(x.Box.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (items.Count == 0) return new List<(DesktopIcon, PointI)>();

        var ordered = sort switch
        {
            FenceSortMode.Type => items.OrderBy(x => x.Category).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            FenceSortMode.Modified => items.OrderBy(x => ModifiedTimeUtc(x.Icon)).ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase),
        };
        var sorted = ordered.ToList();

        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var headerPx = FenceHeader.HeaderPx;
        const int EdgePad = 8;

        // Column count is driven by the box width; widen until every icon fits the height budget.
        var innerW = Math.Max(cellW, bounds.Width - EdgePad * 2);
        var innerH = Math.Max(cellH, bounds.Height - headerPx - EdgePad);
        var cols = Math.Max(1, innerW / cellW);
        var maxRows = Math.Max(1, innerH / cellH);
        var count = sorted.Count;
        while (cols < 16 && (int)Math.Ceiling(count / (double)cols) > maxRows) cols++;

        var targets = new List<PointI>(count);
        for (var i = 0; i < count; i++)
        {
            var x = Math.Clamp(bounds.X + EdgePad + (i % cols) * cellW,
                bounds.Left, Math.Max(bounds.Left, bounds.Right - cellW));
            var y = Math.Clamp(bounds.Y + headerPx + (i / cols) * cellH,
                bounds.Top, Math.Max(bounds.Top, bounds.Bottom - cellH));
            targets.Add(new PointI(x, y));
        }

        var report = new List<(DesktopIcon, PointI)>(count);
        for (var i = 0; i < count; i++)
        {
            _provider.SetPosition(sorted[i].Icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((sorted[i].Icon, targets[i]));
        }
        return report;
    }

    private static DateTime ModifiedTimeUtc(DesktopIcon icon)
    {
        try
        {
            if (icon.Path is null) return DateTime.MinValue;
            return File.GetLastWriteTimeUtc(icon.Path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Chooses the internal column count for one fence: compact (square-ish) for a handful of
    /// icons, but widened so a very large kind never grows taller than the visible area.
    /// </summary>
    private static int PackColumns(int count, int maxRowsPerFence)
    {
        if (count <= 1) return 1;
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)), 1, 6);
        var rows = (int)Math.Ceiling(count / (double)cols);
        if (rows > maxRowsPerFence)
            cols = Math.Clamp((int)Math.Ceiling(count / (double)maxRowsPerFence), 1, 16);
        return Math.Max(1, cols);
    }
}
