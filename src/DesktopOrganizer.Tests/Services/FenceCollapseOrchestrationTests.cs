using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Headless orchestration test for the collapse → expand → collapse → expand cycle of
/// <see cref="FenceOverlayController"/>. This is the regression net for the historical
/// "folder box vanishes after collapse→expand→collapse" bug:
///
///   * Parking used to march coordinates with <c>-32000 - spacing * i</c>, which overflowed the
///     signed 16-bit range <c>LVM_SETITEMPOSITION</c> can carry and came back as a bogus POSITIVE
///     value (e.g. y=+31184). That stranded the icons off-screen AND poisoned the collapsed tab's
///     bounding box, taking the whole box away.
///   * Expanding used to drop its record as soon as ANY icon wasn't immediately matched, stranding
///     the rest.
///
/// The controller is driven through <see cref="NullOverlayHost"/> (no WPF window) and
/// <see cref="FakeDesktopIconProvider"/> (positions observable via GetIcons), with a title resolver
/// that puts every icon in one box so the whole set folds and unfolds together.
///
/// Two hidden environment dependencies are injected so the outcome never depends on the machine
/// running the test: the virtual-desktop rect (otherwise read from SystemParameters, which would make
/// icons laid out past the real right edge look "stranded") and the collapse-record path (otherwise
/// the real %LOCALAPPDATA% file, which tests would both inherit and overwrite).
/// </summary>
public class FenceCollapseOrchestrationTests
{
    private const string BoxTitle = "文件夹";

    // Bounded off-screen parking pocket (mirrors FenceClusterBuilder.ParkSlot). Every parked icon
    // must stay inside it — the regression guard against 16-bit truncation.
    private const int ParkBase = -32000;
    private const int ParkSpan = 700;
    private const int ParkedThreshold = -10000;

