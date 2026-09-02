using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using DesktopOrganizer.Tests.UI;
using DesktopOrganizer.Tests.Win32;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Services;

/// <summary>
/// Headless regression tests for the personalization layer: pinning a box to a rectangle
/// (resize drag / settings layout editor) and per-box color overrides (换色 menu / settings).
/// The controller runs against <see cref="NullOverlayHost"/> + <see cref="FakeDesktopIconProvider"/>
/// with all persistence files routed to a scratch directory, so nothing touches real user state and
/// nothing depends on the test machine's screen.
///
/// Box membership is driven by the REAL classification (folder icons → "文件夹", .txt files → "文件")
/// so the controller and <see cref="DesktopLayoutService"/> agree on who owns which box — an injected
/// title resolver would only affect the overlay layer and break that agreement.
/// </summary>
public class FencePersonalizationTests
{
    private const string BoxA = "文件夹";
    private const string BoxB = "文件";

    private static readonly RectI TestScreen = new(0, 0, 4000, 2000);

    private sealed record Fixture(
        FenceOverlayController Controller,
        FakeDesktopIconProvider Provider,
        NullOverlayHost Host,
        string Scratch,
        string RealDir);

    /// <summary>6 icons: 3 folder icons (each pointing at a REAL subdirectory, so they classify as
    /// 文件夹) + 3 .txt file icons (文件); 96px cells; all screens/paths injected.</summary>
    private static Fixture Build()
    {
        var provider = new FakeDesktopIconProvider();
        var realDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Personal", Guid.NewGuid().ToString("N"))).FullName;

        var icons = new List<DesktopIcon>();
        for (var i = 0; i < 3; i++)
        {
            var sub = Directory.CreateDirectory(Path.Combine(realDir, $"资料{i}")).FullName;
            icons.Add(new DesktopIcon(i, $"资料{i}", sub, new PointI(80 + i * 120, 80)));
        }
        for (var i = 0; i < 3; i++)
            icons.Add(new DesktopIcon(3 + i, $"文档{i}", $@"C:\fake\doc{i}.txt", new PointI(80 + (3 + i) * 120, 240)));
        foreach (var ic in icons)
        {
            provider.Icons.Add(ic);
            provider.SetPosition(ic.Index, ic.Position);
        }

        var host = new NullOverlayHost();
        var scratch = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests.Personal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        // No title resolver: controller + layout service share the real classification.
        var controller = new FenceOverlayController(
            provider, host,
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(scratch, "fence-collapse.json"),
            layoutFilePath: Path.Combine(scratch, "fence-layout.json"),
            colorFilePath: Path.Combine(scratch, "fence-colors.json"),
            boxInsetFilePath: Path.Combine(scratch, "fence-box-insets.json"),
            fenceInsetFilePath: Path.Combine(scratch, "fence-inset.json"),
            desktopLayoutFilePath: Path.Combine(scratch, "layout.json"));
        return new Fixture(controller, provider, host, scratch, realDir);
    }

    private static FenceOverlayController NewController(Fixture f)
        => new(
            f.Provider, new NullOverlayHost(),
            screenProvider: () => TestScreen,
            collapseFilePath: Path.Combine(f.Scratch, "fence-collapse.json"),
            layoutFilePath: Path.Combine(f.Scratch, "fence-layout.json"),
            colorFilePath: Path.Combine(f.Scratch, "fence-colors.json"),
            boxInsetFilePath: Path.Combine(f.Scratch, "fence-box-insets.json"),
            fenceInsetFilePath: Path.Combine(f.Scratch, "fence-inset.json"),
            desktopLayoutFilePath: Path.Combine(f.Scratch, "layout.json"));

    private static string BoxOf(DesktopIcon ic)
        => BoxGrouping.FromEntry(new SoftwareGroupingConfig(), ic.Name, ic.Path, null).Title;

