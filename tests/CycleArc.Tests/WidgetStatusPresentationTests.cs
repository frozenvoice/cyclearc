using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class WidgetStatusPresentationTests
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);
    public WidgetStatusPresentationTests() => UiText.SetLanguage(UiLanguage.English);

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public void UnexplainedStaleUsesSameWarningWithoutInventingFailure(UsageProviderId provider)
    {
        var snapshot = Snapshot(provider) with { Status = CodexQuotaStatus.Stale, TechnicalDetail = null };
        var status = WidgetStatusPresentation.From(snapshot, Now);
        Assert.Equal(WidgetStatusSeverity.Warning, status.Severity);
        Assert.Equal("StaleBrush", status.BrushKey);
        Assert.Equal("Previous data", status.Summary);
        Assert.DoesNotContain("failed", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(snapshot.LastSuccessfulRefresh, status.ObservationTime);
        Assert.Contains("12m ago", status.AgeText!);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, "timed-out")]
    [InlineData(UsageProviderId.Claude, "claude-live-request-failed")]
    [InlineData(UsageProviderId.Cursor, "cursor-live-request-failed")]
    public void KnownFailureSurvivesRetryAndSuccessRestoresNormal(UsageProviderId provider, string detail)
    {
        var previous = Snapshot(provider) with { Status = CodexQuotaStatus.Stale, TechnicalDetail = detail };
        var failed = WidgetStatusPresentation.From(previous, Now);
        var retry = WidgetStatusPresentation.From(previous.AsRefreshing(), Now.AddMinutes(1));
        Assert.Equal("Check failed", failed.Summary);
        Assert.Equal(failed.Summary, retry.Summary);
        Assert.True(retry.IsWarning);
        Assert.Equal(failed.ObservationTime, retry.ObservationTime);
        var recovered = WidgetStatusPresentation.From(Snapshot(provider), Now);
        Assert.Equal(WidgetStatusSeverity.Normal, recovered.Severity);
        Assert.False(recovered.ShowRow);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public void UnknownStaleRetryKeepsWarningWithoutAnyPreviousView(UsageProviderId provider)
    {
        var retry = (Snapshot(provider) with { Status = CodexQuotaStatus.Stale, TechnicalDetail = null }).AsRefreshing();
        var model = WidgetAccountModel.From(Account(retry), false, now: Now);
        Assert.True(model.StatusPresentation!.IsWarning);
        Assert.Equal("Previous data", model.StatusText);
        Assert.False(model.IsStale); // Freshness enum and warning severity are separate contracts.
        Assert.True(WidgetStatusPresentation.From(retry.AsRefreshing(), Now).IsWarning);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, null)]
    [InlineData(UsageProviderId.Claude, "claude-connected-waiting")]
    [InlineData(UsageProviderId.Cursor, "cursor-connected-waiting")]
    public void FirstWaitAndCheckAreNeutralAndNeverInventTime(UsageProviderId provider, string? detail)
    {
        var waiting = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail) with { Provider = provider };
        foreach (var snapshot in new[] { waiting, waiting.AsRefreshing() })
        {
            var status = WidgetStatusPresentation.From(snapshot, Now);
            Assert.Equal(WidgetStatusSeverity.Pending, status.Severity);
            Assert.Equal("MutedBrush", status.BrushKey);
            Assert.Null(status.AgeText);
            Assert.Null(status.ObservationTime);
            Assert.DoesNotContain("failed", status.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Unavailable)]
    [InlineData(CodexQuotaStatus.ProtocolMismatch)]
    [InlineData(CodexQuotaStatus.TimedOut)]
    [InlineData(CodexQuotaStatus.Cancelled)]
    public void FailureWithoutPreviousDataStaysWarningDuringRetry(CodexQuotaStatus failure)
    {
        var snapshot = CodexQuotaSnapshot.Empty(failure) with { LastAttemptedRefresh = Now };
        var initial = WidgetStatusPresentation.From(snapshot, Now);
        var retry = WidgetStatusPresentation.From(snapshot.AsRefreshing(), Now.AddMinutes(1));
        Assert.True(initial.IsWarning);
        Assert.True(retry.IsWarning);
        Assert.Equal(initial.Summary, retry.Summary);
        Assert.Null(retry.AgeText);
    }

    [Theory]
    [InlineData("claude-statusline")]
    [InlineData("claude-desktop-history")]
    public void LocalReceiptKeepsReceiptLabelAndOriginalTime(string detail)
    {
        var snapshot = Snapshot(UsageProviderId.Claude) with { TechnicalDetail = detail };
        var first = WidgetStatusPresentation.From(snapshot, Now);
        var later = WidgetStatusPresentation.From(snapshot, Now.AddHours(1));
        Assert.Equal(WidgetStatusSeverity.Pending, first.Severity);
        Assert.Equal("Received", first.Summary);
        Assert.Equal("Last received 12m ago", first.AgeText);
        Assert.Equal("Last received 1h ago", later.AgeText);
        Assert.Equal(first.ObservationTime, later.ObservationTime);
        Assert.Contains(ClaudeUsagePresentation.LastReceivedText(snapshot), later.DetailText);
    }

    [Fact]
    public void FailedServerCheckDoesNotRelabelPreviousLocalReceiptAsServerSuccess()
    {
        var snapshot = Snapshot(UsageProviderId.Claude) with
        {
            Status = CodexQuotaStatus.Stale, TechnicalDetail = "claude-live-request-failed",
            LastSuccessfulObservationWasServer = false
        };
        var status = WidgetStatusPresentation.From(snapshot, Now);
        Assert.True(status.IsWarning);
        Assert.Equal("Check failed", status.Summary);
        Assert.Equal("Last received 12m ago", status.AgeText);
        Assert.Equal(snapshot.LastSuccessfulRefresh, status.ObservationTime);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, "codex-identity-mismatch")]
    [InlineData(UsageProviderId.Claude, "claude-live-identity-mismatch")]
    [InlineData(UsageProviderId.Cursor, "cursor-live-identity-mismatch")]
    public void IdentityWarningHidesOldQuotaFromWidgetAndTooltip(UsageProviderId provider, string detail)
    {
        var snapshot = Snapshot(provider) with { TechnicalDetail = detail };
        var model = WidgetAccountModel.From(Account(snapshot), false, now: Now);
        Assert.Equal("Check account", model.StatusText);
        Assert.True(model.StatusPresentation!.IsWarning);
        Assert.Empty(model.Periods);
        Assert.False(model.Ring.IsAvailable);
        Assert.DoesNotContain("76.91%", model.Tooltip);
        Assert.DoesNotContain("23.09%", model.Tooltip);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public void StatusWarningKeepsOriginalQuotaPrecisionBandsAndValues(UsageProviderId provider)
    {
        var healthy = Snapshot(provider);
        var stale = healthy with { Status = CodexQuotaStatus.Stale, TechnicalDetail = null };
        var before = WidgetAccountModel.From(Account(healthy), false, now: Now);
        var after = WidgetAccountModel.From(Account(stale), false, now: Now);
        Assert.Equal(before.Ring, after.Ring);
        Assert.Equal(before.Periods, after.Periods);
        Assert.Equal(before.RingRemainingValueText, after.RingRemainingValueText);
        Assert.True(after.StatusPresentation!.IsWarning);
    }

    [Fact]
    public void KoreanWarningsAndAgeUseTwoIndependentLines()
    {
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var status = WidgetStatusPresentation.From(Snapshot(UsageProviderId.Codex) with
                { Status = CodexQuotaStatus.Stale, TechnicalDetail = null }, Now);
            Assert.Equal("이전 데이터", status.Summary);
            Assert.Equal("마지막 확인 12분 전", status.AgeText);
            Assert.DoesNotContain(Environment.NewLine, status.Summary);
            Assert.DoesNotContain(Environment.NewLine, status.AgeText!);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    private static CodexAccountView Account(CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile("fixture-" + snapshot.Provider, "", "Synthetic account")
            { Provider = snapshot.Provider }, snapshot) { IsConnected = true };

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-12), Now.AddMinutes(-12), null, null, null,
            [new(provider == UsageProviderId.Cursor ? "cursor-auto" : "five", 76.91,
                CodexWindowClassifier.FiveHourMinutes, Now.AddHours(2), CodexWindowKind.FiveHour)],
            provider == UsageProviderId.Claude ? ClaudeUsagePresentation.LiveDetail
                : provider == UsageProviderId.Cursor ? CursorUsagePresentation.LiveDetail : null) { Provider = provider };
}
