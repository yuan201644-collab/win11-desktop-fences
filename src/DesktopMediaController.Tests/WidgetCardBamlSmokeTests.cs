using System.Threading;
using System.Windows.Media;
using DesktopMediaController.Core;
using DesktopMediaController.Widget;

namespace DesktopMediaController.Tests;

/// <summary>
/// Instantiates the app's WPF controls for real, on an STA thread. This is the only layer of the
/// suite that executes BAML: XAML attribute values are strings at compile time and converted at
/// parse time at runtime, so an invalid enum value (the <c>VerticalAlignment="Baseline"</c> that
/// killed the exe at startup on 2026-09-14) compiles clean and passes every pure-logic test.
/// </summary>
public class WidgetCardBamlSmokeTests
{
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        Assert.False(thread.IsAlive, "the STA work did not finish within 30 s");
        if (failure is not null) throw failure;
    }

    [Fact]
    public void Constructor_EveryScalePreset_ParsesAndBuilds()
    {
        RunOnStaThread(() =>
        {
            foreach (var scale in WidgetSize.Scales)
            {
                var card = new WidgetCard(WidgetSize.ForScale(scale));

                // The card states the SCALED size — an HwndSource's SizeToContent does not honor a
                // LayoutTransform on its root visual, so the explicit size is what keeps the window
                // from clipping the scaled content (the 2026-09-14 startup complaint).
                Assert.Equal(WidgetSize.BaseWidthDip * scale, card.Width, 1e-6);
                Assert.Equal(WidgetSize.BaseHeightDip * scale, card.Height, 1e-6);

                var transform = Assert.IsAssignableFrom<Transform>(card.ScaleHost.LayoutTransform);
                Assert.Equal(scale, ((ScaleTransform)transform).ScaleX, 1e-6);
                Assert.Equal(scale, ((ScaleTransform)transform).ScaleY, 1e-6);
            }
        });
    }

    [Fact]
    public void Constructor_AppliesTheDefaultThemeWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            var card = new WidgetCard(WidgetSize.ForScale(1.0));
            card.ApplyTheme(CardTheme.Default);
        });
    }
}
