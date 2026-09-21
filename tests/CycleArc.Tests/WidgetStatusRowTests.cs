using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class WidgetStatusRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void HealthyClaudeLiveReceiptHidesRowButKeepsStatusAndReceiptDetails(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Claude, CodexQuotaStatus.Available,
                ClaudeUsagePresentation.LiveDetail, Now);
            var model = WidgetAccountModel.From(Account(UsageProviderId.Claude, snapshot), selected: true, now: Now);

            Assert.False(model.ShowStatusRow);
            Assert.Equal(CycleArcPresentation.StatusLabel(snapshot), model.StatusText);
            Assert.Contains(ClaudeUsagePresentation.SourceText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
            Assert.Contains(ClaudeUsagePresentation.LastReceivedText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English, null)]
    [InlineData(UiLanguage.Korean, null)]
    [InlineData(UiLanguage.English, CursorUsagePresentation.LiveDetail)]
    [InlineData(UiLanguage.Korean, CursorUsagePresentation.LiveDetail)]
    public void HealthyCursorReceiptHidesRowButKeepsStatusAndUpdateTimestamp(
        UiLanguage language, string? detail)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Cursor, CodexQuotaStatus.Available, detail, Now);
            var model = WidgetAccountModel.From(Account(UsageProviderId.Cursor, snapshot), selected: true, now: Now);

            Assert.False(model.ShowStatusRow);
            Assert.Contains(UiText.T("Updated", "업데이트됨"), model.StatusText,
                StringComparison.Ordinal);
            Assert.Contains(CursorUsagePresentation.UpdatedText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void UnknownCursorPercentRemainsQuestionMarkWhenHealthyRowIsHidden(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Cursor, CodexQuotaStatus.Available, null, Now,
                [Window(UsageProviderId.Cursor, "cursor-auto", null)]);
            var model = WidgetAccountModel.From(Account(UsageProviderId.Cursor, snapshot), selected: false, now: Now);

            Assert.False(model.ShowStatusRow);
            Assert.Equal("?", model.Ring.CenterValueText);
            Assert.Equal("?", Assert.Single(model.Periods).RemainingText);
            Assert.Contains("?", model.Tooltip, StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [MemberData(nameof(NonHealthyCases))]
    public void NonHealthyOrUnverifiedStatesKeepStatusRowVisible(
        UiLanguage language, UsageProviderId provider, CodexQuotaStatus status,
        string? detail, bool hasWindows)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(provider, status, detail,
                hasWindows ? Now : null, hasWindows ? Windows(provider) : []);
            var model = WidgetAccountModel.From(Account(provider, snapshot), selected: false, now: Now);

            Assert.True(model.ShowStatusRow,
                $"{language}/{provider}/{status}/{detail} unexpectedly hid its status row.");
            Assert.False(string.IsNullOrWhiteSpace(model.StatusText));
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    public static IEnumerable<object?[]> NonHealthyCases()
    {
        var cases = new[]
        {
            (UsageProviderId.Claude, CodexQuotaStatus.Stale, (string?)ClaudeUsagePresentation.LiveDetail, true),
            (UsageProviderId.Cursor, CodexQuotaStatus.Stale, (string?)null, true),
            (UsageProviderId.Claude, CodexQuotaStatus.Refreshing, (string?)ClaudeUsagePresentation.LiveDetail, true),
            (UsageProviderId.Cursor, CodexQuotaStatus.Refreshing, (string?)null, true),
            (UsageProviderId.Claude, CodexQuotaStatus.Stale, (string?)"claude-live-request-failed", true),
            (UsageProviderId.Cursor, CodexQuotaStatus.Stale, (string?)"cursor-live-request-failed", true),
            (UsageProviderId.Claude, CodexQuotaStatus.Available, (string?)"claude-live-auth-required", true),
            (UsageProviderId.Cursor, CodexQuotaStatus.Available, (string?)"cursor-live-identity-mismatch", true),
            (UsageProviderId.Cursor, CodexQuotaStatus.Available, (string?)"unrecognized-source", true),
            (UsageProviderId.Claude, CodexQuotaStatus.SignedOut, (string?)"claude-live-auth-required", false),
            (UsageProviderId.Cursor, CodexQuotaStatus.SignedOut, (string?)"cursor-auth-required", false),
            (UsageProviderId.Claude, CodexQuotaStatus.Unavailable, (string?)"claude-live-identity-mismatch", false),
            (UsageProviderId.Cursor, CodexQuotaStatus.Unavailable, (string?)"cursor-live-identity-mismatch", false),
            (UsageProviderId.Claude, CodexQuotaStatus.Unavailable, (string?)"claude-connected-waiting", false),
            (UsageProviderId.Cursor, CodexQuotaStatus.Unavailable, (string?)"cursor-connected-waiting", false)
        };

        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var item in cases)
            yield return [language, item.Item1, item.Item2, item.Item3, item.Item4];
    }

    [Theory]
    [InlineData(UiLanguage.English, UsageProviderId.Claude)]
    [InlineData(UiLanguage.Korean, UsageProviderId.Claude)]
    [InlineData(UiLanguage.English, UsageProviderId.Cursor)]
    [InlineData(UiLanguage.Korean, UsageProviderId.Cursor)]
    public void SigningInKeepsStatusRowVisible(UiLanguage language, UsageProviderId provider)
    {
        UiText.SetLanguage(language);
        try
        {
            var detail = provider == UsageProviderId.Claude
                ? ClaudeUsagePresentation.LiveDetail : null;
            var snapshot = Snapshot(provider, CodexQuotaStatus.Available, detail, Now);
            var model = WidgetAccountModel.From(Account(provider, snapshot, signingIn: true),
                selected: false, now: Now);

            Assert.True(model.ShowStatusRow);
            Assert.Equal(UiText.T("Signing in…", "로그인 중…"), model.StatusText);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English, "claude-desktop-history")]
    [InlineData(UiLanguage.Korean, "claude-desktop-history")]
    [InlineData(UiLanguage.English, null)]
    [InlineData(UiLanguage.Korean, null)]
    public void ClaudeDesktopHistoryAndStatusLineReceiptsRemainVisible(
        UiLanguage language, string? detail)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Claude, CodexQuotaStatus.Available, detail, Now);
            var model = WidgetAccountModel.From(Account(UsageProviderId.Claude, snapshot),
                selected: false, now: Now);

            Assert.True(model.ShowStatusRow);
            Assert.Contains(ClaudeUsagePresentation.SourceText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
            Assert.Contains(ClaudeUsagePresentation.LastReceivedText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void CursorPartialGrokFailureRemainsVisibleAndDetailed(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = Snapshot(UsageProviderId.Cursor, CodexQuotaStatus.Available,
                "cursor-sand-unavailable", Now,
                [Window(UsageProviderId.Cursor, "cursor-auto", 42)]);
            var model = WidgetAccountModel.From(Account(UsageProviderId.Cursor, snapshot),
                selected: false, now: Now);

            Assert.True(model.ShowStatusRow);
            Assert.Contains(CursorUsagePresentation.FailureText(snapshot.TechnicalDetail), model.Tooltip,
                StringComparison.Ordinal);
            Assert.Contains(CursorUsagePresentation.UpdatedText(snapshot), model.Tooltip,
                StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English, UsageProviderId.Claude)]
    [InlineData(UiLanguage.Korean, UsageProviderId.Claude)]
    [InlineData(UiLanguage.English, UsageProviderId.Cursor)]
    [InlineData(UiLanguage.Korean, UsageProviderId.Cursor)]
    public void MissingReceiptOrWindowsConservativelyKeepsStatusRowVisible(
        UiLanguage language, UsageProviderId provider)
    {
        UiText.SetLanguage(language);
        try
        {
            var detail = provider == UsageProviderId.Claude
                ? ClaudeUsagePresentation.LiveDetail : null;
            var missingReceipt = Snapshot(provider, CodexQuotaStatus.Available, detail, null);
            var missingWindows = Snapshot(provider, CodexQuotaStatus.Available, detail, Now, []);

            Assert.True(WidgetAccountModel.From(Account(provider, missingReceipt), false, now: Now).ShowStatusRow);
            Assert.True(WidgetAccountModel.From(Account(provider, missingWindows), false, now: Now).ShowStatusRow);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Fact]
    public void CodexDefaultAvailableStatusRemainsEmptyAndHidden()
    {
        var snapshot = Snapshot(UsageProviderId.Codex, CodexQuotaStatus.Available, null, Now,
            [Window(UsageProviderId.Codex, "five", 42, CodexWindowKind.FiveHour)]);
        var model = WidgetAccountModel.From(Account(UsageProviderId.Codex, snapshot), selected: false, now: Now);

        Assert.False(model.ShowStatusRow);
        Assert.Empty(model.StatusText);
    }

    private static CodexAccountView Account(UsageProviderId provider, CodexQuotaSnapshot snapshot,
        bool signingIn = false) =>
        new(new CodexAccountProfile($"{provider.ToString().ToLowerInvariant()}-widget", "",
                $"{provider} fixture") { Provider = provider }, snapshot,
            $"{provider.ToString().ToLowerInvariant()}@example.invalid", IsSigningIn: signingIn)
        { IsConnected = true };

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, CodexQuotaStatus status,
        string? detail, DateTimeOffset? lastSuccessful,
        IReadOnlyList<CodexQuotaWindow>? windows = null) =>
        new(status, "pro", lastSuccessful, lastSuccessful, null, null, null,
            windows ?? Windows(provider), detail)
        { Provider = provider };

    private static IReadOnlyList<CodexQuotaWindow> Windows(UsageProviderId provider) =>
        [Window(provider, provider == UsageProviderId.Claude ? "five_hour" : "cursor-auto", 42,
            provider == UsageProviderId.Claude ? CodexWindowKind.FiveHour : CodexWindowKind.Other)];

    private static CodexQuotaWindow Window(UsageProviderId provider, string id, double? usedPercent,
        CodexWindowKind? kind = null) =>
        new(id, usedPercent,
            kind == CodexWindowKind.FiveHour ? CodexWindowClassifier.FiveHourMinutes : null,
            Now.AddHours(5), kind ?? (provider == UsageProviderId.Claude
                ? CodexWindowKind.FiveHour : CodexWindowKind.Other));
}
