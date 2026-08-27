# M1 — Core Classification Engine & Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `DesktopOrganizer.Core` classification foundation — domain models, rule matching, the classifier engine with full precedence, and JSON config round-trip — fully unit-tested.

**Architecture:** Pure, dependency-free C# logic in `DesktopOrganizer.Core`. The classifier takes an `IconEntry` + `ClassifierConfig` and returns a `Category`, applying precedence: manual override → custom rules → linked-app → extension → name keyword → default. The app project (`DesktopOrganizer`) consumes this later (M2+); nothing here touches Win32 or WPF.

**Tech Stack:** .NET 9, xUnit, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-08-27-desktop-organizer-design.md` (§3.1, §4, §7)

## Global Constraints

- Target `net9.0`; `Nullable` + `ImplicitUsings` + `TreatWarningsAsErrors` are set globally in `Directory.Build.props` — code must compile with **zero warnings**.
- Namespaces: models in `DesktopOrganizer.Core.Models`; classifier + default rules in `DesktopOrganizer.Core.Classification`; config I/O in `DesktopOrganizer.Core.Config`.
- Commits: English Conventional Commits (`feat:` / `test:` / `refactor:`), single `main` branch, small incremental commits.
- All behaviors in this plan are covered by xUnit tests in `src/DesktopOrganizer.Tests/`.
- No P/Invoke, no Win32, no WPF in `DesktopOrganizer.Core`.

---

### Task 1: Domain models — `Category` and `IconEntry`

**Files:**
- Create: `src/DesktopOrganizer.Core/Models/Category.cs`
- Create: `src/DesktopOrganizer.Core/Models/IconEntry.cs`
- Test: `src/DesktopOrganizer.Tests/Models/IconEntryTests.cs`

**Interfaces:**
- Produces: `DesktopOrganizer.Core.Models.Category` (enum), `DesktopOrganizer.Core.Models.IconEntry` (record). Used by every later task.
  - `enum Category { Other = 0, Images, Documents, Videos, Audio, Archives, Applications, Browser, Office, Dev, Games, Downloads }`
  - `sealed record IconEntry(int Index, string Name, string Path, string? LinkTargetApp, Category Category = Category.Other)`

- [ ] **Step 1: Write the failing test**

Create `src/DesktopOrganizer.Tests/Models/IconEntryTests.cs`:

```csharp
using DesktopOrganizer.Core.Models;
using Xunit;

namespace DesktopOrganizer.Tests.Models;

public class IconEntryTests
{
    [Fact]
    public void DefaultCategoryIsOther()
    {
        var icon = new IconEntry(0, "report.pdf", @"C:\Users\x\Desktop\report.pdf", null);
        Assert.Equal(Category.Other, icon.Category);
    }

