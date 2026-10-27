using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeResetContinuityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Theory]
    [InlineData(46, 19)]
    [InlineData(51, 20)]
    public async Task NewerDesktopObservationDoesNotEraseVerifiedFutureResetsWhenUsageHasNotDecreased(
        double fiveUsage, double weeklyUsage)
    {
        using var data = new Data();
        var fiveReset = Now.AddHours(2);
        var weeklyReset = Now.AddDays(3);
        data.Live.Next = new(new(Now, new(46, fiveReset), new(19, weeklyReset)));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        Assert.Equal(fiveReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(weeklyReset, Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.All(service.Snapshot.Windows, window =>
        {
            Assert.Equal(ClaudeResetObservationState.Direct, window.ResetObservation!.State);
            Assert.Equal(ClaudeResetSource.Server, window.ResetObservation.Source);
            Assert.Equal(Now, window.ResetObservation.ObservedAt);
        });

        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, fiveUsage, weeklyUsage));
        await service.RefreshAsync(default);

        Assert.Equal("claude-desktop-history", service.Snapshot.TechnicalDetail);
        Assert.False(service.Snapshot.LastSuccessfulObservationWasServer);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(fiveUsage, Window(service, CodexWindowKind.FiveHour).UsedPercent);
        Assert.Equal(weeklyUsage, Window(service, CodexWindowKind.Weekly).UsedPercent);
        Assert.Equal(fiveReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(weeklyReset, Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.All(service.Snapshot.Windows, window =>
        {
            Assert.Equal(ClaudeResetObservationState.Preserved, window.ResetObservation!.State);
            Assert.Equal(ClaudeResetSource.Server, window.ResetObservation.Source);
            Assert.Equal(Now, window.ResetObservation.ObservedAt);
        });
        Assert.Equal(1, data.Live.RefreshCalls);
    }

    [Theory]
    [InlineData(899, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public async Task InitialPreservationRequiresABoundedObservationGap(int seconds, bool retained)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddSeconds(seconds);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        Assert.Equal(retained ? Now.AddHours(2) : (DateTimeOffset?)null,
            Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(retained ? Now.AddDays(3) : (DateTimeOffset?)null,
            Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task ContinuedObservationsPreserveTheOriginalResetSourceAndClockBeyondFifteenMinutes()
    {
        using var data = new Data();
        var service = await Seed(data);
        foreach (var minutes in new[] { 10, 20, 30 })
        {
            data.Clock.UtcNow = Now.AddMinutes(minutes);
            data.Desktop.Next = new(new(data.Clock.UtcNow, 46 + minutes / 10, 19 + minutes / 10));
            await service.RefreshAsync(default);
            Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
            Assert.All(service.Snapshot.Windows, window =>
            {
                Assert.Equal(ClaudeResetObservationState.Preserved, window.ResetObservation!.State);
                Assert.Equal(ClaudeResetSource.Server, window.ResetObservation.Source);
                Assert.Equal(Now, window.ResetObservation.ObservedAt);
            });
        }
    }

    [Fact]
    public async Task NullLiveFiveHourResetRetainsItsOwnEvidenceWhileWeeklyUsesNewServerDate()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        var newWeek = Now.AddDays(4);
        data.Live.Next = new(new(data.Clock.UtcNow, new(51, null), new(20, newWeek)));
        await service.RefreshLiveAsync(default);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(newWeek, Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.True(service.Snapshot.LastSuccessfulObservationWasServer);
    }

    [Fact]
    public async Task InitiallyMissingResetHasNoCandidateAndIsNotCalculatedFromTheWindowDuration()
    {
        using var data = new Data();
        data.Live.Next = new(new(Now, new(46, null), new(19, null)));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        Assert.All(service.Snapshot.Windows, window =>
        {
            Assert.Null(window.ResetsAt);
            Assert.Equal(ClaudeResetObservationState.NotProvided, window.ResetObservation!.State);
            Assert.Null(window.ResetObservation.LastKnownResetAt);
        });
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, true, 51, 20);
        Assert.All(service.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":51},"seven_day":{"utilization":20,"resets_at":"2030-01-04T10:00:00Z"}}""")]
    [InlineData("""{"five_hour":{"utilization":51,"resets_at":null},"seven_day":{"utilization":20,"resets_at":"2030-01-04T10:00:00Z"}}""")]
    public async Task ActualOAuthMissingOrNullResetPreservesOnlyVerifiedContinuity(string json)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        using var document = JsonDocument.Parse(json);
        data.Live.Next = new(ClaudeOAuthUsageClient.ParseUsage(document.RootElement, data.Clock.UtcNow));
        Assert.Null(data.Live.Next.Sample!.FiveHour!.ResetsAt);
        await service.RefreshLiveAsync(default);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Preserved, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(Now, Window(service, CodexWindowKind.FiveHour).ResetObservation!.ObservedAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.Weekly).ResetObservation!.State);
    }

    [Fact]
    public async Task IdlePreservedEvidenceBecomesStaleWithoutRenewingOriginalSourceClock()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, true, 51, 20);
        var original = Window(service, CodexWindowKind.FiveHour).ResetObservation!;
        Assert.Equal(ClaudeResetObservationState.Preserved, original.State);
        data.Clock.UtcNow = Now.AddMinutes(16).AddSeconds(1);
        var stale = Window(service, CodexWindowKind.FiveHour);
        Assert.Null(stale.ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Stale, stale.ResetObservation!.State);
        Assert.Equal(original.Source, stale.ResetObservation.Source);
        Assert.Equal(original.ObservedAt, stale.ResetObservation.ObservedAt);
        Assert.Equal(original.LastKnownResetAt, stale.ResetObservation.LastKnownResetAt);
        Assert.Equal(Now.AddMinutes(1), service.Snapshot.LastSuccessfulRefresh);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageDecreaseInvalidatesOnlyThatWindowAndLaterGrowthCannotResurrectIt(bool desktop)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, desktop, 1, 20);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);

        data.Clock.UtcNow = Now.AddMinutes(2);
        await Observe(data, service, desktop, 55, 21);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task MissingWindowInvalidatesItsEvidenceRatherThanBorrowingTheOtherWindow()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, null, 20));
        await service.RefreshAsync(default);
        Assert.Single(service.Snapshot.Windows);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(2);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 55, 21));
        await service.RefreshAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public async Task ResetNearClockSkewBoundaryIsNotPreservedAsAnExactCountdown(int secondsRemaining, bool retained)
    {
        using var data = new Data();
        var reset = Now.AddMinutes(2);
        data.Live.Next = new(new(Now, new(46, reset), new(19, Now.AddDays(3))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = reset.AddSeconds(-secondsRemaining);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        Assert.Equal(retained ? reset : (DateTimeOffset?)null, Window(service, CodexWindowKind.FiveHour).ResetsAt);
    }

    [Fact]
    public async Task ObservationAfterResetCannotCopyTheOldDateOrAddAnotherPeriod()
    {
        using var data = new Data();
        var reset = Now.AddMinutes(2);
        data.Live.Next = new(new(Now, new(46, reset), new(19, Now.AddDays(3))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = reset.AddSeconds(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 1, 20));
        await service.RefreshAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Elapsed, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(reset, Window(service, CodexWindowKind.FiveHour).ResetObservation!.LastKnownResetAt);
        Assert.Equal("Reset unconfirmed", ClaudeResetPresentation.Countdown(
            Window(service, CodexWindowKind.FiveHour), data.Clock.UtcNow));
    }

    [Fact]
    public async Task FullRemainderAfterAnElapsedResetStaysUnconfirmedUntilANewFutureDateArrives()
    {
        using var data = new Data();
        var reset = Now.AddMinutes(2);
        var week = Now.AddDays(3);
        data.Live.Next = new(new(Now, new(46, reset), new(19, week)));
        var service = data.Service();
        await service.RefreshLiveAsync(default);

        data.Clock.UtcNow = reset.AddSeconds(1);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, null), new(19, null)));
        await service.RefreshLiveAsync(default);
        var five = Window(service, CodexWindowKind.FiveHour);
        Assert.Equal(0, five.UsedPercent);
        Assert.Null(five.ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Elapsed, five.ResetObservation!.State);
        Assert.Equal(reset, five.ResetObservation.LastKnownResetAt);
        Assert.Equal(ClaudeResetSource.Server, five.ResetObservation.Source);
        Assert.Equal(Now, five.ResetObservation.ObservedAt);
        Assert.Equal(week, Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Preserved, Window(service, CodexWindowKind.Weekly).ResetObservation!.State);
        var tooltip = ClaudeResetPresentation.Tooltip(five, data.Clock.UtcNow, service.Snapshot);
        Assert.Contains("Next reset time has not been confirmed.", tooltip);
        Assert.Contains("OAuth", tooltip);
        Assert.Contains("Usage · ", tooltip);
        Assert.Contains("Reset evidence · ", tooltip);
        Assert.DoesNotContain("in 4h", tooltip);
        Assert.DoesNotContain(CodexDeadlineFormatting.ResetCountdown(reset, data.Clock.UtcNow), tooltip);

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        var next = data.Clock.UtcNow.AddHours(4).AddMinutes(32);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, next), new(19, week)));
        await service.RefreshLiveAsync(default);
        five = Window(service, CodexWindowKind.FiveHour);
        Assert.Equal(0, five.UsedPercent);
        Assert.Equal(next, five.ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, five.ResetObservation!.State);
        Assert.Equal(data.Clock.UtcNow, five.ResetObservation.ObservedAt);
        Assert.Equal("in 4h 32m", ClaudeResetPresentation.Countdown(five, data.Clock.UtcNow));
        Assert.Equal(week, Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task RepeatedPastServerResetWithAFullRemainderDoesNotBecomeACountdown()
    {
        using var data = new Data();
        var reset = Now.AddMinutes(2);
        data.Live.Next = new(new(Now, new(46, reset), new(19, Now.AddDays(3))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = reset.AddSeconds(30);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, reset), new(20, Now.AddDays(3))));
        await service.RefreshLiveAsync(default);
        var five = Window(service, CodexWindowKind.FiveHour);
        Assert.Equal(0, five.UsedPercent);
        Assert.Equal(ClaudeResetObservationState.Elapsed, five.ResetObservation!.State);
        Assert.Equal(reset, five.ResetObservation.LastKnownResetAt);
        Assert.Equal("Reset unconfirmed", ClaudeResetPresentation.Countdown(five, data.Clock.UtcNow));
        Assert.Equal("Next reset unconfirmed", ClaudeResetPresentation.Stamp(five, data.Clock.UtcNow));
        Assert.NotEqual(CodexDeadlineFormatting.ResetCountdown(five.ResetsAt, data.Clock.UtcNow),
            ClaudeResetPresentation.Countdown(five, data.Clock.UtcNow));
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.Weekly).ResetObservation!.State);
    }

    [Fact]
    public async Task RateLimitAfterAnUnconfirmedResetKeepsTheFailureAndDoesNotInventACountdown()
    {
        using var data = new Data();
        var reset = Now.AddMinutes(2);
        data.Live.Next = new(new(Now, new(46, reset), new(19, Now.AddDays(3))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = reset.AddSeconds(1);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, null), new(19, null)));
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        data.Live.Next = new(null, "claude-live-rate-limited", data.Clock.UtcNow);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 0, 19));
        await service.RefreshLiveAsync(default);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-live-rate-limited", service.Snapshot.TechnicalDetail);
        var five = Window(service, CodexWindowKind.FiveHour);
        Assert.Equal(0, five.UsedPercent);
        Assert.Null(five.ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Elapsed, five.ResetObservation!.State);
        Assert.Equal("Reset unconfirmed", ClaudeResetPresentation.Countdown(five, data.Clock.UtcNow));
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task NewExplicitServerDateReestablishesAnInvalidatedWindowEvenIfTheCounterDecreases()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, true, 1, 20);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(2);
        var nextReset = data.Clock.UtcNow.AddHours(4);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, nextReset), new(21, Now.AddDays(4))));
        await service.RefreshLiveAsync(default);
        Assert.Equal(nextReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(4), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task NewExplicitServerDateWinsOverPreviouslyRetainedDate()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, true, 51, 20);
        data.Clock.UtcNow = Now.AddMinutes(2);
        var nextReset = Now.AddHours(1);
        data.Live.Next = new(new(data.Clock.UtcNow, new(52, nextReset), new(21, Now.AddDays(2))));
        await service.RefreshLiveAsync(default);
        Assert.Equal(nextReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(2), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task ExplicitServerDateFromAnotherResponseAtTheSameClockWins()
    {
        using var data = new Data();
        var service = await Seed(data);
        var newReset = Now.AddHours(3);
        data.Live.Next = new(new(Now, new(46, newReset), new(19, Now.AddDays(3))));
        await service.RefreshLiveAsync(default);
        Assert.Equal(newReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(ClaudeResetSource.Server, Window(service, CodexWindowKind.FiveHour).ResetObservation!.Source);
    }

    [Theory]
    [InlineData("claude-live-request-failed")]
    [InlineData("claude-live-rate-limited")]
    public async Task LiveFailureKeepsSafeDesktopResetEvidenceWithoutClearingFailure(string failure)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Live.Next = new(null, failure, data.Clock.UtcNow);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshLiveAsync(default);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal(failure, service.Snapshot.TechnicalDetail);
        Assert.False(service.Snapshot.LastSuccessfulObservationWasServer);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task AuthenticationFailureBlocksBorrowingWhileKeepingTheFailureVisible()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Live.Next = new(null, "claude-live-auth-required", data.Clock.UtcNow);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshLiveAsync(default);
        Assert.Equal("claude-live-auth-required", service.Snapshot.TechnicalDetail);
        Assert.All(service.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task ConflictingOlderOrEqualDesktopObservationInvalidatesAmbiguousResetEvidence(int seconds)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(Now.AddSeconds(seconds), 1, 2));
        await service.RefreshAsync(default);
        Assert.True(service.Snapshot.LastSuccessfulObservationWasServer);
        Assert.Equal(46, Window(service, CodexWindowKind.FiveHour).UsedPercent);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.NotProvided, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
    }

    [Fact]
    public async Task NewerStatusLineWithSameDatesRemainsAnExplicitLocalObservation()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(51, Now.AddHours(2).ToUnixTimeSeconds()), new(20, Now.AddDays(3).ToUnixTimeSeconds())),
            data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.False(service.Snapshot.LastSuccessfulObservationWasServer);
        Assert.Equal(51, Window(service, CodexWindowKind.FiveHour).UsedPercent);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(ClaudeResetSource.StatusLine, Window(service, CodexWindowKind.FiveHour).ResetObservation!.Source);
        Assert.Equal(data.Clock.UtcNow, Window(service, CodexWindowKind.FiveHour).ResetObservation!.ObservedAt);
    }

    [Fact]
    public async Task LiveRecoveryUsesNewConfirmedDateAndClearsOnlyThePreviousRequestFailure()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Live.Next = new(null, "claude-live-request-failed", data.Clock.UtcNow);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshLiveAsync(default);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        data.Clock.UtcNow = Now.AddMinutes(2);
        var newReset = Now.AddHours(3);
        data.Live.Next = new(new(data.Clock.UtcNow, new(52, newReset), new(21, Now.AddDays(4))));
        await service.RefreshLiveAsync(default);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Equal("claude-live", service.Snapshot.TechnicalDetail);
        Assert.Equal(newReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.True(service.Snapshot.LastSuccessfulObservationWasServer);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(data.Clock.UtcNow, Window(service, CodexWindowKind.FiveHour).ResetObservation!.ObservedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FutureDatedLocalObservationCannotSupplyUsageOrResetEvidence(bool desktop)
    {
        using var data = new Data();
        var service = await Seed(data);
        var future = Now.AddMinutes(1);
        if (desktop) data.Desktop.Next = new(new(future, 99, 98));
        else await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(99, future.AddHours(4).ToUnixTimeSeconds()), new(98, future.AddDays(6).ToUnixTimeSeconds())),
            future, default);
        await service.RefreshAsync(default);
        Assert.Equal(46, Window(service, CodexWindowKind.FiveHour).UsedPercent);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now, service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task SameClientDateCannotRearmAnInvalidatedPeriodButANewDateCan()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await Observe(data, service, true, 1, 20);
        data.Desktop.Next = new(null);
        data.Clock.UtcNow = Now.AddMinutes(2);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(55, Now.AddHours(2).ToUnixTimeSeconds()), null), data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(3);
        var newReset = Now.AddHours(4);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(56, newReset.ToUnixTimeSeconds()), null), data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Equal(newReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
    }

    [Fact]
    public async Task ConflictingNewCodeDateCannotReplaceAStillValidServerDateWithoutACycleChangeSignal()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(51, Now.AddHours(3).ToUnixTimeSeconds()), new(20, Now.AddDays(3).ToUnixTimeSeconds())),
            data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Equal(51, Window(service, CodexWindowKind.FiveHour).UsedPercent);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.NotProvided, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task MatchingCodeCannotEraseServerAuthorityBeforeAContradictoryCodeDate()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(51, Now.AddHours(2).ToUnixTimeSeconds()), new(20, Now.AddDays(3).ToUnixTimeSeconds())),
            data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Equal(Now.AddHours(2), Window(service, CodexWindowKind.FiveHour).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(2);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(52, Now.AddHours(3).ToUnixTimeSeconds()), new(21, Now.AddDays(3).ToUnixTimeSeconds())),
            data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task LegacyCodeReceiptAtExactConnectionTimeCannotSeedAResetForTheNewBinding()
    {
        using var data = new Data();
        data.Connections.Save(data.Binding with { ConnectedAt = Now });
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available,
            new(46, Now.AddHours(2).ToUnixTimeSeconds()), new(19, Now.AddDays(3).ToUnixTimeSeconds())), Now, default);
        var service = data.Service();
        await service.RefreshAsync(default);
        Assert.All(service.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
    }

    [Fact]
    public async Task BindingGenerationChangeCannotTransferRetainedResetEvidence()
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        Assert.All(service.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
    }

    [Fact]
    public async Task ConsumedFiveHourResetGrantInvalidatesFiveHourEvidenceEvenWithoutUsageDecrease()
    {
        using var data = new Data();
        data.Live.Next = new(new(Now, new(46, Now.AddHours(2)), new(19, Now.AddDays(3)))
        { ResetCredits = Inventory(1, Now) });
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Live.Next = new(new(data.Clock.UtcNow, new(51, null), new(20, null))
        { ResetCredits = Inventory(0, data.Clock.UtcNow) });
        await service.RefreshLiveAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(2);
        await Observe(data, service, true, 55, 21);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);

        static ClaudeResetCreditInventory Inventory(int count, DateTimeOffset observed) => new(true,
            [new(count, 1, Now.AddDays(-1), Now.AddDays(1), false, true, false, ["five_hour"])], observed);
    }

    [Fact]
    public async Task GrantCountDeclineAtTheSameClockInvalidatesOnlyItsTarget()
    {
        using var data = new Data();
        data.Live.Next = new(new(Now, new(46, Now.AddHours(2)), new(19, Now.AddDays(3)))
        { ResetCredits = Inventory(1) });
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Live.Next = new(data.Live.Next.Sample! with { ResetCredits = Inventory(0) });
        await service.RefreshLiveAsync(default);
        Assert.Null(Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(Now.AddDays(3), Window(service, CodexWindowKind.Weekly).ResetsAt);

        static ClaudeResetCreditInventory Inventory(int count) => new(true,
            [new(count, 1, Now.AddDays(-1), Now.AddDays(1), false, true, false, ["five_hour"])], Now);
    }

    [Fact]
    public async Task NewServerDateInTheSameResponseAsGrantConsumptionEstablishesTheNewPeriod()
    {
        using var data = new Data();
        data.Live.Next = new(new(Now, new(46, Now.AddHours(2)), new(19, Now.AddDays(3)))
        { ResetCredits = Inventory(1, Now) });
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        var newReset = data.Clock.UtcNow.AddHours(5);
        data.Live.Next = new(new(data.Clock.UtcNow, new(0, newReset), new(20, Now.AddDays(3)))
        { ResetCredits = Inventory(0, data.Clock.UtcNow) });
        await service.RefreshLiveAsync(default);
        Assert.Equal(newReset, Window(service, CodexWindowKind.FiveHour).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(data.Clock.UtcNow, Window(service, CodexWindowKind.FiveHour).ResetObservation!.ObservedAt);

        static ClaudeResetCreditInventory Inventory(int count, DateTimeOffset observed) => new(true,
            [new(count, 1, Now.AddDays(-1), Now.AddDays(1), false, true, false, ["five_hour"])], observed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectionOrLiveIdentityMismatchHidesAllResetEvidence(bool disconnect)
    {
        using var data = new Data();
        var service = await Seed(data);
        data.Clock.UtcNow = Now.AddMinutes(1);
        if (disconnect)
        {
            data.Connections.Save(data.Binding with { Disconnected = true });
            await service.RefreshAsync(default);
        }
        else
        {
            data.Live.Next = new(null, "claude-live-identity-mismatch", data.Clock.UtcNow);
            await service.RefreshLiveAsync(default);
        }
        Assert.Empty(service.Snapshot.Windows);
    }

    private static async Task<ClaudeQuotaService> Seed(Data data)
    {
        data.Live.Next = new(new(Now, new(46, Now.AddHours(2)), new(19, Now.AddDays(3))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        return service;
    }

    private static Task Observe(Data data, ClaudeQuotaService service, bool desktop, double five, double week)
    {
        if (desktop)
        {
            data.Desktop.Next = new(new(data.Clock.UtcNow, five, week));
            return service.RefreshAsync(default);
        }
        data.Live.Next = new(new(data.Clock.UtcNow, new(five, null), new(week, null)));
        return service.RefreshLiveAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgingResetStateDoesNotPublishTheSameTransitionOnEveryPassivePoll(bool preserved)
    {
        using var data = new Data();
        var service = await Seed(data);
        if (preserved)
        {
            data.Clock.UtcNow = Now.AddMinutes(1);
            await Observe(data, service, true, 51, 20);
        }
        var changes = 0;
        service.Changed += _ => changes++;
        data.Clock.UtcNow = preserved ? Now.AddMinutes(17) : Now.AddHours(2).AddSeconds(1);
        await service.RefreshAsync(default);
        Assert.Equal(preserved ? ClaudeResetObservationState.Stale : ClaudeResetObservationState.Elapsed,
            Window(service, CodexWindowKind.FiveHour).ResetObservation!.State);
        Assert.Equal(1, changes);
        for (var index = 0; index < 4; index++)
        {
            data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2);
            await service.RefreshAsync(default);
        }
        Assert.Equal(1, changes);
        Assert.Equal(1, data.Live.RefreshCalls);
    }

    private static CodexQuotaWindow Window(ClaudeQuotaService service, CodexWindowKind kind) =>
        Assert.Single(service.Snapshot.Windows, window => window.Kind == kind);

    private sealed class FakeLiveSource : IClaudeLiveUsageSource
    {
        public ClaudeLiveUsageUpdate Next { get; set; } = new(null);
        public int RefreshCalls { get; private set; }
        public ClaudeLiveUsageUpdate ReadCached(ClaudeConnectionBinding? binding) => new(null);
        public Task<ClaudeLiveUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token)
        {
            RefreshCalls++;
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeDesktopSource : IClaudeDesktopUsageSource
    {
        public ClaudeDesktopUsageUpdate Next { get; set; } = new(null);
        public Task<ClaudeDesktopUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token) =>
            Task.FromResult(Next);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class Data : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-reset-continuity-" + Guid.NewGuid().ToString("N"));
        public FakeClock Clock { get; } = new();
        public FakeLiveSource Live { get; } = new();
        public FakeDesktopSource Desktop { get; } = new();
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public ClaudeConnectionBinding Binding { get; }
        public ClaudeConnectionStore Connections { get; }
        public ClaudeStatusLineStore Inbox { get; }

        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Reset continuity");
            Accounts.Save(initial with { Version = 3, Profiles = [Profile], SelectedId = Profile.Id });
            Binding = new(2, Profile.Id, Path.Combine(Root, "claude-home"), Path.Combine(Root, "claude.exe"),
                false, ClaudeIdentity.StableFingerprint("continuity@example.invalid", "org-reset"),
                Now.AddDays(-1), BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro");
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
            Inbox = new(Accounts.ClaudeStatusLinePath(Profile.Id), Profile.Id);
        }

        public ClaudeQuotaService Service() => new(Inbox, Clock, Connections.Read,
            email: _ => "continuity@example.invalid", failureStore: new ClaudeFailureStore(Accounts),
            desktop: Desktop, live: Live);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
