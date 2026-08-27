using DesktopOrganizer.Core.Layout;
using Xunit;

namespace DesktopOrganizer.Tests.Layout;

public class GridLayoutCalculatorTests
{
    private static RectI Fence() => new(100, 200, 400, 300);

    [Fact]
    public void ZeroCount_ReturnsEmpty()
        => Assert.Empty(GridLayoutCalculator.Compute(Fence(), 0, 3, 100, 100));

    [Fact]
    public void FirstIcon_AtFenceOriginPlusPadding()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 1, 3, 100, 100, 10, 20);
        Assert.Equal(new PointI(110, 220), Assert.Single(pts));
    }

    [Fact]
    public void WrapsAtColumnCount()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 4, 3, 100, 100);
        Assert.Equal(new PointI(100, 200), pts[0]);   // row 0 col 0
        Assert.Equal(new PointI(200, 200), pts[1]);   // row 0 col 1
        Assert.Equal(new PointI(300, 200), pts[2]);   // row 0 col 2
        Assert.Equal(new PointI(100, 300), pts[3]);   // row 1 col 0 (wrapped)
    }

    [Fact]
    public void ColumnsMinOne_WhenColumnsTooSmall()
    {
        var pts = GridLayoutCalculator.Compute(Fence(), 2, 0, 50, 50);
        Assert.Equal(new PointI(100, 200), pts[0]);
        Assert.Equal(new PointI(100, 250), pts[1]);   // single column, stacked
    }
}
