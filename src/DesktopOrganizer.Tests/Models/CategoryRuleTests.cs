using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Models;

public class CategoryRuleTests
{
    private static IconEntry Icon(string name, string path, string? target)
        => new(0, name, path, target);

    [Theory]
    [InlineData(RuleField.Extension, RuleOp.Equals, "pdf", true)]
    [InlineData(RuleField.Extension, RuleOp.Equals, "PDF", true)]   // case-insensitive
    [InlineData(RuleField.Extension, RuleOp.Equals, "png", false)]
    [InlineData(RuleField.NameKeyword, RuleOp.Contains, "report", true)]
    [InlineData(RuleField.LinkTargetApp, RuleOp.Contains, "chrome", true)]
    public void PredicateMatchesFieldValue(RuleField field, RuleOp op, string value, bool expected)
    {
        var pred = new RulePredicate(field, op, value);
        var icon = Icon("quarterly-report.pdf", @"C:\d\mid\quarterly-report.pdf", "chrome.exe");
        var actual = field switch
        {
            RuleField.Extension => pred.Matches(Path.GetExtension(icon.Path).TrimStart('.')),
            RuleField.NameKeyword => pred.Matches(icon.Name),
            RuleField.LinkTargetApp => pred.Matches(icon.LinkTargetApp),
            _ => false
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Extension_PredicateMatches_LowercasesBothSides()
    {
        var pred = new RulePredicate(RuleField.Extension, RuleOp.Equals, "PDF");
        Assert.True(pred.Matches("pdf"));
    }

    [Fact]
    public void MatchesAll_WhenNotMatchAny()
    {
        var rule = new CategoryRule
        {
            MatchAny = false,
            Predicates =
            {
                new RulePredicate(RuleField.Extension, RuleOp.Equals, "pdf"),
                new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "report"),
            }
        };
        Assert.True(rule.Matches(Icon("report.pdf", @"C:\d\report.pdf", null)));
        Assert.False(rule.Matches(Icon("invoice.pdf", @"C:\d\invoice.pdf", null))); // passes ext, fails keyword
    }

    [Fact]
    public void MatchesAny_WhenMatchAny()
    {
        var rule = new CategoryRule
        {
            MatchAny = true,
            Predicates =
            {
                new RulePredicate(RuleField.Extension, RuleOp.Equals, "pdf"),
                new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "photo"),
            }
        };
        Assert.True(rule.Matches(Icon("a.pdf", @"C:\d\a.pdf", null)));
        Assert.True(rule.Matches(Icon("photo.png", @"C:\d\photo.png", null)));
        Assert.False(rule.Matches(Icon("plain.txt", @"C:\d\plain.txt", null)));
    }

    [Fact]
    public void NoPredicates_NeverMatches()
    {
        var rule = new CategoryRule { Predicates = { } };
        Assert.False(rule.Matches(Icon("anything.exe", @"C:\d\anything.exe", "anything.exe")));
    }

    [Fact]
    public void LinkTargetApp_Matches_NullSafe()
    {
        var rule = new CategoryRule
        {
            Predicates = { new RulePredicate(RuleField.LinkTargetApp, RuleOp.Contains, "chrome") }
        };
        Assert.False(rule.Matches(Icon("x.lnk", @"C:\d\x.lnk", null)));
    }
}
