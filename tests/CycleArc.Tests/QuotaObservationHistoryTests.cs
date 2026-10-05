using System.Collections.Immutable;
using CycleArc.Codex;
using CycleArc.Observations;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class QuotaObservationHistoryTests
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    internal static QuotaObservationContext Context(UsageProviderId provider = UsageProviderId.Codex,
        string? binding = null, string profile = "default") => new(profile, provider, binding ?? new string('a', 64));
    internal static CodexQuotaWindow Window(double? value = 20, DateTimeOffset? reset = null,
        int? duration = 300, CodexWindowKind kind = CodexWindowKind.FiveHour, string id = "codex") =>
        new(id, value, duration, reset ?? Start.AddHours(5), kind);
    internal static CodexQuotaSnapshot Quota(DateTimeOffset at, CodexQuotaWindow? window = null,
        UsageProviderId provider = UsageProviderId.Codex, CodexQuotaStatus status = CodexQuotaStatus.Available) =>
        new(status, null, at, at, null, null, null, [window ?? Window()], null) { Provider = provider };
    private static QuotaObservationSeries Percent(QuotaObservationHistory history) =>
        history.Snapshot.Series.Single(s => s.Metric == QuotaObservationMetric.UsedPercent);

    [Fact]
    public void ZeroIsAnObservedValueWithOriginalAndAcceptanceTimes()
    {
        var clock = new MutableClock(Start.AddSeconds(10));
        var history = new QuotaObservationHistory(Context(), clock);
        Assert.True(history.Observe(Quota(Start, Window(0)), clock.UtcNow));
        var point = Percent(history).Points.Single();
        Assert.Equal(0m, point.Value);
        Assert.Equal(Start, point.ObservedAt);
        Assert.Equal(clock.UtcNow, point.ReceivedAt);
        Assert.Equal("%", Percent(history).Unit);
    }

    [Fact]
    public void RerenderReplayConflictAndReverseDeliveryDoNotCreatePointsOrNewReferences()
    {
        var clock = new MutableClock(Start.AddMinutes(2));
        var history = new QuotaObservationHistory(Context(), clock);
        var quota = Quota(Start.AddMinutes(1));
        history.Observe(quota, clock.UtcNow);
        var accepted = history.Snapshot;
        Assert.False(history.Observe(quota with { Windows = [Window()] }, clock.UtcNow));
        Assert.False(history.Observe(Quota(Start.AddMinutes(1), Window(90)), clock.UtcNow));
        Assert.False(history.Observe(Quota(Start, Window(10)), clock.UtcNow));
        Assert.Same(accepted, history.Snapshot);
        Assert.Single(Percent(history).Points);
    }

    [Fact]
    public void SeededCacheWatermarkNeverBecomesANewObservation()
    {
        var clock = new MutableClock(Start.AddMinutes(2));
        var history = new QuotaObservationHistory(Context(), clock);
        history.SeedInitialSnapshot(Quota(Start));
        Assert.Empty(Percent(history).Points);
        history.Observe(Quota(Start), clock.UtcNow);
        Assert.Empty(Percent(history).Points);
        history.Observe(Quota(Start.AddMinutes(1)), clock.UtcNow);
        Assert.Single(Percent(history).Points);
    }

    [Fact]
    public void RefreshingIsNotAGapButStaleFailureAndUnknownAreGaps()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        history.Observe(Quota(Start, Window(10)), clock.UtcNow);
        var first = Percent(history).Points.Single().SegmentId;
        Assert.False(history.Observe(Quota(Start).AsRefreshing(), clock.UtcNow));
        clock.UtcNow = Start.AddMinutes(1);
        history.Observe(Quota(clock.UtcNow, Window(20)), clock.UtcNow);
        Assert.Equal(first, Percent(history).Points[^1].SegmentId);
        history.Observe(Quota(clock.UtcNow, status: CodexQuotaStatus.Stale), clock.UtcNow);
        Assert.Equal(2, Percent(history).Points.Length);
        clock.UtcNow = Start.AddMinutes(2);
        history.Observe(Quota(clock.UtcNow, Window(30)), clock.UtcNow);
        Assert.NotEqual(first, Percent(history).Points[^1].SegmentId);
        var beforeUnknown = Percent(history).Points[^1].SegmentId;
        clock.UtcNow = Start.AddMinutes(3);
        history.Observe(Quota(clock.UtcNow, Window(null)), clock.UtcNow);
        Assert.Equal(3, Percent(history).Points.Length);
        clock.UtcNow = Start.AddMinutes(4);
        history.Observe(Quota(clock.UtcNow, Window(40)), clock.UtcNow);
        Assert.NotEqual(beforeUnknown, Percent(history).Points[^1].SegmentId);
    }

    [Fact]
    public void MissingWindowAndDecreasingUsageSplitSegments()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        history.Observe(Quota(Start, Window(50)), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(1);
        history.Observe(Quota(clock.UtcNow) with { Windows = [] }, clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(2);
        history.Observe(Quota(clock.UtcNow, Window(60)), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(3);
        history.Observe(Quota(clock.UtcNow, Window(10)), clock.UtcNow);
        Assert.Equal(3, Percent(history).Points.Select(p => p.SegmentId).Distinct().Count());
    }

    [Fact]
    public void ResetAndUnknownResetNeverConnectAcrossAllowanceWindows()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        history.Observe(Quota(Start), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(1);
        var newWindow = Window(30, Start.AddHours(10));
        history.Observe(Quota(clock.UtcNow, newWindow), clock.UtcNow);
        Assert.NotEqual(Percent(history).Points[0].SegmentId, Percent(history).Points[1].SegmentId);
        Assert.Single(history.Snapshot.GetCurrentWindow(newWindow).Points);
        Assert.Same(history.Snapshot.GetCurrentWindow(newWindow), history.Snapshot.GetCurrentWindow(newWindow));
        var unknown = Window(40) with { ResetsAt = null };
        clock.UtcNow = Start.AddMinutes(2);
        history.Observe(Quota(clock.UtcNow, unknown), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(3);
        history.Observe(Quota(clock.UtcNow, unknown with { UsedPercent = 50 }), clock.UtcNow);
        var points = history.Snapshot.GetCurrentWindow(unknown).Points;
        Assert.Equal(2, points.Length);
        Assert.All(points, p => Assert.False(p.CanConnect));
        Assert.NotEqual(points[0].SegmentId, points[1].SegmentId);
    }

    [Fact]
    public void SharedLimitIdDoesNotMergeFiveHourAndWeeklyAllowances()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        var week = Window(80, Start.AddDays(7), 10080, CodexWindowKind.Weekly);
        history.Observe(Quota(Start) with { Windows = [Window(10), week] }, clock.UtcNow);
        Assert.Equal(2, history.Snapshot.Series.Length);
        Assert.Equal(80m, history.Snapshot.GetCurrentWindow(week).Points.Single().Value);
        Assert.Equal(10m, history.Snapshot.GetCurrentWindow(Window()).Points.Single().Value);
    }

    [Fact]
    public void NullLimitIdCurrentWindowIsCachedAndDistinctFromLiteralAndOtherDurations()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        var five = Window(20) with { LimitId = null };
        var week = Window(70, Start.AddDays(7), 10080, CodexWindowKind.Weekly) with { LimitId = null };
        var named = Window(90, id: "unspecified");
        history.Observe(Quota(Start) with { Windows = [five, week, named] }, clock.UtcNow);
        Assert.Equal(3, history.Snapshot.Series.Length);
        Assert.Equal(2, history.Snapshot.Series.Count(s => s.LimitId is null));
        var fiveView = history.Snapshot.GetCurrentWindow(five);
        Assert.Equal(20m, fiveView.Points.Single().Value);
        Assert.Same(fiveView, history.Snapshot.GetCurrentWindow(five with { UsedPercent = 99 }));
        Assert.Equal(70m, history.Snapshot.GetCurrentWindow(week).Points.Single().Value);
        Assert.Equal(90m, history.Snapshot.GetCurrentWindow(named).Points.Single().Value);
    }

    [Fact]
    public void MoneyMetricsUnitsAndBasisRemainSeparateAndRetainedMoneyDoesNotAdvance()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(UsageProviderId.Cursor), clock);
        var money = Window(20, id: "cursor-api") with
        { UsedAmount = 4m, RemainingAmount = 16m, LimitAmount = 20m, Unit = "USD", AmountObservedAt = Start };
        history.Observe(Quota(Start, money, UsageProviderId.Cursor), clock.UtcNow);
        Assert.Equal(3, history.Snapshot.Series.Length);
        clock.UtcNow = Start.AddMinutes(1);
        history.Observe(Quota(clock.UtcNow, money with { UsedPercent = 30, AmountFailure = "cursor-on-demand-unavailable" }, UsageProviderId.Cursor), clock.UtcNow);
        Assert.Single(history.Snapshot.GetCurrentWindow(money, QuotaObservationMetric.UsedAmount).Points);
        clock.UtcNow = Start.AddMinutes(2);
        var changedBasis = money with { UsedAmount = 5, RemainingAmount = 35, LimitAmount = 40, AmountObservedAt = clock.UtcNow };
        history.Observe(Quota(clock.UtcNow, changedBasis, UsageProviderId.Cursor), clock.UtcNow);
        Assert.Single(history.Snapshot.GetCurrentWindow(changedBasis, QuotaObservationMetric.UsedAmount).Points);
        clock.UtcNow = Start.AddMinutes(3);
        var euro = changedBasis with { Unit = "EUR", AmountObservedAt = clock.UtcNow };
        history.Observe(Quota(clock.UtcNow, euro, UsageProviderId.Cursor), clock.UtcNow);
        Assert.Contains(history.Snapshot.Series, s => s.Unit == "USD" && s.Metric == QuotaObservationMetric.UsedAmount);
        Assert.Contains(history.Snapshot.Series, s => s.Unit == "EUR" && s.Metric == QuotaObservationMetric.UsedAmount);
        Assert.Equal("%", history.Snapshot.GetCurrentWindow(euro).Unit);
        var percentPoints = history.Snapshot.Series.Single(s => s.Metric == QuotaObservationMetric.UsedPercent).Points;
        Assert.NotEqual(percentPoints[^2].SegmentId, percentPoints[^1].SegmentId);
    }

    [Fact]
    public void ProviderProfileAndBindingScopesCannotMerge()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        Assert.False(history.Observe(Quota(Start, provider: UsageProviderId.Claude), clock.UtcNow));
        var other = new QuotaObservationHistory(Context(binding: new string('b', 64)), clock);
        other.Observe(Quota(Start), clock.UtcNow);
        history.MergeLoaded(other.Snapshot);
        Assert.Empty(history.Snapshot.Series);
        history.MergeLoaded(other.Snapshot with { Context = Context(profile: Guid.NewGuid().ToString("N")) });
        Assert.Empty(history.Snapshot.Series);
    }

    [Fact]
    public void DelayedLoadPreservesLivePointsAndBreaksStartupContinuity()
    {
        var clock = new MutableClock(Start);
        var old = new QuotaObservationHistory(Context(), clock);
        old.Observe(Quota(Start, Window(10)), clock.UtcNow);
        var history = new QuotaObservationHistory(Context(), clock);
        history.SeedInitialSnapshot(Quota(Start));
        clock.UtcNow = Start.AddMinutes(1);
        history.Observe(Quota(clock.UtcNow, Window(20)), clock.UtcNow);
        history.MergeLoaded(old.Snapshot);
        Assert.Equal(2, Percent(history).Points.Length);
        Assert.NotEqual(Percent(history).Points[0].SegmentId, Percent(history).Points[1].SegmentId);
        var restart = new QuotaObservationHistory(Context(), clock);
        restart.MergeLoaded(history.Snapshot);
        clock.UtcNow = Start.AddMinutes(2);
        restart.Observe(Quota(clock.UtcNow, Window(30)), clock.UtcNow);
        Assert.NotEqual(Percent(restart).Points[1].SegmentId, Percent(restart).Points[2].SegmentId);
    }

    [Fact]
    public void RetentionAndPerLimitCapSpanResetWindows()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        for (var i = 0; i < QuotaObservationHistory.MaxPointsPerLimit + 3; i++)
        {
            clock.UtcNow = Start.AddSeconds(i);
            history.Observe(Quota(clock.UtcNow, Window(i % 100, Start.AddHours(5 + i / 100))), clock.UtcNow);
        }
        Assert.Equal(QuotaObservationHistory.MaxPointsPerLimit, Percent(history).Points.Length);
        Assert.Equal(Start.AddSeconds(3), Percent(history).Points[0].ObservedAt);
        clock.UtcNow = Start.AddDays(8);
        history.Observe(Quota(clock.UtcNow, Window(0, clock.UtcNow.AddHours(5))), clock.UtcNow);
        Assert.Single(Percent(history).Points);
    }

    [Fact]
    public void MergeReplayedTimestampPreservesItsFirstAcceptanceAndHandlesNewerDiskWatermark()
    {
        var clock = new MutableClock(Start);
        var saved = new QuotaObservationHistory(Context(), clock);
        saved.Observe(Quota(Start, Window(10)), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(2);
        saved.Observe(Quota(clock.UtcNow, Window(30)), clock.UtcNow);
        var live = new QuotaObservationHistory(Context(), clock);
        live.Observe(Quota(Start, Window(10)), clock.UtcNow);
        live.Observe(Quota(Start.AddMinutes(1), Window(20)), clock.UtcNow);
        live.MergeLoaded(saved.Snapshot);
        Assert.Equal(3, Percent(live).Points.Length);
        Assert.Equal(Start, Percent(live).Points[0].ReceivedAt);
        Assert.True(QuotaObservationHistory.ValidSnapshot(live.Snapshot, Context(), clock.UtcNow));
        Assert.False(live.Observe(Quota(Start.AddMinutes(1), Window(20)), clock.UtcNow));
    }

    [Fact]
    public void RemainingAmountsFallWithinASegmentAndRiseStartsANewSegment()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(UsageProviderId.Cursor), clock);
        var window = Window(null, id: "cursor-on-demand") with
            { Unit = "USD", LimitAmount = 100, RemainingAmount = 50, AmountObservedAt = Start };
        history.Observe(Quota(Start, window, UsageProviderId.Cursor), clock.UtcNow);
        clock.UtcNow = Start.AddMinutes(1);
        window = window with { RemainingAmount = 41, AmountObservedAt = clock.UtcNow };
        history.Observe(Quota(clock.UtcNow, window, UsageProviderId.Cursor), clock.UtcNow);
        var contiguous = history.Snapshot.Series.Single(s => s.Metric == QuotaObservationMetric.RemainingAmount).Points;
        Assert.Equal(contiguous[0].SegmentId, contiguous[1].SegmentId);
        Assert.Equal(new[] { 50m, 41m }, contiguous.Select(p => p.Value));
        var restarted = new QuotaObservationHistory(history.Snapshot.Context, clock);
        restarted.MergeLoaded(history.Snapshot);
        var merged = restarted.Snapshot.Series.Single(s => s.Metric == QuotaObservationMetric.RemainingAmount).Points;
        Assert.Equal(merged[0].SegmentId, merged[1].SegmentId);
        Assert.True(QuotaObservationHistory.ValidSnapshot(restarted.Snapshot, history.Snapshot.Context, clock.UtcNow));
        clock.UtcNow = Start.AddMinutes(2);
        window = window with { RemainingAmount = 50, AmountObservedAt = clock.UtcNow };
        history.Observe(Quota(clock.UtcNow, window, UsageProviderId.Cursor), clock.UtcNow);
        var refilled = history.Snapshot.Series.Single(s => s.Metric == QuotaObservationMetric.RemainingAmount).Points;
        Assert.NotEqual(refilled[1].SegmentId, refilled[2].SegmentId);
        Assert.True(QuotaObservationHistory.ValidSnapshot(history.Snapshot, history.Snapshot.Context, clock.UtcNow));
    }

    [Fact]
    public void SeriesCapAndMalformedValuesAreBoundedWithoutInventedZeros()
    {
        var clock = new MutableClock(Start);
        var history = new QuotaObservationHistory(Context(), clock);
        var windows = Enumerable.Range(0, 100).Select(i => Window(20, id: "limit-" + i)).ToArray();
        history.Observe(Quota(Start) with { Windows = windows }, clock.UtcNow);
        Assert.Equal(QuotaObservationHistory.MaxSeries, history.Snapshot.Series.Length);
        var invalid = new QuotaObservationHistory(Context(), clock);
        invalid.Observe(Quota(Start, Window(double.NaN)), clock.UtcNow);
        Assert.Empty(Percent(invalid).Points);
        var reverse = new QuotaObservationHistory(Context(), clock);
        Assert.False(reverse.Observe(Quota(Start.AddDays(1)), clock.UtcNow));
    }
}
