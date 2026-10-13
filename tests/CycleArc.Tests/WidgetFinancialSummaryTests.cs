using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Monitoring;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public sealed class WidgetFinancialSummaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T12:00:00Z");
    private static CodexQuotaSnapshot Snapshot => new(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null, [], null);
    [Fact]
    public void LegacySettingsRemainCompactAndKeepAlertSettingsSeparate()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"FloatingWidgetEnabled\":true,\"UsageAlertsEnabled\":true,\"WidgetZoomPercent\":150}")!;
        Assert.False(settings.WidgetFinancialSummaryEnabled); Assert.False(settings.WidgetResetSummaryEnabled);
        Assert.True(settings.UsageAlertsEnabled); Assert.Equal(150, settings.WidgetZoomPercent);
        Assert.False(settings.FinancialAlerts.CodexLowBalanceEnabled);
        Assert.Equal("", WidgetFinancialSummary.Create(Snapshot with { UsageCredits = new(true, false, 3m, null, Now) }, false, false, Now).Text);
    }
    [Fact]
    public void MissingAndIdentityHiddenValuesOccupyNoWidgetSpace()
    {
        Assert.Equal("", WidgetFinancialSummary.Create(Snapshot, true, true, Now).Text);
        var value = Snapshot with { Status = CodexQuotaStatus.SignedOut, UsageCredits = new(true, false, -1.25m, null, Now), ResetCreditsAvailable = 2 };
        Assert.Equal("", WidgetFinancialSummary.Create(value, true, true, Now).Text);
    }
    [Fact]
    public void OptionalFieldClockControlsWidgetFreshness()
    {
        var snapshot = Snapshot with { UsageCredits = new(true, false, 5m, null, Now.AddHours(-1)) };
        var old = WidgetFinancialSummary.Create(snapshot, true, false, Now).Text;
        var fresh = WidgetFinancialSummary.Create(snapshot with { UsageCredits = snapshot.UsageCredits with { ObservedAt = Now } }, true, false, Now).Text;
        Assert.NotEqual(old, fresh); Assert.Contains("5", old);
        Assert.DoesNotContain("Previous", fresh);
        Assert.Equal("", WidgetFinancialSummary.Create(snapshot, false, false, Now).Text);
    }
    [Fact]
    public void MoneyAndResetMeaningsStayProviderSpecific()
    {
        var codex = Snapshot with { UsageCredits = new(true, false, -1.25m, null, Now), ResetCreditsAvailable = 2 };
        var text = WidgetFinancialSummary.Create(codex, true, true, Now).Text;
        Assert.Contains("-1.25", text); Assert.DoesNotContain("$", text);
        var claude = Snapshot with { Provider = UsageProviderId.Claude, ExtraUsage = new(true, 3.20m, 10, false, "USD", 32, Now),
            ClaudeResetCredits = new(true, [new(2, 3, Now.AddDays(-1), Now.AddDays(1), false, true, false, ["five_hour"]),
                new(3, 3, Now.AddDays(1), Now.AddDays(2), false, true, false, []), new(4, 4, null, Now.AddDays(-1), false, true, false, [])], Now) };
        text = WidgetFinancialSummary.Create(claude, true, true, Now).Text;
        Assert.Contains("$3.20", text); Assert.DoesNotContain("$10", text); Assert.Contains("2", text);
        var cursor = Snapshot with { Provider = UsageProviderId.Cursor, Windows = [new("cursor-team-on-demand", 90, null, null, CodexWindowKind.Other)
            { UsedAmount = 90, Unit = "USD" }, new("cursor-auto", 70, null, null, CodexWindowKind.Other) { UsedAmount = 70, Unit = "USD" }] };
        Assert.Equal("", WidgetFinancialSummary.Create(cursor, true, false, Now).Text);
    }
}
