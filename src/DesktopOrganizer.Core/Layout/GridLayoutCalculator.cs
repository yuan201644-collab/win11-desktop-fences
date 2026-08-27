using System.Collections.Generic;

namespace DesktopOrganizer.Core.Layout;

public sealed class GridLayoutCalculator
{
    public static IReadOnlyList<PointI> Compute(
        RectI fence, int count, int columns, int cellWidth, int cellHeight, int padX = 0, int padY = 0)
    {
        var result = new List<PointI>();
        if (count <= 0) return result;
        var cols = columns < 1 ? 1 : columns;
        var ox = fence.X + padX;
        var oy = fence.Y + padY;
        for (var i = 0; i < count; i++)
        {
            var row = i / cols;
            var col = i % cols;
            result.Add(new PointI(ox + col * cellWidth, oy + row * cellHeight));
        }
        return result;
    }
}
