using CycleArc.Monitoring;
using CycleArc.Services;

namespace CycleArc.Tests;

public class UsageHistoryChartTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");

    [Fact]
    public void Percentages_UseTheWholeZeroToHundredScale()
    {
        Assert.Equal((0d, 100d), UsageHistoryChart.Scale(UsageHistoryMetric.UsagePercent, [40m, 41m]));
        Assert.Equal((0d, 100d), UsageHistoryChart.Scale(UsageHistoryMetric.UsagePercent, []));
        // A reported value beyond the cap is still drawn inside the chart.
        Assert.Equal((0d, 120d), UsageHistoryChart.Scale(UsageHistoryMetric.UsagePercent, [20m, 120m]));
    }

    [Fact]
    public void Amounts_StartAtZero_AndIncludeNegativeChanges()
    {
        var spend = UsageHistoryChart.Scale(UsageHistoryMetric.ActualSpend, [1m, 2m, 3.2m]);
        Assert.Equal(0d, spend.Min);
        Assert.True(spend.Max > 3.2 && spend.Max < 3.6);
        var change = UsageHistoryChart.Scale(UsageHistoryMetric.BalanceChange, [-5m, 10m]);
        Assert.True(change.Min < -5 && change.Max > 10);
        Assert.Equal((0d, 1d), UsageHistoryChart.Scale(UsageHistoryMetric.CreditBalance, [0m, 0m]));
        var extreme = UsageHistoryChart.Scale(UsageHistoryMetric.CreditBalance, [decimal.MinValue, decimal.MaxValue]);
        Assert.True(double.IsFinite(extreme.Min) && double.IsFinite(extreme.Max) && extreme.Max > extreme.Min);
    }

    [Fact]
    public void NearestIndex_PicksTheClosestMeasurementInTime()
    {
        UsageHistoryPoint Point(int minutes) => new(Now.AddMinutes(minutes), "s", "Credits", UsageHistoryMetric.CreditBalance,
            minutes, "credits", UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.None);
        var points = new[] { Point(-60), Point(-30), Point(0) };
        Assert.Equal(-1, UsageHistoryChart.NearestIndex([], Now));
        Assert.Equal(0, UsageHistoryChart.NearestIndex(points, Now.AddHours(-3)));
        Assert.Equal(1, UsageHistoryChart.NearestIndex(points, Now.AddMinutes(-40)));
        Assert.Equal(2, UsageHistoryChart.NearestIndex(points, Now.AddMinutes(-10)));
    }

    [Fact]
    public void SourceAndChangeNames_AreReadableInBothLanguages()
    {
        var previous = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            {
                UiText.SetLanguage(language);
                foreach (var source in Enum.GetValues<UsageHistorySource>())
                {
                    var name = UsageHistoryChart.SourceName(source);
                    Assert.DoesNotContain("OAuth", name, StringComparison.Ordinal);
                    Assert.DoesNotContain("GET", name, StringComparison.Ordinal);
                    Assert.DoesNotContain("usage-summary", name, StringComparison.Ordinal);
                    Assert.DoesNotContain("App Server", name, StringComparison.Ordinal);
                }
                var changes = UsageHistoryChart.DiscontinuityName(UsageHistoryDiscontinuity.SourceChanged | UsageHistoryDiscontinuity.Gap);
                Assert.DoesNotContain("SourceChanged", changes, StringComparison.Ordinal);
                Assert.Contains(", ", changes, StringComparison.Ordinal);
            }
            UiText.SetLanguage(UiLanguage.English);
            Assert.Equal("source changed, observation gap",
                UsageHistoryChart.DiscontinuityName(UsageHistoryDiscontinuity.SourceChanged | UsageHistoryDiscontinuity.Gap));
            Assert.Equal("40%", UsageHistoryChart.AxisValue(40, "%"));
            Assert.Equal("3.25", UsageHistoryChart.AxisValue(3.25, "USD"));
        }
        finally { UiText.SetLanguage(previous); }
    }
}
