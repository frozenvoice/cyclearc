using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorWidgetAccountModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);

    public CursorWidgetAccountModelTests() => UiText.SetLanguage(UiLanguage.English);

    [Fact]
    public void CursorPeriodsUseCanonicalNamedAllowancesAndExcludeDisabledWindows()
    {
        var disabledApi = Window("cursor-api", 99, 1, 1, 0, Now.AddDays(1), enabled: false);
        var api = Window("api", 30, 30, 100, 70, Now.AddDays(2));
        var snapshot = Snapshot(
            Window("cursor-team-pool", 10, 10, 100, 90, Now.AddDays(4)),
            Window("cursor-sand", 40, 4, 10, 6, Now.AddDays(3)),
            disabledApi,
            api,
            Window("cursor-auto", 20, 20, 100, 80, Now.AddDays(1)),
            Window("cursor-on-demand", 5, 5, 100, 95, Now.AddDays(5)));

        var model = WidgetAccountModel.From(Account(snapshot), selected: true,
            UsagePeriodPreference.Auto, Now);

        Assert.Equal(new[] { "Cursor Models", "Other Models", "Grok Bot" },
            model.Periods.Select(period => period.PeriodLabel));
        Assert.Equal(new[] { "Monthly", "Monthly", "Weekly" },
            model.Periods.Select(period => period.CadenceLabel));
        Assert.Equal(new[] { "$80", "$70", "$6" },
            model.Periods.Select(period => period.RemainingText));
        Assert.All(model.Periods, period => Assert.Empty(period.ResetText));
        foreach (var period in model.Periods)
        {
            Assert.NotNull(period.Tooltip);
            Assert.Contains(period.PeriodLabel + " · " + period.CadenceLabel, period.Tooltip!,
                StringComparison.Ordinal);
            Assert.Contains(period.RemainingText, period.Tooltip!, StringComparison.Ordinal);
            Assert.Contains(period.ResetTooltip!, period.Tooltip!, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(model.Periods, period => period.PeriodLabel == "cursor-api");
        Assert.DoesNotContain(model.Periods, period => period.PeriodLabel == "On-demand");
        Assert.Same(disabledApi, snapshot.Windows[2]);
    }

    [Fact]
    public void CursorTooltipKeepsEveryOriginalAllowanceIncludingDisabledAndUnknownBudgets()
    {
        var reset = Now.AddDays(7);
        var disabled = Window("cursor-on-demand", null, 12, 20, 8, reset, enabled: false);
        var unknown = Window("cursor-team-pool", null, null, null, null, null);
        var snapshot = Snapshot([
            Window("cursor-auto", 25, 25, 100, 75, Now.AddDays(1)),
            disabled,
            unknown], CodexQuotaStatus.Available, Now.AddMinutes(-3), "cursor-sand-unavailable");

        var model = WidgetAccountModel.From(Account(snapshot), selected: true,
            UsagePeriodPreference.Auto, Now);

        Assert.Single(model.Periods);
        Assert.Equal("Cursor Models", model.Periods[0].PeriodLabel);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel(disabled.LimitId), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.RemainingText(disabled), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(CodexDeadlineFormatting.ResetStampTooltip(reset)!, model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel(unknown.LimitId), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains("?", model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(UiText.T("Off", "꺼짐"), model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.FailureText("cursor-sand-unavailable"), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(UiText.T("Updated", "업데이트됨"), model.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(CursorUsagePresentation.FailureText("cursor-sand-unavailable"),
            model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void CursorStaleStatusRetainsFailureAndElapsedAge()
    {
        var snapshot = Snapshot(
            Window("cursor-auto", 25, 25, 100, 75, Now.AddDays(1)),
            status: CodexQuotaStatus.Stale,
            lastSuccessful: Now.AddHours(-2),
            detail: "cursor-live-request-failed");

        var model = WidgetAccountModel.From(Account(snapshot), selected: true,
            UsagePeriodPreference.Auto, Now);

        Assert.Contains(UiText.T("Stale data", "오래된 데이터"), model.StatusText, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.FailureText(snapshot.TechnicalDetail), model.StatusText,
            StringComparison.Ordinal);
        Assert.Contains(CodexDeadlineFormatting.Elapsed(snapshot.LastSuccessfulRefresh, Now)!,
            model.StatusText, StringComparison.Ordinal);
        Assert.Contains(UiText.T("Stale data", "오래된 데이터"), model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.UpdatedText(snapshot), model.Tooltip,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CursorIdentityProtectionHidesCachedWidgetNumbersAndRingTarget()
    {
        var cached = Window("cursor-auto", 91, 91, 100, 9, Now.AddDays(1));
        foreach (var snapshot in new[]
        {
            Snapshot(cached, status: CodexQuotaStatus.SignedOut),
            Snapshot(cached, detail: "cursor-live-identity-mismatch")
        })
        {
            var model = WidgetAccountModel.From(Account(snapshot), selected: false,
                UsagePeriodPreference.Auto, Now);

            Assert.Empty(model.Periods);
            Assert.Null(model.RingTargetLabel);
            Assert.Equal("?", model.Ring.CenterValueText);
            Assert.DoesNotContain(CursorUsagePresentation.QuotaDisplayLabel(cached.LimitId),
                model.Tooltip, StringComparison.Ordinal);
            Assert.DoesNotContain("$9", model.Tooltip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CursorUnknownAmountStaysUnknownAndResetMetadataKeepsFullPrecision()
    {
        var reset = Now.AddHours(4);
        var unknown = Window("cursor-auto", null, null, null, null, null);
        var known = Window("cursor-api", 15, 15, 100, 85, reset);
        var snapshot = Snapshot(unknown, known);

        var model = WidgetAccountModel.From(Account(snapshot), selected: true,
            UsagePeriodPreference.Auto, Now);

        var unknownLine = Assert.Single(model.Periods, period => period.PeriodLabel == "Cursor Models");
        var knownLine = Assert.Single(model.Periods, period => period.PeriodLabel == "Other Models");
        Assert.Equal("?", unknownLine.RemainingText);
        Assert.Empty(unknownLine.ResetText);
        Assert.Null(unknownLine.ResetTooltip);
        Assert.Equal(CodexDeadlineFormatting.ResetStampTooltip(reset), knownLine.ResetTooltip);
        Assert.Equal(unknown, snapshot.Windows[0]);
        Assert.Equal(known, snapshot.Windows[1]);
    }

    [Fact]
    public void CursorRingCannotIntroduceAnOmittedBudgetAsAFourthAllowance()
    {
        var budget = Window("cursor-team-pool", 90, 90, 100, 10, Now.AddDays(1));
        var models = Window("cursor-auto", 76.9, null, null, null, Now.AddDays(1));
        var other = Window("cursor-api", 41.2, null, null, null, Now.AddDays(1));
        var grok = Window("cursor-sand", 12.5, null, null, null, Now.AddDays(3));
        var snapshot = Snapshot(budget, models, other, grok);
        var model = WidgetAccountModel.From(Account(snapshot), false, now: Now);

        Assert.Equal("Cursor Models", model.RingTargetLabel);
        Assert.Same(models, model.Ring.Window);
        Assert.Equal("76.9%", model.Ring.CenterValueText);
        Assert.Equal("23.1%", model.Periods[0].RemainingText);
        Assert.True(model.Periods[0].IsRepresentative);
        Assert.Contains("Team pool", model.Tooltip);
        Assert.Same(budget, snapshot.DisplayWindow()); // Shared popup/tray policy is untouched.
    }

    [Theory]
    [InlineData(76.91, "77%", "23%", "76.91%", "23.09%")]
    [InlineData(76.499, "76%", "24%", "76.5%", "23.5%")]
    [InlineData(2.9, "3%", "97%", "2.9%", "97.1%")]
    [InlineData(97.1, "97%", "3%", "97.1%", "2.9%")]
    [InlineData(76.5, "77%", "23%", "76.5%", "23.5%")]
    [InlineData(0.5, "<1%", ">99%", "0.5%", "99.5%")]
    public void CursorWidgetPercentageTextRoundsOnlyTheVisibleStrings(
        double usedPercent, string expectedRing, string expectedRemaining,
        string rawRing, string rawRemaining)
    {
        var window = Window("cursor-auto", usedPercent, null, null, null, Now.AddDays(1));
        var snapshot = Snapshot(window);
        var model = WidgetAccountModel.From(Account(snapshot), selected: true, now: Now);
        var period = Assert.Single(model.Periods);

        Assert.Equal(expectedRing, model.RingValueText);
        Assert.Equal(expectedRemaining, period.DisplayRemainingText);

        // The existing presentation values remain the fractional source used by
        // accessibility/detail surfaces, and the snapshot itself is untouched.
        Assert.Equal(rawRing, model.Ring.CenterValueText);
        Assert.Equal(rawRemaining, period.RemainingText);
        Assert.Equal(usedPercent, snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public void CursorBoundaryMarkersDoNotChangeDangerOrUnderlyingPercent()
    {
        var window = Window("cursor-auto", 99.6, null, null, null, Now.AddDays(1));
        var snapshot = Snapshot(window);
        var model = WidgetAccountModel.From(Account(snapshot), selected: false, now: Now);
        var period = Assert.Single(model.Periods);

        Assert.Equal(">99%", model.RingValueText);
        Assert.Equal("99.6%", model.Ring.CenterValueText);
        Assert.Equal(99.6, model.Ring.UsedPercent);
        Assert.False(model.Ring.IsDangerLevel);
        Assert.Equal("<1%", period.DisplayRemainingText);
        Assert.Equal("0.4%", period.RemainingText);
        Assert.Equal(99.6, snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public void CursorWidgetKeepsUnknownAmountsAndNonFinitePercentagesUnknown()
    {
        var unknown = WidgetAccountModel.From(
            Account(Snapshot(Window("cursor-auto", null, null, null, null, null))),
            selected: false, now: Now);
        Assert.Equal("?", unknown.RingValueText);
        Assert.Equal("?", Assert.Single(unknown.Periods).DisplayRemainingText);
        Assert.Equal("?", unknown.Ring.CenterValueText);

        var nonFinite = WidgetAccountModel.From(
            Account(Snapshot(Window("cursor-auto", double.NaN, null, null, null, null))),
            selected: false, now: Now);
        Assert.Equal("?", nonFinite.RingValueText);
        Assert.Equal("?", Assert.Single(nonFinite.Periods).DisplayRemainingText);
        Assert.Equal("?", nonFinite.Ring.CenterValueText);
    }

    [Fact]
    public void CursorWidgetKeepsMoneyAndUnlimitedRemainingTextUnchanged()
    {
        var money = Window("cursor-auto", 76.91, 50, 100, 23.09m, Now.AddDays(1));
        var moneyModel = WidgetAccountModel.From(Account(Snapshot(money)), selected: false, now: Now);
        var moneyPeriod = Assert.Single(moneyModel.Periods);
        Assert.Equal("$23.09", moneyPeriod.RemainingText);
        Assert.Equal("$23.09", moneyPeriod.DisplayRemainingText);

        var unlimited = Window("cursor-auto", 76.91, null, null, null, Now.AddDays(1))
            with { IsUnlimited = true };
        var unlimitedModel = WidgetAccountModel.From(Account(Snapshot(unlimited)), selected: false, now: Now);
        var unlimitedPeriod = Assert.Single(unlimitedModel.Periods);
        Assert.Equal("Unlimited", unlimitedPeriod.RemainingText);
        Assert.Equal("Unlimited", unlimitedPeriod.DisplayRemainingText);
    }

    [Fact]
    public void CursorWidgetIntegerTextDoesNotRewriteTooltipOrPopupFractionalValues()
    {
        var window = Window("cursor-auto", 76.91, null, null, null, Now.AddDays(1));
        var snapshot = Snapshot(window);
        var model = WidgetAccountModel.From(Account(snapshot), selected: true, now: Now);

        Assert.Equal("77%", model.RingValueText);
        Assert.Equal("23%", Assert.Single(model.Periods).DisplayRemainingText);
        Assert.Contains("23.09%", model.Tooltip, StringComparison.Ordinal);

        var row = Assert.Single(CodexDisplayFormatting.Rows(snapshot, Now),
            candidate => candidate.Label == CursorUsagePresentation.QuotaDisplayLabel(window.LimitId));
        Assert.Equal("Remaining 23.09%", row.Value);
        Assert.Equal("76.91%", model.Ring.CenterValueText);
        Assert.Equal(76.91, snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public void AllProvidersUseIntegerWidgetTextAndPreciseDetailText()
    {
        var codexSnapshot = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available, "pro", Now, Now, null, null, null,
            [new("five", 76.91, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(1), CodexWindowKind.FiveHour)],
            null)
        {
            Provider = UsageProviderId.Codex
        };
        var codexModel = WidgetAccountModel.From(
            new CodexAccountView(new CodexAccountProfile("codex", "", "Codex")
            {
                Provider = UsageProviderId.Codex
            }, codexSnapshot), selected: false, now: Now);
        Assert.Equal("76.91%", codexModel.Ring.CenterValueText);
        Assert.Equal("77%", codexModel.RingValueText);
        Assert.Equal("Left 23.09%", codexModel.Periods[0].RemainingText);
        Assert.Equal("Left 23%", codexModel.Periods[0].DisplayRemainingText);

        var claudeSnapshot = codexSnapshot with { Provider = UsageProviderId.Claude };
        var claudeModel = WidgetAccountModel.From(
            new CodexAccountView(new CodexAccountProfile("claude", "", "Claude")
            {
                Provider = UsageProviderId.Claude
            }, claudeSnapshot), selected: false, now: Now);
        Assert.Equal("76.91%", claudeModel.Ring.CenterValueText);
        Assert.Equal("77%", claudeModel.RingValueText);
        Assert.Equal("Left 23.09%", claudeModel.Periods[0].RemainingText);
        Assert.Equal("Left 23%", claudeModel.Periods[0].DisplayRemainingText);
    }

    [Fact]
    public void CursorRingKeepsUnknownSummaryValuesUnknownEvenWhenAnOmittedBudgetIsKnown()
    {
        var budget = Window("cursor-team-pool", 90, 90, 100, 10, Now.AddDays(1));
        var unknown = Window("cursor-auto", null, null, null, null, null);
        var model = WidgetAccountModel.From(Account(Snapshot(budget, unknown)), false, now: Now);
        Assert.Equal("Cursor Models", model.RingTargetLabel);
        Assert.Equal("?", model.Ring.CenterValueText);
        Assert.Equal("?", Assert.Single(model.Periods).RemainingText);

        var budgetOnly = WidgetAccountModel.From(Account(Snapshot(budget)), false, now: Now);
        Assert.Empty(budgetOnly.Periods);
        Assert.Null(budgetOnly.RingTargetLabel);
        Assert.Equal("?", budgetOnly.Ring.CenterValueText);
        Assert.Contains("Team pool", budgetOnly.Tooltip);
    }

    [Fact]
    public void CursorRingUsesCanonicalOrderAndFallsThroughUnknownAllowances()
    {
        var grok = Window("cursor-sand", 12.5, null, null, null, Now.AddDays(3));
        var other = Window("cursor-api", 41.2, null, null, null, Now.AddDays(2));
        var unknownModels = Window("cursor-auto", null, null, null, null, null);
        var snapshot = Snapshot(grok, other, unknownModels);

        var model = WidgetAccountModel.From(Account(snapshot), selected: false, now: Now);

        Assert.Equal(new[] { "Cursor Models", "Other Models", "Grok Bot" },
            model.Periods.Select(period => period.PeriodLabel));
        Assert.Equal("Other Models", model.RingTargetLabel);
        Assert.Same(other, model.Ring.Window);
        Assert.Equal("41.2%", model.Ring.CenterValueText);
        Assert.Equal("?", model.Periods[0].RemainingText);
        Assert.Same(grok, snapshot.Windows[0]);
        Assert.Same(other, snapshot.Windows[1]);
        Assert.Same(unknownModels, snapshot.Windows[2]);
    }

    [Fact]
    public void CursorCanonicalRingDoesNotTrimFullSnapshotTooltipDetails()
    {
        var reset = Now.AddDays(7);
        var disabled = Window("cursor-on-demand", null, 12, 20, 8, reset, enabled: false);
        var teamPool = Window("cursor-team-pool", null, null, null, null, null);
        var grok = Window("cursor-sand", 12.5, null, null, null, Now.AddDays(3));
        var other = Window("cursor-api", 41.2, null, null, null, Now.AddDays(2));
        var models = Window("cursor-auto", 76.9, null, null, null, Now.AddDays(1));
        var snapshot = Snapshot(disabled, teamPool, grok, other, models);

        var model = WidgetAccountModel.From(Account(snapshot), selected: true, now: Now);

        Assert.Equal("Cursor Models", model.RingTargetLabel);
        Assert.Same(models, model.Ring.Window);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel(disabled.LimitId), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(UiText.T("Off", "꺼짐"), model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel(teamPool.LimitId), model.Tooltip,
            StringComparison.Ordinal);
        Assert.Contains("?", model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CodexDeadlineFormatting.ResetStampTooltip(reset)!, model.Tooltip,
            StringComparison.Ordinal);
        Assert.Equal(5, snapshot.Windows.Count);
    }

    [Fact]
    public void RingTargetUsesFullCursorNameAndOtherProvidersRemainUnchanged()
    {
        var cursor = Snapshot(Window("cursor-auto", 25, 25, 100, 75, Now.AddDays(1)));
        var cursorModel = WidgetAccountModel.From(Account(cursor), selected: true,
            UsagePeriodPreference.Auto, Now);

        Assert.Equal("Cursor Models", cursorModel.RingTargetLabel);
        Assert.Contains("Ring target: Cursor Models", cursorModel.Tooltip, StringComparison.Ordinal);

        var codex = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null,
            [new("five", 25, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(1), CodexWindowKind.FiveHour)], null);
        var codexModel = WidgetAccountModel.From(
            new CodexAccountView(new CodexAccountProfile("codex", "", "Codex") { Provider = UsageProviderId.Codex }, codex),
            selected: false, UsagePeriodPreference.Auto, Now);
        Assert.Null(codexModel.RingTargetLabel);
    }

    private static CodexAccountView Account(CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile("cursor-widget", "", "Cursor widget")
        {
            Provider = UsageProviderId.Cursor
        }, snapshot)
        {
            IsConnected = true
        };

    private static CodexQuotaSnapshot Snapshot(params CodexQuotaWindow[] windows) =>
        Snapshot(windows, CodexQuotaStatus.Available, Now.AddMinutes(-3), null);

    private static CodexQuotaSnapshot Snapshot(
        CodexQuotaWindow window,
        CodexQuotaStatus status = CodexQuotaStatus.Available,
        DateTimeOffset? lastSuccessful = null,
        string? detail = null) =>
        Snapshot([window], status, lastSuccessful ?? Now.AddMinutes(-3), detail);

    private static CodexQuotaSnapshot Snapshot(
        IReadOnlyList<CodexQuotaWindow> windows,
        CodexQuotaStatus status,
        DateTimeOffset lastSuccessful,
        string? detail) => new(
        status,
        "pro",
        lastSuccessful,
        lastSuccessful,
        null,
        null,
        null,
        windows,
        detail)
    {
        Provider = UsageProviderId.Cursor
    };

    private static CodexQuotaWindow Window(string id, double? usedPercent, decimal? used,
        decimal? limit, decimal? remaining, DateTimeOffset? reset, bool enabled = true) =>
        new(id, usedPercent, null, reset, CodexWindowKind.Other)
        {
            UsedAmount = used,
            LimitAmount = limit,
            RemainingAmount = remaining,
            Unit = "USD",
            IsEnabled = enabled
        };
}
