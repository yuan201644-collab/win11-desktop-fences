using System.Collections.Generic;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Regression tests for the "duplicate display name" crash that broke fold/expand.
/// Expanding used to build a lookup with <c>ToDictionary(ic =&gt; ic.Name)</c>; a desktop with two
/// icons sharing a name (e.g. two "健身助手" shortcuts) threw ArgumentException and left every
/// icon stranded off-screen. The fix keys by the file path instead. These lock that the index
/// builder never throws on duplicate names and that path keys stay distinct.
/// </summary>
public class FoldExpandKeyIndexTests
{
    [Fact]
    public void BuildKeyIndex_DuplicateDisplayName_DoesNotThrow_KeysByPath()
    {
        var icons = new List<DesktopIcon>
        {
            new(0, "健身助手", @"C:\Users\x\Desktop\健身助手.lnk", new PointI(10, 10)),
            new(1, "健身助手", @"C:\Users\x\Desktop\健身助手 (2).lnk", new PointI(20, 20)),
            new(2, "Visual Studio", @"C:\Users\x\Desktop\Visual Studio.lnk", new PointI(30, 30)),
        };

        var index = FenceOverlayController.BuildKeyIndex(icons);

        Assert.Equal(3, index.Count);
        Assert.True(index.ContainsKey("path:C:\\Users\\x\\Desktop\\健身助手.lnk"));
        Assert.True(index.ContainsKey("path:C:\\Users\\x\\Desktop\\健身助手 (2).lnk"));
        Assert.True(index.ContainsKey("path:C:\\Users\\x\\Desktop\\Visual Studio.lnk"));
    }

    [Fact]
    public void BuildKeyIndex_AllSameName_StillDistinctByPath()
    {
        var icons = new List<DesktopIcon>
        {
            new(0, "dup", @"C:\a\1.lnk", new PointI(0, 0)),
            new(1, "dup", @"C:\a\2.lnk", new PointI(0, 0)),
            new(2, "dup", @"C:\a\3.lnk", new PointI(0, 0)),
        };

        var index = FenceOverlayController.BuildKeyIndex(icons);

        Assert.Equal(3, index.Count);
    }

    [Fact]
    public void BuildKeyIndex_FirstOccurrenceWins_OnTrueDuplicatePath()
    {
        // Two entries with the SAME path (e.g. a provider quirk) must not throw either.
        var icons = new List<DesktopIcon>
        {
            new(0, "健身助手", @"C:\a\same.lnk", new PointI(0, 0)),
            new(1, "健身助手", @"C:\a\same.lnk", new PointI(9, 9)),
        };

        var index = FenceOverlayController.BuildKeyIndex(icons);

        Assert.Single(index);
        Assert.Equal(0, index["path:C:\\a\\same.lnk"].Index);
    }

    [Fact]
    public void StableKey_UsesPath_WhenPresent()
    {
        var ic = new DesktopIcon(0, "健身助手", @"C:\x\健身助手.lnk", new PointI(0, 0));
        Assert.Equal("path:C:\\x\\健身助手.lnk", FenceOverlayController.StableKey(ic));
    }

    [Fact]
    public void StableKey_FallsBackToName_WhenPathMissing()
    {
        var ic = new DesktopIcon(0, "此电脑", null, new PointI(0, 0));
        Assert.Equal("name:此电脑", FenceOverlayController.StableKey(ic));
    }
}
