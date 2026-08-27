using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class ClassifierEngineTests
{
    private readonly ClassifierEngine _engine = new();

    private static IconEntry Icon(string name, string path, string? target = null)
        => new(0, name, path, target);

    [Fact]
    public void ClassifiesByExtension_WhenNoRulesOrOverride()
    {
        var config = new ClassifierConfig();
        Assert.Equal(Category.Images, _engine.Classify(Icon("pic.png", @"C:\d\pic.png"), config));
        Assert.Equal(Category.Documents, _engine.Classify(Icon("doc.pdf", @"C:\d\doc.pdf"), config));
    }

    [Fact]
    public void ClassifiesByLinkTarget_BeforeExtension()
    {
        var config = new ClassifierConfig();
        // .lnk extension would suggest Applications, but link-target wins.
        Assert.Equal(Category.Browser, _engine.Classify(Icon("Chrome.lnk", @"C:\d\Chrome.lnk", "chrome.exe"), config));
        Assert.Equal(Category.Dev, _engine.Classify(Icon("VS.lnk", @"C:\d\VS.lnk", "devenv.exe"), config));
    }

    [Fact]
    public void ManualOverride_WinsOverEverything()
    {
        var config = new ClassifierConfig();
        config.Overrides["report.pdf"] = Category.Dev; // user override beats extension → Documents
        Assert.Equal(Category.Dev, _engine.Classify(Icon("report.pdf", @"C:\d\report.pdf"), config));
    }

    [Fact]
    public void ManualOverride_IsCaseInsensitiveByName()
    {
        var config = new ClassifierConfig { Overrides = { ["CHROME.LNK"] = Category.Games } };
        Assert.Equal(Category.Games, _engine.Classify(Icon("chrome.lnk", @"C:\d\chrome.lnk", "chrome.exe"), config));
    }

    [Fact]
    public void CustomRule_BeatsExtension_ButNotOverride()
    {
        var config = new ClassifierConfig
        {
            Rules =
            {
                new CategoryRule
                {
                    Category = Category.Downloads,
                    MatchAny = false,
                    Predicates =
                    {
                        new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "weekly"),
                    }
                }
            }
        };
        Assert.Equal(Category.Downloads, _engine.Classify(Icon("weekly-sales.xlsx", @"C:\d\weekly-sales.xlsx"), config));
        Assert.Equal(Category.Documents, _engine.Classify(Icon("annual.xlsx", @"C:\d\annual.xlsx"), config));
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        var config = new ClassifierConfig
        {
            Rules =
            {
                new CategoryRule { Category = Category.Downloads, Predicates = { new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "install") } },
                new CategoryRule { Category = Category.Dev, Predicates = { new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "installer") } },
            }
        };
        Assert.Equal(Category.Downloads, _engine.Classify(Icon("installer.bin", @"C:\d\installer.bin"), config));
    }

    [Fact]
    public void UnknownItem_FallsBackToOther()
    {
        var config = new ClassifierConfig();
        Assert.Equal(Category.Other, _engine.Classify(Icon("mystery.xyz", @"C:\d\mystery.xyz"), config));
    }

    [Fact]
    public void KeywordFallback_IsDeterministic_OrdinalOrder()
    {
        // "backup-installer.xyz" matches both "backup" (Archives) and "installer" (Downloads).
        // Ordinal (case-insensitive) keyword ordering picks "backup" before "installer",
        // so the result must be Archives — and must be stable across repeated calls.
        var config = new ClassifierConfig();
        var icon = Icon("backup-installer.xyz", @"C:\d\backup-installer.xyz");
        var first = _engine.Classify(icon, config);
        var second = _engine.Classify(icon, config);
        Assert.Equal(first, second);
        Assert.Equal(Category.Archives, first);
    }

    [Fact]
    public void CustomRule_BeatsLinkTarget()
    {
        // Link target "chrome.exe" would classify as Browser, but a custom rule
        // matching the name wins (precedence level 2 > level 3).
        var config = new ClassifierConfig
        {
            Rules =
            {
                new CategoryRule
                {
                    Category = Category.Dev,
                    Predicates = { new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "work") }
                }
            }
        };
        Assert.Equal(Category.Dev, _engine.Classify(Icon("work-shortcut.lnk", @"C:\d\work-shortcut.lnk", "chrome.exe"), config));
    }
}
