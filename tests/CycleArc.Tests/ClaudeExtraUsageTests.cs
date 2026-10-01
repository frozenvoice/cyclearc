using System.Text.Json;
using CycleArc.Providers.Claude;

namespace CycleArc.Tests;

public sealed class ClaudeExtraUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Theory]
    [InlineData("USD", 320, 1000, "3.2", "10")]
    [InlineData("eur", 450, 2000, "4.5", "20")]
    [InlineData("JPY", 320, 1000, "320", "1000")]
    [InlineData("KRW", 320, 1000, "320", "1000")]
    [InlineData("VND", 320, 1000, "320", "1000")]
    public void OfficialClientCurrencyUnitNormalizationHappensOnce(string currency, int rawUsed, int rawLimit,
        string expectedUsed, string expectedLimit)
    {
        var sample = Parse($$"""{"is_enabled":true,"used_credits":{{rawUsed}},"monthly_limit":{{rawLimit}},"utilization":32,"currency":"{{currency}}"}""");
        var extra = sample.ExtraUsage!;
        Assert.Equal(decimal.Parse(expectedUsed, System.Globalization.CultureInfo.InvariantCulture), extra.UsedAmount);
        Assert.Equal(decimal.Parse(expectedLimit, System.Globalization.CultureInfo.InvariantCulture), extra.MonthlyLimitAmount);
        Assert.Equal(currency.ToUpperInvariant(), extra.Currency);
        Assert.Equal(extra.MonthlyLimitAmount - extra.UsedAmount, extra.RemainingAmount);
        Assert.True(extra.IsRemainingCalculated);
        Assert.Equal(Now, extra.ObservedAt);
        Assert.Null(sample.ExtraUsageFailure);
        Assert.False(extra.IsUnlimited);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"currency\":null")]
    public void OmittedOrNullCurrencyUsesVerifiedOfficialClientUsdDefault(string currencyField)
    {
        var sample = Parse("{\"is_enabled\":true,\"used_credits\":320.25,\"monthly_limit\":1000,\"utilization\":32" + currencyField + "}");
        Assert.Equal("USD", sample.ExtraUsage!.Currency);
        Assert.Equal(3.2025m, sample.ExtraUsage.UsedAmount);
    }

    [Fact]
    public void ExplicitNullLimitMeansUnlimitedWhenEnabledAndUsedNullRemainsUnknown()
    {
        var sample = Parse("""{"is_enabled":true,"used_credits":null,"monthly_limit":null,"utilization":null}""");
        Assert.True(sample.ExtraUsage!.IsEnabled);
        Assert.True(sample.ExtraUsage.IsUnlimited);
        Assert.Null(sample.ExtraUsage.UsedAmount);
        Assert.Null(sample.ExtraUsage.MonthlyLimitAmount);
        Assert.Null(sample.ExtraUsage.RemainingAmount);
        Assert.False(sample.ExtraUsage.IsRemainingCalculated);
    }

    [Fact]
    public void DisabledWithNullFieldsIsNotUnlimitedOrZeroSpend()
    {
        var sample = Parse("""{"is_enabled":false,"used_credits":null,"monthly_limit":null,"utilization":null}""");
        Assert.False(sample.ExtraUsage!.IsEnabled);
        Assert.False(sample.ExtraUsage.IsUnlimited);
        Assert.Null(sample.ExtraUsage.UsedAmount);
        Assert.Null(sample.ExtraUsage.RemainingAmount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData(null)]
    public void MissingOrNullExtraUsageDoesNotInventAmounts(string? extra)
    {
        var sample = Parse(extra);
        Assert.Null(sample.ExtraUsage);
        Assert.Null(sample.ExtraUsageFailure);
        Assert.Equal(46.25, sample.FiveHour!.UsedPercentage);
    }

    [Theory]
    [InlineData("{\"is_enabled\":true}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"utilization\":32}")]
    [InlineData("{\"is_enabled\":\"true\",\"used_credits\":320,\"monthly_limit\":1000,\"utilization\":32}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":\"320\",\"monthly_limit\":1000,\"utilization\":32}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":-1,\"monthly_limit\":1000,\"utilization\":32}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"monthly_limit\":-1,\"utilization\":32}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"monthly_limit\":1000,\"utilization\":\"32\"}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"monthly_limit\":1000,\"utilization\":32,\"currency\":5}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"monthly_limit\":1000,\"utilization\":32,\"currency\":\"??\"}")]
    [InlineData("{\"is_enabled\":true,\"used_credits\":320,\"used_credits\":0,\"monthly_limit\":1000,\"utilization\":32}")]
    [InlineData("[]")]
    public void MalformedOptionalExtraUsageDoesNotDiscardValidSubscriptionQuota(string extra)
    {
        var sample = Parse(extra);
        Assert.Null(sample.ExtraUsage);
        Assert.Equal("claude-extra-usage-unavailable", sample.ExtraUsageFailure);
        Assert.Equal(46.25, sample.FiveHour!.UsedPercentage);
    }

    [Fact]
    public void ZeroAndSpendAboveCapArePreservedWithoutDividingByZeroOrClipping()
    {
        var sample = Parse("""{"is_enabled":true,"used_credits":450,"monthly_limit":0,"utilization":null}""");
        Assert.Equal(4.5m, sample.ExtraUsage!.UsedAmount);
        Assert.Equal(0m, sample.ExtraUsage.MonthlyLimitAmount);
        Assert.Equal(-4.5m, sample.ExtraUsage.RemainingAmount);
        Assert.Null(sample.ExtraUsage.UsedPercentage);
        Assert.False(sample.ExtraUsage.IsUnlimited);
    }

    private static ClaudeLiveUsageSample Parse(string? extra)
    {
        var field = extra is null ? "" : ",\"extra_usage\":" + extra;
        using var json = JsonDocument.Parse("{\"five_hour\":{\"utilization\":46.25}" + field + "}");
        return ClaudeOAuthUsageClient.ParseUsage(json.RootElement, Now);
    }
}
