using CycleArc.Codex;
using CycleArc.Monitoring;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class AccountComparisonFeatureTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.Parse("2026-10-23T00:00:00Z");

    private static CodexAccountView Account(string id, double? used, UsageProviderId provider = UsageProviderId.Codex,
        CodexQuotaStatus status = CodexQuotaStatus.Available, DateTimeOffset? reset = null) =>
        new(new CodexAccountProfile(id, "", id) { Provider = provider },
            CodexQuotaSnapshot.Empty(status) with
            {
                Provider = provider,
                LastSuccessfulRefresh = DateTimeOffset.UtcNow,
                Windows = [new("weekly", used, 10080, reset ?? Reset, CodexWindowKind.Weekly)]
            }) { IsConnected = true };

    [Fact]
    public void Recommendation_PicksTheSameServiceAccountWithClearlyMoreRoom()
    {
        var overview = UsageAccountOverview.Create([Account("busy", 90), Account("free", 20), Account("other", 50)], "busy");
        var recommendation = AccountRecommendation.For(overview);
        Assert.NotNull(recommendation);
        Assert.Equal("free", recommendation!.Account.Profile.Id);
        Assert.Equal(80, recommendation.RemainingPercent, 3);
        Assert.Contains("free", recommendation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommendation_StaysQuietWhenTheShownAccountIsBestOrNearlyAsGood()
    {
        Assert.Null(AccountRecommendation.For(UsageAccountOverview.Create([Account("a", 10), Account("b", 50)], "a")));
        Assert.Null(AccountRecommendation.For(UsageAccountOverview.Create([Account("a", 30), Account("b", 25)], "a")));
        Assert.Null(AccountRecommendation.For(UsageAccountOverview.Create([Account("only", 90)], "only")));
    }

    [Fact]
    public void Recommendation_NeverComparesServicesOrUnverifiedValues()
    {
        // A Claude account with more room is not a substitute for a Codex limit.
        Assert.Null(AccountRecommendation.For(UsageAccountOverview.Create(
            [Account("codex", 95), Account("claude", 5, UsageProviderId.Claude)], "codex")));
        // Stale and unknown values never win.
        Assert.Null(AccountRecommendation.For(UsageAccountOverview.Create(
            [Account("busy", 95), Account("stale", 5, status: CodexQuotaStatus.Stale), Account("unknown", null)], "busy")));
        // Ties go to the account that resets sooner.
        var tie = AccountRecommendation.For(UsageAccountOverview.Create(
            [Account("busy", 95), Account("late", 10, reset: Reset.AddDays(3)), Account("soon", 10, reset: Reset.AddHours(1))], "busy"));
        Assert.Equal("soon", tie!.Account.Profile.Id);
    }

    [Fact]
    public void TrayTooltip_AddsOtherAccountsOnlyWithinTheNativeLimit()
    {
        var two = UsageAccountOverview.Create([Account("Personal", 40), Account("Work", 75)], "Personal");
        Assert.True(two.Tooltip.Length <= NotifyIconText.MaximumLength, two.Tooltip);
        Assert.Contains("Work 25%", two.Tooltip, StringComparison.Ordinal);

        var many = UsageAccountOverview.Create(Enumerable.Range(0, 12)
            .Select(i => Account("Account with a long name " + i, i * 5)).ToArray(), "Account with a long name 0");
        Assert.True(many.Tooltip.Length <= NotifyIconText.MaximumLength, many.Tooltip);
        Assert.StartsWith(CycleArcPresentation.TrayTooltip(many.Selected!.Snapshot, many.Selected.DisplayName, many.Preference),
            many.Tooltip, StringComparison.Ordinal);

        var single = UsageAccountOverview.Create([Account("Personal", 40)], "Personal");
        Assert.DoesNotContain(UiText.T("Others:", "다른 계정:"), single.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryCsv_KeepsMeasuredValuesTimesAndGaps_AndNeutralisesFormulas()
    {
        var at = DateTimeOffset.Parse("2026-10-22T03:04:05+09:00");
        var view = new UsageHistoryView(
            [new(at, "s", "=cmd|' /C calc'!A0", UsageHistoryMetric.UsagePercent, 42.5m, "%", UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.None),
             new(at.AddHours(1), "s", "Credits, total", UsageHistoryMetric.BalanceChange, -3m, "credits", UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.Gap)],
            [new(at.AddMinutes(30), UsageHistoryQuality.Failure, at, "s")], at);
        var lines = UsageHistoryCsv.Write(view).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(UsageHistoryCsv.Header, lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("2026-10-21T18:04:05Z,'=cmd", lines[1], StringComparison.Ordinal);
        Assert.Contains(",42.5,%,CodexAppServer,,Observed", lines[1], StringComparison.Ordinal);
        // The failure sits between the measurements and carries no value at all.
        Assert.Equal("2026-10-21T18:34:05Z,s,,,,,,Failure", lines[2]);
        Assert.Contains("\"Credits, total\",BalanceChange,-3,credits", lines[3], StringComparison.Ordinal);
        Assert.Contains(",Gap,Observed", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void CompactModules_FitMoreColumnsInTheSameWorkArea()
    {
        var area = new ScreenRect(0, 0, 800, 900);
        var normal = WidgetGridLayout.For(5, 22, 90, area);
        var compact = WidgetGridLayout.For(5, 22, [90, 90, 90, 90, 90], area, 0, WidgetGridLayout.CompactModuleWidth);
        Assert.True(compact.Columns > normal.Columns);
        Assert.Equal(WidgetGridLayout.ChromeWidth + compact.Columns * WidgetGridLayout.CompactModuleWidth
            + (compact.Columns - 1) * WidgetGridLayout.SeparatorThickness, compact.Width, 3);
        // An invalid width falls back to the normal module rather than dividing by zero.
        Assert.Equal(normal.Columns, WidgetGridLayout.For(5, 22, [90, 90, 90, 90, 90], area, 0, double.NaN).Columns);
    }
}
