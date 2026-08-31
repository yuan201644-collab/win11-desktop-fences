using System.Collections.Generic;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.Services;

public sealed class DesktopLayoutService
{
    private readonly IDesktopIconProvider _provider;
    private readonly ClassifierEngine _engine;
    private readonly ClassifierConfig _config;

    public DesktopLayoutService(IDesktopIconProvider provider, ClassifierEngine engine, ClassifierConfig config)
    {
        _provider = provider;
        _engine = engine;
        _config = config;
    }

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

    public IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> ArrangeIntoFence(RectI fence, int maxRows)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, Category, PointI)>();
        var icons = _provider.GetIcons();
        var classified = ClassifyAll(icons);

        // Group by top-level item kind (then name) so the physical placement matches
        // the kind-based overlay boxes: 软件 / 文件夹 / 文件 / 其他 each become a
        // contiguous cluster on the desktop. Category is kept only for the report.
        var ordered = classified
            .Select(c => (Icon: c.Icon, Category: c.Category, Kind: ItemKindClassifier.FromEntry(c.Icon.Name, c.Icon.Path)))
            .OrderBy(x => (int)x.Kind)
            .ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;

        // --- Calculate columns so the grouped grid fits within the visible desktop area ---
        // Picking columns by "count / rows" assumes icons flow continuously, but each kind
        // restarts at column 0 and inserts blank gap rows. That continuous model under-counts
        // the real row use, so the deepest kinds spilled below the fold. Instead simulate the
        // grouped placement for each candidate column count and take the widest one that stays
        // within maxRows — every icon stays on the primary screen.
        var maxCols = Math.Max(1, fence.Width / cellW);
        var cols = ChooseColumns(ordered.Select(x => x.Kind).ToList(), maxRows, maxCols);

        var targets = new List<PointI>(ordered.Count);
        var row = 0;
        var col = 0;
        var prevKind = ordered[0].Kind;

        for (var i = 0; i < ordered.Count; i++)
        {
            var kind = ordered[i].Kind;
            if (i > 0 && kind != prevKind)
            {
                // Start each kind on a fresh row, with blank gap row(s) above it,
                // so groups appear as visually distinct clusters on the desktop.
                if (col != 0) row++;          // finish the current partial row first
                row += CategoryGapRows;        // leave blank row(s) as the visual separator
                col = 0;
                prevKind = kind;
            }
            var x = fence.X + col * cellW;
            var y = fence.Y + row * cellH;
            // Clamp so every icon stays fully inside the fence. Without this, the
            // Explorer grid anchor can render col 0 at a slightly negative coordinate,
            // spilling the leftmost icons onto a secondary monitor to the left.
            x = Math.Clamp(x, fence.X, Math.Max(fence.X, fence.Right - cellW));
            y = Math.Clamp(y, fence.Y, Math.Max(fence.Y, fence.Bottom - cellH));
            targets.Add(new PointI(x, y));
            col++;
            if (col >= cols) { col = 0; row++; }
        }

        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < ordered.Count && i < targets.Count; i++)
        {
            var (icon, category, _) = ordered[i];
            _provider.SetPosition(icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((icon, category, targets[i]));
        }
        return report;
    }

    // Blank rows inserted between kind boundaries for visual separation on the desktop.
    private const int CategoryGapRows = 1;

    /// <summary>
    /// Returns the widest column count whose simulated grouped grid fits within <paramref name="maxRows"/>.
    /// Tries the full width first and walks down; if even one column overflows (far more icons than
    /// rows allow) it still picks 1 so the topmost categories stay visible.
    /// </summary>
    private static int ChooseColumns(IReadOnlyList<ItemKind> kinds, int maxRows, int maxCols)
    {
        for (var cols = Math.Max(1, maxCols); cols >= 1; cols--)
        {
            if (RowsUsed(kinds, cols) <= maxRows) return cols;
        }
        return 1;
    }

    /// <summary>
    /// Counts grid rows (1-based) the placement loop needs for a candidate column count,
    /// mirroring exactly: restart at column 0 on each kind change, insert gap rows between kinds.
    /// </summary>
    private static int RowsUsed(IReadOnlyList<ItemKind> kinds, int cols)
    {
        var row = 0;
        var col = 0;
        var prev = kinds[0];
        for (var i = 0; i < kinds.Count; i++)
        {
            var kind = kinds[i];
            if (i > 0 && kind != prev)
            {
                if (col != 0) row++;
                row += CategoryGapRows;
                col = 0;
                prev = kind;
            }
            col++;
            if (col >= cols) { col = 0; row++; }
        }
        return row + 1;
    }
}
