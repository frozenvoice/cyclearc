using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class WidgetGridLayoutTests
{
    private const double Header = 28;
    private const double Module = 118;
    private static readonly ScreenRect Wide = new(0, 0, 1920, 1040);
    private static readonly ScreenRect Narrow = new(0, 0, 760, 1040);
    private static readonly ScreenRect Short = new(0, 0, 1920, 300);

    private static WidgetGridLayout For(int accounts, ScreenRect area) =>
        WidgetGridLayout.For(accounts, Header, Module, area);

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(5, 5, 1)]
    public void AccountsShareOneRowWhenTheWorkAreaIsWideEnough(int accounts, int columns, int rows)
    {
        var layout = For(accounts, Wide);
        Assert.Equal(columns, layout.Columns);
        Assert.Equal(rows, layout.Rows);
        Assert.False(layout.Scrolls);
    }

    [Fact]
    public void OneAccountTakesOnlyOneModuleOfWidth()
    {
        var layout = For(1, Wide);
        Assert.Equal(WidgetGridLayout.ChromeWidth + WidgetGridLayout.ModuleWidth, layout.Width);
    }

    [Fact]
    public void FiveAccountsInOneRowStayInsideTheWorkArea()
    {
        var layout = For(5, Wide);
        Assert.Equal(WidgetGridLayout.ChromeWidth + (5 * WidgetGridLayout.ModuleWidth)
            + (4 * WidgetGridLayout.SeparatorThickness), layout.Width);
        Assert.True(layout.Width <= Wide.Width - (2 * WidgetGridLayout.EdgeMargin));
    }

    [Fact]
    public void ANarrowWorkAreaWrapsByModuleInsteadOfShrinkingTheRow()
    {
        var layout = For(5, Narrow);
        Assert.Equal(3, layout.Columns); // 3 + 2
        Assert.Equal(2, layout.Rows);
        Assert.True(layout.Width <= Narrow.Width - (2 * WidgetGridLayout.EdgeMargin));
    }

    [Fact]
    public void AVeryNarrowWorkAreaWrapsToTwoAndKeepsTheFullModuleWidth()
    {
        var layout = For(5, new ScreenRect(0, 0, 520, 1040));
        Assert.Equal(2, layout.Columns); // 2 + 2 + 1
        Assert.Equal(3, layout.Rows);
        Assert.Equal(WidgetGridLayout.ChromeWidth + (2 * WidgetGridLayout.ModuleWidth)
            + WidgetGridLayout.SeparatorThickness, layout.Width);
    }

    [Fact]
    public void AWorkAreaNarrowerThanOneModuleStillShowsAWholeModule()
    {
        var layout = For(3, new ScreenRect(0, 0, 200, 1040));
        Assert.Equal(1, layout.Columns);
        Assert.Equal(3, layout.Rows);
        Assert.Equal(WidgetGridLayout.ChromeWidth + WidgetGridLayout.ModuleWidth, layout.Width);
    }

    [Fact]
    public void ColumnsNeverExceedTheAccountCount()
    {
        Assert.Equal(2, For(2, Wide).Columns);
        Assert.Equal(1, For(1, new ScreenRect(0, 0, 3840, 1040)).Columns);
    }

    [Fact]
    public void MoreAccountsThanFitVerticallyScrollInsideTheWorkArea()
    {
        var layout = WidgetGridLayout.For(24, Header, Module, Short);
        Assert.True(layout.Scrolls);
        Assert.True(layout.Height <= Short.Height - (2 * WidgetGridLayout.EdgeMargin));
        Assert.True(layout.ModuleViewportHeight > 0);
        Assert.True(layout.ModuleViewportHeight <= layout.Height);
    }

    [Fact]
    public void AGridThatFitsDoesNotScroll()
    {
        var layout = For(5, Wide);
        Assert.False(layout.Scrolls);
        Assert.Equal(WidgetGridLayout.ChromeHeight + Header + Module, layout.Height);
    }

    [Fact]
    public void AShortFirstAccountDoesNotHideLaterTallerRowsFromScrolling()
    {
        double[] heights = [80, 150, 150, 150, 150];
        var area = new ScreenRect(0, 0, 760, 300);
        var byFirst = WidgetGridLayout.For(5, Header, heights[0], area);
        var byAll = WidgetGridLayout.For(5, Header, heights, area);
        Assert.False(byFirst.Scrolls);
        Assert.True(byAll.Scrolls);
        Assert.True(byAll.Height <= area.Height - (2 * WidgetGridLayout.EdgeMargin));
        Assert.True(byAll.ModuleViewportHeight > 0);
    }

    [Fact]
    public void RowHeightFollowsTheTallestModuleOnThatRowAfterReorder()
    {
        var twoColumns = new ScreenRect(0, 0, 520, 1040);
        var shortFirst = WidgetGridLayout.For(5, Header, [80, 150, 150, 150, 150], twoColumns);
        var tallLast = WidgetGridLayout.For(5, Header, [150, 150, 150, 150, 80], twoColumns);
        Assert.Equal(2, shortFirst.Columns);
        Assert.Equal(3, shortFirst.Rows);
        Assert.True(shortFirst.Height > tallLast.Height);
        Assert.False(shortFirst.Scrolls);
        Assert.False(tallLast.Scrolls);
    }

    [Fact]
    public void AScrollingGridReservesScrollbarWidthSoTheLastColumnIsNotClipped()
    {
        var heights = Enumerable.Repeat(150d, 5).ToArray();
        var fitsThree = new ScreenRect(0, 0, 760, 280);
        var without = WidgetGridLayout.For(5, Header, heights, fitsThree, scrollbarWidth: 0);
        var withBar = WidgetGridLayout.For(5, Header, heights, fitsThree, scrollbarWidth: 17);
        Assert.True(without.Scrolls);
        Assert.Equal(3, without.Columns);
        Assert.Equal(without.Width + 17, withBar.Width);
        Assert.Equal(3, withBar.Columns);
        Assert.True(withBar.Width <= fitsThree.Width - (2 * WidgetGridLayout.EdgeMargin));
    }

    [Fact]
    public void ScrollbarWidthCanWrapOneFewerColumnWhenTheWorkAreaIsTight()
    {
        var heights = Enumerable.Repeat(150d, 5).ToArray();
        var tight = new ScreenRect(0, 0, 740, 280);
        var without = WidgetGridLayout.For(5, Header, heights, tight, scrollbarWidth: 0);
        var withBar = WidgetGridLayout.For(5, Header, heights, tight, scrollbarWidth: 17);
        Assert.Equal(3, without.Columns);
        Assert.True(withBar.Scrolls);
        Assert.Equal(2, withBar.Columns);
        Assert.True(withBar.Width <= tight.Width - (2 * WidgetGridLayout.EdgeMargin));
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(1.25, 0)]
    [InlineData(2.0, 0)]
    public void HairlinesThatLandOnWholeDevicePixelsNeedNoWidthSlack(double scale, double expected)
    {
        Assert.Equal(expected, For(3, Wide).HairlineRoundingSlack(scale));
    }

    [Fact]
    public void HairlinesAt150PercentAddTwoDevicePixelsOnThreeColumns()
    {
        // Four 1 DIP hairlines (two borders + two separators) round 1.5 px up to 2 px.
        Assert.Equal(4.0 / 3.0, For(3, Wide).HairlineRoundingSlack(1.5), 6);
        Assert.Equal((2 - 1.75) / 1.75 * 4, For(3, Wide).HairlineRoundingSlack(1.75), 6);
        Assert.Equal(2.0, For(5, Wide).HairlineRoundingSlack(1.5), 6);
    }

    [Fact]
    public void AccountCountIsNotCappedAtFive()
    {
        var layout = For(9, Narrow);
        Assert.Equal(3, layout.Columns);
        Assert.Equal(3, layout.Rows);
        Assert.True(layout.Columns * layout.Rows >= 9);
    }

    [Fact]
    public void TheMonitorTheWidgetSitsOnDecidesWrapping()
    {
        IReadOnlyList<ScreenRect> areas = [Wide, new ScreenRect(-760, 0, 760, 1040)];
        // A widget parked on the narrow secondary monitor must not be measured against the desktop.
        var secondary = WidgetPlacement.AreaFor(-700, 40, 240, 120, areas);
        Assert.Equal(new ScreenRect(-760, 0, 760, 1040), secondary);
        Assert.Equal(3, WidgetGridLayout.For(5, Header, Module, secondary).Columns);
        Assert.Equal(5, WidgetGridLayout.For(5, Header, Module,
            WidgetPlacement.AreaFor(40, 40, 240, 120, areas)).Columns);
    }

    [Fact]
    public void AreaContainingUsesTheOriginMonitorNotThePreviousWidgetRectangle()
    {
        IReadOnlyList<ScreenRect> areas = [Wide, new ScreenRect(-760, 0, 760, 1040)];
        Assert.Equal(areas[1], WidgetPlacement.AreaContaining(-700, 40, areas));
        Assert.Equal(Wide, WidgetPlacement.AreaContaining(40, 40, areas));
        Assert.Equal(Wide, WidgetPlacement.AreaContaining(9000, 9000, areas));
    }

    [Fact]
    public void AnUnplacedWidgetFallsBackToThePrimaryWorkArea() =>
        Assert.Equal(Wide, WidgetPlacement.AreaFor(9000, 9000, 240, 120, [Wide]));

    [Fact]
    public void PreviousWideSizeAtASecondaryOriginSelectsThePrimaryByOverlap()
    {
        IReadOnlyList<ScreenRect> areas = [Wide, new(-760, 0, 760, 1040)];
        var recovered = WidgetPlacement.Recover(-400, 40, 1186, 140, areas);
        Assert.True(recovered.Left >= 0);
        Assert.Equal(Wide, WidgetPlacement.AreaContaining(recovered.Left, recovered.Top, areas));
    }

    [Fact]
    public void RecoverIntoKeepsTheSecondaryAfterTheWideWidgetWraps()
    {
        var secondary = new ScreenRect(-760, 0, 760, 1040);
        var recovered = WidgetPlacement.RecoverInto(-400, 40, 720, 200, secondary);
        Assert.True(recovered.Left >= secondary.X);
        Assert.True(recovered.Top >= secondary.Y);
        Assert.True(recovered.Left + 720 <= secondary.Right);
        Assert.True(recovered.Top + 200 <= secondary.Bottom);
        Assert.True(recovered.Left < 0);
    }

    [Fact]
    public void RecoverIntoLeavesAFullyVisibleWidgetWhereItIs()
    {
        var secondary = new ScreenRect(-760, 0, 760, 1040);
        Assert.Equal((-750d, 40d), WidgetPlacement.RecoverInto(-750, 40, 10, 10, secondary));
    }
}

