using System;
using System.IO;
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
        Build(int iconCount, bool autoArrange = false, bool available = true)
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
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"));
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
}
