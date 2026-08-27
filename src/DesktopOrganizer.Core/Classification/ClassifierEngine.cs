using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Classification;

public sealed class ClassifierEngine
{
    public Category Classify(IconEntry icon, ClassifierConfig config)
    {
        if (config.Overrides.TryGetValue(icon.Name, out var byOverride))
            return byOverride;

        var byRule = config.Rules.FirstOrDefault(r => r.Matches(icon));
        if (byRule is not null && byRule.Category is { } c)
            return c;

        if (icon.LinkTargetApp is not null
            && DefaultRules.LinkTargetCategories.TryGetValue(icon.LinkTargetApp, out var byLink))
            return byLink;

        var ext = (string.IsNullOrWhiteSpace(icon.Path)
            ? Path.GetExtension(icon.Name)
            : Path.GetExtension(icon.Path)).TrimStart('.');
        if (DefaultRules.ExtensionCategories.TryGetValue(ext, out var byExt))
            return byExt;

        foreach (var (keyword, category) in DefaultRules.KeywordCategories
                     .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            if (icon.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return category;

        return Category.Other;
    }
}
