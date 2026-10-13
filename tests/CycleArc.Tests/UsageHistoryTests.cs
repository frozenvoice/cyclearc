using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Monitoring;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public sealed class UsageHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CycleArc-history-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private UsageHistoryStore Store() => new(_root);

    [Fact]
    public void CacheRereadsAndRapidUpdatesDoNotWriteOnPassiveLoop()
    {
        var store = Store();
        store.Record([Account(Now)], Now);
        var path = Primary();
        var original = File.ReadAllBytes(path);
        for (var i = 1; i <= 29; i++) store.Record([Account(Now)], Now.AddSeconds(i * 2));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(store.Read(Account(Now), TimeSpan.FromDays(1), Now.AddMinutes(1)).Points);
        store.Record([Account(Now.AddSeconds(10), percent: 40)], Now.AddSeconds(10));
        Assert.Equal(original, File.ReadAllBytes(path));
        store.Record([Account(Now.AddMinutes(1), percent: 45)], Now.AddMinutes(1));
        Assert.NotEqual(original, File.ReadAllBytes(path));
        Assert.Equal(45m, store.Read(Account(Now), TimeSpan.FromDays(1), Now.AddMinutes(1)).Points.Last().Value);
    }

    [Fact]
    public void UnchangedObservationsHaveMeasuredHeartbeatAndNeverInventHistory()
    {
        var store = Store();
        store.Record([Account(Now)], Now);
        store.Record([Account(Now.AddMinutes(2))], Now.AddMinutes(2));
        Assert.Single(store.Read(Account(Now), TimeSpan.FromDays(30), Now.AddMinutes(2)).Points);
        store.Record([Account(Now.AddMinutes(15))], Now.AddMinutes(15));
        var view = store.Read(Account(Now), TimeSpan.FromDays(30), Now.AddMinutes(15));
        Assert.Equal(2, view.Points.Count);
        Assert.Equal(Now, view.ObservationStartedAt);
        Assert.All(view.Points, p => Assert.True(p.ObservedAt >= Now));
    }

    [Fact]
    public void ProviderProfileAndIdentityNeverShareHistory()
    {
        var store = Store();
        var original = Account(Now);
        store.Record([original], Now);
        var changed = original with { Snapshot = original.Snapshot with { IdentityFingerprint = new string('B', 64) } };
        Assert.Empty(store.Read(changed, TimeSpan.FromDays(30), Now).Points);
        Assert.Empty(store.Read(original with { Profile = original.Profile with { Id = "different" } }, TimeSpan.FromDays(30), Now).Points);
        Assert.Empty(store.Read(original with { IsConnected = false }, TimeSpan.FromDays(30), Now).Points);
        var mismatch = original with { Profile = original.Profile with { Provider = UsageProviderId.Claude } };
        Assert.Empty(store.Read(mismatch, TimeSpan.FromDays(30), Now).Points);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "usage-history"), "*.json"));
        Assert.DoesNotContain("my-account", File.ReadAllText(Primary()));
        Assert.DoesNotContain("user@example.com", File.ReadAllText(Primary()));
    }

    [Fact]
    public void MissingStaleFailureAndRefreshingNeverRecordFreshValues()
    {
        var store = Store();
        var good = Account(Now);
        store.Record([good], Now);
        var failure = good with { Snapshot = good.Snapshot with { Status = CodexQuotaStatus.Stale, LastAttemptedRefresh = Now.AddMinutes(2) } };
        store.Record([failure], Now.AddMinutes(2));
        var view = store.Read(failure, TimeSpan.FromDays(1), Now.AddMinutes(2));
        Assert.Single(view.Points);
        Assert.Contains(view.Statuses, s => s.Quality == UsageHistoryQuality.Failure && s.At == Now.AddMinutes(2));
        var freshStore = new UsageHistoryStore(Path.Combine(_root, "fresh"));
        freshStore.Record([good], Now.AddHours(1));
        Assert.Empty(freshStore.Read(good, TimeSpan.FromDays(1), Now.AddHours(1)).Points);
        Assert.Contains(freshStore.Read(good, TimeSpan.FromDays(1), Now.AddHours(1)).Statuses, s => s.Quality == UsageHistoryQuality.Stale);
        var unknown = good with { Snapshot = good.Snapshot with { LastSuccessfulRefresh = null, Windows = [] } };
        freshStore.Record([unknown], Now.AddHours(2));
        Assert.Contains(freshStore.Read(unknown, TimeSpan.FromDays(1), Now.AddHours(2)).Statuses, s => s.Quality == UsageHistoryQuality.Missing);
        freshStore.Record([good with { Snapshot = good.Snapshot.AsRefreshing() }], Now);
        Assert.Empty(freshStore.Read(good, TimeSpan.FromDays(1), Now).Points);
    }

    [Fact]
    public void CreditsRemainSignedUnitsAndChangesAreNeverActualSpend()
    {
        var store = Store();
        var one = Account(Now, credits: 5.5m);
        var two = Account(Now.AddMinutes(1), credits: -2.25m);
        store.Record([one], Now);
        store.Record([two], Now.AddMinutes(1));
        var view = store.Read(two, TimeSpan.FromDays(30), Now.AddMinutes(1));
        Assert.Equal(-2.25m, view.Points.Last(p => p.Metric == UsageHistoryMetric.CreditBalance).Value);
        Assert.Equal(-7.75m, Assert.Single(view.Points, p => p.Metric == UsageHistoryMetric.BalanceChange).Value);
        Assert.DoesNotContain(view.Points, p => p.Metric == UsageHistoryMetric.ActualSpend);
        Assert.All(view.Points.Where(p => p.Metric != UsageHistoryMetric.UsagePercent), p => Assert.Equal("credits", p.Unit));
    }

    [Fact]
    public void RapidBalanceChangesCoalesceToTheSameMeasuredDeltaBaseline()
    {
        var store = Store();
        store.Record([Account(Now, credits: 100)], Now);
        store.Record([Account(Now.AddMinutes(1), credits: 90)], Now.AddMinutes(1));
        store.Record([Account(Now.AddMinutes(1).AddSeconds(5), credits: 80)], Now.AddMinutes(1).AddSeconds(5));
        var view = store.Read(Account(Now), TimeSpan.FromDays(1), Now.AddMinutes(2));
        Assert.Equal(80, view.Points.Last(p => p.Metric == UsageHistoryMetric.CreditBalance).Value);
        Assert.Equal(-20, Assert.Single(view.Points, p => p.Metric == UsageHistoryMetric.BalanceChange).Value);
    }

    [Fact]
    public void StaleMoneyDoesNotInheritFreshSubscriptionTimestamp()
    {
        var store = Store();
        var account = Account(Now, provider: UsageProviderId.Claude);
        account = account with { Snapshot = account.Snapshot with
            { ExtraUsage = new(true, 4, 20, false, "USD", 20, Now.AddHours(-1)) } };
        store.Record([account], Now);
        var view = store.Read(account, TimeSpan.FromDays(1), Now);
        Assert.Single(view.Points);
        Assert.Contains(view.Statuses, s => s.Quality == UsageHistoryQuality.Stale && s.SeriesId == "extra"
            && s.LastObservedAt == Now.AddHours(-1));
    }

    [Fact]
    public void MoneyHasIndependentSourceTimeMissingAndFailureRules()
    {
        var store = Store();
        var account = Account(Now, provider: UsageProviderId.Claude) with
        { Snapshot = Account(Now, provider: UsageProviderId.Claude).Snapshot with
            { ExtraUsage = new(true, 4.2m, 20m, false, "USD", 21, Now.AddMinutes(-5)) } };
        store.Record([account], Now);
        var money = store.Read(account, TimeSpan.FromDays(1), Now).Points.Where(p => p.Metric != UsageHistoryMetric.UsagePercent).ToArray();
        Assert.Equal(3, money.Length);
        Assert.All(money, p => Assert.Equal(Now.AddMinutes(-5), p.ObservedAt));
        Assert.Equal(15.8m, money.Single(p => p.Metric == UsageHistoryMetric.RemainingAmount).Value);
        var failed = account with { Snapshot = account.Snapshot with { ExtraUsageFailure = "not-persisted", LastAttemptedRefresh = Now.AddMinutes(1) } };
        store.Record([failed], Now.AddMinutes(1));
        Assert.Equal(3, store.Read(failed, TimeSpan.FromDays(1), Now.AddMinutes(1)).Points.Count(p => p.Metric != UsageHistoryMetric.UsagePercent));
        Assert.DoesNotContain("not-persisted", File.ReadAllText(Primary()));
        var unlimited = account with { Snapshot = account.Snapshot with { ExtraUsage = new(true, 5m, null, true, "USD", null, Now.AddMinutes(2)), LastSuccessfulRefresh = Now.AddMinutes(2) } };
        store.Record([unlimited], Now.AddMinutes(2));
        Assert.DoesNotContain(store.Read(unlimited, TimeSpan.FromDays(1), Now.AddMinutes(2)).Points,
            p => p.ObservedAt == Now.AddMinutes(2) && p.Metric == UsageHistoryMetric.SpendingLimit);
    }

    [Fact]
    public void CursorIncludedAndOnDemandAmountsRemainDifferentMetrics()
    {
        var store = Store();
        var account = Account(Now, provider: UsageProviderId.Cursor);
        account = account with { Snapshot = account.Snapshot with { Windows = [
            new("cursor-auto", 25, null, Now.AddDays(10), CodexWindowKind.Other) { UsedAmount = 4, LimitAmount = 16, Unit = "USD", AmountObservedAt = Now },
            new("cursor-on-demand", null, null, Now.AddDays(10), CodexWindowKind.Other) { UsedAmount = 7, LimitAmount = 20, RemainingAmount = 13, Unit = "USD", AmountObservedAt = Now }
        ] } };
        store.Record([account], Now);
        var view = store.Read(account, TimeSpan.FromDays(1), Now);
        Assert.Equal(4m, view.Points.Single(p => p.Metric == UsageHistoryMetric.ReportedUsageAmount).Value);
        Assert.Equal(7m, view.Points.Single(p => p.Metric == UsageHistoryMetric.ActualSpend).Value);
        Assert.DoesNotContain(view.Points, p => p.SeriesId == "cursor-on-demand" && p.Metric == UsageHistoryMetric.UsagePercent);
    }

    [Fact]
    public void PeriodLimitSourceAndCumulativeDropMarkDiscontinuities()
    {
        var store = Store();
        var one = Account(Now, provider: UsageProviderId.Cursor);
        var end = Now.AddDays(10);
        one = one with { Snapshot = one.Snapshot with { Windows = [new("cursor-on-demand", 80, null, end, CodexWindowKind.Other) { UsedAmount = 8, LimitAmount = 10, Unit = "USD" }] } };
        store.Record([one], Now);
        var two = one with { Snapshot = one.Snapshot with { LastSuccessfulRefresh = Now.AddMinutes(1), Windows = [new("cursor-on-demand", 5, null, end.AddMonths(1), CodexWindowKind.Other) { UsedAmount = 1, LimitAmount = 20, Unit = "USD" }] } };
        store.Record([two], Now.AddMinutes(1));
        var newPoints = store.Read(two, TimeSpan.FromDays(1), Now.AddMinutes(1)).Points.Where(p => p.ObservedAt > Now).ToArray();
        Assert.All(newPoints, p => Assert.True(p.Discontinuity.HasFlag(UsageHistoryDiscontinuity.PeriodChanged)));
        Assert.True(newPoints.Single(p => p.Metric == UsageHistoryMetric.SpendingLimit).Discontinuity.HasFlag(UsageHistoryDiscontinuity.LimitChanged));
        Assert.True(newPoints.Single(p => p.Metric == UsageHistoryMetric.ActualSpend).Discontinuity.HasFlag(UsageHistoryDiscontinuity.CumulativeDecreased));
        var claude = Account(Now, provider: UsageProviderId.Claude) with { Snapshot = Account(Now, provider: UsageProviderId.Claude).Snapshot with { LastSuccessfulObservationWasServer = false } };
        store.Record([claude], Now);
        var live = claude with { Snapshot = claude.Snapshot with { LastSuccessfulRefresh = Now.AddMinutes(1), LastSuccessfulObservationWasServer = true } };
        store.Record([live], Now.AddMinutes(1));
        Assert.True(store.Read(live, TimeSpan.FromDays(1), Now.AddMinutes(1)).Points.Last().Discontinuity.HasFlag(UsageHistoryDiscontinuity.SourceChanged));
    }

    [Fact]
    public void CorruptOversizedOrWrongKeyPrimaryRecoversValidatedBackup()
    {
        var store = Store();
        store.Record([Account(Now)], Now);
        store.Record([Account(Now.AddMinutes(1), percent: 40)], Now.AddMinutes(1));
        var path = Primary();
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "{broken");
        var recovered = Store().Read(Account(Now), TimeSpan.FromDays(30), Now.AddMinutes(1));
        Assert.Single(recovered.Points);
        Assert.Equal("history-backup-recovered", recovered.StorageWarning);
        File.WriteAllBytes(path, new byte[UsageHistoryStore.MaximumFileBytes + 1]);
        Assert.Single(Store().Read(Account(Now), TimeSpan.FromDays(30), Now.AddMinutes(1)).Points);
        File.WriteAllText(path + ".bak", "{}");
        var invalid = Store().Read(Account(Now), TimeSpan.FromDays(30), Now.AddMinutes(1));
        Assert.Empty(invalid.Points);
        Assert.Equal("history-unavailable", invalid.StorageWarning);
    }

    [Fact]
    public void FutureNullAndSecretBearingMalformedStoredRecordsAreRejected()
    {
        Store().Record([Account(Now)], Now);
        var path = Primary();
        var json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Points"]![0]!["ObservedAt"] = Now.AddDays(1).ToString("O");
        File.WriteAllText(path, node.ToJsonString());
        Assert.Empty(Store().Read(Account(Now), TimeSpan.FromDays(30), Now).Points);
        node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Points"]![0]!["Unit"] = "token-secret";
        File.WriteAllText(path, node.ToJsonString());
        Assert.Empty(Store().Read(Account(Now), TimeSpan.FromDays(30), Now).Points);
        node["Points"]![0]!["Label"] = null;
        File.WriteAllText(path, node.ToJsonString());
        Assert.Empty(Store().Read(Account(Now), TimeSpan.FromDays(30), Now).Points);
    }

    [Fact]
    public void RetentionAndQueriesReturnOnlyObservedIntervals()
    {
        var store = Store();
        store.Record([Account(Now.AddDays(-32))], Now.AddDays(-32));
        store.Record([Account(Now.AddDays(-8))], Now.AddDays(-8));
        store.Record([Account(Now.AddDays(-2))], Now.AddDays(-2));
        store.Record([Account(Now)], Now);
        Assert.Single(store.Read(Account(Now), TimeSpan.FromHours(24), Now).Points);
        Assert.Equal(2, store.Read(Account(Now), TimeSpan.FromDays(7), Now).Points.Count);
        Assert.Equal(3, store.Read(Account(Now), TimeSpan.FromDays(30), Now).Points.Count);
        Assert.True(new FileInfo(Primary()).Length <= UsageHistoryStore.MaximumFileBytes);
        Assert.DoesNotContain(store.Read(Account(Now), TimeSpan.FromDays(100), Now).Points, p => p.ObservedAt < Now.AddDays(-31));
    }

    [Fact]
    public void OldMeasurementsBecomeSparseWithoutInventingOrErasingObservationStart()
    {
        var store = Store();
        var first = Now.AddDays(-10);
        for (var i = 0; i < 120; i++)
            store.Record([Account(first.AddMinutes(i), percent: 20 + i)], first.AddMinutes(i));
        store.Record([Account(Now)], Now);
        var view = store.Read(Account(Now), TimeSpan.FromDays(30), Now);
        Assert.Equal(first, view.ObservationStartedAt);
        Assert.InRange(view.Points.Count, 3, 6);
        Assert.All(view.Points.Where(p => p.ObservedAt < Now), p =>
            Assert.Equal((decimal)(20 + (p.ObservedAt - first).TotalMinutes), p.Value));
        Assert.Contains(view.Points, p => p.ObservedAt == first.AddMinutes(119));
        Assert.True(view.Points.Last().Discontinuity.HasFlag(UsageHistoryDiscontinuity.Gap));
    }

    [Theory]
    [InlineData("claude-identity-mismatch")]
    [InlineData("claude-live-identity-mismatch")]
    [InlineData("cursor-live-identity-mismatch")]
    [InlineData("codex-identity-conflict")]
    public void HiddenIdentityFailuresNeverExposePreviouslyBoundHistory(string detail)
    {
        var store = Store();
        var account = Account(Now);
        store.Record([account], Now);
        var hidden = account with { Snapshot = account.Snapshot with { TechnicalDetail = detail } };
        store.Record([hidden], Now.AddMinutes(1));
        var view = store.Read(hidden, TimeSpan.FromDays(30), Now.AddMinutes(1));
        Assert.Empty(view.Points);
        Assert.Equal(UsageHistoryQuality.IdentityUnavailable, Assert.Single(view.Statuses).Quality);
    }

    [Fact]
    public void FailedDiskWritesAreReportedWithoutLosingMeasuredInMemoryData()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "a-file");
        File.WriteAllText(blocked, "not-a-directory");
        var store = new UsageHistoryStore(blocked);
        store.Record([Account(Now)], Now);
        var view = store.Read(Account(Now), TimeSpan.FromDays(1), Now);
        Assert.Single(view.Points);
        Assert.Equal("history-write-unavailable", view.StorageWarning);
    }

    [Fact]
    public void CreditFailureAndRecoveryUseTheExactBalanceSeriesAndMarkGap()
    {
        var store = Store();
        var initial = Account(Now, credits: 100);
        store.Record([initial], Now);
        var failed = Account(Now.AddMinutes(1)) with
            { Snapshot = Account(Now.AddMinutes(1)).Snapshot with { UsageCreditsFailure = "unavailable" } };
        store.Record([failed], Now.AddMinutes(1));
        var recovered = Account(Now.AddMinutes(2), credits: 100);
        store.Record([recovered], Now.AddMinutes(2));
        var view = store.Read(recovered, TimeSpan.FromDays(1), Now.AddMinutes(2));
        var balance = view.Points.Last(point => point.Metric == UsageHistoryMetric.CreditBalance);
        Assert.Equal(Now.AddMinutes(2), balance.ObservedAt);
        Assert.True(balance.Discontinuity.HasFlag(UsageHistoryDiscontinuity.Gap));
        Assert.Contains(view.Statuses, status => status.SeriesId == balance.SeriesId
            && status.Quality == UsageHistoryQuality.Failure && status.At == Now.AddMinutes(1));
        Assert.Contains(view.Statuses, status => status.SeriesId == balance.SeriesId
            && status.Quality == UsageHistoryQuality.Observed && status.At == Now.AddMinutes(2));
    }

    [Fact]
    public void UnrepresentableBalanceDeltaKeepsExactBalancesAndMarksMissingGap()
    {
        var store = Store();
        store.Record([Account(Now, credits: decimal.MaxValue)], Now);
        var minimum = Account(Now.AddMinutes(1), credits: decimal.MinValue);
        var error = Record.Exception(() => store.Record([minimum], Now.AddMinutes(1)));
        Assert.Null(error);
        var view = store.Read(minimum, TimeSpan.FromDays(1), Now.AddMinutes(1));
        var balances = view.Points.Where(point => point.Metric == UsageHistoryMetric.CreditBalance).ToArray();
        Assert.Equal(decimal.MaxValue, balances[0].Value);
        Assert.Equal(decimal.MinValue, balances[1].Value);
        Assert.True(balances[1].Discontinuity.HasFlag(UsageHistoryDiscontinuity.Gap));
        Assert.DoesNotContain(view.Points, point => point.Metric == UsageHistoryMetric.BalanceChange);
        Assert.Contains(view.Statuses, status => status.SeriesId == balances[1].SeriesId
            && status.At == Now.AddMinutes(1) && status.Quality == UsageHistoryQuality.Missing);
    }

    [Fact]
    public void NineteenAccountEvictionsPreservePendingObservationsAndPerKeyWriteThrottle()
    {
        var store = Store();
        CodexAccountView[] Accounts(DateTimeOffset at, decimal balance) => Enumerable.Range(0, 19)
            .Select(i => Account(at, credits: balance) with { Profile = Account(at).Profile with { Id = "account-" + i } }).ToArray();
        var initial = Accounts(Now, 100);
        store.Record(initial, Now);
        var folder = Path.Combine(_root, "usage-history");
        var original = Directory.GetFiles(folder, "*.json").ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(19, original.Count);
        var changed = Accounts(Now.AddSeconds(10), 99);
        store.Record(changed, Now.AddSeconds(10));
        for (var pass = 0; pass < 2; pass++)
            foreach (var account in changed)
                Assert.Equal(99, store.Read(account, TimeSpan.FromDays(1), Now.AddSeconds(20))
                    .Points.Last(point => point.Metric == UsageHistoryMetric.CreditBalance).Value);
        store.Record(changed, Now.AddSeconds(30));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        store.Record(changed, Now.AddMinutes(1));
        var persisted = Store();
        foreach (var account in changed)
        {
            var point = persisted.Read(account, TimeSpan.FromDays(1), Now.AddMinutes(1))
                .Points.Last(point => point.Metric == UsageHistoryMetric.CreditBalance);
            Assert.Equal(99, point.Value);
            Assert.Equal(Now.AddSeconds(10), point.ObservedAt);
        }
        var committed = Directory.GetFiles(folder, "*.json").ToDictionary(path => path, File.ReadAllBytes);
        var next = Accounts(Now.AddMinutes(1).AddSeconds(10), 98);
        persisted.Record(next, Now.AddMinutes(1).AddSeconds(10));
        foreach (var file in committed) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        persisted.Record(next, Now.AddMinutes(2));
        var final = Store();
        Assert.All(next, account => Assert.Equal(98, final.Read(account, TimeSpan.FromDays(1), Now.AddMinutes(2))
            .Points.Last(point => point.Metric == UsageHistoryMetric.CreditBalance).Value));
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "file");
        var unavailable = new UsageHistoryStore(blocked);
        unavailable.Record(initial, Now);
        Assert.All(initial, account =>
        {
            var view = unavailable.Read(account, TimeSpan.FromDays(1), Now.AddSeconds(10));
            Assert.Equal(100, view.Points.Last(point => point.Metric == UsageHistoryMetric.CreditBalance).Value);
            Assert.Equal("history-write-unavailable", view.StorageWarning);
        });
    }

    private static CodexAccountView Account(DateTimeOffset at, double percent = 25, decimal? credits = null,
        UsageProviderId provider = UsageProviderId.Codex) => new(
        new CodexAccountProfile("my-account", "", "My secret nickname") { Provider = provider },
        new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, at, at, true, null, null,
            [new("five_hour", percent, 300, Now.AddHours(5), CodexWindowKind.FiveHour)], null)
        { Provider = provider, IdentityFingerprint = new string('A', 64),
            UsageCredits = credits is null ? null : new(true, false, credits, "codex", at), LastSuccessfulObservationWasServer = true },
        "user@example.com", HasMatchingIdentity: true) { IsConnected = true };
    private string Primary() => Assert.Single(Directory.GetFiles(Path.Combine(_root, "usage-history"), "*.json"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

