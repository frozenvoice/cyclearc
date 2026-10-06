using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// The same current-quota fixture can run against an archived pre-removal product. The
/// explicitly named baseline mode collects the old empty-history row without depending
/// on any history types; the ordinary regression always requires complete removal.
/// </summary>
internal static class ObservationRemovalUiChecks
{
    private static readonly DateTimeOffset Now = new(2035, 1, 15, 9, 30, 0, TimeSpan.Zero);

    public static void Run(string? directory = null, bool baseline = false)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var rows = new List<string>
        {
            "Mode,Language,Theme,Zoom,Fixture,Accounts,SelectedIndex,ModuleIndex,ModuleSelected,ModuleDesiredHeight,ModuleHeight,WidgetDipWidth,WidgetDipHeight,WidgetNativeWidth,WidgetNativeHeight,PopupDipWidth,PopupDipHeight,PopupNativeWidth,PopupNativeHeight,TrendVisuals,RingWidth,RingHeight,NameFontSize,RingFontSize,RingValue,PeriodValues,NameTop,RingCenter,ModuleBackground"
        };
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fixtures = 0;
        var selections = 0;
        var baselineTrendVisuals = 0;
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var zoom in new[] { 80, 100, 150 })
            foreach (var kind in new[] { "one", "three", "five", "wrapped", "mixed" })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var count = kind == "one" ? 1 : kind == "three" ? 3 : 5;
                var mixed = kind == "mixed";
                var accounts = Accounts(count, mixed);
                var workAreas = kind == "wrapped" ? new[] { new ScreenRect(0, 0, 760, 1040) } : WidgetFixture.Desktop;
                var widget = new FloatingWidget { ShowActivated = false, Topmost = false, Left = 20, Top = 20 };
                var flyout = new FlyoutWindow { ShowActivated = false, Left = 30, Top = 30 };
                try
                {
                    widget.SetZoom(zoom, notify: false);
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                    widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, workAreas, Now);
                    flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
                    widget.Show();
                    flyout.Show();
                    Pump(widget, flyout);
                    var originalNative = NativeBounds(widget);
                    var originalHeight = widget.ActualHeight;
                    var naturalHeights = widget.Modules.Select(module => module.DesiredSize.Height).ToArray();
                    for (var selected = 0; selected < count; selected++)
                    {
                        widget.BindAccounts(accounts, accounts[selected].Profile.Id, UsagePeriodPreference.Auto, workAreas, Now);
                        flyout.BindAccounts(accounts, accounts[selected].Profile.Id, false);
                        Pump(widget, flyout);
                        var widgetBounds = NativeBounds(widget);
                        var popupBounds = NativeBounds(flyout);
                        var trends = TrendVisualCount(widget) + TrendVisualCount(flyout);
                        baselineTrendVisuals += trends;
                        if (!baseline)
                        {
                            AssertNoTrend(widget, kind + " widget");
                            AssertNoTrend(flyout, kind + " popup");
                            Require(widgetBounds.Height == originalNative.Height
                                && Math.Abs(widget.ActualHeight - originalHeight) <= .51,
                                $"{language}/{theme}/{zoom}/{kind}: selection changed outer widget height.");
                        }
                        var content = (FrameworkElement)widget.Content;
                        Require(widget.Modules.Count == count, kind + ": account modules disappeared.");
                        for (var moduleIndex = 0; moduleIndex < count; moduleIndex++)
                        {
                            var module = widget.Modules[moduleIndex];
                            Require(module.Model!.IsSelected == (selected == moduleIndex), kind + ": selected surface model changed.");
                            if (!baseline)
                                Require(Math.Abs(module.DesiredSize.Height - naturalHeights[moduleIndex]) <= .51,
                                    $"{kind} module {moduleIndex}: selection added module height.");
                            var ring = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
                            var ringBounds = ring.TransformToAncestor(content).TransformBounds(new Rect(ring.RenderSize));
                            var moduleBounds = module.TransformToAncestor(content).TransformBounds(new Rect(module.RenderSize));
                            var viewport = (FrameworkElement)widget.FindName("ModuleScroller")!;
                            var viewportBounds = viewport.TransformToAncestor(content).TransformBounds(new Rect(viewport.RenderSize));
                            Require(moduleBounds.Right <= viewportBounds.Right + 1
                                && (widget.LastLayout!.Scrolls || moduleBounds.Bottom <= viewportBounds.Bottom + 1),
                                kind + ": module is clipped by the native widget viewport.");
                            rows.Add(string.Join(',', new object?[]
                            {
                                baseline ? "before" : "after", language, theme, zoom, kind, count, selected, moduleIndex,
                                module.Model.IsSelected, N(module.DesiredSize.Height), N(module.ActualHeight),
                                N(widget.ActualWidth), N(widget.ActualHeight), widgetBounds.Width, widgetBounds.Height,
                                N(flyout.ActualWidth), N(flyout.ActualHeight), popupBounds.Width, popupBounds.Height, trends,
                                N(ring.ActualWidth), N(ring.ActualHeight), N(module.NameText.FontSize), N(module.RingValueText.FontSize),
                                Csv(module.RingValueText.Text), Csv(string.Join(" | ", module.Periods.Select(period => period.RemainingText.Text))),
                                N(module.NameText.TranslatePoint(new Point(), content).Y), N(ringBounds.Top + ringBounds.Height / 2),
                                Csv(module.Background?.ToString())
                            }));
                        }
                        if (directory is not null && zoom == 100 && selected == 0)
                        {
                            var stem = $"{kind}-{language}-{theme}".ToLowerInvariant();
                            WidgetFixture.RenderWidget(widget, Path.Combine(directory, stem + "-widget.png"));
                            AccountUiChecks.RenderCurrent((FrameworkElement)flyout.Content, Path.Combine(directory, stem + "-popup.png"));
                            if (mixed)
                            {
                                ((Button)flyout.FindName("UsageCreditsExpandButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Pump(widget, flyout);
                                AccountUiChecks.RenderCurrent((FrameworkElement)flyout.Content, Path.Combine(directory, stem + "-popup-credits-expanded.png"));
                                ((Button)flyout.FindName("UsageCreditsExpandButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Pump(widget, flyout);
                            }
                        }
                        selections++;
                    }
                    fixtures++;
                }
                finally { flyout.Close(); widget.Close(); }
            }
            Require(!baseline || baselineTrendVisuals > 0, "Baseline mode did not load the pre-removal trend product.");
            if (directory is not null)
                File.WriteAllLines(Path.Combine(directory, "observation-removal-layout.csv"), rows);
            Console.WriteLine($"PASS: {fixtures} production observation-removal {(baseline ? "baseline captures" : "fixtures")}, {selections} selections; EN/KO Dark/Light 80/100/150%, 1/3/5 equal-content accounts, wrapped and mixed status/provider fixtures; native HWND/module sizes{(baseline ? "; original empty-history trend row retained" : "; no trend visuals and selection height stable")}.");
        }
        finally { UiText.SetLanguage(previousLanguage); applyTheme.Invoke(null, [AppTheme.Dark]); }
    }

    internal static void AssertNoTrend(DependencyObject root, string label)
    {
        Require(TrendVisualCount(root) == 0, label + ": production visual tree retained a trend/sparkline/history section.");
        foreach (var text in AccountUiChecks.Descendants<TextBlock>(root))
            Require(!new[] { "Observed usage", "Actual observations", "Last observed", "Observed metric", "관측 추이", "실제 관측값", "마지막 관측", "관측 항목" }
                    .Any(phrase => text.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
                label + ": trend-specific text remains in the visual tree.");
    }

    private static int TrendVisualCount(DependencyObject root) => AccountUiChecks.Descendants<FrameworkElement>(root)
        .Count(element => element.GetType().Name is "ObservedTrendView" or "QuotaSparkline"
            || element.Name is "ObservedTrendHost" or "ObservationStorageNotice");

    private static CodexAccountView[] Accounts(int count, bool mixed) => Enumerable.Range(0, count).Select(index =>
    {
        var provider = mixed ? (UsageProviderId)(index % 3) : UsageProviderId.Codex;
        var account = UsageCreditUiChecks.Sample(provider, "normal");
        var snapshot = account.Snapshot with
        {
            LastAttemptedRefresh = Now, LastSuccessfulRefresh = Now,
            Windows = account.Snapshot.Windows.Select(window => window with { ResetsAt = Now.AddHours(2) }).ToArray()
        };
        if (!mixed) snapshot = snapshot with { Windows = snapshot.Windows.Take(1).ToArray() };
        if (mixed && index == 1) snapshot = snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "synthetic-request-failed" };
        if (mixed && index == 2) snapshot = snapshot with { Status = CodexQuotaStatus.TimedOut, TechnicalDetail = "synthetic-timeout" };
        if (mixed && index == 3) snapshot = snapshot with { Status = CodexQuotaStatus.Refreshing };
        if (mixed && index == 4) snapshot = snapshot with { TechnicalDetail = "claude-live-identity-mismatch" };
        return account with
        {
            Profile = account.Profile with { Id = "observation-removal-" + index, Label = UiText.T("A long research account nickname ", "아주 긴 연구용 계정 이름 ") + index },
            Snapshot = snapshot, HasMatchingIdentity = !(mixed && index == 4)
        };
    }).ToArray();

    private static void Pump(params Window[] windows)
    {
        foreach (var window in windows) window.UpdateLayout();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        foreach (var window in windows) window.UpdateLayout();
    }

    private static (int Width, int Height) NativeBounds(Window window)
    {
        Require(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "Could not read fixture HWND size.");
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
