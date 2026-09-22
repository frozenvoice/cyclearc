using CycleArc.Codex;

namespace CycleArc.Tests;

public class WindowEdgeSnapTests
{
    private static readonly ScreenRect Work = new(-1200, 40, 1920, 1040);

    [Fact]
    public void DetectsAllEdgesAndCornersAtInsetTargets()
    {
        var width = 320d;
        var height = 180d;
        var left = Work.X + WindowEdgeSnap.MarginDip;
        var right = Work.Right - WindowEdgeSnap.MarginDip - width;
        var top = Work.Y + WindowEdgeSnap.MarginDip;
        var bottom = Work.Bottom - WindowEdgeSnap.MarginDip - height;

        Assert.Equal(new WindowEdgeAnchors(HorizontalEdgeAnchor.Left, VerticalEdgeAnchor.Top),
            WindowEdgeSnap.Detect(left, top, width, height, Work));
        Assert.Equal(new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Top),
            WindowEdgeSnap.Detect(right, top, width, height, Work));
        Assert.Equal(new WindowEdgeAnchors(HorizontalEdgeAnchor.Left, VerticalEdgeAnchor.Bottom),
            WindowEdgeSnap.Detect(left, bottom, width, height, Work));
        Assert.Equal(new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom),
            WindowEdgeSnap.Detect(right, bottom, width, height, Work));
    }

    [Fact]
    public void ThresholdIsInclusiveAndOutsideThresholdIsFree()
    {
        var width = 320d;
        var height = 180d;
        var target = Work.X + WindowEdgeSnap.MarginDip;

        Assert.Equal(HorizontalEdgeAnchor.Left,
            WindowEdgeSnap.Detect(target + WindowEdgeSnap.ThresholdDip, 400, width, height, Work).Horizontal);
        Assert.Equal(HorizontalEdgeAnchor.None,
            WindowEdgeSnap.Detect(target + WindowEdgeSnap.ThresholdDip + 0.0001, 400, width, height, Work).Horizontal);
        Assert.Equal(VerticalEdgeAnchor.Top,
            WindowEdgeSnap.Detect(400, Work.Y + WindowEdgeSnap.MarginDip - WindowEdgeSnap.ThresholdDip, width, height, Work).Vertical);
        Assert.Equal(VerticalEdgeAnchor.None,
            WindowEdgeSnap.Detect(400, Work.Y + WindowEdgeSnap.MarginDip - WindowEdgeSnap.ThresholdDip - 0.0001, width, height, Work).Vertical);
    }

    [Fact]
    public void ChoosesClosestCandidateAndFavorsLeftAndTopOnTie()
    {
        var width = 320d;
        var height = 180d;
        var leftTarget = Work.X + WindowEdgeSnap.MarginDip;
        var rightTarget = Work.Right - WindowEdgeSnap.MarginDip - width;
        var topTarget = Work.Y + WindowEdgeSnap.MarginDip;
        var bottomTarget = Work.Bottom - WindowEdgeSnap.MarginDip - height;

        Assert.Equal(HorizontalEdgeAnchor.Right,
            WindowEdgeSnap.Detect(rightTarget - 1, 400, width, height, Work).Horizontal);
        Assert.Equal(VerticalEdgeAnchor.Bottom,
            WindowEdgeSnap.Detect(400, bottomTarget + 1, width, height, Work).Vertical);

        // The two candidate targets can both be inside the threshold only when the
        // window nearly fills the work area, leaving the two 8 DIP margins between
        // them. Use that boundary shape to exercise the deterministic tie rule.
        var tieWidth = Work.Width - (int)(WindowEdgeSnap.MarginDip * 2);
        var tieHeight = Work.Height - (int)(WindowEdgeSnap.MarginDip * 2);
        var tieLeftTarget = Work.X + WindowEdgeSnap.MarginDip;
        var tieRightTarget = Work.Right - WindowEdgeSnap.MarginDip - tieWidth;
        var tieTopTarget = Work.Y + WindowEdgeSnap.MarginDip;
        var tieBottomTarget = Work.Bottom - WindowEdgeSnap.MarginDip - tieHeight;
        var horizontalMidpoint = (tieLeftTarget + tieRightTarget) / 2;
        var verticalMidpoint = (tieTopTarget + tieBottomTarget) / 2;
        Assert.Equal(HorizontalEdgeAnchor.Left,
            WindowEdgeSnap.Detect(horizontalMidpoint, 400, tieWidth, height, Work).Horizontal);
        Assert.Equal(VerticalEdgeAnchor.Top,
            WindowEdgeSnap.Detect(400, verticalMidpoint, width, tieHeight, Work).Vertical);
    }

    [Fact]
    public void SupportsOneAxisAndNegativeOriginTaskbarWorkArea()
    {
        var width = 320d;
        var height = 180d;
        var result = WindowEdgeSnap.Detect(
            Work.X + WindowEdgeSnap.MarginDip,
            500,
            width,
            height,
            Work);

        Assert.Equal(HorizontalEdgeAnchor.Left, result.Horizontal);
        Assert.Equal(VerticalEdgeAnchor.None, result.Vertical);
        Assert.Equal((Work.X + WindowEdgeSnap.MarginDip, 500),
            WindowEdgeSnap.Place(700, 500, width, height, Work, result));
    }

    [Fact]
    public void FreePositionRemainsUnchangedWhenSizeChanges()
    {
        var anchors = WindowEdgeSnap.Detect(0, 400, 320, 180, Work);
        Assert.False(anchors.IsAttached);
        Assert.Equal((0d, 400d), WindowEdgeSnap.Place(0, 400, 520, 260, Work, anchors));
    }

    [Fact]
    public void PlaceMaintainsAttachedEdgesAcrossResize()
    {
        var anchors = new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom);
        var placed = WindowEdgeSnap.Place(300, 400, 520, 260, Work, anchors);

        Assert.Equal((Work.Right - WindowEdgeSnap.MarginDip - 520,
                Work.Bottom - WindowEdgeSnap.MarginDip - 260), placed);
    }

    [Fact]
    public void DisabledOrBypassedDetectionReleasesAnchors()
    {
        var left = Work.X + WindowEdgeSnap.MarginDip;
        var top = Work.Y + WindowEdgeSnap.MarginDip;
        Assert.False(WindowEdgeSnap.Detect(left, top, 320, 180, Work, enabled: false).IsAttached);
        Assert.False(WindowEdgeSnap.Detect(left, top, 320, 180, Work, bypass: true).IsAttached);
    }

    [Fact]
    public void NormalizeClearsInvalidEnumValues()
    {
        var anchors = new WindowEdgeAnchors((HorizontalEdgeAnchor)999, (VerticalEdgeAnchor)(-1));
        Assert.Equal(default, anchors.Normalize());
        Assert.False(anchors.Normalize().IsAttached);
    }

    [Fact]
    public void OversizedAxisCannotAttachAndPlacementRemainsFinite()
    {
        var width = Work.Width - (WindowEdgeSnap.MarginDip * 2) + 0.1;
        var result = WindowEdgeSnap.Detect(Work.X + WindowEdgeSnap.MarginDip, 400, width, 180, Work);
        Assert.Equal(HorizontalEdgeAnchor.None, result.Horizontal);

        var placed = WindowEdgeSnap.Place(double.NaN, double.PositiveInfinity, width, double.NaN, Work,
            new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom));
        Assert.True(double.IsFinite(placed.Left));
        Assert.True(double.IsFinite(placed.Top));
    }

    [Fact]
    public void AttachedAxisRecoveryPreservesAValidFreeAxis()
    {
        var work = new ScreenRect(0, 0, 800, 600);
        var placed = WindowEdgeSnap.Place(
            left: 3,
            top: -100,
            width: 200,
            height: 200,
            work,
            new WindowEdgeAnchors(HorizontalEdgeAnchor.None, VerticalEdgeAnchor.Bottom));

        Assert.Equal(3, placed.Left);
        Assert.Equal(work.Bottom - WindowEdgeSnap.MarginDip - 200, placed.Top);
    }

    [Fact]
    public void InvalidCoordinatesClampToSmallFixedWorkArea()
    {
        var work = new ScreenRect(100, 100, 20, 20);
        var placed = WindowEdgeSnap.Place(double.NaN, double.PositiveInfinity, 10, 10, work, default);

        Assert.Equal(work.X + WindowEdgeSnap.MarginDip, placed.Left);
        Assert.Equal(work.Y + WindowEdgeSnap.MarginDip, placed.Top);
        Assert.True(double.IsFinite(placed.Left));
        Assert.True(double.IsFinite(placed.Top));
    }
    [Fact]
    public void SafetyRecoveryUsesTheSelectedWorkArea()
    {
        var other = new ScreenRect(1920, 0, 1920, 1040);
        var placed = WindowEdgeSnap.Place(other.X + 100, other.Y + 100, 320, 180, Work,
            new WindowEdgeAnchors(HorizontalEdgeAnchor.None, VerticalEdgeAnchor.None));

        Assert.True(placed.Left >= Work.X + WindowEdgeSnap.MarginDip);
        Assert.True(placed.Top >= Work.Y + WindowEdgeSnap.MarginDip);
        Assert.True(placed.Left + 320 <= Work.Right - WindowEdgeSnap.MarginDip);
        Assert.True(placed.Top + 180 <= Work.Bottom - WindowEdgeSnap.MarginDip);
    }

    [Theory]
    [InlineData(-1080, 48, 1080, 1872)] // portrait, top taskbar
    [InlineData(-1032, 0, 1032, 1920)] // portrait, left taskbar
    [InlineData(0, 0, 1872, 1080)] // right taskbar
    [InlineData(0, 0, 1920, 1032)] // bottom taskbar
    public void UsesEveryInsetOfTheSuppliedWorkArea(int x, int y, int w, int h)
    {
        var work = new ScreenRect(x, y, w, h);
        foreach (var horizontal in new[] { HorizontalEdgeAnchor.Left, HorizontalEdgeAnchor.Right })
        foreach (var vertical in new[] { VerticalEdgeAnchor.Top, VerticalEdgeAnchor.Bottom })
        {
            var anchors = new WindowEdgeAnchors(horizontal, vertical);
            var placed = WindowEdgeSnap.Place(x + 200, y + 200, 320, 280, work, anchors);
            Assert.Equal(horizontal == HorizontalEdgeAnchor.Left ? x + 8 : work.Right - 8 - 320, placed.Left);
            Assert.Equal(vertical == VerticalEdgeAnchor.Top ? y + 8 : work.Bottom - 8 - 280, placed.Top);
            Assert.Equal(anchors, WindowEdgeSnap.Detect(placed.Left, placed.Top, 320, 280, work));
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PhysicalToDipConversionKeepsMarginIndependentOfAppZoom(double osScale)
    {
        var work = new ScreenRect(-1920, 40, 1920, 1040);
        foreach (var zoom in new[] { .8, 1, 1.5 })
        {
            var width = 320 * zoom;
            var height = 280 * zoom;
            var physicalLeft = (work.Right - 8 - width - 11.99) * osScale;
            var physicalTop = (work.Bottom - 8 - height) * osScale;
            var anchors = WindowEdgeSnap.Detect(physicalLeft / osScale, physicalTop / osScale,
                width, height, work);
            Assert.Equal(new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom), anchors);
            var placed = WindowEdgeSnap.Place(physicalLeft / osScale, physicalTop / osScale,
                width, height, work, anchors);
            Assert.Equal(8 * osScale, (work.Right - placed.Left - width) * osScale, 6);
            Assert.Equal(8 * osScale, (work.Bottom - placed.Top - height) * osScale, 6);
        }
    }
}