    /// <summary>A virtual desktop big enough for every fixture below, so "off-screen" can only ever
    /// mean "parked" and never "the test machine has a small monitor".</summary>
    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    /// <param name="twinOf">When set, the LAST icon reuses this index's path. Two icons then share a
    /// <see cref="FenceOverlayController.StableKey"/>, reproducing the desktop collision that used to
    /// lose an icon's restore point.</param>
    private static (FenceOverlayController controller, FakeDesktopIconProvider provider, Dictionary<int, PointI> original)
        Build(int iconCount, int? twinOf = null)
    {
        var provider = new FakeDesktopIconProvider();
        var original = new Dictionary<int, PointI>();
        for (int i = 0; i < iconCount; i++)
        {
            var pos = new PointI(80 + i * 90, 80);
            // Distinct paths by default; the shared-path case is opt-in per test.
            var path = twinOf is { } t && i == iconCount - 1
                ? $@"C:\fake\icon{t}.lnk"
                : $@"C:\fake\icon{i}.lnk";
            provider.Icons.Add(new DesktopIcon(i, $"图标{i}", path, pos));
            provider.SetPosition(i, pos); // record into the fake's live-position store
            original[i] = pos;
        }

        var host = new NullOverlayHost();
        Func<DesktopIcon, string> resolver = _ => BoxTitle; // every icon belongs to the "文件夹" box

        // A throw-away collapse file per instance: no leftover records from another test or from a
        // real app session, and nothing written back into the user's own state.
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var controller = new FenceOverlayController(
            provider, host, resolver,
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"));
        return (controller, provider, original);
    }

    [Fact]
    public void Constructor_LeavesOnScreenIconsWhereTheyAre()
    {
        // Guards the rescue path: an icon that is plainly on the test desktop must not be "rescued"
        // into a cascade. (This is what silently corrupted the fixture before the screen rect was
        // injectable — icons past the real right edge were moved at construction time.)
        var (_, provider, original) = Build(iconCount: 40);

        AssertAllRestored(provider, original);
    }

    [Fact]
    public void Collapse_Expand_Collapse_Expand_RoundTripsEveryIcon()
    {
        var (controller, provider, original) = Build(iconCount: 20);

        // Precondition: nothing parked, box expanded.
        Assert.False(controller.IsCollapsed(BoxTitle));
        Assert.All(provider.GetIcons(), ic =>
        {
            Assert.True(ic.Position.X >= 0, $"{ic.Name} started off-screen (x={ic.Position.X})");
            Assert.True(ic.Position.Y >= 0, $"{ic.Name} started off-screen (y={ic.Position.Y})");
        });

        // ---- 1st collapse ----
        controller.ToggleFence(BoxTitle);
        Assert.True(controller.IsCollapsed(BoxTitle));
        AssertAllParked(provider, original);

        // ---- 1st expand ----
        controller.ToggleFence(BoxTitle);
        Assert.False(controller.IsCollapsed(BoxTitle));
        AssertAllRestored(provider, original);

        // ---- 2nd collapse — the exact maneuver that used to make the box vanish ----
        controller.ToggleFence(BoxTitle);
        Assert.True(controller.IsCollapsed(BoxTitle));
        AssertAllParked(provider, original);

        // ---- 2nd expand ----
        controller.ToggleFence(BoxTitle);
        Assert.False(controller.IsCollapsed(BoxTitle));
        AssertAllRestored(provider, original);
    }

    [Fact]
    public void MenuState_AnyCollapsed_AnyExpanded_TrackTheFenceSet()
    {
        // Drives the dynamic 全部展开 / 全部折叠 items in the header context menu: each global
        // action must only be offered while it would actually do something. This fixture has a
        // single box, so the two flags are strict opposites here.
        var (controller, _, _) = Build(iconCount: 20);

        // Fresh controller: nothing collapsed → only 全部折叠 would act, 全部展开 would be a no-op.
        Assert.False(controller.AnyCollapsed);
        Assert.True(controller.AnyExpanded);

        controller.ToggleFence(BoxTitle);
        Assert.True(controller.AnyCollapsed);
        Assert.False(controller.AnyExpanded); // the only box is now folded

        controller.ToggleFence(BoxTitle);
        Assert.False(controller.AnyCollapsed);
        Assert.True(controller.AnyExpanded);
    }

    [Fact]
    public void Collapse_ParksEveryIconInsideBoundedPocket_RegardlessOfCount()
    {
        // 40 icons: even with the 2-D wrapping grid, the pocket must stay bounded and never produce a
        // truncated/positive coordinate. Exercises the old int16 cliff well past i>=13.
        var (controller, provider, original) = Build(iconCount: 40);

        controller.ToggleFence(BoxTitle);
        Assert.True(controller.IsCollapsed(BoxTitle));
        AssertAllParked(provider, original);

        // Expanding must bring all 40 back exactly.
        controller.ToggleFence(BoxTitle);
        Assert.False(controller.IsCollapsed(BoxTitle));
        AssertAllRestored(provider, original);
    }

    [Fact]
    public void Collapse_Expand_RestoresEveryIcon_WhenTwoIconsShareAStableKey()
    {
        // Real-world collision: the user desktop and the public desktop can each hold a same-named
        // entry, and DesktopShellEnumerator maps a display name to ONE path — so two distinct icons
        // report the same Path and therefore the same StableKey. Two shortcuts pointing at the same
        // target collide identically.
        //
        // Restore points used to be keyed by StableKey, so the second icon overwrote the first's
        // entry: both icons were parked, only ONE was restored, and the other stayed invisible
        // off-screen for good. The collapse log counted dictionary entries rather than icons, so
        // "parked 30" still matched "restored 30" and hid the loss — the only visible trace was a
        // permanent "[refresh] dropped 1 off-screen icon(s)" in the diagnostics.
        var (controller, provider, original) = Build(iconCount: 20, twinOf: 7);

        controller.ToggleFence(BoxTitle);
        Assert.True(controller.IsCollapsed(BoxTitle));
        AssertAllParked(provider, original);

        controller.ToggleFence(BoxTitle);
        Assert.False(controller.IsCollapsed(BoxTitle));

        // Every icon — including both halves of the shared-path pair — must be back exactly.
        AssertAllRestored(provider, original);
    }

    private static void AssertAllParked(FakeDesktopIconProvider provider, Dictionary<int, PointI> original)
    {
        var icons = provider.GetIcons();
        Assert.Equal(original.Count, icons.Count);
        foreach (var ic in icons)
        {
            // Off-screen, but inside the bounded pocket — no truncation past short.MinValue (-32768),
            // and, critically, no bogus POSITIVE coordinate (the historical +31184 symptom).
            Assert.True(ic.Position.X < ParkedThreshold,
                $"{ic.Name} x={ic.Position.X} not parked far off-screen");
            Assert.True(ic.Position.Y < ParkedThreshold,
                $"{ic.Name} y={ic.Position.Y} not parked far off-screen");
            Assert.True(ic.Position.X >= short.MinValue,
                $"{ic.Name} x={ic.Position.X} underflowed signed 16-bit");
            Assert.True(ic.Position.Y >= short.MinValue,
                $"{ic.Name} y={ic.Position.Y} underflowed signed 16-bit");
            Assert.True(ic.Position.X >= ParkBase && ic.Position.X <= ParkBase + ParkSpan,
                $"{ic.Name} x={ic.Position.X} escaped the bounded pocket [{ParkBase}, {ParkBase + ParkSpan}]");
            Assert.True(ic.Position.Y >= ParkBase && ic.Position.Y <= ParkBase + ParkSpan,
                $"{ic.Name} y={ic.Position.Y} escaped the bounded pocket [{ParkBase}, {ParkBase + ParkSpan}]");
        }
    }

    private static void AssertAllRestored(FakeDesktopIconProvider provider, Dictionary<int, PointI> original)
    {
        var icons = provider.GetIcons();
        Assert.Equal(original.Count, icons.Count);
        foreach (var ic in icons)
        {
            Assert.True(original.TryGetValue(ic.Index, out var expected),
                $"{ic.Name} has no recorded original position");
            Assert.True(ic.Position.X == expected.X && ic.Position.Y == expected.Y,
                $"{ic.Name} (idx={ic.Index}) ended at ({ic.Position.X},{ic.Position.Y}) " +
                $"instead of ({expected.X},{expected.Y})\n{Describe(provider, original)}");
        }
    }

    private static string Describe(FakeDesktopIconProvider provider, Dictionary<int, PointI> original)
    {
        var rows = provider.GetIcons().OrderBy(ic => ic.Index).Select(ic =>
        {
            original.TryGetValue(ic.Index, out var e);
            var mark = ic.Position.X == e.X && ic.Position.Y == e.Y ? " " : "*";
            return $"{mark} {ic.Index,3} actual=({ic.Position.X,7},{ic.Position.Y,7}) expected=({e.X,7},{e.Y,7})";
        });
        return "idx | actual | expected  (* = mismatch)\n" + string.Join("\n", rows);
    }

    [Fact]
    public void Rescue_MustSkip_IconsParkedByACollapsedFence()
    {
        // Regression guard for the "折叠白折" symptom: a collapse parked 31 icons, then the refresh
        // self-heal IMMEDIATELY rescued all 31 back to the visible desktop — the fold did nothing.
        //
        // CollapseHide records restore points under IndexKey ("i:<index>"), but RescueStrandedIcons
        // used to test only StableKey ("path:<path>") when deciding whether an off-screen icon was
        // parked on purpose. The two key schemes never match, so every parked icon looked "orphaned"
        // and was brought back. An icon owned by a collapsed fence must never be rescued.
        var icon = new DesktopIcon(7, "图标7", @"C:\fake\icon7.lnk", new PointI(-31924, -31748));
        var intentional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FenceOverlayController.IndexKey(icon.Index),
        };
        Assert.True(FenceOverlayController.IsIntentionallyCollapsed(intentional, icon));
    }

    [Fact]
    public void Rescue_MustSkip_IconsRecordedByOlderBuilds_UnderStableKey()
    {
        // Records written before the IndexKey migration keep StableKey keys; rescuing those would
        // strand (well, un-hide) icons that were folded with an older build and are still collapsed.
        var icon = new DesktopIcon(3, "图标3", @"C:\fake\icon3.lnk", new PointI(-31700, -31600));
        var intentional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FenceOverlayController.StableKey(icon),
        };
        Assert.True(FenceOverlayController.IsIntentionallyCollapsed(intentional, icon));
    }

    [Fact]
    public void Rescue_MustBringBack_OffscreenIconsOwnedByNoCollapsedFence()
    {
        // The other half of the contract: an off-screen icon that matches NO collapse record is a
        // genuine orphan (parked by a crashed build whose record was lost) and must be rescued.
        var icon = new DesktopIcon(9, "图标9", @"C:\fake\icon9.lnk", new PointI(-31924, -31748));
        var intentional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FenceOverlayController.IndexKey(99),
        };
        Assert.False(FenceOverlayController.IsIntentionallyCollapsed(intentional, icon));
    }
}
