using System.Text.Json;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public sealed class CursorPlanAmountTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Fact]
    public void PlanMoneyAndBreakdownAreIndependentOfAutoApiAndOnDemand()
    {
        var sample = Parse("""{"autoPercentUsed":17,"apiPercentUsed":91,"used":1234,"limit":1000,"remaining":-234,"breakdown":{"included":500,"bonus":734,"total":1234}}""");
        var plan = sample.PlanAmounts!;
        Assert.Equal(12.34m, plan.UsedAmount);
        Assert.Equal(10m, plan.LimitAmount);
        Assert.Equal(-2.34m, plan.RemainingAmount);
        Assert.Equal(5m, plan.IncludedAmount);
        Assert.Equal(7.34m, plan.BonusAmount);
        Assert.Equal(12.34m, plan.TotalAmount);
        Assert.Equal(17, sample.Windows.Single(w => w.LimitId == "cursor-auto").UsedPercent);
        Assert.Equal(91, sample.Windows.Single(w => w.LimitId == "cursor-api").UsedPercent);
        Assert.All(sample.Windows.Where(w => w.LimitId is "cursor-auto" or "cursor-api"), w => Assert.Null(w.UsedAmount));
        Assert.Equal(3m, sample.Windows.Single(w => w.LimitId == "cursor-on-demand").UsedAmount);
        Assert.True(CursorUsageCollector.ValidSample(sample, Now));
    }

    [Theory]
    [InlineData("\"used\":\"bad\"")]
    [InlineData("\"limit\":-1")]
    [InlineData("\"breakdown\":[]")]
    [InlineData("\"enabled\":\"true\"")]
    public void OptionalPlanAmountErrorsPreservePercentageLanes(string field)
    {
        var sample = Parse("{\"autoPercentUsed\":17,\"apiPercentUsed\":91," + field + "}");
        Assert.Null(sample.PlanAmounts);
        Assert.Equal("cursor-plan-amounts-unavailable", sample.PlanAmountsFailure);
        Assert.Equal(17, sample.Windows.Single(w => w.LimitId == "cursor-auto").UsedPercent);
    }

    [Fact]
    public void MissingMoneyIsUnknownAndDoesNotUsePercentageOrUnsupportedFields()
    {
        var sample = Parse("""{"autoPercentUsed":17,"apiPercentUsed":91,"used":null,"limit":null,"autoUsed":1000,"apiUsed":2000}""");
        Assert.Null(sample.PlanAmounts!.UsedAmount);
        Assert.Null(sample.PlanAmounts.LimitAmount);
        Assert.False(sample.PlanAmounts.IsUnlimited);
        Assert.All(sample.Windows.Where(w => w.LimitId is "cursor-auto" or "cursor-api"), w => Assert.Null(w.UsedAmount));
    }

    private static CursorUsageSample Parse(string plan)
    {
        using var json = JsonDocument.Parse("{\"individualUsage\":{\"plan\":" + plan
            + ",\"onDemand\":{\"enabled\":true,\"used\":300,\"limit\":1000,\"remaining\":700}}}");
        return CursorUsageClient.ParseUsage(json.RootElement, Now);
    }
}
