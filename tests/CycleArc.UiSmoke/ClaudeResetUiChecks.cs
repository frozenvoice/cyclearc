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
            var five = new CodexQuotaWindow("five_hour", 51.12, 300, supplied, CodexWindowKind.FiveHour)
            { ResetObservation = new(state, ClaudeResetSource.Server, origin, reset) };
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
                Check(line.ResetTooltip?.Contains(UiText.T("Claude server", "Claude 서버")) == true, "Reset source absent from widget tooltip.");
                Check(line.ResetTooltip!.Contains(origin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")), "Original reset observation clock absent.");
                Check(widget.Modules.Single().RingValueText.ActualHeight > 0, "Reset states displaced the usage ring.");
                flyout.ApplyWindowSettings(settings); flyout.BindAccounts([account], account.Profile.Id, false);
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, stem is null ? null : stem + "-detail.png");
                var rows = CodexDisplayFormatting.Rows(snapshot, now);
                Check(rows.Any(r => r.Tooltip?.Contains(origin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")) == true), "Original provenance absent from detail rows.");
                Check(flyout.DetailRows.Select(border => (Grid)border.Child)
                    .Select(grid => ((StackPanel)grid.Children[1]).Children[0]).OfType<TextBlock>()
                    .Any(t => t.Text == "51.12% / 48.88%"), "Reset state changed current measured usage.");
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
