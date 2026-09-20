using System.Globalization;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorNamingPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void CursorAllowancesUseOfficialNamesAndKnownCadence(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            Assert.Equal("Cursor Models", CursorUsagePresentation.QuotaLabel("cursor-auto"));
            Assert.Equal("Other Models", CursorUsagePresentation.QuotaLabel("cursor-api"));
            Assert.Equal("Grok Bot", CursorUsagePresentation.QuotaLabel("cursor-sand"));

            Assert.Equal(UiText.T("Monthly", "월간"), CursorUsagePresentation.QuotaPeriodLabel("cursor-auto"));
            Assert.Equal(UiText.T("Monthly", "월간"), CursorUsagePresentation.QuotaPeriodLabel("cursor-api"));
            Assert.Equal(UiText.T("Weekly", "주간"), CursorUsagePresentation.QuotaPeriodLabel("cursor-sand"));

            Assert.Equal(UiText.T("Cursor Models · Monthly", "Cursor Models · 월간"),
                CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"));
            Assert.Equal(UiText.T("Other Models · Monthly", "Other Models · 월간"),
                CursorUsagePresentation.QuotaDisplayLabel("cursor-api"));
            Assert.Equal(UiText.T("Grok Bot · Weekly", "Grok Bot · 주간"),
                CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void UnknownCursorAllowanceDoesNotInventACadence(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var id = "cursor-future-bucket";
            Assert.Null(CursorUsagePresentation.QuotaPeriodLabel(id));
            Assert.Equal(CursorUsagePresentation.QuotaLabel(id), CursorUsagePresentation.QuotaDisplayLabel(id));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void PopupRowsTooltipAndWidgetUseTheCombinedCursorLabels(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = CursorSnapshot(
                Window("cursor-auto", 25, 75, 100, 25, Now.AddHours(2)),
                Window("cursor-api", 40, 30, 50, 20, Now.AddHours(3)),
                Window("cursor-sand", 60, 4, 10, 6, Now.AddDays(1)));

            var rows = CodexDisplayFormatting.Rows(snapshot, Now);
            Assert.Contains(rows, row => row.Label == CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"));
            Assert.Contains(rows, row => row.Label == CursorUsagePresentation.QuotaDisplayLabel("cursor-api"));
            Assert.Contains(rows, row => row.Label == CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"));

            var tooltip = CycleArcPresentation.Tooltip(snapshot);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"), tooltip);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-api"), tooltip);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"), tooltip);

            var widgetStatus = WidgetStatusFormatter.CodexLine(snapshot);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"), widgetStatus);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-api"), widgetStatus);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"), widgetStatus);

            var account = new CodexAccountView(
                new CodexAccountProfile("cursor-naming", "", "Cursor naming") { Provider = UsageProviderId.Cursor },
                snapshot)
            {
                IsConnected = true
            };
            var widget = WidgetAccountModel.From(account, selected: true, UsagePeriodPreference.Auto, Now);
            Assert.Equal(new[]
            {
                CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"),
                CursorUsagePresentation.QuotaDisplayLabel("cursor-api"),
                CursorUsagePresentation.QuotaDisplayLabel("cursor-sand")
            }, widget.Periods.Select(period => period.PeriodLabel));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void RingCaptionUsesTheSelectedAllowanceAndItsCadence(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            foreach (var (id, period) in new[]
            {
                ("cursor-auto", UiText.T("Monthly used", "월간 사용")),
                ("cursor-api", UiText.T("Monthly used", "월간 사용")),
                ("cursor-sand", UiText.T("Weekly used", "주간 사용"))
            })
            {
                var ring = CodexRingPresentation.From(CursorSnapshot(Window(id, 42, 58, 100, 42, Now.AddHours(1))));
                Assert.Equal(CursorUsagePresentation.QuotaLabel(id) + Environment.NewLine + period, ring.CenterSubLabel);
            }
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void CursorRowsKeepAmountsResetDetailsAndWindowMetadataUnchanged()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var reset = Now.AddHours(4);
            var window = new CodexQuotaWindow("cursor-auto", 72.5, null, reset, CodexWindowKind.Other)
            {
                UsedAmount = 72.5m,
                LimitAmount = 100m,
                RemainingAmount = 27.5m,
                Unit = "USD",
                IsEnabled = true
            };
            var rows = CodexDisplayFormatting.Rows(CursorSnapshot(window), Now);
            var row = Assert.Single(rows, item => item.Label == CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"));

            Assert.Equal("Remaining $27.5", row.Value);
            Assert.Equal(CodexDisplayFormatting.ResetStamp(reset), row.Detail);
            Assert.Equal(CodexDeadlineFormatting.ResetStampTooltip(reset), row.Tooltip);
            Assert.Equal("cursor-auto", window.LimitId);
            Assert.Equal(CodexWindowKind.Other, window.Kind);
            Assert.Null(window.WindowDurationMinutes);
            Assert.Equal(reset, window.ResetsAt);
            Assert.Equal(27.5m, window.RemainingAmount);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void CodexAndClaudeCompactLabelsRemainUnchanged(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var codexWindow = new CodexQuotaWindow("codex", 42, CodexWindowClassifier.FiveHourMinutes,
                Now.AddHours(1), CodexWindowKind.FiveHour);
            var claudeWindow = new CodexQuotaWindow("claude", 42, CodexWindowClassifier.WeeklyMinutes,
                Now.AddHours(1), CodexWindowKind.Weekly);

            Assert.Equal(UiText.T("Codex 5-hour usage", "Codex 5시간 사용"),
                CodexDisplayFormatting.CompactWindowKindLabel(codexWindow, UsageProviderId.Codex));
            Assert.Equal(UiText.T("Claude weekly usage", "Claude 주간 사용"),
                CodexDisplayFormatting.CompactWindowKindLabel(claudeWindow, UsageProviderId.Claude));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void TrayPrioritizesThreeNamedAllowancesAndKeepsTimestampWithinNativeLimit(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = CursorSnapshot(
                Window("cursor-auto", 25, 75, 100, 25, Now.AddHours(2)),
                Window("cursor-api", 40, 30, 50, 20, Now.AddHours(3)),
                Window("cursor-on-demand", 10, 18, 20, 2, Now.AddDays(2)),
                Window("cursor-sand", 60, 4, 10, 6, Now.AddDays(1)));

            var tray = CycleArcPresentation.TrayTooltip(snapshot, "Fixture");

            Assert.True(tray.Length <= NotifyIconText.MaximumLength, tray);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"), tray);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-api"), tray);
            Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"), tray);
            Assert.Contains(Now.AddMinutes(-3).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), tray);
            Assert.Contains(UiText.T("Updated", "업데이트"), tray);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    private static CodexQuotaSnapshot CursorSnapshot(params CodexQuotaWindow[] windows) => new(
        CodexQuotaStatus.Available,
        "pro",
        Now.AddMinutes(-3),
        Now.AddMinutes(-3),
        null,
        null,
        null,
        windows,
        null)
    {
        Provider = UsageProviderId.Cursor
    };

    private static CodexQuotaWindow Window(string id, double usedPercent, decimal used,
        decimal limit, decimal remaining, DateTimeOffset reset) => new(id, usedPercent, null, reset, CodexWindowKind.Other)
    {
        UsedAmount = used,
        LimitAmount = limit,
        RemainingAmount = remaining,
        Unit = "USD",
        IsEnabled = true
    };
}
