namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// Decides which on-screen box a desktop item belongs to, and in what order that box appears.
/// Software is split into small purpose boxes (办公 / 开发 / 影音娱乐 / 系统小工具 / 其他软件) per a
/// user-editable config; folders, files and other items each keep one box after the software.
/// Both the physical placement and the overlay must agree on this grouping so icons sit
/// inside the box that labels them.
/// </summary>
public static class BoxGrouping
{
    /// <summary>
    /// Box identity (display order + title) for a desktop item. Software is classed by purpose
    /// (via its resolved target exe when available); everything else by item kind. Purpose boxes
    /// come first in config order, then the kind boxes fold/file/other in fixed slots after them.
    /// </summary>
    public static (int Order, string Title) FromEntry(SoftwareGroupingConfig config, string? name, string? path, string? linkTarget)
    {
        var kind = ItemKindClassifier.FromEntry(name, path);
        if (kind == ItemKind.Software)
        {
            var title = SoftwarePurposeClassifier.Classify(config, name, linkTarget);
            return (SoftwarePurposeClassifier.OrderOf(config, title), title);
        }
        var baseOrder = config.Groups.Count; // 其他软件 (software fallback) occupies slot N
        return kind switch
        {
            // Keep every slot distinct so software (incl. fallback 其他软件) always sorts BEFORE
            // folders — folders used to share slot N with 其他软件, interleaving their icons.
            ItemKind.Folder => (baseOrder + 1, "文件夹"),
            ItemKind.File => (baseOrder + 2, "文件"),
            _ => (baseOrder + 3, "其他"),
        };
    }
}