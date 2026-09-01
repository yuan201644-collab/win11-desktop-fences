using System.Linq;
using DesktopOrganizer.Core.Classification;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class KeywordsParserTests
{
    [Fact]
    public void Split_AsciiCommaSpaceAndSemicolon()
        => Assert.Equal(new[] { "word", "excel", "pdf" }, KeywordsParser.Split("word, excel; pdf"));

    [Fact]
    public void Split_FullWidthCommaAndTun()
        => Assert.Equal(new[] { "微信", "qq", "钉钉" }, KeywordsParser.Split("微信、qq，钉钉"));

    [Fact]
    public void Split_InterpunctAndTabs()
        => Assert.Equal(new[] { "a", "b" }, KeywordsParser.Split("a·b\t"));

    [Fact]
    public void Split_TrimsAndRemovesEmpties()
        => Assert.Equal(new[] { "wps" }, KeywordsParser.Split("  ,  wps , \t "));

    [Fact]
    public void Split_Deduplicates()
        => Assert.Equal(new[] { "steam", "epic" }, KeywordsParser.Split("steam，epic, steam"));

    [Fact]
    public void Split_NullOrWhite_ReturnsEmpty()
    {
        Assert.Empty(KeywordsParser.Split(null));
        Assert.Empty(KeywordsParser.Split("   "));
    }
}