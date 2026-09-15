using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class UsagePeriodUiChecks
{
    public static void Run(App app, string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            foreach (var provider in Enum.GetValues<UsageProviderId>())
            {
                var snapshot = Sample(provider);
                var account = new CodexAccountView(new CodexAccountProfile("period-fixture",
                    @"C:/CycleArc-Samples/period", UiText.T("Period sample", "기간 예시"), true)
                    { Provider = provider }, snapshot, "period@example.invalid") { IsConnected = true };
                var flyout = new FlyoutWindow();
                var widget = new FloatingWidget();
                var changes = new List<UsagePeriodPreference>();
                flyout.UsagePeriodChanged += changes.Add;
                try
                {
                    foreach (var preference in Enum.GetValues<UsagePeriodPreference>())
                    {
                        var overview = UsageAccountOverview.Create([account], account.Profile.Id, preference);
                        flyout.BindAccounts(overview.Accounts, overview.SelectedId, false, overview.Preference);
                        widget.BindAccount(overview.Selected, overview.Preference);
                        CheckProjection(flyout, widget, overview, preference == UsagePeriodPreference.Weekly ? 74 : 12);
                        foreach (var zoom in new[] { 80, 100, 150 })
                        {
                            flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                            var preview = directory is not null && zoom == 100 && theme != AppTheme.System
                                && provider == UsageProviderId.Claude
                                ? Path.Combine(directory, $"period-{preference}-{language}-{theme}.png") : null;
                            AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, preview);
                            CheckChoiceLayout(flyout);
                            count++;
                        }
                    }
                    if (changes.Count != 0) throw new InvalidOperationException("Binding a period fired a user change.");

                    // Native selection automation also covers the checked-state path used by arrow keys.
                    Select(flyout, "FiveHourPeriodButton");
                    Check(flyout.UsagePeriod == UsagePeriodPreference.FiveHour && changes.Count == 1,
                        "Accessible period selection did not emit exactly one change.");
                    ((IInvokeProvider)new ButtonAutomationPeer((Button)flyout.FindName("CyclePeriodButton"))
                        .GetPattern(PatternInterface.Invoke)!).Invoke();
                    flyout.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Check(flyout.UsagePeriod == UsagePeriodPreference.Weekly && changes.Count == 2
                        && Text(flyout, "CodexRingValueText") == "74%", "Ring did not switch to weekly usage.");
                    Select(flyout, "WeeklyPeriodButton");
                    Check(changes.Count == 2, "Selecting the same period emitted an extra change.");

                    // Rebinding another account retains the global preference while resolving its own values.
                    var weeklyOnly = account with { Profile = account.Profile with { Id = "weekly-only" },
                        Snapshot = snapshot with { Windows = [snapshot.Windows[1]] } };
                    flyout.BindAccounts([weeklyOnly], "weekly-only", false, UsagePeriodPreference.FiveHour);
                    Check(Text(flyout, "CodexRingValueText") == "74%"
                        && ((TextBlock)flyout.FindName("UsagePeriodFallback")).Visibility == Visibility.Visible
                        && !((Button)flyout.FindName("CyclePeriodButton")).IsEnabled,
                        "Missing requested window did not explain its fallback.");
                    AccountUiChecks.Render(flyout, 440, null, null);
                    CheckChoiceLayout(flyout);
                    foreach (var status in new[] { CodexQuotaStatus.Stale, CodexQuotaStatus.Refreshing })
                    {
                        var overview = UsageAccountOverview.Create([account with { Snapshot = snapshot with { Status = status } }],
                            account.Profile.Id, UsagePeriodPreference.Weekly);
                        flyout.BindAccounts(overview.Accounts, overview.SelectedId, status == CodexQuotaStatus.Refreshing, overview.Preference);
                        widget.BindAccount(overview.Selected, overview.Preference);
                        CheckProjection(flyout, widget, overview, 74);
                    }
                    var unknown = snapshot with { Windows = snapshot.Windows.Select(w => w with { UsedPercent = (double?)null }).ToArray() };
                    flyout.Bind(unknown, preference: UsagePeriodPreference.FiveHour);
                    widget.Bind(unknown, UsagePeriodPreference.FiveHour);
                    Check(Text(flyout, "CodexRingValueText") == "?" && Text(widget, "CodexValue") == "?"
                        && !((Button)flyout.FindName("CyclePeriodButton")).IsEnabled, "Unknown usage was fabricated.");
                    Check(snapshot.LastSuccessfulRefresh == account.Snapshot.LastSuccessfulRefresh
                        && snapshot.Windows[0].UsedPercent == 12 && snapshot.Windows[1].UsedPercent == 74,
                        "Display selection changed the sample.");
                }
                finally { flyout.Close(); widget.Close(); }
            }
        }
        CheckAppPersistence(app);
        Console.WriteLine($"PASS: {count} usage-period WPF renders; shared detail/widget/tray projection, accessible selection, ring switch, account fallback, unknown/stale/refreshing states and App settings persistence.");
    }

    private static void CheckProjection(FlyoutWindow flyout, FloatingWidget widget, UsageAccountOverview overview, int expected)
    {
        var caption = expected == 12 ? UiText.T("5-hour used", "5시간 사용") : UiText.T("Weekly used", "주간 사용");
        Check(Text(flyout, "CodexRingValueText") == $"{expected}%" && Text(flyout, "CodexRingSubLabel") == caption,
            "Detail uses the wrong period.");
        Check(Text(widget, "CodexValue").StartsWith($"{expected}%", StringComparison.Ordinal)
            && Text(widget, "CodexLabel") == caption, "Widget uses the wrong period.");
        Check(overview.Tooltip.Contains($"{expected}%", StringComparison.Ordinal)
            && overview.Tooltip.Length <= 127, "Tray tooltip lost the selected period or exceeded its native limit.");
        var selectedOnly = overview.Snapshot with
        {
            Windows = overview.Snapshot.Windows.Where(w => w.UsedPercent == expected).ToArray()
        };
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        foreach (var light in new[] { false, true })
        {
            using var icon = TrayIconRenderer.Render(overview.Snapshot, style, 24, lightTaskbar: light, preference: overview.Preference);
            using var reference = TrayIconRenderer.Render(selectedOnly, style, 24, lightTaskbar: light);
            using var actualBitmap = icon.ToBitmap();
            using var expectedBitmap = reference.ToBitmap();
            for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++)
                Check(actualBitmap.GetPixel(x, y) == expectedBitmap.GetPixel(x, y), "Tray icon rendered a different period.");
        }
    }

    private static void CheckChoiceLayout(FlyoutWindow flyout)
    {
        var label = (TextBlock)flyout.FindName("UsagePeriodLabel");
        var row = (FrameworkElement)label.Parent;
        double lastRight = label.TranslatePoint(new Point(label.ActualWidth, 0), row).X;
        foreach (var name in new[] { "AutoPeriodButton", "FiveHourPeriodButton", "WeeklyPeriodButton" })
        {
            var choice = (RadioButton)flyout.FindName(name);
            var left = choice.TranslatePoint(new Point(), row).X;
            var right = choice.TranslatePoint(new Point(choice.ActualWidth, 0), row).X;
            Check(left >= lastRight - 1 && right <= row.ActualWidth + 1
                && choice.ActualHeight >= 30 && choice.Focusable, "Period controls overlap or are inaccessible.");
            lastRight = right;
        }
    }

    private static void CheckAppPersistence(App app)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settingsField = typeof(App).GetField("_settings", flags)!;
        var storeField = typeof(App).GetField("_settingsStore", flags)!;
        var flyoutField = typeof(App).GetField("_flyout", flags)!;
        var previous = new[] { settingsField.GetValue(app), storeField.GetValue(app), flyoutField.GetValue(app) };
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-period-ui-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        FlyoutWindow? flyout = null;
        try
        {
            settingsField.SetValue(app, new AppSettings());
            storeField.SetValue(app, store);
            flyoutField.SetValue(app, null);
            typeof(App).GetMethod("EnsureFlyout", flags)!.Invoke(app, null);
            flyout = (FlyoutWindow)flyoutField.GetValue(app)!;
            flyout.Bind(Sample(UsageProviderId.Claude));
            Select(flyout, "WeeklyPeriodButton");
            var restored = new SettingsStore(Path.Combine(root, "settings.json")).Load();
            Check(restored.UsagePeriod == UsagePeriodPreference.Weekly, "The App event did not persist the period.");
            var settingsWindow = new SettingsWindow(restored);
            try
            {
                ((Button)settingsWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(restored.UsagePeriod == UsagePeriodPreference.Weekly, "Unrelated settings save lost the period.");
            }
            finally { settingsWindow.Close(); }
            var reopened = new FlyoutWindow();
            try
            {
                reopened.Bind(Sample(UsageProviderId.Claude), preference: restored.UsagePeriod);
                Check(Text(reopened, "CodexRingValueText") == "74%", "Reload did not retain the saved period.");
            }
            finally { reopened.Close(); }

            // A failed atomic save must not leave the detail ahead of the tray/widget.
            var blockedDirectory = Path.Combine(root, "file-instead-of-directory");
            File.WriteAllText(blockedDirectory, "isolated fixture");
            storeField.SetValue(app, new SettingsStore(Path.Combine(blockedDirectory, "settings.json")));
            var saveFailed = false;
            try { Select(flyout, "FiveHourPeriodButton"); }
            catch (IOException) { saveFailed = true; }
            Check(saveFailed && ((AppSettings)settingsField.GetValue(app)!).UsagePeriod == UsagePeriodPreference.Weekly
                && flyout.UsagePeriod == UsagePeriodPreference.Weekly
                && Text(flyout, "CodexRingValueText") == "74%"
                && store.Load().UsagePeriod == UsagePeriodPreference.Weekly,
                "Failed period save did not restore the previous display and saved setting.");
        }
        finally
        {
            flyout?.Close();
            settingsField.SetValue(app, previous[0]);
            storeField.SetValue(app, previous[1]);
            flyoutField.SetValue(app, previous[2]);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Select(FlyoutWindow flyout, string name) =>
        ((ISelectionItemProvider)new RadioButtonAutomationPeer((RadioButton)flyout.FindName(name))
            .GetPattern(PatternInterface.SelectionItem)!).Select();

    private static string Text(Window window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }

    private static CodexQuotaSnapshot Sample(UsageProviderId provider)
    {
        var received = DateTimeOffset.Now.AddMinutes(-12);
        return new(CodexQuotaStatus.Available, null, received, received, null, null, null,
            [new("five_hour", 12, 300, received.AddHours(2), CodexWindowKind.FiveHour),
             new("seven_day", 74, 10080, received.AddDays(3), CodexWindowKind.Weekly)], null)
        { Provider = provider };
    }
}