using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class UsageRingBandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);

    public UsageRingBandTests() => UiText.SetLanguage(UiLanguage.English);

    [Theory]
    [InlineData(0, UsageRingBand.Normal)]
    [InlineData(69.99, UsageRingBand.Normal)]
    [InlineData(69.5, UsageRingBand.Normal)]
    [InlineData(70, UsageRingBand.Caution)]
    [InlineData(84.99, UsageRingBand.Caution)]
    [InlineData(84.6, UsageRingBand.Caution)]
    [InlineData(85, UsageRingBand.NearLimit)]
    [InlineData(99.5, UsageRingBand.NearLimit)]
    [InlineData(99.6, UsageRingBand.NearLimit)]
    [InlineData(99.999, UsageRingBand.NearLimit)]
    [InlineData(100, UsageRingBand.Exhausted)]
    [InlineData(250, UsageRingBand.Exhausted)]
    public void BandUsesTheUnroundedPercentage(double used, UsageRingBand expected) =>
        Assert.Equal(expected, UsageRingBands.From(used));

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void UnknownValuesHaveNoWarningBand(double? used) =>
        Assert.Equal(UsageRingBand.Normal, UsageRingBands.From(used));

    [Theory]
    [InlineData(69.5, UsageRingBand.Normal, false)]
    [InlineData(84.6, UsageRingBand.Caution, false)]
    [InlineData(99.4, UsageRingBand.NearLimit, false)]
    [InlineData(99.5, UsageRingBand.NearLimit, false)]
    [InlineData(99.6, UsageRingBand.NearLimit, false)]
    [InlineData(100, UsageRingBand.Exhausted, true)]
    public void RoundingBoundaryKeepsBandAndExhaustionSeparateOnEveryService(
        double used, UsageRingBand expected, bool exhausted)
    {
        foreach (var provider in Providers())
        {
            var snapshot = Snapshot(provider, Window(provider, used));
            foreach (var ring in new[]
                     {
                         CodexRingPresentation.From(snapshot),
                         CodexRingPresentation.FromDetail(snapshot),
                         WidgetAccountModel.From(Account(provider, snapshot), selected: true,
                             UsagePeriodPreference.Auto, Now).Ring
                     })
            {
                Assert.Equal(expected, ring.Band);
                Assert.Equal(exhausted, ring.IsDangerLevel);
                // Geometry keeps the unrounded value: 99.6% never draws a full circle.
                Assert.Equal(used, ring.UsedPercent);
            }
        }
    }

    [Fact]
    public void SelectionDoesNotChangeTheBand()
    {
        foreach (var provider in Providers())
        {
            var snapshot = Snapshot(provider, Window(provider, 90));
            var selected = WidgetAccountModel.From(Account(provider, snapshot), selected: true,
                UsagePeriodPreference.Auto, Now);
            var other = WidgetAccountModel.From(Account(provider, snapshot), selected: false,
                UsagePeriodPreference.Auto, Now);
            Assert.Equal(UsageRingBand.NearLimit, selected.Ring.Band);
            Assert.Equal(selected.Ring.Band, other.Ring.Band);
            Assert.Equal(UsageRingBands.ArcBrushKey(selected.Ring.Band, selected.IsStale),
                UsageRingBands.ArcBrushKey(other.Ring.Band, other.IsStale));
        }
    }

    [Theory]
    [InlineData(UsagePeriodPreference.Auto, UsageRingBand.Normal)]
    [InlineData(UsagePeriodPreference.FiveHour, UsageRingBand.Normal)]
    [InlineData(UsagePeriodPreference.Weekly, UsageRingBand.NearLimit)]
    public void BandFollowsTheRepresentedLimitNotTheHighestOne(UsagePeriodPreference preference,
        UsageRingBand expected)
    {
        foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Claude })
        {
            var snapshot = Snapshot(provider,
                Window(provider, 40, "five", CodexWindowKind.FiveHour),
                Window(provider, 95, "week", CodexWindowKind.Weekly));
            var ring = CodexRingPresentation.From(snapshot, preference);
            Assert.Equal(expected, ring.Band);
            Assert.Equal(expected == UsageRingBand.Normal ? 40 : 95, ring.UsedPercent);
        }
    }

    [Fact]
    public void CursorBandFollowsItsRepresentedAllowance()
    {
        var snapshot = Snapshot(UsageProviderId.Cursor,
            Window(UsageProviderId.Cursor, 20, "cursor-auto"),
            Window(UsageProviderId.Cursor, 97, "cursor-api"));
        var model = WidgetAccountModel.From(Account(UsageProviderId.Cursor, snapshot), selected: true,
            UsagePeriodPreference.Auto, Now);
        Assert.Equal(UsageRingBands.From(model.Ring.Window?.UsedPercent), model.Ring.Band);
        Assert.Equal(model.Ring.UsedPercent, model.Ring.Window?.UsedPercent);
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Unavailable)]
    [InlineData(CodexQuotaStatus.CodexNotFound)]
    [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.TimedOut)]
    public void UnknownOrSignedOutRingsStayNormalAndUnlabelled(CodexQuotaStatus status)
    {
        foreach (var provider in Providers())
        {
            var snapshot = Snapshot(provider, Window(provider, 99)) with { Status = status };
            var ring = CodexRingPresentation.From(snapshot);
            Assert.False(ring.IsAvailable);
            Assert.Equal(UsageRingBand.Normal, ring.Band);
            Assert.Equal("text", UsageRingBands.WithLabel("text", ring, stale: false));
        }
    }

    [Fact]
    public void StaleColorKeepsPriorityOverEveryBand()
    {
        foreach (var band in Enum.GetValues<UsageRingBand>())
            Assert.Equal("StaleBrush", UsageRingBands.ArcBrushKey(band, stale: true));

        var snapshot = Snapshot(UsageProviderId.Claude, Window(UsageProviderId.Claude, 90))
            .AsStale(Now, "claude-live-request-failed");
        var model = WidgetAccountModel.From(Account(UsageProviderId.Claude, snapshot), selected: false,
            UsagePeriodPreference.Auto, Now);
        Assert.True(model.IsStale);
        Assert.Equal(90, model.Ring.UsedPercent);
        Assert.Equal(UsageRingBand.NearLimit, model.Ring.Band);
        Assert.Equal("StaleBrush", UsageRingBands.ArcBrushKey(model.Ring.Band, model.IsStale));
        Assert.Equal("text", UsageRingBands.WithLabel("text", model.Ring, model.IsStale));
    }

    [Fact]
    public void ArcBrushKeysAndLabels()
    {
        Assert.Equal("AccentBrush", UsageRingBands.ArcBrushKey(UsageRingBand.Normal, false));
        Assert.Equal("RingCautionBrush", UsageRingBands.ArcBrushKey(UsageRingBand.Caution, false));
        Assert.Equal("RingNearLimitBrush", UsageRingBands.ArcBrushKey(UsageRingBand.NearLimit, false));
        Assert.Equal("RingExhaustedBrush", UsageRingBands.ArcBrushKey(UsageRingBand.Exhausted, false));

        var ring = CodexRingPresentation.From(Snapshot(UsageProviderId.Codex, Window(UsageProviderId.Codex, 72)));
        Assert.Equal("Weekly used 72% · Caution", UsageRingBands.WithLabel("Weekly used 72%", ring, false));
        Assert.Equal("", UsageRingBands.Label(UsageRingBand.Normal));
        Assert.Equal("Near limit", UsageRingBands.Label(UsageRingBand.NearLimit));
        Assert.Equal("Limit reached", UsageRingBands.Label(UsageRingBand.Exhausted));
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("주의", UsageRingBands.Label(UsageRingBand.Caution));
            Assert.Equal("소진 임박", UsageRingBands.Label(UsageRingBand.NearLimit));
            Assert.Equal("소진", UsageRingBands.Label(UsageRingBand.Exhausted));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    private static IEnumerable<UsageProviderId> Providers() =>
        [UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor];

    private static CodexAccountView Account(UsageProviderId provider, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile("band", "", "band") { Provider = provider }, snapshot) { IsConnected = true };

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, params CodexQuotaWindow[] windows) =>
        new(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null, windows, null) { Provider = provider };

    private static CodexQuotaWindow Window(UsageProviderId provider, double? used,
        string? limitId = null, CodexWindowKind? kind = null) =>
        new(limitId ?? (provider == UsageProviderId.Cursor ? "cursor-auto" : "five"),
            used,
            provider == UsageProviderId.Cursor ? null
                : kind == CodexWindowKind.Weekly ? CodexWindowClassifier.WeeklyMinutes : CodexWindowClassifier.FiveHourMinutes,
            null,
            provider == UsageProviderId.Cursor ? CodexWindowKind.Other : kind ?? CodexWindowKind.FiveHour)
        {
            IsEnabled = true
        };
}
