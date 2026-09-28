using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CodexRingPresentationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void KnownPercent_IsAvailableAndClamped(double percent)
    {
        var snapshot = Available(percent);
        var presentation = CodexRingPresentation.From(snapshot);
        Assert.True(presentation.IsAvailable);
        Assert.Equal(Math.Clamp(percent, 0, 100), presentation.UsedPercent);
        Assert.Equal(percent >= 100, presentation.IsDangerLevel);
        Assert.NotEqual("?", presentation.CenterValueText);
    }

    [Fact]
    public void Unavailable_DoesNotFabricateZero()
    {
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
        var presentation = CodexRingPresentation.From(snapshot);
        Assert.False(presentation.IsAvailable);
        Assert.Null(presentation.UsedPercent);
        Assert.Equal("?", presentation.CenterValueText);
        Assert.False(presentation.IsDangerLevel);
    }

    [Fact]
    public void CodexNotFound_DoesNotFabricateZero()
    {
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.CodexNotFound);
        var presentation = CodexRingPresentation.From(snapshot);
        Assert.False(presentation.IsAvailable);
        Assert.Null(presentation.UsedPercent);
    }

    [Fact]
    public void Stale_PreservesLastKnownPercent()
    {
        var snapshot = Available(87).AsStale(DateTimeOffset.UtcNow, "timed-out");
        var presentation = CodexRingPresentation.From(snapshot);
        Assert.Equal(CodexQuotaStatus.Stale, snapshot.Status);
        Assert.True(presentation.IsAvailable);
        Assert.Equal(87, presentation.UsedPercent);
    }

    [Fact]
    public void RingCaption_OmitsProductNameInBothLanguages()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var presentation = CodexRingPresentation.From(Available(10));
            Assert.Equal("Weekly used", presentation.CenterSubLabel);
            UiText.SetLanguage(UiLanguage.Korean);
            var korean = CodexRingPresentation.From(Available(10));
            Assert.Equal("주간 사용", korean.CenterSubLabel);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Available)]
    [InlineData(CodexQuotaStatus.Stale)]
    [InlineData(CodexQuotaStatus.Refreshing)]
    public void UnknownWeeklyLimit_DoesNotHideKnownFiveHourLimit(CodexQuotaStatus status)
    {
        var snapshot = Available(31) with
        {
            Status = status,
            Windows =
            [
                new("codex", null, 10080, null, CodexWindowKind.Weekly),
                new("codex", 42, 300, null, CodexWindowKind.FiveHour)
            ]
        };

        Assert.Equal(CodexWindowKind.FiveHour, snapshot.CompactWindow?.Kind);
        var presentation = CodexRingPresentation.From(snapshot);
        Assert.True(presentation.IsAvailable);
        Assert.Equal(42, presentation.UsedPercent);
        Assert.Equal("42%", presentation.CenterValueText);
        Assert.Contains("42%", CycleArcPresentation.CompactText(snapshot));
        Assert.Contains("42%", CycleArcPresentation.TrayTooltip(snapshot));
        Assert.Null(snapshot.Windows[0].UsedPercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WithoutAKnownPercentage_TheRingStaysUnknown(double? percent)
    {
        var snapshot = Available(31) with
        {
            Windows =
            [
                new("codex", percent, 300, null, CodexWindowKind.FiveHour),
                new("codex", null, 10080, null, CodexWindowKind.Weekly)
            ]
        };

        var presentation = CodexRingPresentation.From(snapshot);
        Assert.False(presentation.IsAvailable);
        Assert.Null(presentation.UsedPercent);
        Assert.Equal("?", presentation.CenterValueText);
    }

    [Fact]
    public void WeeklyLimitArrivesAndDisappears_CompactWindowFollowsAvailablePercentages()
    {
        var fiveHour = new CodexQuotaWindow("codex", 42, 300, null, CodexWindowKind.FiveHour);
        var weekly = new CodexQuotaWindow("codex", 31, 10080, null, CodexWindowKind.Weekly);
        var snapshot = Available(31) with { Windows = [fiveHour] };
        Assert.Same(fiveHour, snapshot.CompactWindow);

        snapshot = snapshot with { Windows = [fiveHour, weekly] };
        Assert.Same(fiveHour, snapshot.CompactWindow);
        Assert.Same(weekly, snapshot.DisplayWindow(UsagePeriodPreference.Weekly));
        Assert.Same(weekly, CodexRingPresentation.From(snapshot, UsagePeriodPreference.Weekly).Window);

        snapshot = snapshot with { Windows = [weekly with { UsedPercent = null }, fiveHour] };
        Assert.Same(fiveHour, snapshot.CompactWindow);

        snapshot = snapshot with { Windows = [weekly] };
        Assert.Same(weekly, snapshot.CompactWindow);
        Assert.DoesNotContain(snapshot.Windows, window => window.Kind == CodexWindowKind.FiveHour);
    }

    [Theory]
    [InlineData(14, 86, "86%")]
    [InlineData(76.91, 23.09, "23.09%")]
    [InlineData(99.6, 0.4, "0.4%")]
    [InlineData(100, 0, "0%")]
    public void RingFillsWithRemainingWhileBandStaysOnUnroundedUsage(double used, double remaining, string text)
    {
        var ring = CodexRingPresentation.FromDetail(Available(used));

        Assert.Equal(remaining, ring.RemainingPercent!.Value, 10);
        Assert.Equal(text, ring.RemainingValueText);
        Assert.Equal(UsageRingBands.From(used), ring.Band);
        Assert.Equal(used >= 100, ring.IsDangerLevel);
        Assert.Equal(UiText.T("Weekly left", "주간 남음"), ring.RemainingSubLabel);
    }

    [Fact]
    public void UnknownOrInvalidUsageNeverBecomesAFullRemainingRing()
    {
        var unknown = CodexRingPresentation.FromDetail(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable));
        Assert.Null(unknown.RemainingPercent);
        Assert.Equal("?", unknown.RemainingValueText);
        Assert.False(RingGeometry.ComputeFillArc(unknown.RemainingPercent, 0, 0, 40).Visible);
        Assert.False(RingGeometry.ComputeFillArc(unknown.RemainingPercent, 0, 0, 40).IsFullCircle);

    }

    [Theory]
    [InlineData(-1)]
    [InlineData(140)]
    public void OutOfRangeUsageDrawsNoRemainingFillAndIsNotExhausted(double used)
    {
        // The clamped usage stays for the tray; the remaining ring agrees with its "?" text.
        var ring = CodexRingPresentation.FromDetail(Available(used));

        Assert.Null(ring.RemainingPercent);
        Assert.Equal("?", ring.RemainingValueText);
        Assert.False(ring.IsExhausted);
        var arc = RingGeometry.ComputeFillArc(ring.RemainingPercent, 0, 0, 40);
        Assert.False(arc.Visible);
        Assert.False(arc.IsFullCircle);
        Assert.Equal(Math.Clamp(used, 0, 100), ring.UsedPercent);
    }

    [Fact]
    public void OnlyAValidFullyUsedLimitIsExhausted()
    {
        Assert.True(CodexRingPresentation.FromDetail(Available(100)).IsExhausted);
        Assert.False(CodexRingPresentation.FromDetail(Available(99.6)).IsExhausted);
    }

    private static CodexQuotaSnapshot Available(double percent) => new(
        CodexQuotaStatus.Available,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        [new CodexQuotaWindow(null, percent, CodexWindowClassifier.WeeklyMinutes, null, CodexWindowKind.Weekly)],
        null);
}
