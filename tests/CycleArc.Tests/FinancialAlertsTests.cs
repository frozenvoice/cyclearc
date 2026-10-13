using CycleArc.Codex;
using CycleArc.Monitoring;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public class FinancialAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static FinancialAlertOptions Options => new()
    {
        CodexLowBalanceEnabled = true, ClaudeSpendEnabled = true, CursorSpendEnabled = true,
        CodexResetExpiryEnabled = true, ClaudeResetExpiryEnabled = true
    };

    [Theory]
    [InlineData(11, false)]
    [InlineData(10, true)]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    public void CreditThresholdUsesRawBalanceIncludingNegativeBalances(int balance, bool alert) =>
        Assert.Equal(alert, Evaluate(Credit("a", balance)).Alerts.Count == 1);

    [Fact]
    public void AllFinancialAlertsAreOptIn() =>
        Assert.Empty(FinancialAlerts.Evaluate([Credit("a", -1), Spend("b", 99, 100), Claude("c", 99, 100)],
            null, new FinancialAlertOptions(), Now).Alerts);

    [Fact]
    public void BalanceRefreshRestartWaveringAndRecoveryHaveDistinctBehavior()
    {
        var first = Evaluate(Credit("a", 10));
        Assert.Single(first.Alerts);
        Assert.Empty(Evaluate(Credit("a", 9), first.Marks).Alerts);
        var restart = new Dictionary<string, string>(first.Marks);
        var wavering = Evaluate(Credit("a", 10.5m), restart);
        Assert.Empty(Evaluate(Credit("a", 9), wavering.Marks).Alerts);
        var recovered = Evaluate(Credit("a", 12), wavering.Marks);
        Assert.Single(Evaluate(Credit("a", 9), recovered.Marks).Alerts);
    }

    [Fact]
    public void FailuresOldFutureUnknownAndDisconnectedSamplesPreserveMarks()
    {
        var initial = Evaluate(Credit("a", 9));
        var healthy = Credit("a", 9);
        var invalid = new[]
        {
            healthy with { IsConnected = false },
            healthy with { Snapshot = healthy.Snapshot with { Status = CodexQuotaStatus.Stale } },
            healthy with { Snapshot = healthy.Snapshot with { TechnicalDetail = "codex-identity-mismatch" } },
            healthy with { Snapshot = healthy.Snapshot with { UsageCreditsFailure = "credits-malformed" } },
            healthy with { Snapshot = healthy.Snapshot with { UsageCredits = healthy.Snapshot.UsageCredits! with { ObservedAt = Now.AddMinutes(-16) } } },
            healthy with { Snapshot = healthy.Snapshot with { UsageCredits = healthy.Snapshot.UsageCredits! with { ObservedAt = Now.AddSeconds(1) } } },
            healthy with { Snapshot = healthy.Snapshot with { UsageCredits = null } },
            healthy with { Snapshot = healthy.Snapshot with { IdentityFingerprint = null } },
            healthy with { Snapshot = healthy.Snapshot with { IdentityFingerprint = "invalid-identity" } },
            healthy with { Snapshot = healthy.Snapshot with { LastSuccessfulObservationWasServer = false } },
            healthy with { Snapshot = healthy.Snapshot with { Provider = UsageProviderId.Claude } }
        };
        foreach (var account in invalid)
        {
            var result = Evaluate(account, initial.Marks);
            Assert.Empty(result.Alerts);
            Assert.Equal(initial.Marks, result.Marks);
            Assert.Empty(Evaluate(healthy, result.Marks).Alerts);
            Assert.Empty(Evaluate(account).Alerts);
        }
    }

    [Fact]
    public void UnlimitedCreditsAndMissingBalanceNeverAlert()
    {
        var account = Credit("a", 0);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with
            { UsageCredits = account.Snapshot.UsageCredits! with { Unlimited = true } } }).Alerts);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with
            { UsageCredits = account.Snapshot.UsageCredits! with { Balance = null } } }).Alerts);
    }

    [Theory]
    [InlineData(84, false)]
    [InlineData(85, true)]
    [InlineData(100, true)]
    [InlineData(140, true)]
    [InlineData(-1, false)]
    public void ConfirmedSpendingSupportsOverLimitAndRejectsNegativeAmounts(int used, bool expected)
    {
        Assert.Equal(expected, Evaluate(Spend("a", used, 100)).Alerts.Count == 1);
        Assert.Equal(expected, Evaluate(Claude("a", used, 100)).Alerts.Count == 1);
    }

    [Fact]
    public void PercentagesTeamAndPlanBucketsCannotCreateMoneyAlerts()
    {
        var original = Spend("a", 90, 100);
        var onDemand = Assert.Single(original.Snapshot.Windows);
        foreach (var window in new[]
        {
            onDemand with { UsedAmount = null, UsedPercent = 100 },
            onDemand with { LimitAmount = null, UsedPercent = 100 },
            onDemand with { LimitAmount = 0 },
            onDemand with { IsUnlimited = true },
            onDemand with { IsEnabled = false },
            onDemand with { Unit = null },
            onDemand with { AmountFailure = "cursor-on-demand-unavailable" },
            onDemand with { AmountObservedAt = Now.AddHours(-1) },
            onDemand with { LimitId = "cursor-team-on-demand" },
            onDemand with { LimitId = "cursor-api" }
        }) Assert.Empty(Evaluate(original with { Snapshot = original.Snapshot with { Windows = [window] } }).Alerts);
        // An unrelated utilization percentage never overrides confirmed spending.
        Assert.Single(Evaluate(original with { Snapshot = original.Snapshot with
            { Windows = [onDemand with { UsedPercent = 1 }] } }).Alerts);
    }

    [Fact]
    public void OptionalSpendFailureDoesNotRearmOnRecovery()
    {
        var account = Claude("a", 90, 100);
        var initial = Evaluate(account);
        var failed = Evaluate(account with { Snapshot = account.Snapshot with
            { ExtraUsageFailure = "claude-extra-usage-unavailable" } }, initial.Marks);
        Assert.Equal(initial.Marks, failed.Marks);
        Assert.Empty(Evaluate(account, failed.Marks).Alerts);
        var extra = account.Snapshot.ExtraUsage!;
        foreach (var value in new[] { extra with { IsEnabled = false }, extra with { IsUnlimited = true },
            extra with { MonthlyLimitAmount = null }, extra with { Currency = null },
            extra with { ObservedAt = Now.AddHours(-1) } })
            Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with { ExtraUsage = value } }).Alerts);
    }

    [Fact]
    public void SpendRecoveryNewBillingPeriodAndCapChangeRearmIndependently()
    {
        var initial = Evaluate(Spend("a", 90, 100));
        var wavering = Evaluate(Spend("a", 83, 100), initial.Marks);
        Assert.Empty(Evaluate(Spend("a", 90, 100), wavering.Marks).Alerts);
        var recovered = Evaluate(Spend("a", 79, 100), initial.Marks);
        Assert.Single(Evaluate(Spend("a", 90, 100), recovered.Marks).Alerts);
        Assert.Single(Evaluate(Spend("a", 90, 100, Now.AddMonths(2)), initial.Marks).Alerts);
        Assert.Empty(Evaluate(Spend("a", 90, 100, Now.AddMonths(1).AddSeconds(30)), initial.Marks).Alerts);
        Assert.Single(Evaluate(Spend("a", 90, 95), initial.Marks).Alerts);
    }

    [Fact]
    public void ChangingThresholdCreatesANewPolicyWithoutInferringSpend()
    {
        var initial = Evaluate(Credit("a", 9));
        var changed = Options;
        changed.CodexLowBalanceThreshold = 8;
        var none = FinancialAlerts.Evaluate([Credit("a", 9)], initial.Marks, changed, Now);
        Assert.Empty(none.Alerts);
        Assert.Single(FinancialAlerts.Evaluate([Credit("a", 8)], none.Marks, changed, Now).Alerts);
        changed.CursorSpendThresholdPercent = 95;
        Assert.Empty(FinancialAlerts.Evaluate([Spend("b", 90, 100)], null, changed, Now).Alerts);
        changed.CursorSpendThresholdPercent = 0;
        Assert.Empty(FinancialAlerts.Evaluate([Spend("b", 100, 100)], null, changed, Now).Alerts);
    }

    [Fact]
    public void ClaudeDoesNotInventABillingBoundaryFromTheCalendarMonth()
    {
        var initial = Evaluate(Claude("a", 90, 100));
        var nextMonth = Now.AddMonths(1);
        var later = Claude("a", 90, 100);
        later = later with { Snapshot = later.Snapshot with { LastSuccessfulRefresh = nextMonth,
            ExtraUsage = later.Snapshot.ExtraUsage! with { ObservedAt = nextMonth } } };
        Assert.Empty(FinancialAlerts.Evaluate([later], initial.Marks, Options, nextMonth).Alerts);
        var recovered = later with { Snapshot = later.Snapshot with
            { ExtraUsage = later.Snapshot.ExtraUsage! with { UsedAmount = 0 } } };
        var rearmed = FinancialAlerts.Evaluate([recovered], initial.Marks, Options, nextMonth);
        Assert.Single(FinancialAlerts.Evaluate([later], rearmed.Marks, Options, nextMonth).Alerts);
    }

    [Fact]
    public void LowConfiguredSpendThresholdStillRearmsAfterVerifiedZeroSpend()
    {
        var options = Options;
        options.CursorSpendThresholdPercent = 1;
        var initial = FinancialAlerts.Evaluate([Spend("a", 1, 100)], null, options, Now);
        Assert.Single(initial.Alerts);
        var recovered = FinancialAlerts.Evaluate([Spend("a", 0, 100)], initial.Marks, options, Now);
        Assert.Single(FinancialAlerts.Evaluate([Spend("a", 1, 100)], recovered.Marks, options, Now).Alerts);
    }

    [Fact]
    public void AccountsProvidersIdentitiesAndCreditBucketsNeverShareMarks()
    {
        var first = FinancialAlerts.Evaluate([Credit("a", 9), Credit("b", 9), Claude("c", 99, 100)], null, Options, Now);
        Assert.Equal(3, first.Alerts.Count);
        Assert.Single(Evaluate(Credit("a", 9) with { Snapshot = Credit("a", 9).Snapshot with
            { IdentityFingerprint = Fingerprint("different-account") } }, first.Marks).Alerts);
        Assert.Single(Evaluate(Credit("a", 9) with { Snapshot = Credit("a", 9).Snapshot with
            { UsageCredits = Credit("a", 9).Snapshot.UsageCredits! with { LimitId = "another-bucket" } } }, first.Marks).Alerts);
        var remaining = Evaluate(Credit("a", 9), first.Marks);
        Assert.All(remaining.Marks.Keys, key => Assert.StartsWith("a|", key));
    }

    [Fact]
    public void CodexExpiryUsesKnownDatesAndCountsWithoutCreditIds()
    {
        var account = Account("a", UsageProviderId.Codex) with { Snapshot = Account("a", UsageProviderId.Codex).Snapshot with
        {
            ResetCreditsAvailable = 4, ResetCreditExpirations = [Now.AddDays(2), Now.AddDays(2), null, Now.AddDays(-1)],
            RedeemableCredits = [new("SECRET-CREDIT-ID", Now.AddDays(2))]
        } };
        var initial = Evaluate(account);
        var alert = Assert.Single(initial.Alerts);
        Assert.Equal(FinancialAlertKind.ResetExpiry, alert.Kind);
        Assert.Equal(2, alert.Count);
        Assert.DoesNotContain("SECRET-CREDIT-ID", string.Join("", initial.Marks));
        Assert.Empty(Evaluate(account, new Dictionary<string, string>(initial.Marks)).Alerts);
        Assert.Single(Evaluate(account with { Snapshot = account.Snapshot with
            { ResetCreditExpirations = [Now.AddDays(2)] } }, initial.Marks).Alerts);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with
            { ResetCreditExpirations = [null, Now.AddDays(-1), Now.AddDays(10)] } }).Alerts);
    }

    [Fact]
    public void ClaudeExpiryExcludesFuturePausedExpiredExhaustedAndUnknownDates()
    {
        var grants = new[]
        {
            Grant(2, Now.AddDays(-1), Now.AddDays(2)),
            Grant(1, Now.AddDays(1), Now.AddDays(2)),
            Grant(1, Now.AddDays(-1), Now.AddDays(2)) with { Paused = true },
            Grant(1, Now.AddDays(-2), Now.AddDays(-1)),
            Grant(0, Now.AddDays(-1), Now.AddDays(2)),
            Grant(1, null, null)
        };
        var account = Account("a", UsageProviderId.Claude);
        account = account with { Snapshot = account.Snapshot with { ClaudeResetCredits = new(true, grants, Now) } };
        var initial = Evaluate(account);
        Assert.Equal(2, Assert.Single(initial.Alerts).Count);
        Assert.Empty(Evaluate(account, initial.Marks).Alerts);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with
            { ClaudeResetCredits = new(false, grants, Now) } }).Alerts);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with
            { ClaudeResetCredits = new(true, grants, Now.AddMinutes(-16)) } }).Alerts);
        var failed = Evaluate(account with { Snapshot = account.Snapshot with
            { ClaudeResetCreditsFailure = "claude-reset-credits-unavailable" } }, initial.Marks);
        Assert.Equal(initial.Marks, failed.Marks);
        Assert.Empty(Evaluate(account, failed.Marks).Alerts);
        Assert.Empty(Evaluate(account with { Snapshot = account.Snapshot with { ClaudeResetCredits = null } }).Alerts);
    }

    [Fact]
    public void FreshnessBoundaryAndNotificationLimitsAreDeterministic()
    {
        var account = Credit("a", 9);
        Assert.Single(Evaluate(account with { Snapshot = account.Snapshot with
            { UsageCredits = account.Snapshot.UsageCredits! with { ObservedAt = Now - FinancialAlerts.MaximumAge } } }).Alerts);
        var accounts = Enumerable.Range(0, 12).Select(i => Credit($"Long account name {i} " + new string('x', 70), 0));
        var alerts = FinancialAlerts.Evaluate(accounts, null, Options, Now).Alerts;
        var summary = FinancialAlerts.Summary(alerts);
        Assert.True(summary.Title.Length <= 63 && summary.Body.Length <= 255);
        var one = FinancialAlerts.Summary([alerts[0]]);
        Assert.True(one.Title.Length <= 63 && one.Body.Length <= 255);
    }

    private static (IReadOnlyList<FinancialAlert> Alerts, Dictionary<string, string> Marks) Evaluate(
        CodexAccountView account, IReadOnlyDictionary<string, string>? marks = null) =>
        FinancialAlerts.Evaluate([account], marks, Options, Now);

    private static CodexAccountView Account(string id, UsageProviderId provider) =>
        new(new CodexAccountProfile(id, "", id) { Provider = provider },
            CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
            { Provider = provider, LastSuccessfulRefresh = Now, IdentityFingerprint = Fingerprint(id) }) { IsConnected = true };

    private static string Fingerprint(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static CodexAccountView Credit(string id, decimal value)
    {
        var account = Account(id, UsageProviderId.Codex);
        return account with { Snapshot = account.Snapshot with { UsageCredits = new(value > 0, false, value, "codex", Now) } };
    }

    private static CodexAccountView Spend(string id, decimal used, decimal cap, DateTimeOffset? period = null)
    {
        var account = Account(id, UsageProviderId.Cursor);
        return account with { Snapshot = account.Snapshot with { Windows =
            [new("cursor-on-demand", null, null, period ?? Now.AddMonths(1), CodexWindowKind.Other)
                { UsedAmount = used, LimitAmount = cap, Unit = "USD", AmountObservedAt = Now }] } };
    }

    private static CodexAccountView Claude(string id, decimal used, decimal cap)
    {
        var account = Account(id, UsageProviderId.Claude);
        return account with { Snapshot = account.Snapshot with { ExtraUsage = new(true, used, cap, false, "USD", null, Now) } };
    }

    private static ClaudeResetCreditGrant Grant(int left, DateTimeOffset? start, DateTimeOffset? end) =>
        new(left, Math.Max(left, 2), start, end, false, null, null, ["five_hour", "seven_day"]);
}
