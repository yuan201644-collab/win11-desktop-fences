using DesktopOrganizer.Core.Classification;
using Xunit;

namespace DesktopOrganizer.Tests.Classification;

public class ItemKindClassifierTests
{
    [Fact]
    public void ShortcutPath_IsSoftware() =>
        Assert.Equal(ItemKind.Software, ItemKindClassifier.FromPath(@"C:\Users\Me\Desktop\Word.lnk"));

    [Fact]
    public void ExePath_IsSoftware() =>
        Assert.Equal(ItemKind.Software, ItemKindClassifier.FromPath(@"C:\Users\Me\Desktop\wemod.exe"));

    [Fact]
    public void UrlShortcut_IsSoftware() =>
        Assert.Equal(ItemKind.Software, ItemKindClassifier.FromPath(@"C:\Users\Me\Desktop\链接.url"));

    [Theory]
    [InlineData(@"C:\Users\Me\Desktop\新建文件夹")]
    [InlineData(@"C:\Users\Me\Desktop\大创项目文件夹")]
    public void DirectoryPath_IsFolder(string path) =>
        Assert.Equal(ItemKind.Folder, ItemKindClassifier.FromPath(path, isDirectory: true));

    [Fact]
    public void DirectoryPath_WithTrailingSlash_IsFolder() =>
        Assert.Equal(ItemKind.Folder, ItemKindClassifier.FromPath(@"C:\Users\Me\Desktop\测试gym", isDirectory: true));

    [Theory]
    [InlineData(@"C:\Users\Me\Desktop\报告.txt")]
    [InlineData(@"C:\Users\Me\Desktop\照片.jpg")]
    [InlineData(@"C:\Users\Me\Desktop\压缩.zip")]
    public void FileWithExtension_IsFile(string path) =>
        Assert.Equal(ItemKind.File, ItemKindClassifier.FromPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unresolvable_IsOther(string? path) =>
        Assert.Equal(ItemKind.Other, ItemKindClassifier.FromPath(path));

    // FromEntry(name, path): path-unresolvable items (UWP / Store / shell shortcuts)
    // are applications unless they are known system virtual items.

    [Fact]
    public void FromEntry_NullPath_AppName_IsSoftware() =>
        Assert.Equal(ItemKind.Software, ItemKindClassifier.FromEntry("Steam", null));

    [Theory]
    [InlineData("回收站")]
    [InlineData("Recycle Bin")]
    [InlineData("此电脑")]
    [InlineData("This PC")]
    public void FromEntry_SystemVirtual_IsOther(string name) =>
        Assert.Equal(ItemKind.Other, ItemKindClassifier.FromEntry(name, null));

    [Fact]
    public void FromEntry_ResolvableFile_IsFile() =>
        Assert.Equal(ItemKind.File, ItemKindClassifier.FromEntry("报告", @"C:\Users\Me\Desktop\报告.txt"));

    [Fact]
    public void FromEntry_ResolvableShortcut_IsSoftware() =>
        Assert.Equal(ItemKind.Software, ItemKindClassifier.FromEntry("Word", @"C:\Users\Me\Desktop\Word.lnk"));

    [Theory]
    [InlineData("harmony.log")]
    [InlineData("大创计划书2.0.docx")]
    [InlineData("「隐盾」AI 个人信息智能脱敏工具 .mp4")]
    public void FromEntry_NullPath_ButNameHasExtension_IsFile(string name) =>
        Assert.Equal(ItemKind.File, ItemKindClassifier.FromEntry(name, null));
}