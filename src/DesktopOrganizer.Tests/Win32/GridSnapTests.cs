using System;
using System.Linq;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;
using Xunit;

namespace DesktopOrganizer.Tests.Win32;

/// <summary>
/// Regression tests for the 2026-09-06 drag-drift incident: with Explorer's "align icons to grid"
/// ON, every raw (non-lattice) write was re-quantized by Explorer to the nearest cell, drifting the
/// whole group by up to half a cell. The fix snaps writes in the provider, so these tests pin the
/// rounding semantics of <see cref="SysListView32Provider.SnapToLattice"/>. Lattice numbers mirror
/// the real desktop measured during the incident: 76×82 pitch, origin (22, 2).
/// </summary>
public sealed class GridSnapTests
{
    private const int Cx = 76, Cy = 82, Ox = 22, Oy = 2;

    [Fact]
    public void AlreadyOnLattice_IsIdentity()
    {
        var on = new PointI(Ox + 3 * Cx, Oy + 5 * Cy);
        Assert.Equal(on, SysListView32Provider.SnapToLattice(on, Cx, Cy, Ox, Oy));
    }

    [Fact]
    public void HalfUpRounding_NearestCellWins()
    {
        // floor(v+0.5): 37/76 < 0.5 → stays; 39/76 > 0.5 → next cell; exactly 38/76 = 0.5 → UP.
        Assert.Equal(Ox, SysListView32Provider.SnapToLattice(new PointI(Ox + 37, Oy), Cx, Cy, Ox, Oy).X);
        Assert.Equal(Ox + Cx, SysListView32Provider.SnapToLattice(new PointI(Ox + 38, Oy), Cx, Cy, Ox, Oy).X);
        Assert.Equal(Ox + Cx, SysListView32Provider.SnapToLattice(new PointI(Ox + 39, Oy), Cx, Cy, Ox, Oy).X);
        Assert.Equal(Oy, SysListView32Provider.SnapToLattice(new PointI(Ox, Oy + 40), Cx, Cy, Ox, Oy).Y);
        Assert.Equal(Oy + Cy, SysListView32Provider.SnapToLattice(new PointI(Ox, Oy + 41), Cx, Cy, Ox, Oy).Y);
    }

    [Fact]
    public void IncidentCoordinates_SnapOntoTheObservedLattice()
    {
        // Real pairs from drag-diag.log: Explorer's observed got values must equal snap(want).
        Assert.Equal(new PointI(1466, 330),
            SysListView32Provider.SnapToLattice(new PointI(1482, 339), Cx, Cy, Ox, Oy));
        Assert.Equal(new PointI(934, 330),
            SysListView32Provider.SnapToLattice(new PointI(970, 289), Cx, Cy, Ox, Oy));
    }

    [Fact]
    public void UniformPhaseGroup_StaysRigid_NoSplit_NoCollisions()
    {
        // THE critical property (2026-09-06): a drag-restored group shares one phase f; snap must
        // shift every member by the SAME offset. Banker's rounding splits a group with f = 0.5
        // (even/odd k parity) — half-up must not, at ANY phase, including the exact midpoint.
        var starts = new[]
        {
            new PointI(120, 60), new PointI(120, 142), new PointI(120, 224),
            new PointI(196, 60), new PointI(196, 142),
        };
        foreach (var delta in new[] { new PointI(1460, 255), new PointI(67, 38), new PointI(-54, -24) })
        {
            var raw = starts.Select(s => new PointI(s.X + delta.X, s.Y + delta.Y)).ToList();
            var snapped = raw.Select(p => SysListView32Provider.SnapToLattice(p, Cx, Cy, Ox, Oy)).ToList();

            var offsetX = snapped[0].X - raw[0].X;
            var offsetY = snapped[0].Y - raw[0].Y;
            for (var i = 1; i < snapped.Count; i++)
            {
                Assert.Equal(offsetX, snapped[i].X - raw[i].X);
                Assert.Equal(offsetY, snapped[i].Y - raw[i].Y);
            }
            Assert.Equal(snapped.Count, snapped.Distinct().Count()); // no two icons share a cell
        }
    }

    [Fact]
    public void NegativeCoordinates_SnapCorrectly()
    {
        // Multi-monitor and fold-neighbour coords go negative; the math must not depend on C# %
        // sign semantics. Exact lattice points stay put; in-between points go to the nearest cell.
        var on = new PointI(Ox - Cx, Oy - Cy); // exact lattice cell below the origin
        Assert.Equal(on, SysListView32Provider.SnapToLattice(on, Cx, Cy, Ox, Oy));

        var between = new PointI(-30, -60);
        var snapped = SysListView32Provider.SnapToLattice(between, Cx, Cy, Ox, Oy);
        Assert.Equal(Ox - Cx, snapped.X); // -30: |−30−(−54)|=24 < |−30−22|=52
        Assert.Equal(Oy - Cy, snapped.Y); // -60: |−60−(−80)|=20 < |−60−2|=62
    }

    [Fact]
    public void UnusablePitch_IsIdentity()
    {
        var raw = new PointI(123, 456);
        Assert.Equal(raw, SysListView32Provider.SnapToLattice(raw, 0, 82, Ox, Oy));
        Assert.Equal(raw, SysListView32Provider.SnapToLattice(raw, 76, 0, Ox, Oy));
        Assert.Equal(raw, SysListView32Provider.SnapToLattice(raw, -76, 82, Ox, Oy));
    }
}
