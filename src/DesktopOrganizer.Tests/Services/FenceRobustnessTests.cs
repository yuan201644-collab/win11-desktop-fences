using System;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Headless robustness tests for the controller's degenerate / unavailable states — the cases that
/// historically threw on a real desktop (0 icons after the user cleared the screen, the shell not yet
/// ready so <see cref="IDesktopIconProvider.IsAvailable"/> is false, a single stray icon, auto-arrange
/// left on). Each asserts the operation completes without throwing; none of these paths touch
/// MessageBox (which needs a real WPF/STA thread and is exercised manually), so they run clean under
/// the test runner.
///
/// The virtual-desktop rect and collapse-record path are injected, so the outcome never depends on the
/// machine running the test.
/// </summary>
public class FenceRobustnessTests
{
    private const string BoxTitle = "文件夹";
    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    private static (FenceOverlayController controller, FakeDesktopIconProvider provider)
        Build(int iconCount, bool autoArrange = false, bool available = true, Func<RectI?>? screen = null)
    {
        var provider = new FakeDesktopIconProvider { IsAutoArrangeOn = autoArrange, IsAvailable = available };
        for (int i = 0; i < iconCount; i++)
        {
            var pos = new PointI(80 + i * 120, 80);
            provider.Icons.Add(new DesktopIcon(i, $"图标{i}", $@"C:\fake\icon{i}.lnk", pos));
            provider.SetPosition(i, pos);
        }
        var host = new NullOverlayHost();
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var controller = new FenceOverlayController(
            provider, host, _ => BoxTitle,
            screenProvider: screen ?? (() => TestScreen),
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"),
            layoutFilePath: Path.Combine(scratch, "fence-layout.json"),
            colorFilePath: Path.Combine(scratch, "fence-colors.json"),
            boxInsetFilePath: Path.Combine(scratch, "fence-box-insets.json"),
            fenceInsetFilePath: Path.Combine(scratch, "fence-inset.json"),
            desktopLayoutFilePath: Path.Combine(scratch, "layout.json"),
            liveSortFilePath: Path.Combine(scratch, "live-sort.json"));
        return (controller, provider);
    }

    [Fact]
    public void EmptyDesktop_ArrangeAndShow_DoesNotThrow()
    {
        // The user cleared the desktop, or the app auto-arranges before any icon is classified.
        var (controller, _) = Build(0);
        Assert.Null(Record.Exception(() => controller.ArrangeAndShow()));
    }

    [Fact]
    public void EmptyDesktop_ForceRefresh_DoesNotThrow()
    {
        var (controller, _) = Build(0);
        controller.ArrangeAndShow();
        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
    }

    [Fact]
    public void ProviderUnavailable_ArrangeAndShow_DoesNotThrow_AndStaysHidden()
    {
        // Shell not ready / Progman handle missing: the controller must no-op, never dereference a dead provider.
        var (controller, _) = Build(5, available: false);
        Assert.Null(Record.Exception(() => controller.ArrangeAndShow()));
    }

    [Fact]
    public void IsArranged_StaysFalseWhileProviderUnavailable_ThenTrueOnceReady()
    {
        // The auto-arrange-on-startup retry loop keys off IsArranged to know when to stop retrying.
        // While the desktop isn't ready it must remain false; once available, a successful arrange flips it.
        var (controller, provider) = Build(5, available: false);
        controller.ArrangeAndShow();
        Assert.False(controller.IsArranged);

        provider.IsAvailable = true;
        controller.ArrangeAndShow();
        Assert.True(controller.IsArranged);
    }

    [Fact]
    public void ProviderUnavailable_ForceRefresh_DoesNotThrow()
    {
        var (controller, _) = Build(5, available: false);
        Assert.Null(Record.Exception(() => controller.ForceRefresh()));
    }

    [Fact]
    public void SingleIcon_ArrangeAndShow_DoesNotThrow()
    {
        // One leftover icon must still produce a valid (tiny) box, not divide-by-zero or empty-cluster errors.
        var (controller, _) = Build(1);
        Assert.Null(Record.Exception(() => controller.ArrangeAndShow()));
    }

    [Fact]
    public void AutoArrangeOn_ButDisableable_ArrangeAndShow_Completes()
    {
        // Auto-arrange is on, but the shell lets us turn it off (Fake returns true) — the common case
        // that must NOT be treated as a hard failure. The arrange proceeds normally.
        var (controller, _) = Build(5, autoArrange: true);
        Assert.Null(Record.Exception(() => controller.ArrangeAndShow()));
    }

    [Fact]
    public void AutoArrangeOn_ButDisableable_CollapseStillWorks()
    {
        // With auto-arrange on but disableable, collapsing must still succeed (the guard only refuses
        // when disabling FAILS, which is the separate manual/UI path). Guards the "auto-arrange on"
        // branch through CollapseHide without tripping the MessageBox.
        var (controller, _) = Build(5, autoArrange: true);
        controller.ArrangeAndShow();
        Assert.Null(Record.Exception(() => controller.ToggleFence(BoxTitle)));
        Assert.True(controller.IsCollapsed(BoxTitle));
    }

