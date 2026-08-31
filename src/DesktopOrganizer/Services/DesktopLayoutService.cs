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
    private readonly SoftwareGroupingConfig _grouping;

    public DesktopLayoutService(IDesktopIconProvider provider, ClassifierEngine engine, ClassifierConfig config)
    {
        _provider = provider;
        _engine = engine;
        _config = config;
        _grouping = SoftwareGroupStore.Load(SoftwareGroupStore.DefaultFilePath);
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

        // Group by on-screen box (软件按用途拆成 办公/开发/影音/系统/其他 小框;文件夹/文件/其他各一个),
        // then name. Software needs its resolved target exe to tell purpose apart, so we resolve
        // link targets here too — placement and overlay must agree on the same box labels.
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
            .OrderBy(x => x.Box.Order)
            .ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var headerPx = FenceHeader.HeaderPx;
        const int FenceGapX = 24;
        const int FenceGapY = 20;

        // Compact "fence" packaging: each kind packs into its own tight box whose size comes
        // from its own icon count (few columns, so the box hugs its contents and reads as a
        // tidy container, not a wide strip). Fences stack vertically and wrap to a new column
        // when the next would run past the bottom of the screen. That, plus per-icon clamping,
        // keeps every icon on the primary display and boxes non-overlapping by construction.
        var top = fence.Y; var left = fence.X; var right = fence.Right; var bottom = fence.Bottom;
        var availableH = Math.Max(1, fence.Height);
        var maxRowsPerFence = Math.Max(3, availableH / Math.Max(1, cellH) - 1);

        // items is sorted by box order, so grouping by box title preserves that order and
        // concatenating the groups reproduces `items` — targets line up with `items[i]` below.
        var groups = items.GroupBy(x => x.Box.Title).Select(g => g.ToList()).ToList();
        var targets = new List<PointI>(items.Count);
        var cursorX = left; var cursorY = top; var columnMaxW = 0;

        foreach (var group in groups)
        {
            var count = group.Count;
            var cols = PackColumns(count, maxRowsPerFence);
            var rows = Math.Max(1, (int)Math.Ceiling(count / (double)cols));
            var fenceWidth = cols * cellW;
            var fenceHeight = headerPx + rows * cellH;

            // Wrap to a new column when this fence wouldn't fit under the ones above it.
            if (cursorY > top && cursorY + fenceHeight > bottom)
            {
                cursorX += columnMaxW + FenceGapX;
                cursorY = top;
                columnMaxW = 0;
            }
            columnMaxW = Math.Max(columnMaxW, fenceWidth);

            for (var i = 0; i < count; i++)
            {
                // Icons start below the reserved title band, rounded/clamped to the fence.
                var x = Math.Clamp(cursorX + (i % cols) * cellW, left, Math.Max(left, right - cellW));
                var y = Math.Clamp(cursorY + headerPx + (i / cols) * cellH, top, Math.Max(top, bottom - cellH));
                targets.Add(new PointI(x, y));
            }
            cursorY += fenceHeight + FenceGapY;
        }

        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < items.Count && i < targets.Count; i++)
        {
            var (icon, category, _) = items[i];
            _provider.SetPosition(icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((icon, category, targets[i]));
        }
        return report;
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
