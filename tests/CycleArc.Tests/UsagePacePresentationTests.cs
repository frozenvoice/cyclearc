using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class UsagePacePresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Fact]
    public void FasterThanEvenRunsOutBeforeReset()
    {
        // Three hours into a five-hour window with 90% used: 30% an hour, 10% left lasts 20 minutes.
        var window = FiveHour(90, resetIn: TimeSpan.FromHours(2));
        var pace = UsagePacePresentation.From(Snapshot(window), window, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceTone.RunsOut, pace.Tone);
        Assert.Equal(Now.AddMinutes(20), pace.RunsOutAt);
        Assert.Null(pace.RemainingAtResetPercent);
        Assert.Equal(40, pace.EvenRemainingPercent, 6);
    }

    [Fact]
    public void SlowerThanEvenProjectsWhatIsLeftAtReset()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        var pace = UsagePacePresentation.From(Snapshot(window), window, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceTone.Ahead, pace.Tone);
        Assert.Null(pace.RunsOutAt);
        Assert.Equal(50, pace.RemainingAtResetPercent!.Value, 6);
    }

    [Fact]
    public void LittleLeftAtResetIsTight()
    {
        var window = FiveHour(55, resetIn: TimeSpan.FromHours(2));
        var pace = UsagePacePresentation.From(Snapshot(window), window, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceTone.Tight, pace.Tone);
        Assert.InRange(pace.RemainingAtResetPercent!.Value, 8, 9);
    }

    [Fact]
    public void EvenMarkerMovesWithTheClockButTheProjectionKeepsTheSampleTime()
    {
        // Ten minutes after the sample: the marker uses now, the rate uses the sample.
        var window = FiveHour(90, resetIn: TimeSpan.FromHours(2));
        var snapshot = Snapshot(window);
        var pace = UsagePacePresentation.From(snapshot, window, Now.AddMinutes(10));

        Assert.NotNull(pace);
        Assert.Equal(Now.AddMinutes(20), pace.RunsOutAt);
        Assert.Equal(110.0 / 300 * 100, pace.EvenRemainingPercent, 6);
    }

    [Theory]
    [InlineData(UiLanguage.English, "before reset", "1h 40m")]
    [InlineData(UiLanguage.Korean, "소진 예상 · 리셋", "1시간 40분 전")]
    public void RunsOutTextNamesTheGapBeforeReset(UiLanguage language, string phrase, string gap)
    {
        var previous = UiText.Language;
        UiText.SetLanguage(language);
        try
        {
            var window = FiveHour(90, resetIn: TimeSpan.FromHours(2));
            var pace = UsagePacePresentation.From(Snapshot(window), window, Now)!;
            Assert.Contains(phrase, pace.Text);
            Assert.Contains(gap, pace.Text);
            Assert.Contains("40%", pace.MarkerText);
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Fact]
    public void LiveClaudeSampleHasAPace()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        var snapshot = Snapshot(window) with
        {
            Provider = UsageProviderId.Claude,
            TechnicalDetail = ClaudeUsagePresentation.LiveDetail
        };
        Assert.NotNull(UsagePacePresentation.From(snapshot, window, Now));
    }

    [Fact]
    public void RefreshInProgressKeepsThePaceOfTheSampleItStartedFrom()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        var snapshot = Snapshot(window) with
        {
            Status = CodexQuotaStatus.Refreshing,
            RefreshOriginStatus = CodexQuotaStatus.Available
        };
        Assert.NotNull(UsagePacePresentation.From(snapshot, window, Now));
    }

    [Fact]
    public void DesktopHistoryAndOtherReceiptsHaveNoPace()
    {
        // Desktop history never reports a reset, and a received sample is not a server check.
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        var history = Snapshot(window) with
        {
            Provider = UsageProviderId.Claude,
            TechnicalDetail = "claude-desktop-history"
        };
        Assert.Null(UsagePacePresentation.From(history, window, Now));
    }

    [Fact]
    public void StaleOrFailedSamplesHaveNoPace()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        Assert.Null(UsagePacePresentation.From(Snapshot(window) with { Status = CodexQuotaStatus.Stale }, window, Now));
        Assert.Null(UsagePacePresentation.From(Snapshot(window) with { Status = CodexQuotaStatus.TimedOut }, window, Now));
    }

    [Fact]
    public void CursorAllowancesHaveNoPace()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        Assert.Null(UsagePacePresentation.From(Snapshot(window) with { Provider = UsageProviderId.Cursor }, window, Now));
    }

    [Fact]
    public void UnknownValuesNeverBecomeAPace()
    {
        var known = FiveHour(30, resetIn: TimeSpan.FromHours(2));
        CodexQuotaWindow[] unknown =
        [
            known with { UsedPercent = null },
            known with { WindowDurationMinutes = null },
            known with { ResetsAt = null },
            known with { ResetsAt = Now.AddMinutes(-1) }
        ];
        foreach (var window in unknown)
            Assert.Null(UsagePacePresentation.From(Snapshot(window), window, Now));
        Assert.Null(UsagePacePresentation.From(Snapshot(known), null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void UnusedOrExhaustedLimitsHaveNoPace(double used)
    {
        var window = FiveHour(used, resetIn: TimeSpan.FromHours(2));
        Assert.Null(UsagePacePresentation.From(Snapshot(window), window, Now));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void AYoungWindowIsTooNoisyToProject(int minutesIn, bool projected)
    {
        // A five-hour window needs 30 minutes: 10% of it, and the 30-minute floor.
        var window = FiveHour(20, resetIn: TimeSpan.FromMinutes(300 - minutesIn));
        Assert.Equal(projected, UsagePacePresentation.From(Snapshot(window), window, Now) is not null);
    }

    [Fact]
    public void AWeeklyWindowNeedsATenthOfItsLength()
    {
        var window = new CodexQuotaWindow(null, 20, CodexWindowClassifier.WeeklyMinutes,
            Now + TimeSpan.FromMinutes(CodexWindowClassifier.WeeklyMinutes - 900), CodexWindowKind.Weekly);
        Assert.Null(UsagePacePresentation.From(Snapshot(window), window, Now));
        var later = window with { ResetsAt = Now + TimeSpan.FromMinutes(CodexWindowClassifier.WeeklyMinutes - 1008) };
        Assert.NotNull(UsagePacePresentation.From(Snapshot(later), later, Now));
    }

    [Fact]
    public void UnconfirmedClaudeResetHasNoPace()
    {
        var window = FiveHour(30, resetIn: TimeSpan.FromHours(2)) with
        {
            ResetObservation = new ClaudeResetObservation(ClaudeResetObservationState.Stale, null, Now, Now.AddHours(2))
        };
        var snapshot = Snapshot(window) with
        {
            Provider = UsageProviderId.Claude,
            TechnicalDetail = ClaudeUsagePresentation.LiveDetail
        };
        Assert.Null(UsagePacePresentation.From(snapshot, window, Now));
    }

    [Fact]
    public void RingTickFollowsTheFillArcDirection()
    {
        var top = RingGeometry.ComputeTick(0, 50, 50, 40, 60)!.Value;
        Assert.Equal(50, top.Inner.X, 6);
        Assert.Equal(10, top.Inner.Y, 6);
        Assert.Equal(-10, top.Outer.Y, 6);
        var right = RingGeometry.ComputeTick(25, 50, 50, 40, 60)!.Value;
        Assert.Equal(90, right.Inner.X, 6);
        Assert.Equal(50, right.Inner.Y, 6);
        Assert.Null(RingGeometry.ComputeTick(null, 50, 50, 40, 60));
        Assert.Null(RingGeometry.ComputeTick(double.NaN, 50, 50, 40, 60));
    }

    private static CodexQuotaWindow FiveHour(double used, TimeSpan resetIn) => new(
        null, used, CodexWindowClassifier.FiveHourMinutes, Now + resetIn, CodexWindowKind.FiveHour);

    private static CodexQuotaSnapshot Snapshot(CodexQuotaWindow window) => new(
        CodexQuotaStatus.Available, null, Now, Now, null, null, null, [window], null);
}
