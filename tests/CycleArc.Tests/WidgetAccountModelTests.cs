using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class WidgetAccountModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    public WidgetAccountModelTests() => UiText.SetLanguage(UiLanguage.English);

    private static CodexAccountView Account(string id, string label, UsageProviderId provider,
        CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label) { Provider = provider }, snapshot with { Provider = provider });

    private static CodexQuotaSnapshot Both(UsageProviderId provider = UsageProviderId.Codex) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
        [
            new("five", 85, CodexWindowClassifier.FiveHourMinutes, Now.AddMinutes(35), CodexWindowKind.FiveHour),
            new("week", 23, CodexWindowClassifier.WeeklyMinutes, Now.AddHours(12).AddMinutes(45), CodexWindowKind.Weekly)
        ], null) { Provider = provider };

    [Fact]
    public void EveryProvidedPeriodIsShown_NotOnlyTheRepresentedOne()
    {
        var model = WidgetAccountModel.From(Account("a", "Work", UsageProviderId.Claude, Both(UsageProviderId.Claude)),
            selected: true, UsagePeriodPreference.Auto, Now);
        Assert.Equal(2, model.Periods.Count);
        Assert.Equal(new[] { CodexWindowKind.FiveHour, CodexWindowKind.Weekly }, model.Periods.Select(p => p.Kind));
        Assert.Equal("Left 15%", model.Periods[0].RemainingText);
        Assert.Equal("in 35m", model.Periods[0].ResetText);
        Assert.Equal("Left 77%", model.Periods[1].RemainingText);
        Assert.Equal("in 12h 45m", model.Periods[1].ResetText);
    }

    [Fact]
    public void TheRingShowsUsageWhileTheLinesShowWhatIsLeft()
    {
        var model = WidgetAccountModel.From(Account("a", "Work", UsageProviderId.Codex, Both()),
            selected: false, UsagePeriodPreference.Auto, Now);
        Assert.Equal("85%", model.Ring.CenterValueText);
        Assert.Contains("used", model.Ring.CenterSubLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Left 15%", model.Periods.Single(p => p.IsRepresentative).RemainingText);
    }

    [Fact]
    public void EachPeriodKeepsItsOwnResetTime()
    {
        var model = WidgetAccountModel.From(Account("a", "Work", UsageProviderId.Codex, Both()),
            selected: false, UsagePeriodPreference.Auto, Now);
        var five = model.Periods.Single(p => p.Kind == CodexWindowKind.FiveHour);
        var weekly = model.Periods.Single(p => p.Kind == CodexWindowKind.Weekly);
        Assert.Equal(CodexDeadlineFormatting.ResetStampTooltip(Now.AddMinutes(35)), five.ResetTooltip);
        Assert.Equal(CodexDeadlineFormatting.ResetStampTooltip(Now.AddHours(12).AddMinutes(45)), weekly.ResetTooltip);
        Assert.NotEqual(five.ResetText, weekly.ResetText);
    }

    [Theory]
    [InlineData(UsagePeriodPreference.Auto, CodexWindowKind.FiveHour)]
    [InlineData(UsagePeriodPreference.FiveHour, CodexWindowKind.FiveHour)]
    [InlineData(UsagePeriodPreference.Weekly, CodexWindowKind.Weekly)]
    public void TheExistingPeriodPreferenceChoosesTheRing_WithoutHidingTheOtherPeriod(
        UsagePeriodPreference preference, CodexWindowKind expected)
    {
        var model = WidgetAccountModel.From(Account("a", "Work", UsageProviderId.Codex, Both()),
            selected: false, preference, Now);
        Assert.Equal(expected, model.Ring.Window!.Kind);
        Assert.True(model.Periods[0].IsRepresentative);
        Assert.Equal(expected, model.Periods[0].Kind);
        Assert.Equal(2, model.Periods.Count);
        Assert.Single(model.Periods, p => p.IsRepresentative);
    }

    [Fact]
    public void AProviderThatReportsOnePeriodDoesNotGainAnInventedOne()
    {
        var weekly = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null,
            [new("week", 41, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(3).AddHours(2), CodexWindowKind.Weekly)], null);
        var model = WidgetAccountModel.From(Account("a", "Lab", UsageProviderId.Codex, weekly), false, UsagePeriodPreference.Auto, Now);
        Assert.Single(model.Periods);
        Assert.Equal(CodexWindowKind.Weekly, model.Periods[0].Kind);
        Assert.Equal("in 3d 2h", model.Periods[0].ResetText);
    }

    [Fact]
    public void AnUnknownPercentageStaysUnknownInsteadOfBecomingZeroOrFull()
    {
        var unknown = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, Now, Now, null, null, null,
            [new("five", null, CodexWindowClassifier.FiveHourMinutes, null, CodexWindowKind.FiveHour)], null);
        var model = WidgetAccountModel.From(Account("a", "Lab", UsageProviderId.Codex, unknown), false, UsagePeriodPreference.Auto, Now);
        Assert.Equal("?", model.Ring.CenterValueText);
        Assert.False(model.Ring.IsAvailable);
        Assert.Equal("Left ?", model.Periods[0].RemainingText);
        Assert.Equal(UiText.ResetNotProvided, model.Periods[0].ResetText);
        Assert.Null(model.Periods[0].ResetTooltip);
    }

    [Fact]
    public void AwaitingUsageKeepsItsModuleAndSaysWhy()
    {
        var waiting = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "claude-connected-waiting")
            with { Provider = UsageProviderId.Claude };
        var account = new CodexAccountView(
            new CodexAccountProfile("c", "", "Work") { Provider = UsageProviderId.Claude }, waiting) { IsConnected = true };
        var model = WidgetAccountModel.From(account, false, UsagePeriodPreference.Auto, Now);
        Assert.Empty(model.Periods);
        Assert.Equal("Awaiting usage", model.StatusText);
        Assert.Equal("?", model.Ring.CenterValueText);
    }

    [Fact]
    public void StaleValuesAreMarkedSoTheyCannotReadAsFresh()
    {
        var stale = Both(UsageProviderId.Claude) with { Status = CodexQuotaStatus.Stale };
        var model = WidgetAccountModel.From(Account("c", "Work", UsageProviderId.Claude, stale), false, UsagePeriodPreference.Auto, Now);
        Assert.True(model.IsStale);
        Assert.Equal("Stale data", model.StatusText);
        Assert.Equal(2, model.Periods.Count);
    }

    [Fact]
    public void AuthenticationFailureNamesTheStateInText()
    {
        var failed = Both(UsageProviderId.Claude) with { TechnicalDetail = "claude-live-auth-required" };
        var model = WidgetAccountModel.From(Account("c", "Work", UsageProviderId.Claude, failed), false, UsagePeriodPreference.Auto, Now);
        Assert.Equal(ClaudeUsagePresentation.FailureLabel("claude-live-auth-required"), model.StatusText);
    }

    [Fact]
    public void ADisconnectedAccountShowsNoCachedNumbers()
    {
        var signedOut = CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut);
        var model = WidgetAccountModel.From(Account("a", "Old", UsageProviderId.Codex, signedOut), false, UsagePeriodPreference.Auto, Now);
        Assert.Empty(model.Periods);
        Assert.Equal("Sign in required", model.StatusText);
    }

    [Fact]
    public void AMismatchedIdentityNeverSurfacesThePreviousBindingsNumbers()
    {
        var mismatch = Both() with { TechnicalDetail = "codex-identity-mismatch" };
        var model = WidgetAccountModel.From(Account("a", "Main", UsageProviderId.Codex, mismatch), false, UsagePeriodPreference.Auto, Now);
        Assert.Empty(model.Periods);
        Assert.Equal(CodexIdentityPresentation.Label(mismatch), model.StatusText);
    }

    [Fact]
    public void AccountsKeepTheirManagementOrderAndAreNeverCombined()
    {
        CodexAccountView[] accounts =
        [
            Account("a", "Main", UsageProviderId.Codex, Both()),
            Account("b", "Kakao", UsageProviderId.Codex, Both() with { Windows = [new("week", 100, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(1), CodexWindowKind.Weekly)] }),
            Account("c", "Personal Claude", UsageProviderId.Claude, Both(UsageProviderId.Claude))
        ];
        var models = WidgetAccountModel.All(accounts, "c", UsagePeriodPreference.Auto, Now);
        Assert.Equal(new[] { "Main", "Kakao", "Personal Claude" }, models.Select(m => m.DisplayName));
        Assert.Equal(new[] { false, false, true }, models.Select(m => m.IsSelected));
        Assert.Equal(new[] { "a", "b", "c" }, models.Select(m => m.ProfileId));
        // Two Codex profiles stay two modules with their own numbers.
        Assert.Equal("85%", models[0].Ring.CenterValueText);
        Assert.Equal("100%", models[1].Ring.CenterValueText);
    }

    [Fact]
    public void SameProviderAccountsWithTheSameNameStayDistinctModules()
    {
        CodexAccountView[] accounts =
        [
            Account("a", "Work", UsageProviderId.Claude, Both(UsageProviderId.Claude)),
            Account("b", "Work", UsageProviderId.Claude, Both(UsageProviderId.Claude))
        ];
        var models = WidgetAccountModel.All(accounts, "a", UsagePeriodPreference.Auto, Now);
        Assert.Equal(2, models.Count);
        Assert.Equal(new[] { "a", "b" }, models.Select(m => m.ProfileId));
    }

    [Fact]
    public void TheFullNameAndSourceStayAvailableInTheTooltip()
    {
        var name = "a-very-long-synthetic-widget-account-nickname-for-trimming";
        var model = WidgetAccountModel.From(Account("c", name, UsageProviderId.Claude, Both(UsageProviderId.Claude)),
            false, UsagePeriodPreference.Auto, Now);
        Assert.StartsWith(name, model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(ClaudeUsagePresentation.SharedScope, model.Tooltip, StringComparison.Ordinal);
        Assert.Contains(ClaudeUsagePresentation.LastReceivedText(Both(UsageProviderId.Claude)), model.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void RebindingAtALaterMinuteChangesOnlyTheCountdown()
    {
        var account = Account("a", "Main", UsageProviderId.Codex, Both());
        var first = WidgetAccountModel.From(account, false, UsagePeriodPreference.Auto, Now);
        var later = WidgetAccountModel.From(account, false, UsagePeriodPreference.Auto, Now.AddMinutes(10));
        Assert.Equal("in 35m", first.Periods[0].ResetText);
        Assert.Equal("in 25m", later.Periods[0].ResetText);
        Assert.Equal(first.Periods[0].RemainingText, later.Periods[0].RemainingText);
        Assert.Equal(first.Ring.CenterValueText, later.Ring.CenterValueText);
    }

    [Fact]
    public void APassedResetDoesNotLocallyRestartTheCycle()
    {
        var account = Account("a", "Main", UsageProviderId.Codex, Both());
        var after = WidgetAccountModel.From(account, false, UsagePeriodPreference.Auto, Now.AddHours(1));
        Assert.Equal("Awaiting refresh", after.Periods[0].ResetText);
        Assert.Equal("Left 15%", after.Periods[0].RemainingText);
        Assert.Equal("85%", after.Ring.CenterValueText);
    }

    [Fact]
    public void OnlyDisplayableAccountsReachTheWidget()
    {
        // The widget consumes the same overview as the popup and tray, so the display policy
        // for disconnected profiles is decided once, not again here.
        var overview = UsageAccountOverview.Create(
        [
            Account("a", "Main", UsageProviderId.Codex, Both()),
            Account("z", "Removed", UsageProviderId.Codex, CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut))
        ], "a");
        var models = WidgetAccountModel.All(overview.Accounts, overview.SelectedId, overview.Preference, Now);
        Assert.Equal(new[] { "a" }, models.Select(m => m.ProfileId));
    }

    [Fact]
    public void WidgetHeaderUsesAShortAccountCount()
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal("1 account", UiText.WidgetAccountsConnected(1));
        Assert.Equal("3 accounts", UiText.WidgetAccountsConnected(3));
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("계정 1", UiText.WidgetAccountsConnected(1));
            Assert.Equal("계정 5", UiText.WidgetAccountsConnected(5));
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }
}
