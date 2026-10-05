using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Observations;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class ObservedTrendUiChecks
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 5, 0, 0, TimeSpan.Zero);

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var checks = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            foreach (var provider in Enum.GetValues<UsageProviderId>())
            foreach (var zoom in new[] { 80, 100, 150 })
            {
                var account = Account(provider);
                var flyout = new FlyoutWindow();
                var widget = new FloatingWidget();
                try
                {
                    var peers = Enumerable.Range(0, 5).Select(i => account with
                    {
                        Profile = account.Profile with { Id = i == 0 ? account.Profile.Id : "peer-" + i,
                            Label = i == 0 ? UiText.T("Research account with a long label", "긴 이름의 연구용 계정") : "Account " + i },
                        Observations = i == 0 ? account.Observations : null
                    }).ToArray();
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                    flyout.BindAccounts(peers, account.Profile.Id, false);
                    var output = directory is not null && zoom == 100
                        ? Path.Combine(directory, $"trend-{provider}-{language}-{theme}.png") : null;
                    AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, output);
                    var view = flyout.ObservedTrend;
                    Check(view.Chart.Data?.Points.Length == 4, "Detail lost actual observations.");
                    Check(view.TitleText.Text.Contains(UiText.T("Used", "사용")) && view.TitleText.Text.Contains('%'),
                        "Percent chart did not clearly label used units.");
                    Check(view.EmptyText.Visibility == Visibility.Collapsed && view.LastObservationText.Text.Length > 0,
                        "Populated graph omitted last observation time.");
                    var renderCount = view.Chart.GeometryBuildCount;
                    for (var repeat = 0; repeat < 40; repeat++) view.Bind(peers[0], account.Snapshot.Windows[0]);
                    AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                    Check(view.Chart.GeometryBuildCount == renderCount, "Same snapshot rebuilt graph geometry.");
                    view.ValuesExpander.IsExpanded = true;
                    Check(view.ValueRowCount == 4, "Actual-value list did not retain every observation.");
                    AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                    if (directory is not null && zoom == 100)
                        AccountUiChecks.RenderCurrent(view, Path.Combine(directory, $"trend-values-{provider}-{language}-{theme}.png"));
                    var rowsBuilt = view.ValuesBuildCount;
                    for (var repeat = 0; repeat < 40; repeat++) view.Bind(peers[0], account.Snapshot.Windows[0]);
                    Check(view.ValuesBuildCount == rowsBuilt, "Same snapshot reformatted the expanded list.");
                    Check(AutomationProperties.GetName(view.Chart).Length > 0
                        && AutomationProperties.GetName(view.MetricPicker).Length > 0, "Graph accessibility labels missing.");
                    if (provider == UsageProviderId.Cursor)
                    {
                        Check(view.MetricPicker.Items.Count == 3, "Money and percent were not separate choices.");
                        Check(VisualText(view.MetricPicker).Any(text => text == UiText.T("Used (%)", "사용 (%)")),
                            "The metric selection template exposed its object instead of the metric label.");
                        view.MetricPicker.SelectedIndex = 1;
                        Check(view.Chart.Data?.Metric == QuotaObservationMetric.UsedAmount
                            && view.TitleText.Text.Contains("USD"), "Money chart reused percentage units.");
                        view.MetricPicker.SelectedIndex = 2;
                        Check(view.Chart.Data?.Metric == QuotaObservationMetric.RemainingAmount
                            && view.TitleText.Text.Contains(UiText.T("Remaining", "잔여")), "Remaining amount was labeled used.");
                        AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                        Check(view.Chart.RenderedConnectionCount == 2 && view.Chart.MaximumValue == 50,
                            "Naturally decreasing remaining amounts lost their segments or amount scale.");
                        if (directory is not null && zoom == 100)
                            AccountUiChecks.RenderCurrent(view, Path.Combine(directory, $"trend-remaining-{language}-{theme}.png"));
                    }
                    view.ValuesExpander.IsExpanded = false;
                    Check(view.ValueRowCount == 0, "Collapsed observation rows retained formatted copies.");
                    widget.SetZoom(zoom, notify: false);
                    widget.BindAccounts(peers, account.Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop);
                    WidgetFixture.RenderWidget(widget, directory is not null && zoom == 100
                        ? Path.Combine(directory, $"trend-widget-{provider}-{language}-{theme}.png") : null);
                    Check(widget.Modules.Count == 5 && widget.Modules.Count(module => module.Trend?.Visibility == Visibility.Visible) == 1,
                        "Widget created visible charts for unselected accounts.");
                    Check(widget.Modules[0].Trend?.Chart.Data?.Points.Length == 4, "Widget lost selected history.");
                    Check(widget.Modules.All(module => module.Trend is null || module.ActualHeight >= module.Trend.ActualHeight), "Widget graph clipped its module.");
                    checks++;
                }
                finally { flyout.Close(); widget.Close(); }
            }
            CheckStates(directory, language, theme);
        }
        CheckMetricPickerReuse();
        CheckGeometry(directory);
        Console.WriteLine($"PASS: {checks} observed-trend production detail/widget combinations; EN/KO, dark/light, 80/100/150%, five accounts, real-value access, metric isolation, empty/zero/stale/reconnect states, stable geometry and 96/144/192-DPI renders.");
    }

    private static void CheckMetricPickerReuse()
    {
        var previousLanguage = UiText.Language;
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var account = Account(UsageProviderId.Cursor);
            var view = new ObservedTrendView();
            view.Bind(account, account.Snapshot.Windows[0]);
            view.MetricPicker.SelectedIndex = 1;
            var source = view.MetricPicker.ItemsSource;
            var selection = view.MetricPicker.SelectedItem;
            var selectionChanges = 0;
            view.MetricPicker.SelectionChanged += (_, _) => selectionChanges++;
            for (var minute = 10; minute < 16; minute++)
            {
                account = NextObservation(account, minute);
                view.Bind(account, account.Snapshot.Windows[0]);
                Check(ReferenceEquals(source, view.MetricPicker.ItemsSource)
                    && ReferenceEquals(selection, view.MetricPicker.SelectedItem)
                    && view.Chart.Data?.Metric == QuotaObservationMetric.UsedAmount,
                    "A newly accepted observation reset unchanged metric choices or selection.");
            }
            Check(selectionChanges == 0, "Unchanged metric choices raised selection changes on accepted observations.");

            var changedUnit = account.Snapshot.Windows[0] with { Unit = "EUR" };
            view.Bind(account, changedUnit);
            Check(!ReferenceEquals(source, view.MetricPicker.ItemsSource)
                && view.MetricPicker.SelectedIndex == 1 && view.TitleText.Text.Contains("EUR"),
                "A changed amount unit did not refresh labels while retaining the metric.");
            source = view.MetricPicker.ItemsSource;
            UiText.SetLanguage(UiLanguage.Korean);
            view.Bind(account, changedUnit);
            Check(!ReferenceEquals(source, view.MetricPicker.ItemsSource)
                && view.MetricPicker.SelectedIndex == 1
                && view.MetricPicker.SelectedItem!.ToString()!.Contains("사용"),
                "A changed language did not refresh the metric labels or lost selection.");
            source = view.MetricPicker.ItemsSource;
            view.Bind(account with { Profile = account.Profile with { Id = "another-profile" } }, changedUnit);
            Check(ReferenceEquals(source, view.MetricPicker.ItemsSource)
                && view.MetricPicker.SelectedIndex == 0 && view.Chart.Data?.Metric == QuotaObservationMetric.UsedPercent,
                "A changed profile did not reset the metric without rebuilding identical choices.");
            view.MetricPicker.SelectedIndex = 1;
            view.Bind(account with { Profile = account.Profile with { Id = "another-profile" },
                Observations = account.Observations! with { Context = account.Observations!.Context with { BindingKey = new string('b', 64) } } }, changedUnit);
            Check(view.MetricPicker.SelectedIndex == 0, "A reconnected binding retained the previous selected metric.");

            UiText.SetLanguage(UiLanguage.English);
            var remainingOnly = changedUnit with { UsedPercent = null, UsedAmount = null };
            view.Bind(account with { Observations = null }, remainingOnly);
            Check(view.MetricPicker.Items.Count == 1 && view.MetricPicker.SelectedIndex == 0
                && view.MetricPicker.Visibility == Visibility.Collapsed
                && view.TitleText.Text.Contains("Remaining") && view.TitleText.Text.Contains("EUR"),
                "Changed metric availability did not remove missing choices and select the remaining amount.");

            var compact = new ObservedTrendView(compact: true);
            var compactAccount = Account(UsageProviderId.Cursor);
            compact.Bind(compactAccount, compactAccount.Snapshot.Windows[0]);
            Check(compact.MetricPicker.ItemsSource is null && compact.MetricPicker.Items.Count == 0
                && CachedWindowCount(compactAccount.Observations!) == 1
                && compact.Chart.Data?.Metric == QuotaObservationMetric.UsedPercent
                && compact.Chart.Data?.Points.Length == 4,
                "Compact percent graph built an unused picker or unused metric snapshots.");
            compactAccount = NextObservation(compactAccount, 10);
            compact.Bind(compactAccount, compactAccount.Snapshot.Windows[0]);
            Check(compact.MetricPicker.ItemsSource is null && CachedWindowCount(compactAccount.Observations!) == 1
                && compact.Chart.Data?.Points.Length == 5,
                "Compact accepted history built unused choices or lost graph points.");

            var amountHistory = compactAccount.Observations!;
            amountHistory = new(amountHistory.Context, amountHistory.Revision, amountHistory.Watermark,
                amountHistory.Series.Where(series => series.Metric != QuotaObservationMetric.UsedPercent).ToImmutableArray());
            var amountWindow = compactAccount.Snapshot.Windows[0] with { UsedPercent = null };
            compact.Bind(compactAccount with { Observations = amountHistory }, amountWindow);
            Check(compact.Chart.Data?.Metric == QuotaObservationMetric.UsedAmount
                && compact.Chart.Data?.Points.Length == 5 && CachedWindowCount(amountHistory) == 2,
                "Compact amount fallback probed the unused remaining metric or changed graph semantics.");
            var nextAmountHistory = new QuotaObservationHistorySnapshot(amountHistory.Context,
                amountHistory.Revision + 1, amountHistory.Watermark, amountHistory.Series);
            compact.Bind(compactAccount with { Observations = nextAmountHistory }, amountWindow);
            Check(compact.Chart.Data?.Metric == QuotaObservationMetric.UsedAmount
                && CachedWindowCount(nextAmountHistory) == 1 && compact.MetricPicker.ItemsSource is null,
                "Compact valid amount metric requested unused snapshots or reset its selection.");
        }
        finally { UiText.SetLanguage(previousLanguage); }
    }

    private static CodexAccountView NextObservation(CodexAccountView account, int minute)
    {
        var at = Start.AddMinutes(minute);
        var clock = new MutableClock(at.AddSeconds(7));
        var history = new QuotaObservationHistory(account.Observations!.Context, clock);
        history.MergeLoaded(account.Observations);
        var window = account.Snapshot.Windows[0] with
        {
            UsedPercent = 46 + minute - 8, UsedAmount = 23 + (minute - 8) / 2m,
            RemainingAmount = 27 - (minute - 8) / 2m, AmountObservedAt = at
        };
        var snapshot = account.Snapshot with { LastAttemptedRefresh = at, LastSuccessfulRefresh = at, Windows = [window] };
        Check(history.Observe(snapshot, clock.UtcNow), "Metric-choice fixture did not accept the next real observation.");
        return account with { Snapshot = snapshot, Observations = history.Snapshot };
    }

    private static int CachedWindowCount(QuotaObservationHistorySnapshot history) =>
        ((System.Collections.IDictionary)typeof(QuotaObservationHistorySnapshot)
            .GetField("_views", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!).Count;

    private static void CheckStates(string? directory, UiLanguage language, AppTheme theme)
    {
        var account = Account(UsageProviderId.Codex);
        var view = new ObservedTrendView();
        var window = new Window { Content = view, Width = 420, SizeToContent = SizeToContent.Height };
        try
        {
            view.Bind(account with { Observations = null }, account.Snapshot.Windows[0]);
            Check(view.EmptyText.Text == UiText.T("No observations yet", "관측 기록이 아직 없어요")
                && view.Chart.Data is null, "Empty history fabricated a point.");
            view.Bind(account with { ObservationStorageUnavailable = true }, account.Snapshot.Windows[0]);
            Check(view.StorageNotice.Visibility == Visibility.Visible && view.Chart.Data?.Points.Length == 4,
                "Storage warning blocked existing quota observations.");
            AccountUiChecks.Render(window, 420, null, directory is null ? null
                : Path.Combine(directory, $"trend-storage-{language}-{theme}.png"));
            foreach (var state in new[] { CodexQuotaStatus.Stale, CodexQuotaStatus.TimedOut })
            {
                view.Bind(account with { Snapshot = account.Snapshot with { Status = state } }, account.Snapshot.Windows[0]);
                Check(view.Chart.Data?.Points.Length == 4, "Failure erased last actual observations.");
            }
            view.Bind(account, account.Snapshot.Windows[0], hidden: true);
            Check(view.Visibility == Visibility.Collapsed && view.Chart.Data is null && view.ValueRowCount == 0
                && view.Chart.RenderedPointCount == 0,
                "Protected identity retained a visible graph.");
            var reset = account.Snapshot.Windows[0] with { ResetsAt = Start.AddHours(10) };
            view.Bind(account, reset);
            Check(view.Chart.Data?.Points.IsEmpty == true, "Reset window displayed old observations.");
        }
        finally { window.Close(); }
    }

    private static void CheckGeometry(string? directory)
    {
        var chart = new QuotaSparkline();
        var window = new Window { Content = chart, Width = 360, Height = 100 };
        var first = new QuotaObservationPoint(Start, Start.AddSeconds(3), 0, 1, Start.AddHours(5), 300, CodexWindowKind.FiveHour, null);
        try
        {
            void Draw(ImmutableArray<QuotaObservationPoint> points, double dpi, string name)
            {
                chart.SetData(new(QuotaObservationMetric.UsedPercent, "%", points));
                chart.Measure(new Size(340, 52));
                chart.Arrange(new Rect(0, 0, 340, 52));
                chart.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(340 * dpi), (int)(52 * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(chart);
                if (directory is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, name + ".png"));
                    encoder.Save(stream);
                }
            }
            foreach (var dpi in new[] { 1d, 1.5, 2d })
            {
                Draw([first], dpi, $"trend-zero-dot-{dpi}");
                Check(chart.RenderedPointCount == 1 && chart.RenderedConnectionCount == 0, "One zero point acquired a trend line.");
                var second = first with { ObservedAt = Start.AddMinutes(1), Value = 20 };
                var third = second with { ObservedAt = Start.AddMinutes(3), Value = 40, SegmentId = 2 };
                var fourth = third with { ObservedAt = Start.AddMinutes(4), Value = 50 };
                Draw([first, second, third, fourth], dpi, $"trend-gap-{dpi}");
                Check(chart.RenderedPointCount == 4 && chart.RenderedConnectionCount == 2, "Unknown gap was connected or padded.");
                Draw([first with { ResetAt = null }, second with { ResetAt = null }], dpi, $"trend-no-reset-{dpi}");
                Check(chart.RenderedPointCount == 2 && chart.RenderedConnectionCount == 0, "Unknown reset invented window continuity.");
            }
        }
        finally { window.Close(); }
    }

    internal static CodexAccountView Account(UsageProviderId provider)
    {
        var clock = new MutableClock(Start.AddMinutes(10));
        var context = new QuotaObservationContext(new string((char)('a' + (int)provider), 32), provider, new string('a', 64));
        var history = new QuotaObservationHistory(context, clock);
        CodexQuotaSnapshot snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
        foreach (var (minute, value) in new[] { (0, 0), (2, 18), (6, 35), (8, 46) })
        {
            var at = Start.AddMinutes(minute);
            var quota = new CodexQuotaWindow(provider == UsageProviderId.Cursor ? "cursor-auto" : "quota", value, 300,
                Start.AddHours(5), CodexWindowKind.FiveHour);
            if (provider == UsageProviderId.Cursor) quota = quota with
            {
                UsedAmount = value / 2m, RemainingAmount = 50 - value / 2m, LimitAmount = 50, Unit = "USD", AmountObservedAt = at
            };
            snapshot = new(CodexQuotaStatus.Available, "pro", at, at, null, null, null, [quota], null) { Provider = provider };
            history.Observe(snapshot, at.AddSeconds(7));
            if (minute == 2) history.Observe(snapshot with { Status = CodexQuotaStatus.Stale }, at.AddSeconds(10));
        }
        return new(new CodexAccountProfile(context.ProfileId, "", "Observation sample") { Provider = provider }, snapshot)
            { IsConnected = true, Observations = history.Snapshot };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<string> VisualText(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var value in VisualText(VisualTreeHelper.GetChild(parent, i))) yield return value;
    }
}
