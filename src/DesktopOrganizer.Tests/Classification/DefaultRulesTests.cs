using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class DefaultRulesTests
{
    [Theory]
    [InlineData("jpg", Category.Images)]
    [InlineData("PNG", Category.Images)]
    [InlineData("pdf", Category.Documents)]
    [InlineData("docx", Category.Documents)]
    [InlineData("mp4", Category.Videos)]
    [InlineData("mp3", Category.Audio)]
    [InlineData("zip", Category.Archives)]
    [InlineData("txt", Category.Documents)]
    public void ExtensionCategories_MapCommonExtensions(string ext, Category expected)
        => Assert.Equal(expected, DefaultRules.ExtensionCategories[ext]);

    [Theory]
    [InlineData("chrome.exe", Category.Browser)]
    [InlineData("msedge.exe", Category.Browser)]
    [InlineData("firefox.exe", Category.Browser)]
    [InlineData("devenv.exe", Category.Dev)]
    [InlineData("Code.exe", Category.Dev)]
    [InlineData("WINWORD.EXE", Category.Office)]
    [InlineData("steam.exe", Category.Games)]
    public void LinkTargetCategories_MapKnownApps(string exe, Category expected)
        => Assert.Equal(expected, DefaultRules.LinkTargetCategories[exe]);

    [Theory]
    [InlineData("screenshot_001.png", Category.Images)]
    [InlineData("桌面截图.png", Category.Images)]
    [InlineData("project-backup.zip", Category.Archives)]
    public void KeywordCategories_MatchByNameContains(string name, Category expected)
    {
        var hit = DefaultRules.KeywordCategories
            .FirstOrDefault(kv => name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hit.Key);
        Assert.Equal(expected, hit.Value);
    }

    [Fact]
    public void KeywordCategories_NoMatch_ReturnsDefaultKey()
    {
        var hit = DefaultRules.KeywordCategories
            .FirstOrDefault(kv => "unrelated.txt".Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
        Assert.Null(hit.Key);
    }

    [Fact]
    public void ExtensionKeys_AreLowercaseAndUnique()
    {
        Assert.All(DefaultRules.ExtensionCategories.Keys, k => Assert.Equal(k, k.ToLowerInvariant()));
    }
}
