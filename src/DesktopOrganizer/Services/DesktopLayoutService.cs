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

    public IReadOnlyList<(DesktopIcon Icon, Category Category, PointI Target)> ArrangeIntoFence(RectI fence, int columns)
    {
        if (!_provider.IsAvailable) return new List<(DesktopIcon, Category, PointI)>();
        var icons = _provider.GetIcons();

        // Classify every icon; resolve .lnk targets so the LinkTarget rule table applies.
        var classified = new List<(DesktopIcon Icon, Category Category)>(icons.Count);
        foreach (var icon in icons)
        {
            var linkApp = icon.Path is not null ? DesktopShellEnumerator.LinkTargetAppFromPath(icon.Path) : null;
            var entry = new IconEntry(icon.Index, icon.Name, icon.Path ?? string.Empty, linkApp);
            classified.Add((icon, _engine.Classify(entry, _config)));
        }

        // Group by category (then name) so same-type icons cluster together on the desktop.
        var ordered = classified
            .OrderBy(x => (int)x.Category)
            .ThenBy(x => x.Icon.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cellW = _provider.IconSpacingX;
        var cellH = _provider.IconSpacingY;
        var cols = Math.Max(1, columns);
        // Extra rows to insert between category boundaries for visual separation.
        const int CategoryGapRows = 1;

        var targets = new List<PointI>(ordered.Count);
        var row = 0;
        var col = 0;
        var prevCat = ordered[0].Category;

        for (var i = 0; i < ordered.Count; i++)
        {
            var cat = ordered[i].Category;
            if (i > 0 && cat != prevCat)
            {
                // Start each category on a fresh row, with blank gap row(s) above it,
                // so groups appear as visually distinct clusters on the desktop.
                if (col != 0) row++;          // finish the current partial row first
                row += CategoryGapRows;       // leave blank row(s) as the visual separator
                col = 0;
                prevCat = cat;
            }
            targets.Add(new PointI(
                fence.X + col * cellW,
                fence.Y + row * cellH));
            col++;
            if (col >= cols) { col = 0; row++; }
        }

        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < ordered.Count && i < targets.Count; i++)
        {
            var (icon, category) = ordered[i];
            _provider.SetPosition(icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((icon, category, targets[i]));
        }
        return report;
    }
}
