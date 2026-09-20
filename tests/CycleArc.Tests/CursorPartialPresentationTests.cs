using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorPartialPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void OptionalSandFailureKeepsAnAvailableMonthlySampleFresh(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = MonthlySnapshot();

            Assert.Equal(UiText.T("Updated", "업데이트됨"), CycleArcPresentation.StatusLabel(snapshot));
            Assert.Equal("72.5%", CodexRingPresentation.From(snapshot).CenterValueText);

            // The detailed popup may explain that the optional Grok allowance was
            // unavailable, while the compact status remains the monthly status.
            Assert.Equal(UiText.T("Grok usage unavailable", "Grok 사용량 확인 불가"),
                CodexDisplayFormatting.StatusText(snapshot));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void PopupAndTrayShowOnlyReportedMonthlyAllowance(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = MonthlySnapshot();
            var rows = CodexDisplayFormatting.Rows(snapshot, Now);
            var popup = CycleArcPresentation.Tooltip(snapshot);
            var tray = CycleArcPresentation.TrayTooltip(snapshot, "Fixture");

            Assert.Contains(UiText.T("Auto", "자동"), popup);
            Assert.Contains(UiText.T("Auto", "자동"), tray);
            Assert.Contains("$27.5", popup);
            Assert.Contains("$27.5", tray);
            Assert.DoesNotContain(rows, row => row.Label == UiText.T("Grok", "Grok"));
            Assert.DoesNotContain("Grok", popup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Grok", tray, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$0", popup, StringComparison.Ordinal);
            Assert.DoesNotContain("$0", tray, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void WidgetKeepsFreshRingAndMonthlyLineWhenSandIsUnavailable(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            var snapshot = MonthlySnapshot();
            var account = new CodexAccountView(
                new CodexAccountProfile("cursor-fixture", "", "Cursor fixture")
                {
                    Provider = UsageProviderId.Cursor
                },
                snapshot)
            {
                IsConnected = true
            };

            var model = WidgetAccountModel.From(account, selected: true,
                UsagePeriodPreference.Auto, Now);

            Assert.False(model.IsStale);
            Assert.Equal("72.5%", model.Ring.CenterValueText);
            Assert.Single(model.Periods);
            Assert.Contains(UiText.T("Updated", "업데이트됨"), model.StatusText);
            Assert.DoesNotContain(UiText.T("Stale data", "오래된 데이터"), model.StatusText);
            Assert.Contains("$27.5", model.Periods[0].RemainingText);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    private static CodexQuotaSnapshot MonthlySnapshot() => new(
        CodexQuotaStatus.Available,
        "pro",
        Now.AddMinutes(-3),
        Now.AddMinutes(-3),
        null,
        null,
        null,
        [
            new CodexQuotaWindow("cursor-auto", 72.5, null, Now.AddHours(8), CodexWindowKind.Other)
            {
                UsedAmount = 72.5m,
                LimitAmount = 100m,
                RemainingAmount = 27.5m,
                Unit = "USD",
                IsEnabled = true
            }
        ],
        "cursor-sand-unavailable")
    {
        Provider = UsageProviderId.Cursor
    };
}
