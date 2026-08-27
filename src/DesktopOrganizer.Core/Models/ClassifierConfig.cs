namespace DesktopOrganizer.Core.Models;

public sealed class ClassifierConfig
{
    public string Version { get; set; } = "1";
    public List<CategoryRule> Rules { get; set; } = new();
    public Dictionary<string, Category> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