public class WidgetResetCountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    public WidgetResetCountdownTests() => UiText.SetLanguage(UiLanguage.English);

    [Theory]
    [InlineData(35, "in 35m")]
    [InlineData(80, "in 1h 20m")]
    [InlineData(120, "in 2h")]
    [InlineData(3120, "in 2d 4h")]
    [InlineData(2880, "in 2d")]
    public void ShortCountdownsReadAtAGlance(int minutes, string expected)
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal(expected, CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(minutes), Now));
    }

    [Fact]
    public void KoreanCountdownsUseTheSameBreakpoints()
    {
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("35분 후", CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(35), Now));
            Assert.Equal("1시간 20분 후", CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(80), Now));
            Assert.Equal("2일 4시간 후", CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(3120), Now));
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Fact]
    public void AMissingResetStaysUnknownInsteadOfBecomingZero()
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal(UiText.ResetNotProvided, CodexDeadlineFormatting.ResetCountdown(null, Now));
        Assert.Null(CodexDeadlineFormatting.ResetStampTooltip(null));
    }

    [Fact]
    public void APassedResetAwaitsTheServerInsteadOfCountingBelowZero()
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal("Awaiting refresh", CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(-5), Now));
        Assert.Equal("Awaiting refresh", CodexDeadlineFormatting.ResetCountdown(Now, Now));
        Assert.DoesNotContain("-", CodexDeadlineFormatting.ResetCountdown(Now.AddDays(-3), Now), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExactResetTimeIsAvailableForTheTooltip() =>
        Assert.Equal(Now.AddMinutes(35).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            CodexDeadlineFormatting.ResetStampTooltip(Now.AddMinutes(35)));
}
