using DesktopOrganizer.Core.Classification;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class SoftwarePurposeClassifierTests
{
    // Tests use the same seed the app ships with, so pass/fail reflects real desktop behavior.
    private static readonly SoftwareGroupingConfig Config = SoftwareGroupStore.Default();

    public sealed class Classify
    {
        [Theory]
        // Display name only (likely UWP / Store, no resolvable target).
        [InlineData("Microsoft Word")]
        [InlineData("WPS Office")]
        public void OfficeByName_LandsInOfficeBox(string name)
            => Assert.Equal("办公软件", SoftwarePurposeClassifier.Classify(Config, name, null));

        // Target-exe driven (typical .lnk).
        [Theory]
        [InlineData(null, "Code.exe")]
        [InlineData("Visual Studio", "devenv.exe")]
        [InlineData(null, "navicat.exe")]
        [InlineData(null, "pycharm64.exe")]
        public void DevTargets_LandInDevBox(string? name, string? target)
            => Assert.Equal("开发/信息安全", SoftwarePurposeClassifier.Classify(Config, name, target));

        [Theory]
        [InlineData(null, "steam.exe")]
        [InlineData("QQ音乐", "QQMusic.exe")]
        [InlineData("GTA V", null)]
        [InlineData("Slay the Spire", null)]
        [InlineData("明日方舟", null)]
        [InlineData("鹰角启动器", null)]
        public void GameAndMedia_LandInMediaBox(string? name, string? target)
            => Assert.Equal("影音娱乐/游戏", SoftwarePurposeClassifier.Classify(Config, name, target));

        [Theory]
        [InlineData(null, "everything.exe")]
        [InlineData(null, "snipaste.exe")]
        public void SystemUtilities_LandInToolsBox(string? name, string? target)
            => Assert.Equal("系统小工具", SoftwarePurposeClassifier.Classify(Config, name, target));
    }

    public sealed class Fallback
    {
        [Fact]
        public void UnrecognizedSoftware_IsOther()
            => Assert.Equal("其他软件", SoftwarePurposeClassifier.Classify(Config, "某不知名工具", "unknown_tool.exe"));
    }

    public sealed class ExplicitFixes
    {
        [Theory]
        [InlineData("夸克网盘", "网盘/下载工具")]
        [InlineData("百度网盘", "网盘/下载工具")]
        public void Netdisk_IsNotSwallowedByBrowserBox(string name, string expected)
            => Assert.Equal(expected, SoftwarePurposeClassifier.Classify(Config, name, null));

        [Fact]
        public void BareQuark_StillIsBrowser()
            => Assert.Equal("上网/网络工具", SoftwarePurposeClassifier.Classify(Config, "夸克", null));

        [Fact]
        public void Access_IsOffice()
            => Assert.Equal("办公软件", SoftwarePurposeClassifier.Classify(Config, "Microsoft Access", "MSACCESS.EXE"));
    }

    public sealed class Ordering
    {
        [Fact]
        public void SoftwarePurposeOrder_KeepsFirstConfigGroupBeforeFallback()
        {
            var office = SoftwarePurposeClassifier.OrderOf(Config, "办公软件");
            var other = SoftwarePurposeClassifier.OrderOf(Config, SoftwarePurposeClassifier.FallbackTitle);
            Assert.True(office < other);
        }

        [Fact]
        public void UnknownTitle_GetsFallbackPosition()
        {
            // A box title from an older config that no longer exists sorts just past the last group.
            Assert.Equal(Config.Groups.Count, SoftwarePurposeClassifier.OrderOf(Config, "不存在的框"));
        }

        [Fact]
        public void EmptyConfiguredBoxes_DoNotLeakIntoOrdering()
        {
            var titles = Config.Groups.Select(g => g.Title).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("文件夹", titles);
            Assert.DoesNotContain("文件", titles);
        }
    }
}

public class BoxGroupingTests
{
    private static readonly SoftwareGroupingConfig Config = SoftwareGroupStore.Default();

    [Fact]
    public void SoftwareByPurpose_UsesItsPurposeBox()
    {
        var (order, title) = BoxGrouping.FromEntry(Config, "Steam", null, "steam.exe");
        Assert.Equal("影音娱乐/游戏", title);
        Assert.Equal(SoftwarePurposeClassifier.OrderOf(Config, title), order);
    }

    [Fact]
    public void SoftwareByStorageLikeTarget_FallsToOtherSoftware()
    {
        var (_, title) = BoxGrouping.FromEntry(Config, "某工具", "C:\\x\\a.exe", "a.exe");
        Assert.Equal("其他软件", title);
    }

    [Fact]
    public void Folder_File_Other_KeepTheirKindBox()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dorg-box-测试");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("文件夹", BoxGrouping.FromEntry(Config, "新建文件夹", dir, null).Title);
            Assert.Equal("文件", BoxGrouping.FromEntry(Config, "报告", "C:\\x\\报告.txt", null).Title);
            Assert.Equal("其他", BoxGrouping.FromEntry(Config, "回收站", null, null).Title);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir); }
    }

    [Fact]
    public void SoftwareBoxes_PrecedeFolderAndFile()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dorg-box-排序");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var (officeOrder, _) = BoxGrouping.FromEntry(Config, "Word", "C:\\x\\Word.lnk", "WINWORD.EXE");
            var (folderOrder, _) = BoxGrouping.FromEntry(Config, "新建文件夹", dir, null);
            Assert.True(officeOrder < folderOrder);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir); }
    }
}

// Real desktop software mapped against the seeded keyword table — guards the分类 of the actual
// apps that previously fell into 其他软件 and ended up inside the folder box.
public class RealDesktopSoftwareTests
{
    private static readonly SoftwareGroupingConfig Config = SoftwareGroupStore.Default();

    [Theory]
    [InlineData("办公软件", "vivo办公套件", "pcsuite.exe")]
    [InlineData("办公软件", "WorkBuddy", "WorkBuddy.exe")]
    public void OfficeApps_GoToOfficeBox(string expected, string name, string? target)
        => Assert.Equal(expected, SoftwarePurposeClassifier.Classify(Config, name, target));

    [Fact]
    public void Scratch3_IsDev()
        => Assert.Equal("开发/信息安全", SoftwarePurposeClassifier.Classify(Config, "Scratch 3", "Scratch 3.exe"));

    [Theory]
    [InlineData("学习教育", "学习通", "cxstudy.exe")]
    [InlineData("学习教育", "中国大学MOOC_优质在线课程学习平台", "msedge_proxy.exe")]
    public void StudyApps_GoToLearningBox(string expected, string name, string? target)
        => Assert.Equal(expected, SoftwarePurposeClassifier.Classify(Config, name, target));

    [Theory]
    [InlineData("硬件工具", "ZhuAudio USB Device", "XearAudioCenter_x64.exe")]
    [InlineData("硬件工具", "图吧工具箱", "图吧工具箱2026.exe")]
    public void HardwareApps_GoToHardwareBox(string expected, string name, string? target)
        => Assert.Equal(expected, SoftwarePurposeClassifier.Classify(Config, name, target));

    [Fact]
    public void Parsec_IsSystemUtility()
        => Assert.Equal("系统小工具", SoftwarePurposeClassifier.Classify(Config, "ParsecVDisplay", "ParsecVDisplay.exe"));

    [Fact]
    public void Universe_StaysOtherSoftware()
        => Assert.Equal(SoftwarePurposeClassifier.FallbackTitle, SoftwarePurposeClassifier.Classify(Config, "Universe", "Universe.exe"));
}