    [Fact]
    public void SetFenceLayout_RelaysOutOnlyThePinnedBox()
    {
        var f = Build();
        // Fixture sanity: three icons really live in each box — otherwise the assertions below
        // would pass over an empty set (a false green).
        var boxAIcons = f.Provider.GetIcons().Where(ic => BoxOf(ic) == BoxA).ToList();
        var boxBIcons = f.Provider.GetIcons().Where(ic => BoxOf(ic) == BoxB).ToList();
        Assert.Equal(3, boxAIcons.Count);
        Assert.Equal(3, boxBIcons.Count);
        var beforeB = boxBIcons.ToDictionary(ic => ic.Index, ic => ic.Position);

        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));

        var icons = f.Provider.GetIcons();
        foreach (var ic in icons.Where(ic => BoxOf(ic) == BoxA))
        {
            Assert.True(ic.Position.X >= 500 && ic.Position.X < 500 + 420, $"{ic.Name} x={ic.Position.X} escaped box");
            Assert.True(ic.Position.Y >= 300 + FenceHeader.HeaderPx, $"{ic.Name} y={ic.Position.Y} overlapped title band");
        }
        // The other box is untouched.
        foreach (var (idx, pos) in beforeB) Assert.Equal(pos, f.Provider.GetPosition(idx));
    }

    [Fact]
    public void SetFenceLayout_PersistsAcrossControllerInstances()
    {
        var f = Build();
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));

        // A fresh controller reading the same scratch files must see the pin.
        var controller2 = NewController(f);

        var loaded = controller2.GetFenceLayout(BoxA);
        Assert.NotNull(loaded);
        Assert.Equal(500, loaded.X);
        Assert.Equal(300, loaded.Y);
        Assert.Equal(420, loaded.Width);
        Assert.Equal(300, loaded.Height);
        Assert.Null(controller2.GetFenceLayout(BoxB)); // untouched box stays unpinned
    }

    [Fact]
    public void ClearFenceLayout_UnpinsTheBox()
    {
        var f = Build();
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));
        Assert.NotNull(f.Controller.GetFenceLayout(BoxA));

        f.Controller.ClearFenceLayout(BoxA);
        Assert.Null(f.Controller.GetFenceLayout(BoxA));
    }

    [Fact]
    public void SetFenceLayout_OversizedRect_IsClampedToScreen()
    {
        var f = Build();
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(0, 0, 999_999, 999_999));

        var pinned = f.Controller.GetFenceLayout(BoxA)!;
        Assert.True(pinned.Width <= TestScreen.Width, $"width {pinned.Width} not clamped");
        Assert.True(pinned.Height <= TestScreen.Height, $"height {pinned.Height} not clamped");

        // Every icon must land inside the virtual desktop.
        foreach (var ic in f.Provider.GetIcons().Where(ic => BoxOf(ic) == BoxA))
        {
            Assert.InRange(ic.Position.X, TestScreen.Left, TestScreen.Right - 1);
            Assert.InRange(ic.Position.Y, TestScreen.Top, TestScreen.Bottom - 1);
        }
    }

    [Fact]
    public void SetFenceAppearance_OverridesHostAndPersists()
    {
        var f = Build();
        var overrideAppearance = FencePalette.FromPrimary(ArgbColor.FromArgb(0xFF, 0xB2, 0x6E, 0x2E));

        f.Controller.SetFenceAppearance(BoxA, overrideAppearance);

        Assert.Equal(overrideAppearance, f.Host.FenceColors[BoxA]);
        Assert.Equal(overrideAppearance, f.Controller.GetFenceAppearance(BoxA));
        Assert.Null(f.Controller.GetFenceAppearance(BoxB)); // other box unaffected
    }

    [Fact]
    public void ResetFenceAppearance_ClearsOverrideFromHostAndStore()
    {
        var f = Build();
        f.Controller.SetFenceAppearance(BoxA, OverlayAppearance.Default);
        Assert.NotNull(f.Controller.GetFenceAppearance(BoxA));

        f.Controller.ResetFenceAppearance(BoxA);

        Assert.Null(f.Controller.GetFenceAppearance(BoxA));
        Assert.False(f.Host.FenceColors.ContainsKey(BoxA));
    }

    // --- per-box edge padding (框边距): right-click 边距 menu, one box's override ---

    private static FenceCluster ClusterOf(NullOverlayHost host, string title)
        => Assert.Single(host.LastClusters, c => c.Title == title);

    private static int MinX(FakeDesktopIconProvider provider, string title)
        => provider.GetIcons().Where(ic => BoxOf(ic) == title).Min(ic => ic.Position.X);

    [Fact]
    public void SetFenceInsets_ReshapesOnlyThatBoxesCluster()
    {
        var f = Build();
        // A known global default makes the assertion exact; the scratch path keeps the user's real
        // fence-inset.json untouched.
        f.Controller.BoxInsets = new FenceInsets(Left: 18, Right: 8, Top: 4, Bottom: 8);
        f.Controller.ArrangeAndShow();
        var before = ClusterOf(f.Host, BoxB).Bounds; // untouched box's geometry, for later

        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8));

        var boxA = ClusterOf(f.Host, BoxA).Bounds;
        var boxB = ClusterOf(f.Host, BoxB).Bounds;
        // The override widened BoxA's left edge to minX-80 (global default would be minX-18)…
        Assert.Equal(MinX(f.Provider, BoxA) - 80, boxA.Left);
        // …and left the neighbor box exactly as it was.
        Assert.Equal(before, boxB);
    }

    [Fact]
    public void ResetFenceInsets_FallsBackToGlobalDefault()
    {
        var f = Build();
        f.Controller.BoxInsets = new FenceInsets(Left: 18, Right: 8, Top: 4, Bottom: 8);
        f.Controller.ArrangeAndShow();

        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8));
        Assert.Equal(MinX(f.Provider, BoxA) - 80, ClusterOf(f.Host, BoxA).Bounds.Left);

        f.Controller.ResetFenceInsets(BoxA);

        Assert.Null(f.Controller.GetFenceInsets(BoxA));
        Assert.Equal(MinX(f.Provider, BoxA) - 18, ClusterOf(f.Host, BoxA).Bounds.Left);
    }

    [Fact]
    public void SetFenceInsets_PersistsAcrossControllerInstances()
    {
        var f = Build();
        var wide = new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8);
        f.Controller.SetFenceInsets(BoxA, wide);

        var controller2 = NewController(f);

        Assert.Equal(wide, controller2.GetFenceInsets(BoxA));
        Assert.Null(controller2.GetFenceInsets(BoxB)); // untouched box has no override
    }

    [Fact]
    public void BoxInsetsFor_ReturnsOverrideElseGlobalDefault()
    {
        var f = Build();
        f.Controller.BoxInsets = new FenceInsets(Left: 18, Right: 8, Top: 4, Bottom: 8);
        Assert.Equal(new FenceInsets(18, 8, 4, 8), f.Controller.BoxInsetsFor(BoxA));

        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8));

        Assert.Equal(80, f.Controller.BoxInsetsFor(BoxA).Left);
        Assert.Equal(18, f.Controller.BoxInsetsFor(BoxB).Left); // neighbor unaffected
    }

    // --- pinned box + 边距: the pinned rect is rendered verbatim, so the slider must reshape IT ---

    [Fact]
    public void SetFenceInsets_OnPinnedBox_ReshapesPinnedRectByDelta()
    {
        var f = Build();
        f.Controller.BoxInsets = new FenceInsets(Left: 18, Right: 8, Top: 4, Bottom: 8);
        f.Controller.ArrangeAndShow();
        // Pin BoxA: RefreshOverlay renders this stored rectangle verbatim, ignoring the
        // auto-derived (inset-shaped) bounds — previously that made the slider a no-op here.
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));
        Assert.Equal(new RectI(500, 300, 420, 300), ClusterOf(f.Host, BoxA).Bounds);

        // Left inset 18 → 80 (delta +62), other sides unchanged.
        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8));

        var b = ClusterOf(f.Host, BoxA).Bounds;
        Assert.Equal(500 - 62, b.Left);    // left edge slides out by the delta…
        Assert.Equal(420 + 62, b.Width);   // …and the rectangle widens by it
        Assert.Equal(300, b.Top);          // unchanged sides stay put
        Assert.Equal(300, b.Height);
        // The reshape is persisted into the pinned-layout store, not just rendered once.
        Assert.Equal(new FenceLayout(438, 300, 482, 300), f.Controller.GetFenceLayout(BoxA));
    }

    [Fact]
    public void ResetFenceInsets_OnPinnedBox_UnappliesDeltaFromPinnedRect()
    {
        var f = Build();
        f.Controller.BoxInsets = new FenceInsets(Left: 18, Right: 8, Top: 4, Bottom: 8);
        f.Controller.ArrangeAndShow();
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));
        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 30, Top: 4, Bottom: 8));

        f.Controller.ResetFenceInsets(BoxA);

        // Falling back to the global default un-applies the whole override delta from the rect.
        Assert.Equal(new RectI(500, 300, 420, 300), ClusterOf(f.Host, BoxA).Bounds);
    }

    // --- one-shot "clear all personalization" (设置页 清除所有个性化设置 按钮) ---

    [Fact]
    public void ResetAllPersonalization_ClearsAllThreeKindsAndHostAndStores()
    {
        var f = Build();
        // Seed one of each kind of per-box personalization on BoxA.
        f.Controller.SetFenceAppearance(BoxA, OverlayAppearance.Default);
        f.Controller.SetFenceInsets(BoxA, new FenceInsets(Left: 80, Right: 8, Top: 4, Bottom: 8));
        f.Controller.SetFenceLayout(BoxA, new FenceLayout(500, 300, 420, 300));
        Assert.True(f.Controller.HasPersonalization);

        f.Controller.ResetAllPersonalization();

        // All three kinds are gone (and HasPersonalization flips).
        Assert.False(f.Controller.HasPersonalization);
        Assert.Null(f.Controller.GetFenceAppearance(BoxA));
        Assert.Null(f.Controller.GetFenceInsets(BoxA));
        Assert.Null(f.Controller.GetFenceLayout(BoxA));
        // The host's cached color for that box is cleared too — RefreshOverlay only re-applies bounds,
        // so a stale color would otherwise survive the refresh.
        Assert.False(f.Host.FenceColors.ContainsKey(BoxA));
        // Each store file is flushed as an empty map (so a later launch doesn't reload the overrides).
        Assert.Equal("{}", File.ReadAllText(Path.Combine(f.Scratch, "fence-colors.json")).Trim());
        Assert.Equal("{}", File.ReadAllText(Path.Combine(f.Scratch, "fence-box-insets.json")).Trim());
        Assert.Equal("{}", File.ReadAllText(Path.Combine(f.Scratch, "fence-layout.json")).Trim());
    }

    [Fact]
    public void ResetAllPersonalization_IsNoOpWhenNothingSet()
    {
        var f = Build();
        Assert.False(f.Controller.HasPersonalization);

        f.Controller.ResetAllPersonalization(); // must not throw

        Assert.False(f.Controller.HasPersonalization);
    }
}
