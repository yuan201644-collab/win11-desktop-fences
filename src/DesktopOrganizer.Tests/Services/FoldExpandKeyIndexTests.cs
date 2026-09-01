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

/// <summary>
/// Regression tests for the expand record-keeping rule. A mid-restore failure used to drop the
/// collapse record anyway (the old <c>ExpandRestore</c> removed it unconditionally after a
/// <c>break</c> on <see cref="DesktopAutoArrangeException"/>), permanently stranding the remaining
/// icons off-screen with no tab to bring them back — another "folded but won't open" symptom.
/// <see cref="FenceOverlayController.IsExpandComplete"/> is what now decides whether the record is
/// safe to drop; these lock that decision.
///
/// The expand is only "complete" when EVERY parked icon was actually restored (restored == total).
/// A single unaccounted-for icon (missing, e.g. a transient GetIcons flicker) MUST keep the record:
/// counting it as "gone" used to strand it off-screen and, after a re-collapse, drag the tab off-screen
/// — the "collapse→expand→collapse makes a box vanish" bug.
/// </summary>
public class ExpandRecordKeepTests
{
    [Fact]
    public void IsExpandComplete_AllRestored_DropsRecord()
    {
        // 3 of 3 restored, none failed, none missing → safe to drop the record (normal expand).
        Assert.True(FenceOverlayController.IsExpandComplete(restored: 3, missing: 0, failed: 0, total: 3));
    }

    [Fact]
    public void IsExpandComplete_MidRestoreFailure_KeepsRecord()
    {
        // Auto-arrange re-engaged on the 2nd of 3 icons → 1 restored, 1 failed, 1 untouched.
        // The record MUST be kept so icons #2 and #3 are not stranded with no way back.
        Assert.False(FenceOverlayController.IsExpandComplete(restored: 1, missing: 0, failed: 1, total: 3));
    }

    [Fact]
    public void IsExpandComplete_PartialMissing_KeepsRecord()
    {
        // GetIcons transiently missed one icon (explorer refresh flicker) → 2 restored, 1 missing,
        // none failed. The missing icon is NOT proven gone, so stranding it would be wrong: keep the
        // record so the user can retry (this is the bug that made a re-collapsed box vanish).
        Assert.False(FenceOverlayController.IsExpandComplete(restored: 2, missing: 1, failed: 0, total: 3));
    }

    [Fact]
    public void IsExpandComplete_AllMissing_KeepsRecord()
    {
        // Nothing matched the lookup at all (e.g. lookup ran mid-shell-restart). Do NOT treat that as
        // "everything deleted" and drop the record — the icons are almost certainly still parked.
        Assert.False(FenceOverlayController.IsExpandComplete(restored: 0, missing: 3, failed: 0, total: 3));
    }

    [Fact]
    public void IsExpandComplete_NothingToRestore_DropsRecord()
    {
        Assert.True(FenceOverlayController.IsExpandComplete(restored: 0, missing: 0, failed: 0, total: 0));
    }
}
