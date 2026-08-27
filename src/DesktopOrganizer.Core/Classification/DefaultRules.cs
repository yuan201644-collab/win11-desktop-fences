using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Classification;

public static class DefaultRules
{
    public static IReadOnlyDictionary<string, Category> ExtensionCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = Category.Images, ["jpeg"] = Category.Images,
            ["png"] = Category.Images, ["gif"] = Category.Images,
            ["bmp"] = Category.Images, ["webp"] = Category.Images,
            ["svg"] = Category.Images, ["heic"] = Category.Images,

            ["txt"] = Category.Documents, ["md"] = Category.Documents,
            ["pdf"] = Category.Documents, ["doc"] = Category.Documents,
            ["docx"] = Category.Documents, ["xls"] = Category.Documents,
            ["xlsx"] = Category.Documents, ["ppt"] = Category.Documents,
            ["pptx"] = Category.Documents, ["rtf"] = Category.Documents,

            ["mp4"] = Category.Videos, ["mkv"] = Category.Videos,
            ["avi"] = Category.Videos, ["mov"] = Category.Videos,
            ["webm"] = Category.Videos, ["wmv"] = Category.Videos,

            ["mp3"] = Category.Audio, ["wav"] = Category.Audio,
            ["flac"] = Category.Audio, ["aac"] = Category.Audio,
            ["ogg"] = Category.Audio, ["m4a"] = Category.Audio,

            ["zip"] = Category.Archives, ["rar"] = Category.Archives,
            ["7z"] = Category.Archives, ["tar"] = Category.Archives,
            ["gz"] = Category.Archives, ["iso"] = Category.Archives,
        };

    public static IReadOnlyDictionary<string, Category> LinkTargetCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = Category.Browser, ["msedge.exe"] = Category.Browser,
            ["firefox.exe"] = Category.Browser, ["brave.exe"] = Category.Browser,
            ["opera.exe"] = Category.Browser,

            ["devenv.exe"] = Category.Dev, ["Code.exe"] = Category.Dev,
            ["windbg.exe"] = Category.Dev, ["git-gui.exe"] = Category.Dev,
            ["cmd.exe"] = Category.Dev, ["powershell.exe"] = Category.Dev,
            ["wt.exe"] = Category.Dev,

            ["winword.exe"] = Category.Office, ["excel.exe"] = Category.Office,
            ["powerpnt.exe"] = Category.Office, ["outlook.exe"] = Category.Office,
            ["onenote.exe"] = Category.Office, ["notepad.exe"] = Category.Office,

            ["steam.exe"] = Category.Games, ["explorer.exe"] = Category.Applications,
            ["mspaint.exe"] = Category.Applications,
        };

    public static IReadOnlyDictionary<string, Category> KeywordCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["screenshot"] = Category.Images,
            ["截图"] = Category.Images,
            ["backup"] = Category.Archives,
            ["备份"] = Category.Archives,
            ["download"] = Category.Downloads,
            ["downloads"] = Category.Downloads,
            ["installer"] = Category.Downloads,
        };
}
