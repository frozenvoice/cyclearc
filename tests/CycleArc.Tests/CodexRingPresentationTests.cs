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
