using System.Linq;
using DesktopOrganizer.Core.Config;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class FenceCategoryConfigTests
{
    [Fact]
    public void IsHidden_IsCaseInsensitive()
    {
        var cfg = new FenceCategoryConfig { Hidden = { "Games", "其他" } };
        Assert.True(cfg.IsHidden("Games"));
        Assert.True(cfg.IsHidden("games")); // ASCII titles must match regardless of case
        Assert.True(cfg.IsHidden("其他"));
        Assert.False(cfg.IsHidden("文件夹"));
    }

    [Fact]
    public void SortByPreference_PutsPreferredFirstInGivenOrder()
    {
        var titles = new[] { "文件夹", "开发", "文件", "游戏" };
        var ordered = FenceCategoryConfig.SortByPreference(titles, new[] { "游戏", "开发" });
        Assert.Equal(new[] { "游戏", "开发", "文件夹", "文件" }, ordered);
    }

    [Fact]
    public void SortByPreference_AppendsUnlistedTitlesInOriginalOrder()
    {
        var titles = new[] { "A", "B", "C" };
        var ordered = FenceCategoryConfig.SortByPreference(titles, new[] { "C" });
        Assert.Equal(new[] { "C", "A", "B" }, ordered);
    }

    [Fact]
    public void SortByPreference_IgnoresBlankAndRepeatedPreferredEntries()
    {
        var titles = new[] { "A", "B" };
        var ordered = FenceCategoryConfig.SortByPreference(titles, new[] { "B", " ", "B", "" });
        Assert.Equal(new[] { "B", "A" }, ordered);
    }

    [Fact]
    public void SortByPreference_NullOrEmptyPreferenceKeepsOriginalOrder()
    {
        var titles = new[] { "A", "B", "C" };
        Assert.Equal(titles, FenceCategoryConfig.SortByPreference(titles, null));
        Assert.Equal(titles, FenceCategoryConfig.SortByPreference(titles, Enumerable.Empty<string>().ToList()));
    }

    [Fact]
    public void SortByPreference_UnknownPreferredTitlesAreIgnored()
    {
        var titles = new[] { "A", "B" };
        var ordered = FenceCategoryConfig.SortByPreference(titles, new[] { "从未存在的框", "B" });
        Assert.Equal(new[] { "B", "A" }, ordered);
    }
}
