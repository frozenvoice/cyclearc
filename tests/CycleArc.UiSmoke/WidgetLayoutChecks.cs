using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
        count += RelayoutOrder(directory);
        count += RelayoutQueueDoesNotTouchAClosedWindow();
        count += BindingResizesTheShownWindow();
        Console.WriteLine($"PASS: {count} widget layout checks; mixed-height work-area scroll, monitor relayout without a usage bind, scrollbar thumb vs window drag, account-bind native resize; synthetic accounts only.");
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
            var byFirst = WidgetGridLayout.For(accounts.Length, headerHeight, heights[0], probe);
            var byAll = WidgetGridLayout.For(accounts.Length, headerHeight, heights, probe);
            var firstTotal = WidgetGridLayout.ChromeHeight + headerHeight
                + (byFirst.Rows * heights[0]) + ((byFirst.Rows - 1) * WidgetGridLayout.SeparatorThickness);
            var allTotal = WidgetGridLayout.ChromeHeight + headerHeight
                + RowSum(accounts.Length, byAll.Columns, heights);
            Check(allTotal > firstTotal + 4,
                $"{suffix}: later rows are not taller than a first-module estimate.");
            var shortHeight = (int)Math.Round((firstTotal + allTotal) / 2 + (2 * WidgetGridLayout.EdgeMargin));
            IReadOnlyList<ScreenRect> shortArea = [new ScreenRect(0, 0, 760, shortHeight)];
            var expected = WidgetGridLayout.For(accounts.Length, headerHeight, heights, shortArea[0],
                SystemParameters.VerticalScrollBarWidth);
            var firstOnShort = WidgetGridLayout.For(accounts.Length, headerHeight, heights[0], shortArea[0]);
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

            var reordered = Enumerable.Reverse(accounts).ToArray();
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
            CheckAccountsFullyVisible(widget, $"{suffix}/five-row");
            WidgetFixture.RenderWidget(widget, PathFor(directory, $"widget-layout-5-row-{suffix}"));

            widget.Relayout(Narrow);
            Layout(widget);
            Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                $"{suffix}: Relayout to a narrow work area did not wrap to 3+2.");
            Check(before.SequenceEqual(widget.Modules.Select(module => module.RingValueText.Text)),
                $"{suffix}: Relayout rebound usage numbers.");
            CheckAccountsFullyVisible(widget, $"{suffix}/wrapped");
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

    private static int RelayoutOrder(string? directory)
    {
        var secondary = Dual[1];
        var primary = Dual[0];
        var log = new StringBuilder();
        LogHostMonitors(log);
        var focus = new Window
        {
            Title = "CycleArc relayout-order focus",
            Width = 220,
            Height = 80,
            ShowInTaskbar = false,
            ShowActivated = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var persisted = new List<(double Left, double Top)>();
        var widget = new FloatingWidget { ShowActivated = false };
        widget.Moved += (left, top) => persisted.Add((left, top));
        try
        {
            focus.Show();
            Pump();
            var accounts = Five();
            widget.BindAccounts(accounts, accounts[2].Profile.Id, UsagePeriodPreference.Auto, Wide, Now);
            widget.Show();
            Pump();
            Layout(widget);
            Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                "Relayout-order fixture did not start as five columns.");
            var wide = DipSize(widget);
            Check(wide.Width > secondary.Width,
                $"Five-column width {wide.Width} is not wider than the secondary work area.");
            LogProbe(log, "start five-column", widget, primary, Wide, persisted);

            var foreground = GetForegroundWindow();
            widget.Left = -400;
            widget.Top = 40;
            var beforeSecondary = (widget.Left, widget.Top);
            persisted.Clear();
            widget.Relayout(Dual);
            Pump();
            Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                $"Drop onto the secondary did not wrap ({widget.LastLayout.Columns}x{widget.LastLayout.Rows}).");
            var after = DipSize(widget);
            Check(after.Width < wide.Width - 8,
                $"Wrapped width {after.Width} did not shrink from the five-column width {wide.Width}.");
            Check(Inside(widget.Left, widget.Top, after.Width, after.Height, secondary),
                $"Wrapped widget {widget.Left},{widget.Top} {after.Width}x{after.Height} left the secondary {secondary}.");
            CheckMoved(persisted, widget, secondary, mustMove: true, before: beforeSecondary,
                "secondary wrap");
            Check(GetForegroundWindow() == foreground, "Relayout stole focus from another window.");
            CheckUnchangedAfterPump(persisted, widget, secondary, "secondary wrap");
            CheckAccountsFullyVisible(widget, "relayout-secondary");
            LogProbe(log, "after secondary 3+2", widget, secondary, Dual, persisted);
            WidgetFixture.RenderWidget(widget, PathFor(directory, "widget-relayout-secondary"));
            PersistReload(accounts, Dual, secondary, widget, "secondary wrap reload");

            widget.BindAccounts(accounts, accounts[2].Profile.Id, UsagePeriodPreference.Auto, Dual, Now);
            Pump();
            Layout(widget);
            Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                "A later account bind snapped the secondary widget back to five columns.");

            widget.Left = 40;
            widget.Top = 40;
            var beforePrimary = (widget.Left, widget.Top);
            persisted.Clear();
            widget.Relayout();
            Pump();
            Check(widget.LastLayout!.Columns == 5 && widget.LastLayout.Rows == 1,
                "Moving onto the primary did not restore one row.");
            var onPrimary = DipSize(widget);
            Check(onPrimary.Width > after.Width + 8,
                $"Primary width {onPrimary.Width} was not taken from the new five-column size.");
            CheckWindowMatchesLayout(widget, "primary five-column");
            Check(Inside(widget.Left, widget.Top, onPrimary.Width, onPrimary.Height, primary),
                $"Primary widget {widget.Left},{widget.Top} {onPrimary.Width}x{onPrimary.Height} left the primary.");
            CheckMoved(persisted, widget, primary, mustMove: false, before: beforePrimary,
                "primary restore");
            CheckUnchangedAfterPump(persisted, widget, primary, "primary restore");
            CheckAccountsFullyVisible(widget, "relayout-primary");
            LogProbe(log, "after primary five-column", widget, primary, Dual, persisted);
            WidgetFixture.RenderWidget(widget, PathFor(directory, "widget-relayout-primary"));

            widget.Left = 9000;
            widget.Top = 40;
            var beforeOffscreen = (widget.Left, widget.Top);
            persisted.Clear();
            widget.Relayout(Wide);
            Pump();
            Check(widget.LastLayout!.Columns == 5,
                "Off-screen recovery skipped because the five-column layout was unchanged.");
            var recovered = DipSize(widget);
            Check(Inside(widget.Left, widget.Top, recovered.Width, recovered.Height, primary),
                $"Unchanged layout left the widget off-screen at {widget.Left},{widget.Top}.");
            CheckMoved(persisted, widget, primary, mustMove: true, before: beforeOffscreen,
                "off-screen recovery");
            CheckUnchangedAfterPump(persisted, widget, primary, "off-screen recovery");
            LogProbe(log, "after off-screen recovery", widget, primary, Wide, persisted);

            widget.Left = -400;
            widget.Top = 40;
            widget.Relayout(Dual);
            Pump();
            persisted.Clear();
            var beforeRemoval = (widget.Left, widget.Top);
            widget.Relayout([primary]);
            Pump();
            var removed = DipSize(widget);
            Check(widget.LastLayout!.Columns == 5 && Inside(widget.Left, widget.Top, removed.Width, removed.Height, primary),
                "Removing the secondary monitor did not recover onto the remaining primary.");
            CheckMoved(persisted, widget, primary, mustMove: true, before: beforeRemoval,
                "monitor removal");
            LogProbe(log, "after secondary removed", widget, primary, [primary], persisted);

            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                VisualTreeHelper.SetRootDpi(widget, new DpiScale(scale, scale));
                widget.Left = -400;
                widget.Top = 40;
                persisted.Clear();
                widget.Relayout(Dual);
                Pump();
                Check(widget.LastLayout!.Columns == 3 && widget.LastLayout.Rows == 2,
                    $"{scale:0.0}x drop onto the secondary did not wrap.");
                var scaled = DipSize(widget);
                Check(Inside(widget.Left, widget.Top, scaled.Width, scaled.Height, secondary),
                    $"{scale:0.0}x wrapped widget left the secondary at {widget.Left},{widget.Top} {scaled.Width}x{scaled.Height}.");
                CheckMoved(persisted, widget, secondary, mustMove: true, before: (-400, 40),
                    $"{scale:0.0}x secondary wrap");
            }
            VisualTreeHelper.SetRootDpi(widget, new DpiScale(1, 1));
            WriteLog(directory, log);
            return 4;
        }
        finally
        {
            widget.Close();
            focus.Close();
        }
    }

    private static int RelayoutQueueDoesNotTouchAClosedWindow()
    {
        var widget = new FloatingWidget { ShowActivated = false };
        widget.BindAccounts(Five(), "five-c1", UsagePeriodPreference.Auto, Wide, Now);
        widget.Show();
        Pump();
        typeof(FloatingWidget).GetMethod("QueueRelayout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(widget, null);
        widget.Close();
        Pump();
        return 1;
    }

    /// <summary>
    /// Production Update() must apply the arranged size on the same HWND. The check does
    /// not call Relayout or ApplyNativeSize after Update: those belong to the bind path.
    /// </summary>
    private static int BindingResizesTheShownWindow()
    {
        var focus = new Window
        {
            Title = "CycleArc bind-resize focus",
            Width = 220,
            Height = 80,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var settings = new AppSettings
        {
            FloatingWidgetEnabled = true,
            WidgetLeft = 40,
            WidgetTop = 40,
            WidgetOpacity = 0.92,
            WidgetAlwaysOnTop = false
        };
        var moved = new List<(double Left, double Top)>();
        var configured = 0;
        FloatingWidgetController? controller = null;
        controller = new FloatingWidgetController(window =>
        {
            configured++;
            window.Moved += (left, top) =>
            {
                if (!ReferenceEquals(controller?.CurrentWindow, window)) return;
                moved.Add((left, top));
            };
        });
        try
        {
            focus.Show();
            Pump();
            UsageAccountOverview ThreeOverview() => UsageAccountOverview.Create(Three(), "three-c");
            UsageAccountOverview FiveOverview() => UsageAccountOverview.Create(Five(), "five-c1");
            UsageAccountOverview OneOverview() => UsageAccountOverview.Create([Three()[0]], "three-a");

            controller.Update(settings, ThreeOverview());
            Pump();
            var window = controller.CurrentWindow ?? throw new InvalidOperationException("Update did not show the widget.");
            var hwnd = new WindowInteropHelper(window).Handle;
            Check(hwnd != IntPtr.Zero && window.IsVisible, "Controller.Update did not Show() the widget.");
            Check(window.Modules.Count == 3, "The 3-account bind dropped a module.");
            CheckWindowMatchesLayout(window, "bind-3");
            CheckAccountsFullyVisible(window, "bind-3");
            var three = DipSize(window);
            var threeNative = NativeDipSize(window);

            controller.Update(settings, FiveOverview());
            Pump();
            Check(configured == 1 && ReferenceEquals(controller.CurrentWindow, window)
                && new WindowInteropHelper(window).Handle == hwnd,
                "Adding accounts recreated the widget HWND.");
            Check(window.Modules.Count == 5, "The 5-account Update() did not bind five modules.");
            CheckWindowMatchesLayout(window, "bind-5");
            CheckAccountsFullyVisible(window, "bind-5");
            var five = DipSize(window);
            var fiveNative = NativeDipSize(window);
            Check(five.Width > three.Width + 8 || five.Height > three.Height + 8,
                $"5-account size {five} did not grow from 3-account {three}.");
            Check(fiveNative.Width > threeNative.Width + 8 || fiveNative.Height > threeNative.Height + 8,
                $"5-account HWND {fiveNative} stayed at 3-account {threeNative}.");

            var beforeNumbers = (Left: window.Left, Top: window.Top, Width: five.Width, Height: five.Height, Moved: moved.Count);
            var scroller = (ScrollViewer)window.FindName("ModuleScroller");
            var scrollBefore = scroller.VerticalOffset;
            var grown = Five();
            grown[0] = Codex(grown[0].Profile.Id, grown[0].DisplayName, Both(11, 12));
            controller.Update(settings, UsageAccountOverview.Create(grown, "five-c1"));
            Pump();
            Check(ReferenceEquals(controller.CurrentWindow, window)
                && new WindowInteropHelper(window).Handle == hwnd,
                "A number-only Update recreated the widget.");
            Check(window.Modules.Count == 5, "A number-only Update dropped a module.");
            var afterNumbers = DipSize(window);
            Check(Math.Abs(afterNumbers.Width - beforeNumbers.Width) <= 2
                && Math.Abs(afterNumbers.Height - beforeNumbers.Height) <= 2
                && (window.Left, window.Top) == (beforeNumbers.Left, beforeNumbers.Top)
                && moved.Count == beforeNumbers.Moved
                && Math.Abs(scroller.VerticalOffset - scrollBefore) <= 0.5,
                "A number-only Update resized, moved, or scrolled the widget.");

            controller.Update(settings, OneOverview());
            Pump();
            Check(configured == 1 && window.Modules.Count == 1
                && new WindowInteropHelper(window).Handle == hwnd,
                "Removing accounts recreated the widget or left extra modules.");
            CheckWindowMatchesLayout(window, "bind-1");
            CheckAccountsFullyVisible(window, "bind-1");
            var one = DipSize(window);
            var oneNative = NativeDipSize(window);
            Check(one.Width + 8 < five.Width && oneNative.Width + 8 < fiveNative.Width,
                $"1-account size {one}/{oneNative} kept the previous 5-account footprint {five}/{fiveNative}.");

            controller.Update(settings, ThreeOverview());
            Pump();
            Check(configured == 1 && window.Modules.Count == 3
                && new WindowInteropHelper(window).Handle == hwnd,
                "Restoring three accounts recreated the widget.");
            CheckWindowMatchesLayout(window, "bind-3-again");
            CheckAccountsFullyVisible(window, "bind-3-again");
            var threeAgain = DipSize(window);
            Check(Math.Abs(threeAgain.Width - three.Width) <= 4 && Math.Abs(threeAgain.Height - three.Height) <= 4,
                $"Returning to 3 accounts produced {threeAgain} instead of {three}.");

            var weekly = Three()[1];
            controller.Update(settings, UsageAccountOverview.Create([weekly], weekly.Profile.Id));
            Pump();
            Check(window.Modules[0].Periods.Count == 1, "The weekly-only fixture still has two period lines.");
            CheckWindowMatchesLayout(window, "bind-one-period");
            var weeklyNative = NativeDipSize(window);
            var both = Three()[0];
            controller.Update(settings, UsageAccountOverview.Create([both], both.Profile.Id));
            Pump();
            Check(window.Modules[0].Periods.Count == 2, "The two-period bind did not add a period line.");
            CheckWindowMatchesLayout(window, "bind-two-periods");
            CheckAccountsFullyVisible(window, "bind-two-periods");
            var bothLayout = window.LastLayout!.Height;
            var bothNative = NativeDipSize(window);
            // Two compact period lines sit beside the 60 DIP ring, so height may stay.
            // The HWND must still match the arranged layout rather than a previous bind.
            Check(Math.Abs(bothNative.Height - bothLayout) <= 4,
                $"Two-period HWND {bothNative.Height} does not match layout {bothLayout}.");

            var stale = both with { Snapshot = both.Snapshot with { Status = CodexQuotaStatus.Stale } };
            controller.Update(settings, UsageAccountOverview.Create([stale], stale.Profile.Id));
            Pump();
            Check(window.Modules[0].StatusText.Visibility == Visibility.Visible,
                "Status text did not appear after Update().");
            CheckWindowMatchesLayout(window, "bind-status");
            CheckAccountsFullyVisible(window, "bind-status");
            var staleLayout = window.LastLayout!.Height;
            var staleNative = NativeDipSize(window);
            Check(staleLayout > bothLayout + 4,
                $"Status text did not grow the arranged layout ({bothLayout} → {staleLayout}).");
            Check(staleNative.Height > bothNative.Height + 4
                && staleNative.Height > weeklyNative.Height + 4,
                $"Status text did not grow the HWND ({weeklyNative.Height}/{bothNative.Height} → {staleNative.Height}).");
            controller.Update(settings, UsageAccountOverview.Create([both], both.Profile.Id));
            Pump();
            Check(window.Modules[0].StatusText.Visibility != Visibility.Visible,
                "Status text stayed after it was cleared.");
            CheckWindowMatchesLayout(window, "bind-status-cleared");
            var clearedNative = NativeDipSize(window);
            Check(clearedNative.Height + 4 < staleNative.Height,
                $"Clearing status text left the previous HWND height {staleNative.Height} → {clearedNative.Height}.");

            controller.Update(settings, ThreeOverview());
            Pump();
            var beforeDrag = DipSize(window);
            var drag = typeof(FloatingWidget).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var finish = typeof(FloatingWidget).GetMethod("FinishDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
            drag.SetValue(window, new WidgetDragSession(window.Left, window.Top, 0, 0));
            controller.Update(settings, FiveOverview());
            Pump();
            Check(new WindowInteropHelper(window).Handle == hwnd, "A drag-time Update recreated the HWND.");
            var duringDrag = DipSize(window);
            Check(Math.Abs(duringDrag.Width - beforeDrag.Width) <= 2
                && Math.Abs(duringDrag.Height - beforeDrag.Height) <= 2,
                "A drag-time Update resized the window under the pointer.");
            Check(window.Modules.Count == 5, "A drag-time Update did not bind the new accounts.");
            finish.Invoke(window, [false]);
            Pump();
            CheckWindowMatchesLayout(window, "bind-after-drag");
            CheckAccountsFullyVisible(window, "bind-after-drag");
            var afterDrag = DipSize(window);
            Check(afterDrag.Width > beforeDrag.Width + 8 || afterDrag.Height > beforeDrag.Height + 8,
                "Releasing the drag did not apply the pending 5-account size.");

            drag.SetValue(window, new WidgetDragSession(window.Left, window.Top, 0, 0));
            controller.Update(settings, OneOverview());
            window.Close();
            Pump();
            finish.Invoke(window, [false]);
            Pump();
            Check(!window.IsVisible && !IsWindowVisible(hwnd),
                "A queued bind-size apply revived a closed widget.");

            controller.Update(settings, ThreeOverview());
            Pump();
            window = controller.CurrentWindow!;
            hwnd = new WindowInteropHelper(window).Handle;
            controller.Update(settings, UsageAccountOverview.Create([], ""));
            Pump();
            Check(!window.IsVisible && !IsWindowVisible(hwnd),
                "Clearing accounts left the widget visible.");
            Check(configured == 2, "Hiding an accountless widget recreated it.");
            return 6;
        }
        finally
        {
            controller.Dispose();
            focus.Close();
        }
    }

    private static bool Inside(double left, double top, double width, double height, ScreenRect area) =>
        left >= area.X && top >= area.Y && left + width <= area.Right + 0.5 && top + height <= area.Bottom + 0.5;

    private static (double Width, double Height) DipSize(FloatingWidget widget)
    {
        var content = (FrameworkElement)widget.Content;
        return (
            widget.ActualWidth > 0 ? widget.ActualWidth : content.DesiredSize.Width,
            widget.ActualHeight > 0 ? widget.ActualHeight : content.DesiredSize.Height);
    }

    private static (double Width, double Height) NativeDipSize(FloatingWidget widget)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return (0, 0);
        var fromDevice = PresentationSource.FromVisual(widget)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = fromDevice.Transform(new Point(rect.Right, rect.Bottom));
        return (Math.Max(0, bottomRight.X - topLeft.X), Math.Max(0, bottomRight.Y - topLeft.Y));
    }

    private static void CheckWindowMatchesLayout(FloatingWidget widget, string name)
    {
        var layout = widget.LastLayout ?? throw new InvalidOperationException($"{name}: no layout.");
        var content = (FrameworkElement)widget.Content;
        var native = NativeDipSize(widget);
        var slack = layout.HairlineRoundingSlack(1) + 2;
        Check(content.DesiredSize.Width + 0.5 >= layout.Width - slack,
            $"{name}: DesiredSize {content.DesiredSize.Width} is smaller than layout {layout.Width}.");
        if (widget.IsLoaded && widget.ActualWidth > 0)
        {
            Check(widget.ActualWidth + slack >= layout.Width,
                $"{name}: ActualWidth {widget.ActualWidth} is smaller than arranged layout {layout.Width}.");
            Check(widget.ActualWidth <= layout.Width + slack + 2,
                $"{name}: ActualWidth {widget.ActualWidth} kept a previous footprint larger than layout {layout.Width}.");
        }
        if (widget.IsLoaded && widget.ActualHeight > 0)
        {
            Check(widget.ActualHeight + slack >= layout.Height,
                $"{name}: ActualHeight {widget.ActualHeight} is smaller than arranged layout {layout.Height}.");
            Check(widget.ActualHeight <= layout.Height + slack + 2,
                $"{name}: ActualHeight {widget.ActualHeight} kept a previous footprint larger than layout {layout.Height}.");
        }
        if (widget.IsLoaded && native.Width > 0)
        {
            Check(native.Width + slack >= layout.Width,
                $"{name}: HWND width {native.Width} is smaller than arranged layout {layout.Width} (ActualWidth={widget.ActualWidth}).");
            Check(native.Width <= layout.Width + slack + 2,
                $"{name}: HWND width {native.Width} kept a previous footprint larger than layout {layout.Width}.");
        }
        if (widget.IsLoaded && native.Height > 0)
        {
            Check(native.Height + slack >= layout.Height,
                $"{name}: HWND height {native.Height} is smaller than arranged layout {layout.Height} (ActualHeight={widget.ActualHeight}).");
            Check(native.Height <= layout.Height + slack + 2,
                $"{name}: HWND height {native.Height} kept a previous footprint larger than layout {layout.Height}.");
        }
    }

    private static void CheckAccountsFullyVisible(FloatingWidget widget, string name)
    {
        var content = (FrameworkElement)widget.Content;
        var layout = widget.LastLayout!;
        var scroller = (ScrollViewer)widget.FindName("ModuleScroller");
        var host = widget.IsLoaded ? (FrameworkElement)widget : content;
        var clip = ClipSize(widget, content);
        CheckWindowMatchesLayout(widget, name);
        foreach (var module in widget.Modules)
        {
            var bounds = module.TransformToAncestor(host).TransformBounds(new Rect(module.RenderSize));
            var belowFold = layout.Scrolls && scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible
                && module.TransformToAncestor(scroller).TransformBounds(new Rect(module.RenderSize)).Bottom
                    > scroller.ViewportHeight + 2;
            if (belowFold) continue;
            Check(bounds.Right <= clip.Width + 0.5 && bounds.Bottom <= clip.Height + 0.5
                && bounds.Left >= -0.5 && bounds.Top >= -0.5,
                $"{name}: {module.NameText.Text} is outside the visible widget ({bounds} vs {clip.Width}x{clip.Height}).");
            CheckKeyText(module, host, clip, name);
        }
        if (layout.Scrolls) ScrollToLastPeriod(widget, scroller, name);
    }

    private static (double Width, double Height) ClipSize(FloatingWidget widget, FrameworkElement content)
    {
        var native = NativeDipSize(widget);
        var width = content.RenderSize.Width > 0 ? content.RenderSize.Width : content.ActualWidth;
        var height = content.RenderSize.Height > 0 ? content.RenderSize.Height : content.ActualHeight;
        if (widget.IsLoaded)
        {
            if (widget.ActualWidth > 0) width = width > 0 ? Math.Min(width, widget.ActualWidth) : widget.ActualWidth;
            if (widget.ActualHeight > 0) height = height > 0 ? Math.Min(height, widget.ActualHeight) : widget.ActualHeight;
            if (native.Width > 0) width = width > 0 ? Math.Min(width, native.Width) : native.Width;
            if (native.Height > 0) height = height > 0 ? Math.Min(height, native.Height) : native.Height;
        }
        return (width, height);
    }

    private static void CheckKeyText(WidgetAccountModuleView module, FrameworkElement host,
        (double Width, double Height) clip, string name)
    {
        void InsideElement(FrameworkElement element, string part)
        {
            if (element.Visibility != Visibility.Visible || element.RenderSize.Width <= 0) return;
            var bounds = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Right <= clip.Width + 0.5 && bounds.Bottom <= clip.Height + 0.5
                && bounds.Left >= -0.5 && bounds.Top >= -0.5,
                $"{name}: {module.NameText.Text} {part} is clipped ({bounds} vs {clip.Width}x{clip.Height}).");
            if (element is TextBlock text)
            {
                Check(text.ActualWidth + 0.5 >= text.DesiredSize.Width,
                    $"{name}: {module.NameText.Text} {part} text is truncated.");
            }
        }
        InsideElement(module.Badge, "provider badge");
        foreach (var line in module.Periods)
        {
            InsideElement(line.RemainingText, "remaining");
            InsideElement(line.ResetText, "reset");
        }
    }

    private static void CheckMoved(List<(double Left, double Top)> persisted, FloatingWidget widget,
        ScreenRect target, bool mustMove, (double Left, double Top) before, string name)
    {
        if (mustMove)
        {
            Check(persisted.Count > 0, $"{name}: position recovery produced no Moved event.");
            Check(persisted[^1] != before,
                $"{name}: Moved stayed at the unclamped origin {before}.");
        }
        if (persisted.Count == 0) return;
        Check(persisted[^1] == (widget.Left, widget.Top),
            $"{name}: last Moved {persisted[^1]} != window ({widget.Left}, {widget.Top}).");
        foreach (var point in persisted)
        {
            Check(point.Left >= target.X && point.Left < target.Right
                && point.Top >= target.Y && point.Top < target.Bottom,
                $"{name}: an intermediate position {point} was saved off the target {target}.");
        }
    }

    private static void CheckUnchangedAfterPump(List<(double Left, double Top)> persisted,
        FloatingWidget widget, ScreenRect target, string name)
    {
        var count = persisted.Count;
        var last = (widget.Left, widget.Top);
        var layout = widget.LastLayout!;
        Pump();
        Check(widget.LastLayout!.Columns == layout.Columns && widget.LastLayout.Rows == layout.Rows
            && Inside(widget.Left, widget.Top, DipSize(widget).Width, DipSize(widget).Height, target),
            $"{name}: extra dispatcher work bounced the widget.");
        Check(persisted.Count == count && (widget.Left, widget.Top) == last,
            $"{name}: extra dispatcher work overwrote the recovered position.");
    }

    private static void PersistReload(CodexAccountView[] accounts, IReadOnlyList<ScreenRect> areas,
        ScreenRect target, FloatingWidget source, string name)
    {
        var pixels = source.PixelPosition;
        var expected = (source.Left, source.Top);
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-widget-relayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                FloatingWidgetEnabled = true,
                WidgetLeft = source.Left,
                WidgetTop = source.Top,
                WidgetPixelLeft = pixels?.X,
                WidgetPixelTop = pixels?.Y
            });
            var loaded = new SettingsStore(path).Load();
            Check(loaded.WidgetLeft == expected.Left && loaded.WidgetTop == expected.Top,
                $"{name}: isolated settings did not keep DIP coordinates.");
            Check(loaded.WidgetPixelLeft == pixels?.X && loaded.WidgetPixelTop == pixels?.Y,
                $"{name}: isolated settings did not keep pixel coordinates.");

            var restored = new FloatingWidget { ShowActivated = false };
            try
            {
                restored.BindAccounts(accounts, accounts[2].Profile.Id, UsagePeriodPreference.Auto, areas, Now);
                restored.Apply(loaded);
                restored.Show();
                Pump();
                Check(restored.LastLayout!.Columns == source.LastLayout!.Columns
                    && restored.LastLayout.Rows == source.LastLayout.Rows,
                    $"{name}: reloaded widget lost the wrapped layout.");
                var size = DipSize(restored);
                Check(Inside(restored.Left, restored.Top, size.Width, size.Height, target),
                    $"{name}: reloaded widget left the target at {restored.Left},{restored.Top}.");
                Check(Math.Abs(restored.Left - expected.Left) < 1 && Math.Abs(restored.Top - expected.Top) < 1,
                    $"{name}: reloaded DIP {restored.Left},{restored.Top} != saved {expected}.");
                if (pixels is { } savedPixels && restored.PixelPosition is { } restoredPixels)
                {
                    Check(Math.Abs(restoredPixels.X - savedPixels.X) <= 2
                        && Math.Abs(restoredPixels.Y - savedPixels.Y) <= 2,
                        $"{name}: reloaded pixels {restoredPixels} != saved {savedPixels}.");
                }
            }
            finally { restored.Close(); }
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private static void LogHostMonitors(StringBuilder log)
    {
        log.AppendLine("Host monitors (physical pixels; tests inject DIP work areas and do not change this desktop):");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderByDescending(s => s.Primary))
        {
            log.AppendLine(
                $"  primary={screen.Primary} bounds={screen.Bounds} work={screen.WorkingArea}");
        }
        log.AppendLine($"Injected dual: primary={Dual[0]} secondary={Dual[1]}");
        if (System.Windows.Forms.Screen.AllScreens.Length < 2)
            log.AppendLine("Host has one monitor; dual-monitor recovery is exercised with injected ScreenRect values.");
        Console.Write(log.ToString());
    }

    private static void LogProbe(StringBuilder log, string phase, FloatingWidget widget, ScreenRect target,
        IReadOnlyList<ScreenRect> injected, List<(double Left, double Top)> persisted)
    {
        var content = (FrameworkElement)widget.Content;
        var native = NativeDipSize(widget);
        var hwnd = new WindowInteropHelper(widget).Handle;
        GetWindowRect(hwnd, out var rect);
        var scroller = (ScrollViewer)widget.FindName("ModuleScroller");
        var last = widget.Modules[^1];
        var lastBounds = last.TransformToAncestor(content).TransformBounds(new Rect(last.RenderSize));
        var badge = last.Badge.TransformToAncestor(content).TransformBounds(new Rect(last.Badge.RenderSize));
        var line = last.Periods.Count > 0 ? last.Periods[^1] : null;
        var remaining = line is null ? default(Rect?)
            : line.RemainingText.TransformToAncestor(content).TransformBounds(new Rect(line.RemainingText.RenderSize));
        var reset = line is null ? default(Rect?)
            : line.ResetText.TransformToAncestor(content).TransformBounds(new Rect(line.ResetText.RenderSize));
        var block =
            $"{phase}: injected={string.Join(" | ", injected)} target={target} " +
            $"LastLayout={widget.LastLayout} Desired={content.DesiredSize.Width:0.##}x{content.DesiredSize.Height:0.##} " +
            $"Render={content.RenderSize.Width:0.##}x{content.RenderSize.Height:0.##} " +
            $"Actual={widget.ActualWidth:0.##}x{widget.ActualHeight:0.##} " +
            $"HWND_px=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) HWND_dip={native.Width:0.##}x{native.Height:0.##} " +
            $"LeftTop={widget.Left:0.##},{widget.Top:0.##} scroller={scroller.ViewportWidth:0.##}x{scroller.ViewportHeight:0.##} " +
            $"lastModule={lastBounds} badge={badge} remaining={remaining} reset={reset} " +
            $"Moved=[{string.Join("; ", persisted.Select(p => $"{p.Left:0.##},{p.Top:0.##}"))}]";
        log.AppendLine(block);
        Console.WriteLine(block);
    }

    private static void WriteLog(string? directory, StringBuilder log)
    {
        if (directory is null) return;
        File.WriteAllText(Path.Combine(directory, "widget-relayout-log.txt"), log.ToString());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

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
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
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
            thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            });

            // A down-wheel after the thumb already sat on ScrollableHeight is a no-op.
            scroller.ScrollToVerticalOffset(0);
            scroller.UpdateLayout();
            Pump();
            Check(scroller.VerticalOffset + 8 < scroller.ScrollableHeight,
                "Scrollbar fixture has no remaining scroll range after returning to the top.");
            var beforeWheel = scroller.VerticalOffset;
            var host = (UIElement)widget.FindName("ModuleHost");
            host.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            });
            Pump();
            if (scroller.VerticalOffset <= beforeWheel)
            {
                scroller.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                });
                Pump();
            }
            Check(scroller.VerticalOffset > beforeWheel,
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
        for (var i = 0; i < 5; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
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
