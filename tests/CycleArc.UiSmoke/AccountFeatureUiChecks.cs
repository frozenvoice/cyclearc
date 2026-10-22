using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Monitoring;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// Compact widget modules, the "most room now" suggestion and the trends CSV export, in the
/// production windows with synthetic accounts.
/// </summary>
internal static class AccountFeatureUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = UiText.Language;
        var now = DateTimeOffset.UtcNow;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var accounts = new[] { Codex("busy", "Busy · Codex 업무", 92, now), Codex("free", "Free · Codex 개인", 15, now), Codex("mid", "Mid", 60, now) };
                var stem = directory is null ? null : Path.Combine(directory,
                    $"compact-widget-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}");
                CheckCompactWidget(accounts, stem);
                CheckRecommendation(accounts, now);
            }
            CheckCsvExport(now);
        }
        finally { UiText.SetLanguage(previous); }
        Console.WriteLine("PASS: compact widget modules, same-service 'most room now' suggestion and trends CSV export in EN/KO Dark/Light.");
    }

    private static CodexAccountView Codex(string id, string label, double used, DateTimeOffset now)
    {
        var sample = UsageCreditUiChecks.Sample(UsageProviderId.Codex, "normal");
        return sample with
        {
            Profile = sample.Profile with { Id = id, Label = label },
            Snapshot = sample.Snapshot with
            {
                LastSuccessfulRefresh = now, LastAttemptedRefresh = now,
                Windows = [new("weekly", used, 10080, now.AddDays(3), CodexWindowKind.Weekly) { IsEnabled = true }]
            },
            IsConnected = true
        };
    }

    private static void CheckCompactWidget(CodexAccountView[] accounts, string? stem)
    {
        var widget = new FloatingWidget();
        try
        {
            widget.Apply(new AppSettings());
            widget.BindAccounts(accounts, "busy", workAreas: WidgetFixture.Desktop);
            WidgetFixture.RenderWidget(widget, null);
            var normalWidth = widget.LastLayout!.Width;
            widget.Apply(new AppSettings { WidgetCompact = true });
            widget.BindAccounts(accounts, "busy", workAreas: WidgetFixture.Desktop);
            WidgetFixture.RenderWidget(widget, stem is null ? null : stem + ".png");
            Check(widget.Modules.All(module => module.IsCompact && Math.Abs(module.Width - WidgetGridLayout.CompactModuleWidth) < 0.01),
                "Compact did not reach every module.");
            Check(widget.LastLayout!.Width < normalWidth, "Compact modules did not make the widget narrower.");
            foreach (var module in widget.Modules)
            {
                Check(module.RingValueText.ActualWidth > 0 && module.NameText.ActualWidth > 0, "A compact module lost its name or ring.");
                Check(module.RingTargetText.Visibility == Visibility.Visible && module.RingTargetText.Text.Length > 0,
                    "A compact module does not name its ring's period.");
                Check(module.Badge.Visibility == Visibility.Collapsed && module.FinancialSummary.Visibility == Visibility.Collapsed,
                    "A compact module still shows the badge or summary.");
                Check(module.NameText.ActualWidth <= module.ActualWidth, "A compact module's name escapes it.");
            }
            widget.Apply(new AppSettings());
            widget.BindAccounts(accounts, "busy", workAreas: WidgetFixture.Desktop);
            WidgetFixture.RenderWidget(widget, null);
            Check(widget.Modules.All(module => !module.IsCompact) && Math.Abs(widget.LastLayout!.Width - normalWidth) < 1,
                "Turning compact off did not restore the normal modules.");
        }
        finally { widget.Close(); }
    }

    private static void CheckRecommendation(CodexAccountView[] accounts, DateTimeOffset now)
    {
        var flyout = new FlyoutWindow();
        try
        {
            var selected = new List<string>();
            flyout.AccountSelected += selected.Add;
            flyout.BindAccounts(accounts, "busy", false);
            AccountUiChecks.Render(flyout, 440, null, null);
            var button = (Button)flyout.FindName("RecommendationButton");
            Check(button.Visibility == Visibility.Visible && ((string)button.Content).Contains("Free", StringComparison.Ordinal),
                "The popup does not suggest the Codex account with the most room.");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(selected.SequenceEqual(["free"]), "Choosing the suggestion did not select that account.");
            flyout.BindAccounts(accounts, "free", false);
            Check(button.Visibility == Visibility.Collapsed, "The popup suggests switching away from the best account.");
        }
        finally { flyout.Close(); }
    }

    private static void CheckCsvExport(DateTimeOffset now)
    {
        var flyout = new FlyoutWindow();
        var file = Path.Combine(Path.GetTempPath(), "cyclearc-export-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var account = Codex("busy", "Busy", 50, now);
            flyout.HistoryReader = (_, _) => new(
                [new(now.AddHours(-1), "w", "Weekly", UsageHistoryMetric.UsagePercent, 40, "%", UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.None),
                 new(now, "w", "Weekly", UsageHistoryMetric.UsagePercent, 50, "%", UsageHistorySource.CodexAppServer, null, UsageHistoryDiscontinuity.None)],
                [], now.AddHours(-1));
            flyout.BindAccounts([account], "busy", false);
            AccountUiChecks.Render(flyout, 440, null, null);
            var panel = (UsageHistoryPanel)((StackPanel)flyout.FindName("HistoryHost")).Children[0];
            panel.ChooseExportPathForTest = () => file;
            panel.IsExpanded = true;
            for (var i = 0; i < 20 && !AccountUiChecks.Descendants<Button>(panel).Any(b => b.IsVisible); i++)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                AccountUiChecks.Render(flyout, 440, null, null);
                Thread.Sleep(5);
            }
            var export = AccountUiChecks.Descendants<Button>(panel).Single(b => (string)b.Content == UiText.T("Export CSV…", "CSV로 내보내기…"));
            Check(export.Visibility == Visibility.Visible, "Export is hidden although measurements are shown.");
            export.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var text = File.ReadAllText(file);
            Check(text.StartsWith(UsageHistoryCsv.Header, StringComparison.Ordinal) && text.Contains(",50,%,", StringComparison.Ordinal)
                && !text.Contains("busy", StringComparison.Ordinal), "The CSV is missing measurements or includes the account identity.");
        }
        finally
        {
            flyout.Close();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