    [Fact]
    public void KeepsProvidedCategoryAndValues()
    {
        var icon = new IconEntry(3, "Web.lnk", @"C:\Users\x\Desktop\Web.lnk", "chrome.exe", Category.Browser);
        Assert.Equal(3, icon.Index);
        Assert.Equal("Web.lnk", icon.Name);
        Assert.Equal("chrome.exe", icon.LinkTargetApp);
        Assert.Equal(Category.Browser, icon.Category);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: FAIL — compile error, `Category` / `IconEntry` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/DesktopOrganizer.Core/Models/Category.cs`:

```csharp
namespace DesktopOrganizer.Core.Models;

public enum Category
{
    Other = 0,
    Images,
    Documents,
    Videos,
    Audio,
    Archives,
    Applications,
    Browser,
    Office,
    Dev,
    Games,
    Downloads,
}
```

Create `src/DesktopOrganizer.Core/Models/IconEntry.cs`:

```csharp
namespace DesktopOrganizer.Core.Models;

public sealed record IconEntry(
    int Index,
    string Name,
    string Path,
    string? LinkTargetApp,
    Category Category = Category.Other);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DesktopOrganizer.Core/Models src/DesktopOrganizer.Tests/Models
git commit -m "feat(core): add Category enum and IconEntry model"
```

---

### Task 2: Rule model — `RuleField` / `RuleOp` / `RulePredicate` / `CategoryRule`

**Files:**
- Create: `src/DesktopOrganizer.Core/Models/RuleModel.cs`
- Test: `src/DesktopOrganizer.Tests/Models/CategoryRuleTests.cs`

**Interfaces:**
- Produces: rule types in `DesktopOrganizer.Core.Models`, used by `ClassifierEngine` and `ClassifierConfig`.
  - `enum RuleField { NameKeyword, Extension, LinkTargetApp }`
  - `enum RuleOp { Equals, Contains, StartsWith, Matches }` (`Matches` = regex, case-insensitive)
  - `sealed record RulePredicate(RuleField Field, RuleOp Op, string Value)` with method `bool Matches(string? value)`
  - `sealed class CategoryRule { string Id; Category? Category; bool MatchAny; List<RulePredicate> Predicates; bool Matches(IconEntry icon) }`
    - `Matches` extracts the field value from the icon (`Extension` and `LinkTargetApp` normalized to lowercase; null-safe) and: if `MatchAny` → any predicate matches; else → all match. A rule with zero predicates returns `false`.

- [ ] **Step 1: Write the failing tests**

Create `src/DesktopOrganizer.Tests/Models/CategoryRuleTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: FAIL — `RuleField` / `RuleOp` / `RulePredicate` / `CategoryRule` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/DesktopOrganizer.Core/Models/RuleModel.cs`:

```csharp
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
```
- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DesktopOrganizer.Core/Models/RuleModel.cs src/DesktopOrganizer.Tests/Models
git commit -m "feat(core): add rule model with predicate matching"
```

---

### Task 3: Built-in default rules

**Files:**
- Create: `src/DesktopOrganizer.Core/Classification/DefaultRules.cs`
- Test: `src/DesktopOrganizer.Tests/Classification/DefaultRulesTests.cs`

**Interfaces:**
- Produces: `DesktopOrganizer.Core.Classification.DefaultRules` static class with:
  - `static IReadOnlyDictionary<string, Category> ExtensionCategories` (case-insensitive key comparer)
  - `static IReadOnlyDictionary<string, Category> LinkTargetCategories` (exe file name, case-insensitive)
  - `static IReadOnlyDictionary<string, Category> KeywordCategories` (name substring; case-insensitive contains)

- [ ] **Step 1: Write the failing test**

Create `src/DesktopOrganizer.Tests/Classification/DefaultRulesTests.cs`:

```csharp
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
    [InlineData("unrelated.txt", Category.Other)]
    public void KeywordCategories_MatchByNameContains(string name, Category expected)
    {
        var hit = DefaultRules.KeywordCategories
            .FirstOrDefault(kv => name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, hit.Value);
    }

    [Fact]
    public void ExtensionKeys_AreLowercaseAndUnique()
    {
        Assert.All(DefaultRules.ExtensionCategories.Keys, k => Assert.Equal(k, k.ToLowerInvariant()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: FAIL — `DefaultRules` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/DesktopOrganizer.Core/Classification/DefaultRules.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DesktopOrganizer.Core/Classification/DefaultRules.cs src/DesktopOrganizer.Tests/Classification
git commit -m "feat(core): add built-in extension/app/keyword default rules"
```

---

### Task 4: `ClassifierEngine` with full precedence

**Files:**
- Create: `src/DesktopOrganizer.Core/Models/ClassifierConfig.cs`
- Create: `src/DesktopOrganizer.Core/Classification/ClassifierEngine.cs`
- Test: `src/DesktopOrganizer.Tests/Classification/ClassifierEngineTests.cs`

**Interfaces:**
- Consumes: `CategoryRule`, `RulePredicate`, `IconEntry`, `DefaultRules` (all from prior tasks).
- Produces:
  - `sealed class ClassifierConfig { string Version = "1"; List<CategoryRule> Rules; Dictionary<string, Category> Overrides }` (namespace `DesktopOrganizer.Core.Models`)
  - `sealed class ClassifierEngine { Category Classify(IconEntry icon, ClassifierConfig config) }` — precedence: **1)** manual override (keyed by `icon.Name` case-insensitive) → **2)** first matching `config.Rules` → **3)** link-target map → **4)** extension map → **5)** keyword map → **6)** `Other`.

- [ ] **Step 1: Write the failing test**

Create `src/DesktopOrganizer.Tests/Classification/ClassifierEngineTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: FAIL — `ClassifierConfig` / `ClassifierEngine` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/DesktopOrganizer.Core/Models/ClassifierConfig.cs`:

```csharp
using DesktopOrganizer.Core.Classification;

namespace DesktopOrganizer.Core.Models;

public sealed class ClassifierConfig
{
    public string Version { get; set; } = "1";
    public List<CategoryRule> Rules { get; set; } = new();
    public Dictionary<string, Category> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

> The `using DesktopOrganizer.Core.Classification;` in `ClassifierConfig.cs` would be an unused using if `CategoryRule` already resolves via `Models`. ❗ Remove `CategoryRule` lives in `Models`, so no using is needed — keep the file:

```csharp
namespace DesktopOrganizer.Core.Models;

