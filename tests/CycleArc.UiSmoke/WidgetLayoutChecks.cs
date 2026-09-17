using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// Mixed-height scrolling, scrollbar-vs-drag routing, and monitor relayout without a new
/// usage bind. Production widget, synthetic accounts only.
/// </summary>
internal static class WidgetLayoutChecks
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ScreenRect> Wide = WidgetFixture.Desktop;
    private static readonly IReadOnlyList<ScreenRect> Narrow = [new ScreenRect(0, 0, 760, 1040)];
    private static readonly IReadOnlyList<ScreenRect> Dual =
        [new ScreenRect(0, 0, 1920, 1040), new ScreenRect(-760, 0, 760, 1040)];

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var suffix = $"{language}-{theme}".ToLowerInvariant();
            count += MixedHeightScroll(directory, suffix);
            count += RelayoutWithoutBind(directory, suffix);
            count += ScrollbarDoesNotDragWindow();
        }
        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine($"PASS: {count} widget layout checks; mixed-height work-area scroll, monitor relayout without a usage bind, scrollbar thumb vs window drag; synthetic accounts only.");
    }

    private static int MixedHeightScroll(string? directory, string suffix)
    {
        var accounts = MixedHeights();
        var widget = new FloatingWidget { ShowActivated = false };
        try
        {
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, Narrow, Now);
            Layout(widget);
            var header = (FrameworkElement)widget.FindName("WidgetHeader");
            var headerHeight = header.DesiredSize.Height + header.Margin.Bottom;
            var heights = widget.Modules.Select(module =>
            {
                module.Measure(new Size(WidgetGridLayout.ModuleWidth, double.PositiveInfinity));
                return module.DesiredSize.Height;
            }).ToArray();
            Check(heights[0] + 8 < heights.Max(),
                $"{suffix}: the first mixed-height module is not shorter than a later one.");
            var probe = new ScreenRect(0, 0, 760, 1040);
            var byFirst = WidgetGridLayout.For(accounts.Count, headerHeight, heights[0], probe);
            var byAll = WidgetGridLayout.For(accounts.Count, headerHeight, heights, probe);
            var firstTotal = WidgetGridLayout.ChromeHeight + headerHeight
                + (byFirst.Rows * heights[0]) + ((byFirst.Rows - 1) * WidgetGridLayout.SeparatorThickness);
            var allTotal = WidgetGridLayout.ChromeHeight + headerHeight
                + RowSum(accounts.Count, byAll.Columns, heights);
            Check(allTotal > firstTotal + 4,
                $"{suffix}: later rows are not taller than a first-module estimate.");
            var shortHeight = (int)Math.Round((firstTotal + allTotal) / 2 + (2 * WidgetGridLayout.EdgeMargin));
            IReadOnlyList<ScreenRect> shortArea = [new ScreenRect(0, 0, 760, shortHeight)];
            var expected = WidgetGridLayout.For(accounts.Count, headerHeight, heights, shortArea[0],
                SystemParameters.VerticalScrollBarWidth);
            var firstOnShort = WidgetGridLayout.For(accounts.Count, headerHeight, heights[0], shortArea[0]);
            Check(!firstOnShort.Scrolls && expected.Scrolls,
                $"{suffix}: the fixture does not reproduce a short first row hiding later overflow.");

            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, shortArea, Now);
            Layout(widget);
            var layout = widget.LastLayout!;
            var content = (FrameworkElement)widget.Content;
            Check(layout.Scrolls && layout.Columns == expected.Columns && layout.Rows == expected.Rows,
                $"{suffix}: mixed-height layout did not scroll from every module ({layout.Columns}x{layout.Rows} scroll={layout.Scrolls}).");
            Check(layout.Height <= shortArea[0].Height - (2 * WidgetGridLayout.EdgeMargin),
                $"{suffix}: layout height {layout.Height} exceeds the work area.");
            Check(content.DesiredSize.Height <= layout.Height + 2,
                $"{suffix}: rendered height {content.DesiredSize.Height} exceeds layout {layout.Height}.");
            Check(content.DesiredSize.Width <= layout.Width + layout.HairlineRoundingSlack(1) + 1.01,
                $"{suffix}: rendered width {content.DesiredSize.Width} exceeds layout {layout.Width}.");

            widget.Show();
            Pump();
            Check(widget.ActualHeight <= shortArea[0].Height - (2 * WidgetGridLayout.EdgeMargin) + 4,
                $"{suffix}: native height {widget.ActualHeight} exceeds the work area.");

            var scroller = (ScrollViewer)widget.FindName("ModuleScroller");
            Check(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                $"{suffix}: mixed-height overflow did not show a vertical scrollbar.");
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-mixed-heights-{suffix}"));
            ScrollToLastPeriod(widget, scroller, suffix);
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-scrolled-last-row-{suffix}"));

            var reordered = accounts.Reverse().ToArray();
            widget.BindAccounts(reordered, reordered[0].Profile.Id, UsagePeriodPreference.Auto, shortArea, Now);
            Layout(widget);
            Check(widget.LastLayout!.Scrolls, $"{suffix}: reordering dropped scrolling.");
            ScrollToLastPeriod(widget, scroller, suffix + "/reordered");

            var grown = accounts.ToArray();
            grown[0] = Claude(grown[0].Profile.Id, grown[0].DisplayName,
                Both(85, 23) with { Status = CodexQuotaStatus.Stale });
            widget.BindAccounts(grown, grown[0].Profile.Id, UsagePeriodPreference.Auto, shortArea, Now);
            Layout(widget);
            Check(widget.LastLayout!.Scrolls, $"{suffix}: adding status text dropped scrolling.");
            Check(widget.Modules[0].StatusText.Visibility == Visibility.Visible,
                $"{suffix}: status text did not appear on the same window.");
            return 3;
        }
        finally { widget.Close(); }
    }

    private static double RowSum(int count, int columns, double[] heights)
    {
        var rows = (int)Math.Ceiling(count / (double)columns);
        var grid = 0d;
        for (var row = 0; row < rows; row++)
        {
            var start = row * columns;
            var end = Math.Min(start + columns, count);
            var rowHeight = 1d;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, heights[i]);
            grid += rowHeight;
        }
        return grid + ((rows - 1) * WidgetGridLayout.SeparatorThickness);
    }

    private static int RelayoutWithoutBind(string? directory, string suffix)
    {
        var accounts = Five();
        var widget = new FloatingWidget();
        try
        {
            widget.BindAccounts(accounts, accounts[2].Profile.Id, UsagePeriodPreference.Auto, Wide, Now);
            Layout(widget);
            var before = widget.Modules.Select(module => module.RingValueText.Text).ToArray();
            Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                $"{suffix}: wide relayout fixture is not five columns.");
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-layout-5-row-{suffix}"));

            widget.Relayout(Narrow);
            Layout(widget);
            Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                $"{suffix}: Relayout to a narrow work area did not wrap to 3+2.");
            Check(before.SequenceEqual(widget.Modules.Select(module => module.RingValueText.Text)),
                $"{suffix}: Relayout rebound usage numbers.");
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-layout-5-wrapped-{suffix}"));

            widget.Relayout(Wide);
            Layout(widget);
            Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                $"{suffix}: Relayout back to a wide work area did not restore one row.");

            var content = (FrameworkElement)widget.Content;
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                widget.Relayout(Narrow);
                Layout(widget);
                Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                    $"{suffix}: {scale:0.0}x Relayout to a narrow work area did not wrap.");
                widget.Relayout(Wide);
                Layout(widget);
                Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                    $"{suffix}: {scale:0.0}x Relayout to a wide work area did not restore one row.");
            }
            VisualTreeHelper.SetRootDpi(content, new DpiScale(1, 1));

            widget.Left = -700;
            widget.Top = 40;
            widget.Relayout(Dual);
            Layout(widget);
            Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                $"{suffix}: origin on the narrow monitor did not wrap.");
            widget.Left = 40;
            widget.Relayout();
            Layout(widget);
            Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                $"{suffix}: origin on the wide monitor did not restore one row.");

            widget.BindAccounts(Three(), "three-c", UsagePeriodPreference.Auto, Wide, Now);
            Layout(widget);
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-layout-3-accounts-{suffix}"));
            return 1;
        }
        finally { widget.Close(); }
    }

    private static int ScrollbarDoesNotDragWindow()
    {
        var widget = new FloatingWidget { ShowActivated = false };
        var selected = "";
        var flyouts = 0;
        widget.AccountSelected += id => selected = id;
        widget.FlyoutRequested += () => flyouts++;
        try
        {
            widget.BindAccounts(MixedHeights(), "mix-a", UsagePeriodPreference.Auto,
                [new ScreenRect(0, 0, 760, 280)], Now);
            widget.Show();
            Pump();
            Layout(widget);
            var scroller = (ScrollViewer)widget.FindName("ModuleScroller");
            Check(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "Scrollbar fixture is not scrolling.");
            var bar = AccountUiChecks.Descendants<ScrollBar>(scroller)
                .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical)
                ?? throw new InvalidOperationException("Vertical scrollbar is missing from the template.");
            bar.ApplyTemplate();
            var thumb = AccountUiChecks.Descendants<Thumb>(bar).FirstOrDefault()
                ?? throw new InvalidOperationException("Scrollbar thumb is missing from the template.");
            Check(!FloatingWidget.ShouldBeginWindowDrag(thumb) && !FloatingWidget.ShouldBeginWindowDrag(bar),
                "Scrollbar chrome is classified as a window-drag source.");
            Check(FloatingWidget.ShouldBeginWindowDrag(widget.Modules[0]),
                "An account module is no longer a window-drag source.");
            var header = (FrameworkElement)widget.FindName("WidgetHeader");
            Check(FloatingWidget.ShouldBeginWindowDrag(header),
                "The header is no longer a window-drag source.");
            Check(!FloatingWidget.ShouldBeginWindowDrag((DependencyObject)widget.FindName("WidgetCloseButton")),
                "Close is no longer excluded from window drag.");

            var left = widget.Left;
            var top = widget.Top;
            var offset = scroller.VerticalOffset;
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseLeftButtonDownEvent
            };
            thumb.RaiseEvent(down);
            var drag = typeof(FloatingWidget).GetField("_drag", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(widget);
            Check(drag is null, "Scrollbar thumb preview-down started a window drag.");
            Check(flyouts == 0 && selected.Length == 0, "Scrollbar thumb preview-down selected an account.");

            thumb.RaiseEvent(new DragDeltaEventArgs(0, 48) { RoutedEvent = Thumb.DragDeltaEvent });
            Pump();
            Check(scroller.VerticalOffset > offset, "Thumb DragDelta did not change VerticalOffset.");
            Check(widget.Left == left && widget.Top == top, "Thumb drag moved the widget.");
            Check(flyouts == 0 && selected.Length == 0, "Thumb drag selected an account or opened the flyout.");

            var afterThumb = scroller.VerticalOffset;
            scroller.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            });
            Pump();
            Check(scroller.VerticalOffset != afterThumb || scroller.ScrollableHeight == 0,
                "Mouse wheel did not scroll the module grid.");
            Check(widget.Left == left && widget.Top == top, "Mouse wheel moved the widget.");

            var line = AccountUiChecks.Descendants<RepeatButton>(bar).FirstOrDefault();
            if (line is not null)
            {
                Check(!FloatingWidget.ShouldBeginWindowDrag(line),
                    "A scrollbar repeat button is classified as a window-drag source.");
            }
            return 1;
        }
        finally { widget.Close(); }
    }

    private static void ScrollToLastPeriod(FloatingWidget widget, ScrollViewer scroller, string name)
    {
        scroller.UpdateLayout();
        scroller.ScrollToVerticalOffset(scroller.ScrollableHeight);
        scroller.UpdateLayout();
        var last = widget.Modules[^1];
        FrameworkElement target = last.Periods.Count > 0 ? last.Periods[^1] : last;
        var bounds = target.TransformToAncestor(scroller).TransformBounds(new Rect(target.RenderSize));
        Check(bounds.Bottom <= scroller.ViewportHeight + 2,
            $"{name}: scrolling cannot reach the last period of the last account ({bounds.Bottom} > {scroller.ViewportHeight}).");
        foreach (var line in last.Periods)
        {
            Check(line.RemainingText.ActualWidth + 0.5 >= line.RemainingText.DesiredSize.Width
                && line.ResetText.ActualWidth + 0.5 >= line.ResetText.DesiredSize.Width,
                $"{name}: a quota or reset value is clipped on the last account.");
        }
    }

    private static void Layout(FloatingWidget widget)
    {
        var content = (FrameworkElement)widget.Content;
        content.InvalidateMeasure();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static string? PathFor(string? directory, string name) =>
        directory is null ? null : Path.Combine(directory, name + ".png");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static CodexQuotaSnapshot Both(double fiveHourUsed, double weeklyUsed) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
        [
            new("five", fiveHourUsed, CodexWindowClassifier.FiveHourMinutes, Now.AddMinutes(35), CodexWindowKind.FiveHour),
            new("week", weeklyUsed, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(2).AddHours(4), CodexWindowKind.Weekly)
        ], null);

    private static CodexQuotaSnapshot WeeklyOnly(double used) =>
        Both(0, used) with
        {
            Windows = [new("week", used, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(1).AddHours(9), CodexWindowKind.Weekly)]
        };

    private static CodexAccountView Codex(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label), snapshot);

    private static CodexAccountView Claude(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label) { Provider = UsageProviderId.Claude },
            snapshot with { Provider = UsageProviderId.Claude, TechnicalDetail = ClaudeUsagePresentation.LiveDetail })
        { IsConnected = true };

    private static CodexAccountView[] MixedHeights() =>
    [
        Codex("mix-a", UiText.T("Main", "메인"), WeeklyOnly(41)),
        Claude("mix-b", UiText.T("Work", "작업용"), Both(62, 18) with { Status = CodexQuotaStatus.Stale }),
        Claude("mix-c", UiText.T("Personal Claude", "개인 Claude"), Both(85, 23)),
        Codex("mix-d", UiText.T("Kakao", "카카오"), Both(97, 44) with { Status = CodexQuotaStatus.Stale }),
        Claude("mix-e", UiText.T("Lab", "실험용"), Both(55, 30) with { TechnicalDetail = "claude-live-auth-required" })
    ];

    private static CodexAccountView[] Three() =>
    [
        Codex("three-a", UiText.T("Main", "메인"), Both(97, 44)),
        Codex("three-b", UiText.T("Kakao", "카카오"), WeeklyOnly(100)),
        Claude("three-c", UiText.T("Personal Claude", "개인 Claude"), Both(85, 23))
    ];

    private static CodexAccountView[] Five() =>
    [
        Codex("five-a", UiText.T("Main", "메인"), Both(97, 44)),
        Codex("five-b", UiText.T("Kakao", "카카오"), WeeklyOnly(100)),
        Claude("five-c1", UiText.T("Personal Claude", "개인 Claude"), Both(85, 23)),
        Claude("five-d", UiText.T("Work", "작업용"), Both(62, 18)),
        Codex("five-e", UiText.T("Lab", "실험용"), Both(41, 59))
    ];
}
