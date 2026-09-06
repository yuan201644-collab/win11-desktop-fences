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

/// <summary>
/// Regression tests for the 2026-09-06 SECOND drift incident: the one-shot lattice-phase
/// calibration ran during the boot auto-arrange, BEFORE Explorer's async correction landed, so it
/// locked onto OUR written positions' phase (42, 6) instead of Explorer's true phase (22, 2) — every
/// subsequent write was snapped to a grid half-a-cell away from Explorer's, and Explorer re-snapped
/// each write, drifting the whole group rigidly by a per-gesture random vector ≤ half a cell.
/// Fix: continuous phase self-correction. These tests pin <see cref="SysListView32Provider.ResolveLatticePhase"/>.
/// </summary>
public sealed class LatticePhaseTests
{
    private const int Cx = 76, Cy = 82;
    private const int TrueOx = 22, TrueOy = 2;    // Explorer's real lattice phase (from drag-diag.log)
    private const int WrongOx = 42, WrongOy = 6;  // the phase the one-shot calibration locked onto

    private static PointI On(int ox, int oy, int kx, int ky) => new(ox + kx * Cx, oy + ky * Cy);

    [Fact]
    public void ConfirmedSample_OverridesWrongPhase_Unconditionally()
    {
        // 10 icons still sitting where WE wrote them (wrong phase, uncorrected) — one Explorer
        // correction is authoritative and must win over any majority of stale positions.
        var candidates = Enumerable.Range(0, 10).Select(k => On(WrongOx, WrongOy, k, 0)).ToList();
        var confirmed = new List<PointI> { On(TrueOx, TrueOy, 3, 4) };

        var phase = SysListView32Provider.ResolveLatticePhase(confirmed, candidates, Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Equal((TrueOx, TrueOy), phase);
    }

    [Fact]
    public void ConfirmedSamples_ModeWins_SingleOutlierIgnored()
    {
        var confirmed = new List<PointI>
        {
            On(TrueOx, TrueOy, 0, 0), On(TrueOx, TrueOy, 1, 0), On(TrueOx, TrueOy, 2, 0),
            On(TrueOx, TrueOy, 0, 1), new(WrongOx, WrongOy), // one index-shift artifact
        };

        var phase = SysListView32Provider.ResolveLatticePhase(confirmed, new List<PointI>(), Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Equal((TrueOx, TrueOy), phase);
    }

    [Fact]
    public void UnknownPhase_TopVoteAdopted()
    {
        var candidates = new List<PointI>
        {
            On(TrueOx, TrueOy, 0, 0), On(TrueOx, TrueOy, 1, 0), On(TrueOx, TrueOy, 0, 1),
            On(WrongOx, WrongOy, 0, 0),
        };

        var phase = SysListView32Provider.ResolveLatticePhase(new List<PointI>(), candidates, Cx, Cy, 0, 0, known: false);

        Assert.Equal((TrueOx, TrueOy), phase);
    }

    [Fact]
    public void KnownPhase_CurrentLosesMajority_ConvergeEarly()
    {
        // Mixed desktop mid-correction: 5 icons on true phase, 4 not yet corrected on ours.
        // No confirmation this tick, but the true phase holds a strict majority — switching
        // early converges (remaining writes snap correctly); the hysteresis only blocks a
        // switch while the current phase still wins or ties.
        var candidates = new List<PointI>();
        candidates.AddRange(Enumerable.Range(0, 5).Select(k => On(TrueOx, TrueOy, k, 0)));
        candidates.AddRange(Enumerable.Range(0, 4).Select(k => On(WrongOx, WrongOy, k, 0)));

        var phase = SysListView32Provider.ResolveLatticePhase(new List<PointI>(), candidates, Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Equal((TrueOx, TrueOy), phase);
    }

    [Fact]
    public void KnownPhase_CurrentStillWins_KeepStable()
    {
        // Hysteresis: current phase still holds (or ties) the majority → no switch.
        var candidates = new List<PointI>();
        candidates.AddRange(Enumerable.Range(0, 4).Select(k => On(TrueOx, TrueOy, k, 0)));
        candidates.AddRange(Enumerable.Range(0, 5).Select(k => On(WrongOx, WrongOy, k, 0)));

        var phase = SysListView32Provider.ResolveLatticePhase(new List<PointI>(), candidates, Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Null(phase);
    }

    [Fact]
    public void KnownPhase_CurrentAbandoned_TopOtherWins()
    {
        // Icon pitch changed (DPI / icon size): nothing matches the cached phase anymore.
        var candidates = Enumerable.Range(0, 5).Select(k => On(TrueOx, TrueOy, k, 0)).ToList();

        var phase = SysListView32Provider.ResolveLatticePhase(new List<PointI>(), candidates, Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Equal((TrueOx, TrueOy), phase);
    }

    [Fact]
    public void KnownPhase_FewStrayVotes_DoNotSwitch()
    {
        // 2 stray votes below the ≥3 threshold must not move a healthy phase.
        var candidates = new List<PointI>
        {
            On(WrongOx, WrongOy, 0, 0), On(WrongOx, WrongOy, 1, 0), On(WrongOx, WrongOy, 2, 0),
            On(TrueOx, TrueOy, 0, 0), On(TrueOx, TrueOy, 1, 0),
        };

        var phase = SysListView32Provider.ResolveLatticePhase(new List<PointI>(), candidates, Cx, Cy, WrongOx, WrongOy, known: true);

        Assert.Null(phase);
    }

    [Fact]
    public void NoSamples_ReturnsNull()
    {
        Assert.Null(SysListView32Provider.ResolveLatticePhase(new List<PointI>(), new List<PointI>(), Cx, Cy, TrueOx, TrueOy, known: true));
        Assert.Null(SysListView32Provider.ResolveLatticePhase(new List<PointI>(), new List<PointI>(), Cx, Cy, 0, 0, known: false));
    }
}
