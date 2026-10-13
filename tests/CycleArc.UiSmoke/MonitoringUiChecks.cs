using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Monitoring;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class MonitoringUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var themeMethod = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var scenarios = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var zoom in new[] { 80, 100, 150 })
        {
            UiText.SetLanguage(language); themeMethod.Invoke(null, [theme]);
            var samples = Enum.GetValues<UsageProviderId>().Select(provider =>
            {
                var sample = UsageCreditUiChecks.Sample(provider, "normal");
                return sample with { Profile = sample.Profile with { Id = provider.ToString(), Label = provider + " · Synthetic long account name 테스트 계정" },
                    Snapshot = sample.Snapshot with { LastSuccessfulRefresh = now, LastAttemptedRefresh = now,
                        Windows = sample.Snapshot.Windows.Select(w => w with { ResetsAt = provider == UsageProviderId.Cursor ? now.AddDays(15) : now.AddHours(2), AmountObservedAt = now }).ToArray(),
                        UsageCredits = sample.Snapshot.UsageCredits is { } credits ? credits with { ObservedAt = now } : null,
                        ExtraUsage = sample.Snapshot.ExtraUsage is { } extra ? extra with { ObservedAt = now } : null,
                        IdentityFingerprint = new string('A', 64), ClaudeResetCredits = provider == UsageProviderId.Claude ? new(true,
                            [new(2, 3, now.AddDays(-1), now.AddDays(2), false, true, false, ["five_hour", "seven_day"])], now) : null }, IsConnected = true };
            }).ToArray();
            var settings = new AppSettings { WidgetFinancialSummaryEnabled = true, WidgetResetSummaryEnabled = true, WidgetZoomPercent = zoom, FlyoutZoomPercent = zoom };
            var widget = new FloatingWidget(); var flyout = new FlyoutWindow();
            try
            {
                widget.Apply(settings); widget.BindAccounts(samples, "Claude", workAreas: WidgetFixture.Desktop, now: now);
                var stem = directory is not null && zoom == 100 ? Path.Combine(directory, $"monitoring-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}") : null;
                WidgetFixture.RenderWidget(widget, stem is null ? null : stem + "-widget.png");
                Require(widget.Modules.All(m => m.FinancialSummary.Visibility == Visibility.Visible), "Expected financial summary missing.");
                foreach (var module in widget.Modules)
                {
                    Require(module.RingValueText.ActualHeight > 0 && module.FinancialSummary.ActualHeight > 0, "Summary replaced ring or disappeared.");
                    Require(module.FinancialSummary.ActualWidth <= module.ActualWidth, "Summary escapes account module.");
                    Require(module.FinancialSummary.ToolTip is string { Length: > 0 }, "Summary tooltip missing.");
                }
                flyout.ApplyWindowSettings(settings);
                flyout.HistoryReader = (account, span) => new(
                    [new(now.AddHours(-2), "spend", "Extra usage", UsageHistoryMetric.ActualSpend, 1, "USD", UsageHistorySource.ClaudeOAuth, null, UsageHistoryDiscontinuity.None),
                     new(now.AddHours(-1), "spend", "Extra usage", UsageHistoryMetric.ActualSpend, 2, "USD", UsageHistorySource.ClaudeOAuth, null, UsageHistoryDiscontinuity.None),
                     new(now, "spend", "Extra usage", UsageHistoryMetric.ActualSpend, 3.2m, "USD", UsageHistorySource.ClaudeOAuth, null, UsageHistoryDiscontinuity.None),
                     new(now, "spend", "Extra usage", UsageHistoryMetric.SpendingLimit, 10m, "USD", UsageHistorySource.ClaudeOAuth, null, UsageHistoryDiscontinuity.None)],
                    [new(now, UsageHistoryQuality.Observed, now), new(now, UsageHistoryQuality.Failure, now, "unrelated-plan")], now.AddHours(-2));
                flyout.BindAccounts(samples, "Claude", false);
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                var panel = (UsageHistoryPanel)((StackPanel)flyout.FindName("HistoryHost")).Children[0];
                panel.IsExpanded = true;
                for (var i = 0; i < 10; i++)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null); Thread.Sleep(5);
                }
                ((Button)flyout.FindName("CreditExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, stem is null ? null : stem + "-detail.png");
                Require(((TextBlock)flyout.FindName("ResetCreditsCount")).Text == "2", "Claude reset count incorrect.");
                Require(((Button)flyout.FindName("CreditHelpButton")).Visibility == Visibility.Visible, "Claude reset count hidden with its parent.");
                string? opened = null; flyout.OpenExternalForTest = uri => { opened = uri; };
                ((Button)flyout.FindName("ClaudeResetUsageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(opened == "https://claude.ai/settings/usage", "Claude reset action didn't open official Usage.");
                Require(AccountUiChecks.Descendants<Button>((ItemsControl)flyout.FindName("CreditExpiryRows")).All(b => b.Content?.ToString() is not ("Use reset" or "리셋권 사용")), "Claude exposed a redemption action.");
                Require(AccountUiChecks.Descendants<TextBlock>(panel).Any(t => t.Text.Contains("3.2")), "Measured history was not rendered.");
                Require(AccountUiChecks.Descendants<ComboBox>(panel).Last().Items.Count == 2, "Spend and spending cap were mixed into one series.");
                Require(!AccountUiChecks.Descendants<TextBlock>(panel).Any(t => t.Text.Contains(UiText.T("Check failed", "조회 실패"))), "Unrelated metric failure leaked into selected history.");
                var range = AccountUiChecks.Descendants<ComboBox>(panel).First();
                Require(range.Items.Count == 3, "24-hour/7-day/30-day ranges missing.");
                // A different/hidden account must immediately clear the previous account's history.
                flyout.BindAccounts([samples[1] with { Snapshot = samples[1].Snapshot with { TechnicalDetail = "claude-live-identity-mismatch" } }], "Claude", false);
                Require(!AccountUiChecks.Descendants<TextBlock>(panel).Any(t => t.Text.Contains("3.2")), "Identity mismatch retained history.");
                Require(AccountUiChecks.Descendants<FrameworkElement>(panel).All(e => e.ToolTip is not string tip || !tip.Contains("3.2")), "Identity mismatch retained previous history tooltip.");
                settings.WidgetFinancialSummaryEnabled = settings.WidgetResetSummaryEnabled = false;
                widget.Apply(settings); widget.BindAccounts(samples, "Claude", workAreas: WidgetFixture.Desktop);
                Require(widget.Modules.All(m => m.FinancialSummary.Visibility == Visibility.Collapsed), "Off summaries occupy space.");
                scenarios++;
            }
            finally { widget.Close(); flyout.Close(); }
        }
        UiText.SetLanguage(UiLanguage.English); themeMethod.Invoke(null, [AppTheme.Dark]);
        var settingsWindow = new SettingsWindow(new AppSettings());
        AccountUiChecks.Render(settingsWindow, 700, 800, null);
        Require(!settingsWindow.HasPendingChanges, "New financial controls spuriously modify settings.");
        settingsWindow.Close();
        var extreme = new HistoryPlot();
        extreme.Set([new(now.AddMinutes(-1), "balance", "Credits", UsageHistoryMetric.CreditBalance, decimal.MinValue, "credits",
            UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.None),
            new(now, "balance", "Credits", UsageHistoryMetric.CreditBalance, decimal.MaxValue, "credits",
            UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.Gap)], [], now.AddHours(-1), now);
        extreme.Measure(new Size(400, 96)); extreme.Arrange(new Rect(0, 0, 400, 96));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(extreme);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"PASS: monitoring {scenarios} EN/KO Dark/Light 80/100/150% production views; history, identity clearing, read-only Claude reset action, optional widget summary. Host DPI={System.Windows.Media.VisualTreeHelper.GetDpi(new Border()).PixelsPerDip * 96}; process working set={process.WorkingSet64 / 1048576} MiB.");
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
