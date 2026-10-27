using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class ClaudeResetUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var themeMethod = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var zoom in new[] { 80, 100, 150 })
        foreach (var state in Enum.GetValues<ClaudeResetObservationState>())
        {
            UiText.SetLanguage(language); themeMethod.Invoke(null, [theme]);
            var now = DateTimeOffset.UtcNow;
            var origin = now.AddMinutes(-4);
            var reset = state == ClaudeResetObservationState.Elapsed ? now.AddMinutes(-1) : now.AddHours(2);
            var supplied = state is ClaudeResetObservationState.Direct or ClaudeResetObservationState.Preserved ? reset : (DateTimeOffset?)null;
            var used = state == ClaudeResetObservationState.Elapsed ? 0d : 51.12;
            var five = new CodexQuotaWindow("five_hour", used, 300, supplied, CodexWindowKind.FiveHour)
            { ResetObservation = new(state, ClaudeResetSource.Server, origin, state == ClaudeResetObservationState.NotProvided ? null : reset) };
            var week = new CodexQuotaWindow("seven_day", 20.03, 10080, now.AddDays(3), CodexWindowKind.Weekly)
            { ResetObservation = new(ClaudeResetObservationState.Direct, ClaudeResetSource.Server, now, now.AddDays(3)) };
            var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now, now, null, null, null,
                [five, week], "claude-desktop-history") { Provider = UsageProviderId.Claude, LastSuccessfulObservationWasServer = false,
                ExtraUsage = new(true, 12.34m, 50m, false, "USD", 24.68, now),
                ClaudeResetCredits = new(true, [new(2, 3, now.AddDays(-1), now.AddDays(2), false, true, false, ["five_hour"])], now) };
            var account = new CodexAccountView(new CodexAccountProfile("synthetic-reset-ui", "", "Claude · Long account 계정 이름")
                { Provider = UsageProviderId.Claude }, snapshot) { IsConnected = true };
            var settings = new AppSettings { WidgetFinancialSummaryEnabled = true, WidgetResetSummaryEnabled = true,
                WidgetZoomPercent = zoom, FlyoutZoomPercent = zoom };
            var widget = new FloatingWidget { ShowActivated = false }; var flyout = new FlyoutWindow { ShowActivated = false };
            try
            {
                widget.Apply(settings); widget.BindAccounts([account], account.Profile.Id, workAreas: WidgetFixture.Desktop, now: now);
                var stem = directory is not null && zoom == 100 && state == ClaudeResetObservationState.Preserved
                    ? Path.Combine(directory, $"claude-reset-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}") : null;
                WidgetFixture.RenderWidget(widget, stem is null ? null : stem + "-widget.png");
                var model = WidgetAccountModel.From(account, true, now: now);
                var line = model.Periods.Single(p => p.Kind == CodexWindowKind.FiveHour);
                Check(line.ResetTooltip?.Contains("OAuth") == true, "Reset source absent from widget tooltip.");
                Check(line.ResetTooltip!.Contains(origin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")), "Original reset observation clock absent.");
                if (state == ClaudeResetObservationState.Elapsed)
                {
                    Check(line.ResetText == UiText.T("Reset unconfirmed", "리셋 미확인"), "Elapsed reset still reads as a pending quota refresh.");
                    Check(line.ResetTooltip.Contains(UiText.T("Next reset time has not been confirmed.", "새로운 리셋 시각은 아직 확인되지 않았습니다.")),
                        "Elapsed tooltip does not say the next reset is unconfirmed.");
                    Check(line.ResetTooltip.Contains("Desktop") && line.ResetTooltip.Contains(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")),
                        "Elapsed tooltip does not separate the usage receipt from the reset evidence.");
                    Check(!line.ResetTooltip.Contains(UiText.T("Awaiting refresh", "갱신 대기")), "Elapsed tooltip presents the old reset as a countdown.");
                }
                if (state == ClaudeResetObservationState.NotProvided)
                    Check(line.ResetText == UiText.ResetNotProvided, "A reset that was never supplied invented another label.");
                Check(widget.Modules.Single().RingValueText.ActualHeight > 0, "Reset states displaced the usage ring.");
                foreach (var period in widget.Modules.Single().Periods)
                    Check(period.ResetText.Visibility != Visibility.Visible
                        || period.ResetText.ActualWidth + 1 >= period.ResetText.DesiredSize.Width, "Widget reset text clipped.");
                var bars = AccountUiChecks.Descendants<Grid>(widget.Modules.Single())
                    .Where(grid => Equals(grid.Tag, "WidgetPeriodBar") && grid.Visibility == Visibility.Visible).ToArray();
                Check(bars.Length == 1 && bars[0].ActualWidth >= 20, "Reset label collapsed the period bar.");
                flyout.ApplyWindowSettings(settings); flyout.BindAccounts([account], account.Profile.Id, false);
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, stem is null ? null : stem + "-detail.png");
                var rows = CodexDisplayFormatting.Rows(snapshot, now);
                if (state == ClaudeResetObservationState.NotProvided)
                    Check(rows[0].Label != UiText.Reset && rows[1].Label != UiText.Reset,
                        "A reset that was never supplied invented a detail row.");
                else
                    Check(rows.Any(r => r.Tooltip?.Contains(origin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")) == true),
                        "Original provenance absent from detail rows.");
                var usageText = state == ClaudeResetObservationState.Elapsed ? "0% / 100%" : "51.12% / 48.88%";
                Check(flyout.DetailRows.Select(border => (Grid)border.Child)
                    .Select(grid => ((StackPanel)grid.Children[1]).Children[0]).OfType<TextBlock>()
                    .Any(t => t.Text == usageText), "Reset state changed current measured usage.");
                if (state == ClaudeResetObservationState.Elapsed)
                    Check(rows.Any(r => r.Value == UiText.T("Next reset unconfirmed", "다음 리셋 시각 미확인")),
                        "Detail still labels an elapsed reset as awaiting update.");
                foreach (var row in flyout.DetailRows.Select(border => (Grid)border.Child))
                {
                    var value = (TextBlock)((StackPanel)row.Children[1]).Children[0];
                    Check(value.ActualWidth + 1 >= value.DesiredSize.Width, "Reset detail value clipped.");
                }
                if (stem is not null)
                {
                    widget.Show(); flyout.Show();
                    flyout.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Check(widget.IsVisible && flyout.IsVisible, "Native reset detail/widget pair was not shown.");
                }
                if (state == ClaudeResetObservationState.Preserved)
                    Check(line.ResetText.Contains(UiText.T("Prior", "이전")) && rows.Any(r => r.Value.Contains(UiText.T("prior", "이전 확인"))), "Preserved reset is presented as a current server value.");
                if (state is ClaudeResetObservationState.Elapsed or ClaudeResetObservationState.Stale)
                    Check(!line.ResetText.Contains("2h"), "Unavailable period invented a countdown.");
                count++;
            }
            finally { widget.Close(); flyout.Close(); }
        }
        UiText.SetLanguage(UiLanguage.English); themeMethod.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine($"PASS: Claude reset provenance {count} EN/KO Dark/Light 80/100/150% production detail/widget states; original source/time, preserved marker, elapsed and stale, independent periods and financial summary retained.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
