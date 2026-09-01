using System;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class SoftwareGroupStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DoTestGroups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "software-groups.json");
    }

    [Fact]
    public void Save_NormalizesKeywordsToTrimmedLowercase()
    {
        var cfg = new SoftwareGroupingConfig
        {
            Groups = { new SoftwareGroup("办公软件", new[] { " Word ", "EXCEL", "", "   " }) },
        };
        var path = TempPath();
        SoftwareGroupStore.Save(path, cfg);

        Assert.Equal(new[] { "word", "excel" }, cfg.Groups[0].Keywords);
        Assert.Equal(new[] { "word", "excel" }, SoftwareGroupStore.Load(path).Groups[0].Keywords);
    }

    [Fact]
    public void Save_ThenClassify_MixedCaseKeywordStillMatches()
    {
        // The rule editor hands over whatever the user typed; the classifier lowercases only the
        // haystack, so an un-normalized "Word" keyword would silently never match.
        var cfg = new SoftwareGroupingConfig
        {
            Groups = { new SoftwareGroup("办公软件", new[] { "Word" }) },
        };
        SoftwareGroupStore.Save(TempPath(), cfg);

        Assert.Equal("办公软件", SoftwarePurposeClassifier.Classify(cfg, "Microsoft Word", "WINWORD.EXE"));
        Assert.Equal(SoftwarePurposeClassifier.FallbackTitle,
            SoftwarePurposeClassifier.Classify(cfg, "记事本", "notepad.exe"));
    }

    [Fact]
    public void SaveLoad_RoundTripsGroups()
    {
        var path = TempPath();
        SoftwareGroupStore.Save(path, new SoftwareGroupingConfig
        {
            Groups =
            {
                new SoftwareGroup("游戏", new[] { "gta" }),
                new SoftwareGroup("开发", new[] { "vscode" }),
            },
        });

        var loaded = SoftwareGroupStore.Load(path);
        Assert.Equal(new[] { "游戏", "开发" }, loaded.Groups.Select(g => g.Title));
        Assert.Equal(new[] { "gta" }, loaded.Groups[0].Keywords);
    }

    [Fact]
    public void Load_MissingFile_SeedsDefaultsToDisk()
    {
        var path = TempPath();
        var loaded = SoftwareGroupStore.Load(path);

        Assert.NotEmpty(loaded.Groups);
        Assert.True(File.Exists(path)); // seeded so the user can hand-edit it
    }
}
