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
        var targets = GridLayoutCalculator.Compute(fence, icons.Count, columns, _provider.IconSpacingX, _provider.IconSpacingY);
        var report = new List<(DesktopIcon, Category, PointI)>();
        for (var i = 0; i < icons.Count && i < targets.Count; i++)
        {
            var icon = icons[i];
            var entry = new IconEntry(icon.Index, icon.Name, icon.Path ?? string.Empty, null);
            var category = _engine.Classify(entry, _config);
            _provider.SetPosition(icon.Index, targets[i]); // DesktopAutoArrangeException bubbles to caller
            report.Add((icon, category, targets[i]));
        }
        return report;
    }
}
