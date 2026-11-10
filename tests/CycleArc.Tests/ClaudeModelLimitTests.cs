using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// Claude's per-model weekly limit (the server's <c>weekly_scoped</c> entry). The response
/// shape here follows the key names and value kinds seen in one live response; the values are made up.
/// </summary>
public sealed class ClaudeModelLimitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    public ClaudeModelLimitTests() => UiText.SetLanguage(UiLanguage.English);

    private const string Windows = """
        "five_hour":{"utilization":5,"resets_at":"2030-01-01T12:00:00Z"},
        "seven_day":{"utilization":35,"resets_at":"2030-01-04T17:00:00Z"}
        """;

    private const string Limits = """
        "limits":[
          {"kind":"session","group":"session","percent":5,"severity":"ok","resets_at":"2030-01-01T12:00:00Z","scope":null,"is_active":true},
          {"kind":"weekly_all","group":"weekly","percent":35,"severity":"ok","resets_at":"2030-01-04T17:00:00Z","scope":null,"is_active":true},
          {"kind":"weekly_scoped","group":"weekly","percent":2,"severity":"ok","resets_at":"2030-01-03T17:00:00Z",
           "scope":{"model":{"id":null,"display_name":"Fable"},"surface":null},"is_active":true}],
        "weekly_scoped_shares":[{"limit_index":2,"used_percent_of_weekly":1,"allowance_percent_of_weekly":50}]
        """;

    [Fact]
    public void ScopedWeeklyLimitIsReadWithItsModelNameResetAndShare()
    {
        var sample = Parse($"{{{Windows},{Limits}}}");

        var limit = Assert.Single(sample.ModelLimits!);
        Assert.Equal(new ClaudeModelLimit("Fable", 2, DateTimeOffset.Parse("2030-01-03T17:00:00Z"), 50), limit);
        Assert.Equal(35, sample.SevenDay!.UsedPercentage);
    }

    [Fact]
    public void ResponseWithoutLimitsHasNoModelLimit() => Assert.Null(Parse($"{{{Windows}}}").ModelLimits);

    [Theory]
    // Not a list.
    [InlineData("\"limits\":\"none\"")]
    // The weekly entry disagrees with seven_day.utilization: an unknown scale.
    [InlineData("\"limits\":[{\"kind\":\"weekly_all\",\"percent\":0.35},{\"kind\":\"weekly_scoped\",\"percent\":0.02,\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]")]
    [InlineData("\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":101,\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]")]
    [InlineData("\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":\"2\",\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]")]
    [InlineData("\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":2,\"resets_at\":\"2030-01-03T17:00:00\",\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]")]
    // No model name to show.
    [InlineData("\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":2,\"scope\":null}]")]
    [InlineData("\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":2,\"scope\":{\"model\":{\"display_name\":\"\"}}}]")]
    public void UnreadableLimitsShowNothingAndNeverFailTheSharedWindows(string limits)
    {
        var sample = Parse($"{{{Windows},{limits}}}");

        Assert.Null(sample.ModelLimits);
        Assert.Equal(5, sample.FiveHour!.UsedPercentage);
        Assert.Equal(35, sample.SevenDay!.UsedPercentage);
    }

    [Fact]
    public void ShareIsTakenOnlyFromTheEntryItPointsAt()
    {
        var sample = Parse($"{{{Windows},{Limits.Replace("\"limit_index\":2", "\"limit_index\":1")}}}");

        Assert.Null(Assert.Single(sample.ModelLimits!).WeeklySharePercent);
    }

    [Fact]
    public void StoredSampleWithAnInvalidModelLimitIsRejected()
    {
        var sample = new ClaudeLiveUsageSample(Now, new(5, null), null);
        Assert.True(ClaudeLiveUsageCollector.ValidSample(sample with { ModelLimits = [new("Fable", 2, null, 50)] }, Now));
        Assert.False(ClaudeLiveUsageCollector.ValidSample(sample with { ModelLimits = [] }, Now));
        Assert.False(ClaudeLiveUsageCollector.ValidSample(sample with { ModelLimits = [new("Fable", 120, null, null)] }, Now));
        Assert.False(ClaudeLiveUsageCollector.ValidSample(sample with { ModelLimits = [new("Fable\n", 2, null, null)] }, Now));
        Assert.False(ClaudeLiveUsageCollector.ValidSample(sample with
        { ModelLimits = [new("Fable", 2, null, null), new("fable", 3, null, null)] }, Now));
    }

    [Fact]
    public void ModelLimitAddsItsUsageAndResetRowsBelowTheSharedWindows()
    {
        var snapshot = Snapshot(UsageProviderId.Claude) with
        { ClaudeModelLimits = [new("Fable", 2, Now.AddDays(2), 50)] };
        var ring = CodexRingPresentation.FromDetail(snapshot, UsagePeriodPreference.FiveHour);

        var (primary, secondary, start) = CodexDisplayFormatting.DetailSections(snapshot, ring.Window, Now);

        Assert.Equal(0, start);
        Assert.Equal(2, primary.Count);
        Assert.DoesNotContain(primary, row => row.Label.Contains("Fable", StringComparison.Ordinal));
        var usage = Assert.Single(secondary, row => row.Label == "Fable weekly used / left");
        Assert.Equal("2% / 98%", usage.Value);
        Assert.Equal("Up to 50% of the weekly limit", usage.Detail);
        Assert.False(usage.EmphasizeDanger);
        var reset = Assert.Single(secondary, row => row.Label == "Fable reset");
        Assert.Equal(CodexDeadlineFormatting.Remaining(Now.AddDays(2), Now), reset.Detail);
        // Shared weekly rows first, then the model's, then the check time.
        Assert.Equal(2, secondary.ToList().IndexOf(usage));
        Assert.Equal(3, secondary.ToList().IndexOf(reset));
    }

    [Fact]
    public void ModelLimitDoesNotBecomeAWindowOrChangeTheRing()
    {
        var plain = Snapshot(UsageProviderId.Claude);
        var with = plain with { ClaudeModelLimits = [new("Fable", 99, Now.AddDays(2), null)] };

        Assert.Equal(2, with.Windows.Count);
        foreach (var period in new[] { UsagePeriodPreference.FiveHour, UsagePeriodPreference.Weekly })
            Assert.Equal(CodexRingPresentation.FromDetail(plain, period).Window?.LimitId,
                CodexRingPresentation.FromDetail(with, period).Window?.LimitId);
    }

    [Fact]
    public void ExhaustedExpiredOtherProviderAndSignedOutCases()
    {
        var full = Snapshot(UsageProviderId.Claude) with { ClaudeModelLimits = [new("Fable", 100, null, null)] };
        var row = Assert.Single(CodexDisplayFormatting.Rows(full, Now), x => x.Label.StartsWith("Fable", StringComparison.Ordinal));
        Assert.True(row.EmphasizeDanger);
        Assert.Equal("100% / 0%", row.Value);
        Assert.Null(row.Detail);

        // A limit whose reset has passed is out of date and is not shown.
        var expired = full with { ClaudeModelLimits = [new("Fable", 40, Now.AddMinutes(-1), 50)] };
        Assert.DoesNotContain(CodexDisplayFormatting.Rows(expired, Now), x => x.Label.Contains("Fable", StringComparison.Ordinal));

        var codex = Snapshot(UsageProviderId.Codex) with { ClaudeModelLimits = [new("Fable", 2, null, null)] };
        Assert.DoesNotContain(CodexDisplayFormatting.Rows(codex, Now), x => x.Label.Contains("Fable", StringComparison.Ordinal));

        Assert.Null((full with { Status = CodexQuotaStatus.SignedOut }).AsRefreshing().ClaudeModelLimits);
        Assert.NotNull(full.AsRefreshing().ClaudeModelLimits);
    }

    [Fact]
    public void LongModelNameIsCutInTheRowLabel()
    {
        var snapshot = Snapshot(UsageProviderId.Claude) with
        { ClaudeModelLimits = [new("Claude Sonnet 4.5 Extended", 2, Now.AddDays(2), null)] };

        var rows = CodexDisplayFormatting.Rows(snapshot, Now);

        Assert.Single(rows, row => row.Label == "Claude Sonnet 4… weekly used / left");
        Assert.Single(rows, row => row.Label == "Claude Sonnet 4… reset");
    }

    [Fact]
    public void KoreanLabels()
    {
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Claude) with { ClaudeModelLimits = [new("Fable", 2, Now.AddDays(2), 50)] };
            var rows = CodexDisplayFormatting.Rows(snapshot, Now);
            var usage = Assert.Single(rows, row => row.Label == "Fable 주간 사용량 / 남음");
            Assert.Equal("주간 한도의 최대 50%", usage.Detail);
            Assert.Single(rows, row => row.Label == "Fable 리셋");
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    private static ClaudeLiveUsageSample Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeOAuthUsageClient.ParseUsage(document.RootElement, Now);
    }

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider) =>
        new(CodexQuotaStatus.Available, null, Now.AddMinutes(-1), Now.AddMinutes(-1), null, null, null,
            [new("five_hour", 5, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(2), CodexWindowKind.FiveHour),
             new("seven_day", 35, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(3), CodexWindowKind.Weekly)],
            provider == UsageProviderId.Claude ? ClaudeUsagePresentation.LiveDetail : null)
        { Provider = provider };
}
