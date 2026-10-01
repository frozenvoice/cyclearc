using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class UsageCreditPresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");
    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider) => new(
        CodexQuotaStatus.Available, "pro", Now, Now, null, null, 2,
        [new("five_hour", 90, 300, Now.AddHours(1), CodexWindowKind.FiveHour)],
        provider == UsageProviderId.Claude ? "claude-live" : null) { Provider = provider };

    [Theory]
    [InlineData("1250.5", "1,250.5")]
    [InlineData("0", "0")]
    [InlineData("-2.75", "-2.75")]
    public void CodexPreservesBalanceAndNeverUsesCurrency(string number, string formatted)
    {
        var snapshot = Snapshot(UsageProviderId.Codex) with
        {
            UsageCredits = new(true, false, decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture), "codex", Now)
        };
        var card = UsageCreditPresentation.Create(snapshot);
        Assert.Contains(formatted, card.Summary);
        Assert.DoesNotContain("$", card.Tooltip);
        Assert.Equal(2, snapshot.ResetCreditsAvailable);
        Assert.Single(snapshot.Windows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlagWithoutBalanceDoesNotInventZero(bool hasCredits)
    {
        var card = UsageCreditPresentation.Create(Snapshot(UsageProviderId.Codex) with
            { UsageCredits = new(hasCredits, false, null, "codex", Now) });
        Assert.DoesNotContain("0", card.Summary);
        Assert.Contains(UiText.T("Balance unavailable", "잔액 확인 불가"), card.Summary);
    }

    [Fact]
    public void ClaudeShowsMonthlySpendAndCalculatedCapRemainderNotWallet()
    {
        var extra = new ClaudeExtraUsage(true, 3.2m, 10m, false, "USD", 32, Now);
        var card = UsageCreditPresentation.Create(Snapshot(UsageProviderId.Claude) with { ExtraUsage = extra });
        Assert.Contains("$3.20", card.Summary);
        Assert.Contains(card.Rows, row => row.Value == "$6.80" && row.Tooltip is not null);
        Assert.DoesNotContain("balance", card.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CursorUsesServerRemainingAndDoesNotDoubleConvertOrClampSpend()
    {
        using var json = JsonDocument.Parse("""{"individualUsage":{"plan":{"autoPercentUsed":25},"onDemand":{"enabled":true,"used":450,"limit":200,"remaining":75}},"teamUsage":{"onDemand":{"used":99999,"limit":100000}}}""");
        var sample = CursorUsageClient.ParseUsage(json.RootElement, Now);
        var card = UsageCreditPresentation.Create(Snapshot(UsageProviderId.Cursor) with { Windows = sample.Windows });
        Assert.Contains("$4.50", card.Summary);
        Assert.Contains(card.Rows, row => row.Value == "$0.75");
        Assert.DoesNotContain("999.99", card.Tooltip);
        Assert.Contains(UiText.T("Spending cap exceeded", "상한 초과"), card.Notice);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("0", false)]
    [InlineData("null", true)]
    public void CursorUnknownZeroAndUnlimitedCapsRemainDistinct(string limit, bool unlimited)
    {
        using var json = JsonDocument.Parse("{\"individualUsage\":{\"onDemand\":{\"enabled\":true,\"used\":450,\"limit\":"
            + limit + ",\"isUnlimited\":" + unlimited.ToString().ToLowerInvariant() + "}}}");
        var sample = CursorUsageClient.ParseUsage(json.RootElement, Now);
        var card = UsageCreditPresentation.Create(Snapshot(UsageProviderId.Cursor) with { Windows = sample.Windows });
        Assert.Contains(unlimited ? UiText.T("Unlimited", "무제한") : limit == "0" ? "$0.00" : UiText.T("Not provided", "미제공"), card.Summary);
        Assert.DoesNotContain("NaN", card.Tooltip);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"used\":\"wrong\"}")]
    [InlineData("{\"enabled\":1}")]
    public void MalformedCursorOnDemandDoesNotDiscardPlanPercentages(string extra)
    {
        using var json = JsonDocument.Parse("{\"individualUsage\":{\"plan\":{\"autoPercentUsed\":25},\"onDemand\":" + extra + "}}");
        var sample = CursorUsageClient.ParseUsage(json.RootElement, Now);
        Assert.Equal(25, sample.Windows.Single(window => window.LimitId == "cursor-auto").UsedPercent);
        Assert.Equal("cursor-on-demand-unavailable", sample.Windows.Single(window => window.LimitId == "cursor-on-demand").AmountFailure);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, "codex-identity-mismatch")]
    [InlineData(UsageProviderId.Claude, "claude-live-identity-mismatch")]
    [InlineData(UsageProviderId.Cursor, "cursor-live-identity-mismatch")]
    public void IdentityMismatchHidesEvenAnAccidentallyCarriedBalance(UsageProviderId provider, string detail)
    {
        var snapshot = Snapshot(provider) with
        {
            TechnicalDetail = detail, UsageCredits = new(true, false, 12345m, "codex", Now),
            ExtraUsage = new(true, 12345m, 99999m, false, "USD", null, Now),
            Windows = [new("cursor-on-demand", null, null, null, CodexWindowKind.Other) { UsedAmount = 12345, Unit = "USD" }]
        };
        var card = UsageCreditPresentation.Create(snapshot);
        Assert.Empty(card.Rows);
        Assert.DoesNotContain("12,345", card.Tooltip);
    }

    [Fact]
    public void CreditChangesCannotTriggerOrResetUsageAlerts()
    {
        var snapshot = Snapshot(UsageProviderId.Codex);
        var profile = new CodexAccountProfile("fixture", "", "Synthetic");
        var account = new CodexAccountView(profile, snapshot, null);
        var (_, marks) = UsageAlerts.Evaluate([account], null);
        var (alerts, preserved) = UsageAlerts.Evaluate([account with
            { Snapshot = snapshot with { UsageCredits = new(true, false, -100m, "codex", Now.AddMinutes(1)) } }], marks);
        Assert.Empty(alerts);
        Assert.Equal(marks.OrderBy(pair => pair.Key), preserved.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Cursor)]
    public void RequestFailureOverridesEarlierMissingMoney(UsageProviderId provider)
    {
        var snapshot = Snapshot(provider) with
        {
            Status = CodexQuotaStatus.Stale, TechnicalDetail = "timed-out",
            UsageCreditsFailure = provider == UsageProviderId.Codex ? "credits-not-provided" : null,
            UsageCredits = null
        };
        var card = UsageCreditPresentation.Create(snapshot);
        Assert.Equal(UiText.T("Check failed", "조회 실패"), card.Notice);
        Assert.Equal(UiText.T("Not available", "확인 불가"), card.Summary);
    }

    [Fact]
    public void OverRangeOptionalPercentCannotDiscardValidCursorMoney()
    {
        using var json = JsonDocument.Parse("""{"individualUsage":{"plan":{"autoPercentUsed":25},"onDemand":{"enabled":true,"used":450,"limit":200,"percentUsed":225}}}""");
        var sample = CursorUsageClient.ParseUsage(json.RootElement, Now);
        var demand = sample.Windows.Single(window => window.LimitId == "cursor-on-demand");
        Assert.Null(demand.UsedPercent);
        Assert.Null(demand.AmountFailure);
        Assert.Equal(4.50m, demand.UsedAmount);
    }

    [Fact]
    public void InitialConnectedWaitingDoesNotBecomeMoneyFailure()
    {
        var waiting = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "claude-connected-waiting")
            with { Provider = UsageProviderId.Claude };
        var card = UsageCreditPresentation.Create(waiting);
        Assert.Empty(card.Notice);
        Assert.Equal(UiText.T("Not provided", "미제공"), card.Summary);
    }
}
