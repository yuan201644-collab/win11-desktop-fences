using System.Text.RegularExpressions;

namespace DesktopOrganizer.Core.Models;

public enum RuleField { NameKeyword, Extension, LinkTargetApp }

public enum RuleOp { Equals, Contains, StartsWith, Matches }

public sealed record RulePredicate(RuleField Field, RuleOp Op, string Value)
{
    public bool Matches(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return Op switch
        {
            RuleOp.Equals => string.Equals(value, Value, StringComparison.OrdinalIgnoreCase),
            RuleOp.Contains => value.Contains(Value, StringComparison.OrdinalIgnoreCase),
            RuleOp.StartsWith => value.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            RuleOp.Matches => Regex.IsMatch(value, Value, RegexOptions.IgnoreCase),
            _ => false,
        };
    }
}

public sealed class CategoryRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Category? Category { get; set; }
    public bool MatchAny { get; set; }
    public List<RulePredicate> Predicates { get; set; } = new();

    public bool Matches(IconEntry icon)
    {
        if (Predicates.Count == 0) return false;
        string? ValueFor(RuleField f) => f switch
        {
            RuleField.NameKeyword => icon.Name,
            RuleField.LinkTargetApp => icon.LinkTargetApp,
            RuleField.Extension => (string.IsNullOrWhiteSpace(icon.Path)
                ? Path.GetExtension(icon.Name)
                : Path.GetExtension(icon.Path)).TrimStart('.'),
            _ => null,
        };
        return MatchAny
            ? Predicates.Any(pr => pr.Matches(ValueFor(pr.Field)))
            : Predicates.All(pr => pr.Matches(ValueFor(pr.Field)));
    }
}
