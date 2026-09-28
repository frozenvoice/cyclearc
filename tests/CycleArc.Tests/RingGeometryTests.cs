using CycleArc.Codex;

namespace CycleArc.Tests;

public class RingGeometryTests
{
    [Fact]
    public void Unavailable_NullPercent_ProducesNoVisibleArcAndNoFullCircle()
    {
        var arc = RingGeometry.ComputeUsedArc(null, 0, 0, 40);
        Assert.False(arc.Visible);
        Assert.False(arc.IsFullCircle);
    }

    [Fact]
    public void ZeroPercent_ProducesNoVisibleArc_ButIsNotTreatedAsUnavailable()
    {
        var zero = RingGeometry.ComputeUsedArc(0, 0, 0, 40);
        Assert.False(zero.Visible);
        Assert.False(zero.IsFullCircle);
    }

    [Fact]
    public void OnePercent_ProducesSmallVisibleArc()
    {
        var arc = RingGeometry.ComputeUsedArc(1, 0, 0, 40);
        Assert.True(arc.Visible);
        Assert.False(arc.IsFullCircle);
        Assert.False(arc.IsLargeArc);
        Assert.NotEqual(arc.Start, arc.End);
    }

    [Fact]
    public void FiftyPercent_ProducesHalfSweep_NotLargeArc()
    {
        var arc = RingGeometry.ComputeUsedArc(50, 0, 0, 40);
        Assert.True(arc.Visible);
        Assert.False(arc.IsLargeArc);
        // Half sweep starting at the top (12 o'clock) ends at the bottom (6 o'clock).
        Assert.InRange(arc.End.Y, 39.9, 40.1);
        Assert.InRange(arc.End.X, -0.1, 0.1);
    }

    [Fact]
    public void NinetyNinePercent_ProducesLargeArc_AndLeavesAVisibleRemainder()
    {
        var arc = RingGeometry.ComputeUsedArc(99, 0, 0, 40);
        Assert.True(arc.Visible);
        Assert.True(arc.IsLargeArc);
        Assert.False(arc.IsFullCircle);
        // The remaining 1% keeps the end point short of the start point.
        Assert.False(PointsClose(arc.Start, arc.End));
    }

    [Fact]
    public void HundredPercent_IsFullCircleSpecialCase_NotADegenerateArc()
    {
        var arc = RingGeometry.ComputeUsedArc(100, 0, 0, 40);
        Assert.True(arc.IsFullCircle);
        Assert.False(arc.Visible);
    }

    [Fact]
    public void AbovePlausibleRange_IsClampedToFullCircle()
    {
        var arc = RingGeometry.ComputeUsedArc(140, 0, 0, 40);
        Assert.True(arc.IsFullCircle);
    }

    [Fact]
    public void BelowZero_IsClampedToNoArc()
    {
        var arc = RingGeometry.ComputeUsedArc(-5, 0, 0, 40);
        Assert.False(arc.Visible);
        Assert.False(arc.IsFullCircle);
    }

    private static bool PointsClose(RingPoint a, RingPoint b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    [Fact]
    public void FillArcMatchesUsedArcAndRejectsNonFiniteShares()
    {
        foreach (var percent in new double?[] { null, 0, 1, 50, 99, 100, 140, -5 })
            Assert.Equal(RingGeometry.ComputeUsedArc(percent, 0, 0, 40), RingGeometry.ComputeFillArc(percent, 0, 0, 40));
        Assert.False(RingGeometry.ComputeFillArc(double.NaN, 0, 0, 40).Visible);
        Assert.False(RingGeometry.ComputeFillArc(double.PositiveInfinity, 0, 0, 40).IsFullCircle);
    }
}