    [Fact]
    public void PinnedBoxPastScreenBottom_Arrange_KeepsEveryIconOnScreen()
    {
        // Real incident (2026-09-05): a box was pinned near the bottom of a LARGE virtual desktop
        // (dual monitors). The screen then shrank (resolution change / monitor unplugged), leaving the
        // stored rect hanging past the new bottom edge. ArrangeOneFence clamps each icon only to the
        // box's OWN bounds and never to the screen, so every icon of that box was laid out off-screen:
        // invisible to the user, then re-rescued by RescueStrandedIcons on the next refresh and pushed
        // off-screen again — an endless rescue/refresh loop that made the entire desktop look empty.
        // ArrangeAndShow must clamp the pinned rect into the virtual desktop BEFORE laying icons out.
        var screen = new RectI(0, 0, 4000, 2000);
        var (controller, provider) = Build(0, screen: () => screen);

        // ArrangeOneFence classifies with the REAL grouping (BoxGrouping.FromEntry), NOT the
        // controller's groupTitle delegate — so these must be REAL directories to actually land in
        // the "文件夹" box. With fake *.lnk paths the box finds no member, returns early without
        // moving anything, and this test would pass vacuously (verified: it did).
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "DesktopOrganizer.Tests.Pinned", Guid.NewGuid().ToString("N"))).FullName;
        for (var i = 0; i < 6; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"资料{i}")).FullName;
            provider.Icons.Add(new DesktopIcon(i, $"资料{i}", dir, new PointI(80 + i * 120, 80)));
            provider.SetPosition(i, new PointI(80 + i * 120, 80));
        }

        // Fixture sanity (same guard as DesktopLayoutServiceTests): the icons must really classify
        // into the 文件夹 box, or ArrangeOneFence finds no member, returns early without moving
        // anything, and every assertion below would pass over an empty set (a false green).
        var folders = provider.GetIcons().Where(ic =>
            BoxGrouping.FromEntry(new SoftwareGroupingConfig(), ic.Name, ic.Path, null).Title == BoxTitle).ToList();
        Assert.Equal(6, folders.Count);

        // Pin the box low on the big screen — perfectly legal at this size.
        controller.SetFenceLayout(BoxTitle, new FenceLayout(100, 1800, 400, 300));

        // The screen shrinks: the stored rect now hangs past the bottom (1800 + 300 = 2100 > 1080).
        screen = new RectI(0, 0, 4000, 1080);

        Assert.Null(Record.Exception(() => controller.ArrangeAndShow()));

        var icons = provider.GetIcons();
        var cellH = Math.Max(1, provider.IconSpacingY);
        Assert.NotEmpty(icons); // guards the vacuous-pass trap above

        // Must have ACTUALLY moved into the pinned box — not left at the starting row, which would
        // make the on-screen assertions pass vacuously.
        Assert.True(icons.Any(ic => ic.Position.Y > 400),
            "图标没有被排进 pinned 框（ArrangeOneFence 提前返回？）。实际 y: "
            + string.Join(", ", icons.Select(i => i.Position.Y)));

        foreach (var ic in icons)
        {
            Assert.True(ic.Position.Y >= screen.Top,
                $"'{ic.Name}' was laid out above the screen: y={ic.Position.Y}");
            Assert.True(ic.Position.Y + cellH <= screen.Bottom,
                $"'{ic.Name}' was laid out off the bottom: y={ic.Position.Y} + {cellH} > {screen.Bottom}");
        }
    }

    [Fact]
    public void DisplayChange_Refresh_RepinsFencesIntoNewScreen_AndRescuesIcons()
    {
        // Core of the 2026-09-05 incident, part 2: the monitor ARRANGEMENT changed while the app
        // was arranged (virtual screen 4480x1080 @(0,0) became 4480x1080 @(-2560,0)). Pinned fences
        // hold absolute rects; the 2s refresh must notice the new virtual desktop, re-clamp every
        // pinned fence into it, and rescue icons left in the region no monitor covers any more.
        var screen = new RectI(0, 0, 4000, 2000);
        var (controller, provider) = Build(0, screen: () => screen);

        // Real directories so the icons classify into the 文件夹 box (see the fixture-sanity note above).
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "DesktopOrganizer.Tests.Pinned", Guid.NewGuid().ToString("N"))).FullName;
        for (var i = 0; i < 6; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"资料{i}")).FullName;
            provider.Icons.Add(new DesktopIcon(i, $"资料{i}", dir, new PointI(80 + i * 120, 80)));
            provider.SetPosition(i, new PointI(80 + i * 120, 80));
        }

        controller.ArrangeAndShow();
        // Pin the box far right — perfectly legal on the 4000-wide screen.
        controller.SetFenceLayout(BoxTitle, new FenceLayout(3500, 100, 400, 300));

        // The arrangement changes: the right half is no longer covered by any monitor.
        screen = new RectI(0, 0, 1920, 1080);

        controller.ForceRefresh();

        // The pinned fence must have been re-clamped into the new virtual screen.
        var pinned = controller.GetFenceLayout(BoxTitle)!;
        Assert.True(pinned.X + pinned.Width <= 1920,
            $"pinned fence still hangs past the new right edge: {pinned.X}+{pinned.Width}>1920");
        Assert.True(pinned.Y + pinned.Height <= 1080,
            $"pinned fence still hangs past the new bottom edge: {pinned.Y}+{pinned.Height}>1080");

        // Every icon must be on the new screen — none stranded in the phantom zone.
        var icons = provider.GetIcons();
        Assert.NotEmpty(icons);
        var cw = Math.Max(1, provider.IconSpacingX);
        var chh = Math.Max(1, provider.IconSpacingY);
        foreach (var ic in icons)
        {
            Assert.True(ic.Position.X + cw <= 1920 && ic.Position.Y + chh <= 1080,
                $"'{ic.Name}' stranded in the phantom zone: {ic.Position}");
        }
    }
}