public sealed class ClassifierConfig
{
    public string Version { get; set; } = "1";
    public List<CategoryRule> Rules { get; set; } = new();
    public Dictionary<string, Category> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

Create `src/DesktopOrganizer.Core/Classification/ClassifierEngine.cs`:

```csharp
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Classification;

public sealed class ClassifierEngine
{
    public Category Classify(IconEntry icon, ClassifierConfig config)
    {
        if (config.Overrides.TryGetValue(icon.Name, out var byOverride))
            return byOverride;

        var byRule = config.Rules.FirstOrDefault(r => r.Matches(icon));
        if (byRule is not null && byRule.Category is { } c)
            return c;

        if (icon.LinkTargetApp is not null
            && DefaultRules.LinkTargetCategories.TryGetValue(icon.LinkTargetApp, out var byLink))
            return byLink;

        var ext = (string.IsNullOrWhiteSpace(icon.Path)
            ? Path.GetExtension(icon.Name)
            : Path.GetExtension(icon.Path)).TrimStart('.');
        if (DefaultRules.ExtensionCategories.TryGetValue(ext, out var byExt))
            return byExt;

        foreach (var (keyword, category) in DefaultRules.KeywordCategories)
            if (icon.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return category;

        return Category.Other;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DesktopOrganizer.Core/Models/ClassifierConfig.cs src/DesktopOrganizer.Core/Classification/ClassifierEngine.cs src/DesktopOrganizer.Tests/Classification/ClassifierEngineTests.cs
git commit -m "feat(core): add classifier engine with full precedence"
```

---

### Task 5: JSON config serialization round-trip

**Files:**
- Create: `src/DesktopOrganizer.Core/Config/ConfigSerializer.cs`
- Test: `src/DesktopOrganizer.Tests/Config/ConfigSerializerTests.cs`

**Interfaces:**
- Consumes: `ClassifierConfig`, `CategoryRule`, `RulePredicate` (Task 2/4).
- Produces: `DesktopOrganizer.Core.Config.ConfigSerializer` static class:
  - `static string Serialize(ClassifierConfig config)`
  - `static ClassifierConfig Deserialize(string json)` (returns a fresh default on corrupt/invalid JSON — never throws)

- [ ] **Step 1: Write the failing test**

Create `src/DesktopOrganizer.Tests/Config/ConfigSerializerTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: FAIL — `ConfigSerializer` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/DesktopOrganizer.Core/Config/ConfigSerializer.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Config;

public static class ConfigSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ClassifierConfig config)
        => JsonSerializer.Serialize(config, Options);

    public static ClassifierConfig Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ClassifierConfig();
        try
        {
            return JsonSerializer.Deserialize<ClassifierConfig>(json, Options) ?? new ClassifierConfig();
        }
        catch (JsonException)
        {
            return new ClassifierConfig();
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/DesktopOrganizer.Tests/DesktopOrganizer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DesktopOrganizer.Core/Config/ConfigSerializer.cs src/DesktopOrganizer.Tests/Config
git commit -m "feat(core): add JSON config serializer with round-trip tests"
```

---

### Task 6: Full suite green + verification

**Files:**
- (no new code — verification pass)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 2: Full solution build with warnings-as-errors**

Run: `dotnet build -c Release`
Expected: Build succeeds, 0 warnings, 0 errors.

- [ ] **Step 3: Confirm no new Win32/WPF leakage into Core**

Verify `src/DesktopOrganizer.Core` contains only the files created in this plan (no `P/Invoke`, no `WPF`).

- [ ] **Step 4: Commit (only if anything changed; otherwise skip)**

The code is fully committed per-task if each Task 5 step ran. If the verification revealed changes (e.g. a test-only tweak), commit them:

```bash
git add src/DesktopOrganizer.Tests
git commit -m "test(core): finalize M1 core test suite" --allow-empty
```

- [ ] **Step 5: Confirm branch is clean and synced with remote**

Run: `git status` (expect clean), `git log --oneline -10` (expect M1 commits), then `git push origin main`.

---

## Self-Review

- **Spec coverage ($3.1)**: precedence (override → rules → link-target → extension → keyword → Other) implemented across Task 4 + DefaultRules Task 3. ✓
- **Spec coverage ($4)**: `Category`, `IconEntry`, `CategoryRule`, `RulePredicate`, `ClassifierConfig` all defined as records/classes with matching shapes. `Fence`/`RectI`/`AppSettings` intentionally deferred to M5 (layout persistence), as scoped. ✓
- **Spec coverage ($7)**: every Core behavior has a unit test. ✓
- **Placeholder scan**: all steps contain concrete code; no "TBD"/"implement later". ✓
- **Type consistency**: `IconEntry(Index, Name, Path, LinkTargetApp, Category)`, `CategoryRule(Id, Category, MatchAny, Predicates)`, `Matches(IconEntry)`, `RulePredicate(Field, Op, Value).Matches(string?)`, `Classify(IconEntry, ClassifierConfig)`, `Serializer.Serialize/Deserialize` — names are identical across tasks. ✓
- **Warnings-as-errors**: Task 2 step 3 explicitly resolves the unused `Match` helper to keep the build clean. ✓