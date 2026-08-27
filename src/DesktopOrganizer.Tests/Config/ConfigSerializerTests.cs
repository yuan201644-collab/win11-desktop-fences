using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Config;

public class ConfigSerializerTests
{
    [Fact]
    public void RoundTrips_RulesAndOverrides()
    {
        var config = new ClassifierConfig
        {
            Version = "1",
            Rules =
            {
                new CategoryRule
                {
                    Id = "r1",
                    Category = Category.Downloads,
                    MatchAny = true,
                    Predicates =
                    {
                        new RulePredicate(RuleField.NameKeyword, RuleOp.Contains, "install"),
                        new RulePredicate(RuleField.Extension, RuleOp.Equals, "exe"),
                    }
                }
            },
            Overrides = { ["report.pdf"] = Category.Dev }
        };

        var json = ConfigSerializer.Serialize(config);
        var back = ConfigSerializer.Deserialize(json);

        var rule = Assert.Single(back.Rules);
        Assert.Equal("r1", rule.Id);
        Assert.Equal(Category.Downloads, rule.Category);
        Assert.True(rule.MatchAny);
        Assert.Equal(2, rule.Predicates.Count);
        Assert.Equal(Category.Dev, back.Overrides["report.pdf"]);
        Assert.Equal("1", back.Version);
    }

    [Fact]
    public void Enum_SerializesAsStrings()
    {
        var json = ConfigSerializer.Serialize(new ClassifierConfig
        {
            Overrides = { ["x"] = Category.Games }
        });
        Assert.Contains("Games", json);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsDefaultConfig()
    {
        var config = ConfigSerializer.Deserialize("{ not valid json !!!");
        Assert.NotNull(config);
        Assert.Empty(config.Rules);
        Assert.Empty(config.Overrides);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsDefaultConfig()
    {
        var config = ConfigSerializer.Deserialize("");
        Assert.NotNull(config);
        Assert.Empty(config.Rules);
    }

    [Fact]
    public void Deserialize_PreservesCaseInsensitiveOverrides()
    {
        var config = new ClassifierConfig
        {
            Overrides = { ["REPORT.PDF"] = Category.Dev }
        };

        var json = ConfigSerializer.Serialize(config);
        var back = ConfigSerializer.Deserialize(json);

        Assert.True(back.Overrides.ContainsKey("report.pdf"));
        Assert.Equal(Category.Dev, back.Overrides["report.pdf"]);
    }
}
