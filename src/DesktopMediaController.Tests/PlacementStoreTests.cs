using DesktopMediaController.Core;

namespace DesktopMediaController.Tests;

public sealed class PlacementStoreTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "dmc-tests", Guid.NewGuid().ToString("N"), "placement.json");

    [Fact]
    public void Load_MissingFile_ReturnsNullSoTheWidgetFallsBackToItsFirstRunSpot()
    {
        Assert.Null(PlacementStore.Load(TempFile()));
    }

    [Fact]
    public void Load_UnreadableFile_ReturnsNullInsteadOfThrowing()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(PlacementStore.Load(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThePlacement()
    {
        var path = TempFile();
        var saved = new WidgetPlacement(-1200, 340, 520, 220);

        PlacementStore.Save(path, saved);

        Assert.Equal(saved, PlacementStore.Load(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThePin()
    {
        var path = TempFile();
        var saved = new WidgetPlacement(40, 50, 440, 176, Pinned: true);

        PlacementStore.Save(path, saved);

        Assert.Equal(saved, PlacementStore.Load(path));
    }

    [Fact]
    public void Load_FileWrittenBeforeThePinExisted_IsNotPinned()
    {
        // The pin's whole point is that it is opt-in: a placement file from before the button — and
        // therefore every card upgraded in place — lands unpinned, under the user's software.
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "X": 100, "Y": 100, "WidthDip": 440, "HeightDip": 176 }""");

        var placement = PlacementStore.Load(path);

        Assert.NotNull(placement);
        Assert.False(placement!.Value.Pinned);
    }

    [Fact]
    public void Load_FileWrittenBeforeTheCardWasResizable_KeepsThePositionButTakesTheDefaultSize()
    {
        // Exactly what an older build left behind: a position and nothing else. The position is the
        // part the user actually chose, so it must survive; the size did not exist yet.
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "X": -1912, "Y": 12 }""");

        var placement = PlacementStore.Load(path);

        Assert.NotNull(placement);
        Assert.Equal(-1912, placement!.Value.X);
        Assert.Equal(12, placement.Value.Y);
        Assert.Equal(WidgetSize.Default, placement.Value.Size);
    }

    [Fact]
    public void Load_FileWithANonsenseSize_ClampsItIntoTheRenderableRange()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "X": 0, "Y": 0, "WidthDip": 40, "HeightDip": 9000 }""");

        var placement = PlacementStore.Load(path);

        Assert.NotNull(placement);
        Assert.Equal(WidgetSize.MinWidthDip, placement!.Value.WidthDip);
        Assert.Equal(WidgetSize.MaxHeightDip, placement.Value.HeightDip);
    }

    [Fact]
    public void Clamp_LeavesAPositionThatIsAlreadyFullyOnScreenAlone()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(new WidgetPosition(400, 500),
            PlacementStore.Clamp(new WidgetPosition(400, 500), screen, 320, 112));
    }

    [Fact]
    public void Clamp_PullsBackAPositionWhoseMonitorIsGone()
    {
        // Saved while a second monitor sat at x = 2560; that monitor is now unplugged, so the
        // widget would be invisible off-desktop if the raw value were used.
        var screen = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(new WidgetPosition(1600, 968),
            PlacementStore.Clamp(new WidgetPosition(4200, 1900), screen, 320, 112));
    }

    [Fact]
    public void Clamp_HonoursANegativeVirtualScreenOrigin()
    {
        // This machine's real layout: secondary 2560×1600 at x = -2560, primary 1920×1080 at (0,0).
        var virtualScreen = new ScreenRect(-2560, 0, 4480, 1600);

        // A position on the left-hand monitor stays there — the near edge is -2560, not 0.
        Assert.Equal(new WidgetPosition(-2000, 300),
            PlacementStore.Clamp(new WidgetPosition(-2000, 300), virtualScreen, 320, 112));

        // And a position past the far edge (virtual screen spans -2560..1920) is pulled back.
        Assert.Equal(new WidgetPosition(1600, 1488),
            PlacementStore.Clamp(new WidgetPosition(9999, 9999), virtualScreen, 320, 112));
    }

    [Fact]
    public void Clamp_KeepsTheTopLeftCornerVisibleWhenTheWindowIsBiggerThanTheScreen()
    {
        var tiny = new ScreenRect(0, 0, 200, 100);

        // maxX/maxY would go negative; the clamp must not push the widget further off-screen than
        // the screen origin itself.
        Assert.Equal(new WidgetPosition(0, 0),
            PlacementStore.Clamp(new WidgetPosition(-500, -500), tiny, 320, 112));
    }
}
