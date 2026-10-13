using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;

namespace CycleArc.Tests;

public sealed class ClaudeResetCreditTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");
    private const string Grant = """{"id":"must-not-persist","label":"untrusted label","resets_left":2,"resets_total":3,"starts_at":"2030-01-01T09:00:00Z","ends_at":"2030-01-02T10:00:00Z","paused":false,"usable_now":true,"clears":["five_hour","seven_day"]}""";

    [Fact]
    public void CountsRemainingResetsAndProjectsOnlyKnownWindowsWithoutGrantIds()
    {
        var sample = Parse("{\"eligible\":true,\"grants\":[" + Grant + "]}");
        var inventory = sample.ResetCredits!;
        Assert.Equal(2, inventory.AvailableCount(Now));
        Assert.Equal(2, inventory.UsableCount(Now));
        Assert.Equal(Now.AddDays(1), inventory.NextExpiration(Now));
        Assert.Equal(new[] { "five_hour", "seven_day" }, inventory.Grants[0].Clears);
        var saved = JsonSerializer.Serialize(sample);
        Assert.DoesNotContain("must-not-persist", saved);
        Assert.DoesNotContain("untrusted label", saved);
        Assert.Equal(0, inventory.AvailableCount(Now.AddDays(1)));
    }

    [Theory]
    [InlineData("\"paused\":false", "\"paused\":true")]
    [InlineData("2030-01-01T09:00:00Z", "2030-01-01T11:00:00Z")]
    [InlineData("2030-01-02T10:00:00Z", "2030-01-01T09:30:00Z")]
    [InlineData("\"resets_left\":2", "\"resets_left\":0")]
    public void PausedFutureExpiredAndExhaustedAreExcluded(string original, string replacement)
    {
        var sample = Parse("{\"eligible\":true,\"grants\":[" + Grant.Replace(original, replacement) + "]}");
        Assert.Equal(0, sample.ResetCredits!.AvailableCount(Now));
        Assert.Null(sample.ResetCredits.NextExpiration(Now));
    }

    [Fact]
    public void UseGatesAreSeparateFromSavedInventoryAndNullUseStateRemainsUnknown()
    {
        var gated = Parse("{\"eligible\":true,\"grants\":[" + Grant.Replace("\"usable_now\":true", "\"usable_now\":false") + "]}").ResetCredits!;
        Assert.Equal(2, gated.AvailableCount(Now));
        Assert.Equal(0, gated.UsableCount(Now));
        var unknown = Parse("{\"eligible\":true,\"grants\":[" + Grant.Replace("\"usable_now\":true,", "") + "]}").ResetCredits!;
        Assert.Equal(2, unknown.AvailableCount(Now));
        Assert.Null(unknown.UsableCount(Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void AbsentInventoryIsNotZero(string? program)
    {
        var sample = Parse(program);
        Assert.Null(sample.ResetCredits);
        Assert.Null(sample.ResetCreditsFailure);
        Assert.Equal(42.25, sample.FiveHour!.UsedPercentage);
    }

    [Fact]
    public void ExplicitEmptyAndIneligibleRemainDifferent()
    {
        Assert.True(Parse("{\"eligible\":true,\"grants\":[]}").ResetCredits!.Eligible);
        var gated = Parse("{\"eligible\":false,\"ineligible_reason\":\"surface\"}");
        Assert.False(gated.ResetCredits!.Eligible);
        Assert.Empty(gated.ResetCredits.Grants);
    }

    [Theory]
    [InlineData("{\"eligible\":true}")]
    [InlineData("{\"eligible\":null,\"grants\":[]}")]
    [InlineData("{\"eligible\":\"true\",\"grants\":[]}")]
    [InlineData("{\"eligible\":true,\"eligible\":false,\"grants\":[]}")]
    [InlineData("{\"eligible\":true,\"grants\":[{}]}")]
    [InlineData("[]")]
    public void MalformedOptionalProgramPreservesSubscriptionAndExtraUsage(string program)
    {
        var sample = Parse(program);
        Assert.Null(sample.ResetCredits);
        Assert.Equal("claude-reset-credits-unavailable", sample.ResetCreditsFailure);
        Assert.Equal(42.25, sample.FiveHour!.UsedPercentage);
        Assert.Equal(3.2m, sample.ExtraUsage!.UsedAmount);
    }

    [Theory]
    [InlineData("\"resets_left\":2", "\"resets_left\":4")]
    [InlineData("\"resets_left\":2", "\"resets_left\":-1")]
    [InlineData("\"resets_left\":2", "\"resets_left\":null")]
    [InlineData("\"resets_total\":3", "\"resets_total\":51")]
    [InlineData("\"paused\":false", "\"paused\":null")]
    [InlineData("2030-01-01T09:00:00Z", "2030-01-01T09:00:00")]
    public void InvalidGrantNeverInflatesInventory(string original, string replacement)
    {
        Assert.Null(Parse("{\"eligible\":true,\"grants\":[" + Grant.Replace(original, replacement) + "]}").ResetCredits);
    }

    [Fact]
    public void NullExpirySupportedAndCapsBoundProjectedCacheSize()
    {
        var noExpiry = Parse("{\"eligible\":true,\"grants\":[" + Grant.Replace("\"2030-01-02T10:00:00Z\"", "null") + "]}").ResetCredits!;
        Assert.Equal(2, noExpiry.AvailableCount(Now));
        Assert.Null(noExpiry.NextExpiration(Now));
        Assert.Null(Parse("{\"eligible\":true,\"grants\":[" + string.Join(',', Enumerable.Repeat(Grant, 26)) + "]}").ResetCredits);
    }

    private static ClaudeLiveUsageSample Parse(string? program)
    {
        using var json = JsonDocument.Parse("{\"five_hour\":{\"utilization\":42.25},\"extra_usage\":{\"is_enabled\":true,\"used_credits\":320,\"monthly_limit\":1000,\"utilization\":32}"
            + (program is null ? "" : ",\"cedar_ember\":" + program) + "}");
        return ClaudeOAuthUsageClient.ParseUsage(json.RootElement, Now);
    }
}
