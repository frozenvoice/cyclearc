using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Cursor;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class FlyoutHeaderChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 9, 30, 0, TimeSpan.Zero);

    internal static string Status(FlyoutWindow flyout) => AutomationProperties.GetName((FrameworkElement)flyout.FindName("StatusIndicator"));

    internal static void Run(string? directory = null, bool baseline = false)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var zoom in new[] { 80, 100, 150 })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var flyout = new FlyoutWindow { ShowActivated = false, Left = 40, Top = 40 };
                var widget = new FloatingWidget { ShowActivated = false, Left = 40, Top = 40 };
                try
                {
                    var accounts = Accounts();
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom, FlyoutPinned = true });
                    widget.SetZoom(zoom, notify: false);
                    widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
                    flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
                    flyout.Show(); widget.Show(); Pump();
                    Console.WriteLine($"HEADER host WPF DPI={VisualTreeHelper.GetDpi(flyout).DpiScaleX * 100:0}/{VisualTreeHelper.GetDpi(flyout).DpiScaleY * 100:0}% app zoom={zoom}% {language}/{theme}; synthetic accounts.");
                    var suffix = $"{language}-{theme}-zoom{zoom}".ToLowerInvariant();
                    if (baseline) Require(flyout.FindName("StatusText") is TextBlock && flyout.FindName("RefreshProgressText") is TextBlock,
                        "Baseline must load the original production text header.");
                    else CheckIndicator(flyout, "healthy", UiText.T("All updated", "전체 최신"));
                    Capture(flyout, directory, "healthy-" + suffix);
                    if (directory is not null) WidgetFixture.RenderWidget(widget, Path.Combine(directory, "widget-" + suffix + ".png"));
                    if (!baseline)
                    {
                        // Use the actual pin path without reapplying zoom/work-area sizing.
                        var pin = (System.Windows.Controls.Primitives.ToggleButton)flyout.FindName("PinButton");
                        var toggle = (System.Windows.Automation.Provider.IToggleProvider)
                            UIElementAutomationPeer.CreatePeerForElement(pin)!.GetPattern(PatternInterface.Toggle)!;
                        toggle.Toggle();
                        Pump(); Capture(flyout, directory, "unpinned-" + suffix);
                        toggle.Toggle();
                        Pump();
                    }
                    flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(false, true, UiText.RefreshAllProgress));
                    Pump(); Capture(flyout, directory, "refreshing-" + suffix);
                    var warning = accounts.ToArray();
                    warning[1] = warning[1] with { Snapshot = warning[1].Snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "claude-live-rate-limited" } };
                    flyout.BindAccounts(warning, accounts[0].Profile.Id, false);
                    Pump(); Capture(flyout, directory, "warning-" + suffix);
                    if (!baseline) CheckTransitions(flyout, widget, accounts, warning, zoom, suffix);
                }
                finally { flyout.Close(); widget.Close(); }
                if (!baseline) CheckInjectedDpi(language, theme, zoom, directory);
            }
        }
        finally { UiText.SetLanguage(previousLanguage); applyTheme.Invoke(null, [AppTheme.Dark]); }
        Console.WriteLine(baseline
            ? "PASS: fixed synthetic production popup/widget baseline captures; original text header, no native input or account access."
            : "PASS: production compact-header states, accessible descriptions, fixed action bounds, direct Close hide/reopen and clock lifetime; EN/KO Dark/Light 80/100/150% app zoom, separately injected 100/125/150/175/200% DPI; synthetic accounts, no native input or account access.");
    }

    internal static CodexAccountView[] Accounts() => new[] { UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor }
        .Select((provider, index) =>
        {
            var account = UsageCreditUiChecks.Sample(provider, "normal");
            return account with
            {
                Profile = account.Profile with { Id = "header-" + index, Label = UiText.T("Synthetic research account ", "합성 연구 계정 ") + index },
                Snapshot = account.Snapshot with
                {
                    LastSuccessfulRefresh = Now, LastAttemptedRefresh = Now,
                    Windows = [new("five_hour", 63, 300, null, CodexWindowKind.FiveHour), new("seven_day", 73, 10080, null, CodexWindowKind.Weekly)]
                }
            };
        }).ToArray();

    private static void Capture(FlyoutWindow flyout, string? directory, string name)
    {
        if (directory is null) return;
        var scale = ((ScaleTransform)flyout.FindName("FlyoutScale")).ScaleX;
        AccountUiChecks.RenderCurrent((FrameworkElement)flyout.Content, Path.Combine(directory, "popup-" + name + ".png"), scale);
        CaptureHeader(flyout, directory, name, VisualTreeHelper.GetDpi(flyout).DpiScaleX * scale, "header-host");
    }

    private static void CheckTransitions(FlyoutWindow flyout, FloatingWidget widget, CodexAccountView[] accounts,
        CodexAccountView[] warning, int zoom, string context)
    {
        var controls = ActionBounds(flyout);
        CheckIndicator(flyout, "warning", UiText.T("1 need attention", "1개 확인 필요"));
        Stable(flyout, controls, context + "/warning");
        flyout.BindAccounts(warning, accounts[0].Profile.Id, true); Pump();
        CheckIndicator(flyout, "warning", UiText.T("1 need attention", "1개 확인 필요"));
        Require(Status(flyout).Contains(UiText.CodexRefreshing, StringComparison.Ordinal)
            || Status(flyout).Contains(UiText.RefreshAllProgress, StringComparison.Ordinal), "Busy warning lost refresh progress.");
        Stable(flyout, controls, context + "/warning-refresh");
        flyout.BindAccounts(warning, accounts[0].Profile.Id, false); Pump();
        CheckIndicator(flyout, "warning", UiText.T("1 need attention", "1개 확인 필요"));
        flyout.BindAccounts(accounts, accounts[0].Profile.Id, false); Pump();
        CheckIndicator(flyout, "healthy", UiText.T("All updated", "전체 최신"));
        Stable(flyout, controls, context + "/recovered");

        var pending = accounts.ToArray();
        pending[1] = pending[1] with { Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "claude-connected-waiting") with { Provider = UsageProviderId.Claude } };
        flyout.BindAccounts(pending, accounts[0].Profile.Id, false); Pump();
        CheckIndicator(flyout, "pending", UiText.T("1 awaiting usage", "1개 수신 대기"));
        Stable(flyout, controls, context + "/waiting");
        pending[1] = accounts[1] with { Snapshot = accounts[1].Snapshot with { TechnicalDetail = "claude-desktop-history" } };
        flyout.BindAccounts(pending, accounts[0].Profile.Id, false); Pump();
        CheckIndicator(flyout, "pending", UiText.T("Received", "수신값 포함"));
        Stable(flyout, controls, context + "/local-receipt");
        var unknown = accounts[0] with { Snapshot = accounts[0].Snapshot with { Windows = [] } };
        flyout.BindAccounts([unknown], unknown.Profile.Id, false); Pump();
        CheckIndicator(flyout, "pending", null);
        Stable(flyout, controls, context + "/unknown");
        flyout.BindAccounts([], "", false); Pump();
        CheckIndicator(flyout, "pending", UiText.T("No usage yet", "사용량 대기"));
        Stable(flyout, controls, context + "/empty");

        var partialCursor = accounts[2] with
        {
            Snapshot = accounts[2].Snapshot with
            {
                TechnicalDetail = "cursor-sand-unavailable",
                Windows = [new("cursor-auto", 76.9, null, null, CodexWindowKind.Other), new("cursor-api", 0, null, null, CodexWindowKind.Other)]
            }
        };
        flyout.BindAccounts([partialCursor], partialCursor.Profile.Id, false); Pump();
        CheckIndicator(flyout, "healthy", UiText.T("Updated", "업데이트됨"));
        var optionalNotice = (TextBlock)flyout.FindName("CodexStatusText");
        Require(optionalNotice.IsVisible && optionalNotice.Text == CursorUsagePresentation.FailureText("cursor-sand-unavailable")
            && !Status(flyout).Contains(optionalNotice.Text, StringComparison.Ordinal),
            "Cursor optional Grok failure was removed from detail or promoted to global status.");
        var cursorCheckedRow = flyout.DetailRows.Select(border => (Grid)border.Child)
            .Single(row => row.Children.OfType<TextBlock>().Any(text => text.Text == UiText.LastChecked));
        Require(cursorCheckedRow.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().First().Text
            .StartsWith(CodexDisplayFormatting.ResetStamp(partialCursor.Snapshot.LastSuccessfulRefresh), StringComparison.Ordinal),
            "Cursor optional Grok failure lost the successful monthly Last checked timestamp.");
        Stable(flyout, controls, context + "/cursor-optional-grok");
        var mixedPartial = accounts.ToArray(); mixedPartial[2] = partialCursor;
        flyout.BindAccounts(mixedPartial, partialCursor.Profile.Id, false); Pump();
        CheckIndicator(flyout, "healthy", UiText.T("All updated", "전체 최신"));
        Require(optionalNotice.IsVisible && optionalNotice.Text == CursorUsagePresentation.FailureText("cursor-sand-unavailable"),
            "Mixed-account header dropped the optional Cursor warning from selected detail.");
        Stable(flyout, controls, context + "/mixed-cursor-optional-grok");

        flyout.BindAccounts(accounts, accounts[0].Profile.Id, false); Pump();
        var longProgress = string.Join(Environment.NewLine, Enumerable.Repeat(UiText.RefreshAllProgress + " · " + UiText.T("Checking all connected synthetic accounts", "연결된 모든 합성 계정 확인 중"), 12));
        flyout.SetRefreshPresentation(new(false, true, longProgress)); Pump();
        CheckIndicator(flyout, "pending", null);
        Require(AutomationProperties.GetHelpText((FrameworkElement)flyout.FindName("StatusIndicator")).Contains(longProgress, StringComparison.Ordinal),
            "Long progress explanation was lost from accessibility help.");
        Stable(flyout, controls, context + "/long-progress");
        CheckTooltip(flyout, longProgress);
        var refresh = (Button)flyout.FindName("RefreshAllButton");
        Require(!refresh.IsEnabled && ToolTipService.GetShowOnDisabled(refresh)
            && TooltipText(refresh.ToolTip).Contains(longProgress, StringComparison.Ordinal), "Disabled refresh button lost its progress tooltip.");
        var clocks = typeof(FlyoutWindow).GetField("_spinnerClock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Require(AccountUiChecks.Descendants<FrameworkElement>(flyout).Count(element => element.Name == "RefreshSpinner") == 1,
            "The header created another refresh spinner.");
        var clock = (AnimationClock?)clocks.GetValue(flyout);
        Require(clock is { CurrentState: ClockState.Active } && flyout.RefreshIndicator.IsAnimating, "Refresh spinner did not start.");
        for (var repeat = 0; repeat < 3; repeat++) flyout.SetRefreshPresentation(new(false, true, longProgress));
        Pump();
        Require(ReferenceEquals(clock, clocks.GetValue(flyout)), "Repeated progress binding replaced its animation clock.");

        var close = (Button)flyout.FindName("CloseButton");
        Require(close.IsVisible && close.IsEnabled && close.MinWidth >= 28 && close.MinHeight >= 28
            && Equals(close.ToolTip, UiText.Close) && AutomationProperties.GetName(close) == UiText.Close, "Direct Close is hidden, undersized or unlocalized.");
        var selected = flyout.SelectedProfileId;
        var position = flyout.PixelPosition;
        var anchors = flyout.EdgeAnchors;
        var pinChanges = 0; var selectionChanges = 0; var requests = 0; var moves = 0;
        flyout.PinChanged += _ => pinChanges++;
        flyout.AccountSelected += _ => selectionChanges++;
        flyout.SyncRequested += () => requests++;
        flyout.PositionChanged += (_, _) => moves++;
        var sourceCheck = typeof(FlyoutWindow).GetMethod("HeaderSourceIsInteractive", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var name in new[] { "FlyoutZoomOutButton", "FlyoutZoomInButton", "RefreshAllButton", "SettingsButton", "PinButton", "CloseButton" })
            Require((bool)sourceCheck.Invoke(flyout, [(FrameworkElement)flyout.FindName(name)])!, name + " leaks into header drag.");
        var selector = (ComboBox)flyout.FindName("AccountSelector");
        selector.IsDropDownOpen = true; Pump();
        Require(flyout.FindName("WindowOptionsMenu") is null, "Removed options menu is still attached.");
        var statusTip = (ToolTip)((FrameworkElement)flyout.FindName("StatusIndicator")).ToolTip;
        var refreshTip = (ToolTip)refresh.ToolTip;
        statusTip.PlacementTarget = (UIElement)flyout.FindName("StatusIndicator");
        refreshTip.PlacementTarget = refresh;
        statusTip.IsOpen = true; refreshTip.IsOpen = true; Pump();
        Require(statusTip.IsOpen && refreshTip.IsOpen, "Fixture did not expose the header tooltips before close.");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(!flyout.IsVisible && widget.IsVisible && flyout.Pinned && flyout.SelectedProfileId == selected
            && flyout.PixelPosition == position && flyout.EdgeAnchors == anchors && pinChanges == 0 && selectionChanges == 0 && requests == 0 && moves == 0
            && !selector.IsDropDownOpen && !statusTip.IsOpen && !refreshTip.IsOpen,
            "Direct Close changed another surface, pin, selection, position or refresh requests, or retained an open popup.");
        Require(clocks.GetValue(flyout) is null && clock!.CurrentState == ClockState.Stopped, "Direct Close left refresh animation running.");
        flyout.Show(); Pump();
        var resumed = (AnimationClock?)clocks.GetValue(flyout);
        Require(resumed is { CurrentState: ClockState.Active } && !ReferenceEquals(resumed, clock)
            && flyout.SelectedProfileId == selected && flyout.Pinned && flyout.PixelPosition == position && flyout.EdgeAnchors == anchors, "Reopening did not preserve state and resume one clock.");
        flyout.SetRefreshPresentation(new(true, false, "")); Pump();
        Require(resumed!.CurrentState == ClockState.Stopped && clocks.GetValue(flyout) is null, "Completion left the resumed clock running.");
        flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = false, FlyoutZoomPercent = zoom });
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(!flyout.IsVisible && widget.IsVisible && !flyout.Pinned && pinChanges == 0 && selectionChanges == 0,
            "Unpinned direct Close changed selection or pin state.");
    }

    private static void CheckInjectedDpi(UiLanguage language, AppTheme theme, int zoom, string? directory)
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var flyout = new FlyoutWindow { ShowActivated = false };
            try
            {
                // Inject before the first measurement. A layout first measured at the host
                // DPI otherwise keeps its rounded sizes until a status change remeasures it.
                VisualTreeHelper.SetRootDpi(flyout, new DpiScale(scale, scale));
                VisualTreeHelper.SetRootDpi((Visual)flyout.Content, new DpiScale(scale, scale));
                var accounts = Accounts();
                flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                var context = $"{language}/{theme}/zoom{zoom}/injected DPI{scale * 100:0}";
                var controls = ActionBounds(flyout);
                CheckLayout(flyout, context);
                flyout.SetRefreshPresentation(new(false, true, UiText.RefreshAllProgress));
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                Stable(flyout, controls, context);
                var warning = accounts.ToArray();
                warning[1] = warning[1] with { Snapshot = warning[1].Snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "claude-live-rate-limited" } };
                flyout.BindAccounts(warning, accounts[0].Profile.Id, false);
                AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                Stable(flyout, controls, context);
                CheckIndicator(flyout, "warning", UiText.T("1 need attention", "1개 확인 필요"));
                Require(Math.Abs(VisualTreeHelper.GetDpi((Visual)flyout.FindName("CloseIcon")).DpiScaleX - scale) < .001,
                    "Close icon lost injected DPI: " + context);
                if (directory is not null) CaptureHeader(flyout, directory, context.Replace('/', '-').Replace(' ', '-'), scale * zoom / 100d);
            }
            finally { flyout.Close(); }
        }
    }

    private static void CheckIndicator(FlyoutWindow flyout, string state, string? expected)
    {
        var indicator = (FrameworkElement)flyout.FindName("StatusIndicator");
        Require(indicator.Visibility == Visibility.Visible && Math.Abs(indicator.Width - 20) < .001 && Math.Abs(indicator.Height - 28) < .001,
            "Status indicator lost its fixed compact slot.");
        Require(flyout.FindName("StatusText") is null && flyout.FindName("RefreshProgressText") is null
            && !AccountUiChecks.Descendants<TextBlock>(indicator).Any(), "The header retained a text status control.");
        foreach (var (name, shown) in new[] { ("StatusDot", state == "healthy"), ("StatusWarningIcon", state == "warning"), ("StatusPendingIcon", state == "pending") })
            Require(((FrameworkElement)flyout.FindName(name)).Visibility == (shown ? Visibility.Visible : Visibility.Collapsed), "Incorrect " + name + " state for " + state);
        var nameText = AutomationProperties.GetName(indicator);
        var help = AutomationProperties.GetHelpText(indicator);
        var peer = UIElementAutomationPeer.CreatePeerForElement(indicator);
        Require(peer is not null && peer.GetName() == nameText && peer.GetHelpText() == help
            && (!flyout.IsVisible || peer.IsContentElement()),
            $"Status screen-reader peer disagrees with its production metadata/content membership: "
            + $"peer={peer?.GetType().Name ?? "null"}, window visible={flyout.IsVisible}, indicator visible={indicator.IsVisible}, "
            + $"content={peer?.IsContentElement()}, name metadata={nameText}, peer name={peer?.GetName()}, "
            + $"help metadata={help}, peer help={peer?.GetHelpText()}.");
        Require(!string.IsNullOrWhiteSpace(nameText) && !string.IsNullOrWhiteSpace(help)
            && !string.IsNullOrWhiteSpace(TooltipText(indicator.ToolTip)), "Status explanation was lost from tooltip/accessibility.");
        if (expected is not null) Require(nameText.Contains(expected, StringComparison.Ordinal)
            && help.Contains(expected, StringComparison.Ordinal) && TooltipText(indicator.ToolTip).Contains(expected, StringComparison.Ordinal), "Header status lost " + expected);
    }

    private static Dictionary<string, Rect> ActionBounds(FlyoutWindow flyout)
    {
        flyout.UpdateLayout();
        var header = (FrameworkElement)flyout.FindName("FlyoutHeaderGrid");
        return new[] { "FlyoutZoomOutButton", "FlyoutZoomPercentText", "FlyoutZoomInButton", "RefreshAllButton", "SettingsButton", "PinButton", "CloseButton" }
            .ToDictionary(name => name, name => ((FrameworkElement)flyout.FindName(name)).TransformToAncestor(header).TransformBounds(new Rect(((FrameworkElement)flyout.FindName(name)).RenderSize)));
    }

    private static void Stable(FlyoutWindow flyout, Dictionary<string, Rect> before, string context)
    {
        var after = ActionBounds(flyout);
        foreach (var (name, bounds) in before)
        {
            var next = after[name];
            Require(bounds == next, $"Status transition moved/resized {name}: {context}; "
                + $"before=[{bounds.X:R},{bounds.Y:R} {bounds.Width:R}x{bounds.Height:R}], after=[{next.X:R},{next.Y:R} {next.Width:R}x{next.Height:R}]; "
                + $"delta=[{next.X - bounds.X:R},{next.Y - bounds.Y:R} {next.Width - bounds.Width:R}x{next.Height - bounds.Height:R}]; "
                + $"header={((FrameworkElement)flyout.FindName("FlyoutHeaderGrid")).RenderSize}; content={((FrameworkElement)flyout.Content).RenderSize}; "
                + $"root DPI={VisualTreeHelper.GetDpi(flyout).DpiScaleX:R}; header DPI={VisualTreeHelper.GetDpi((Visual)flyout.FindName("FlyoutHeaderGrid")).DpiScaleX:R}; "
                + $"app scale={((ScaleTransform)flyout.FindName("FlyoutScale")).ScaleX:R}");
        }
        CheckLayout(flyout, context);
    }

    private static void CheckLayout(FlyoutWindow flyout, string context)
    {
        var header = (FrameworkElement)flyout.FindName("FlyoutHeaderGrid");
        var boxes = ActionBounds(flyout).ToArray();
        var tolerance = 1.1 / (VisualTreeHelper.GetDpi(header).DpiScaleX * flyout.ZoomPercent / 100d);
        Rect previous = default;
        foreach (var (name, bounds) in boxes)
        {
            Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -tolerance && bounds.Right <= header.ActualWidth + tolerance
                && bounds.Top >= -tolerance && bounds.Bottom <= header.ActualHeight + tolerance, name + " is clipped: " + context);
            Require(previous.Right <= bounds.Left + tolerance, name + " overlaps its previous action: " + context);
            previous = bounds;
            if (flyout.FindName(name) is System.Windows.Controls.Primitives.ButtonBase button) Require(button.ActualWidth + tolerance >= 28 && button.ActualHeight + tolerance >= 28, name + " shrank its hit target: " + context);
        }
        var title = (TextBlock)flyout.FindName("TitleText");
        var titleBox = title.TransformToAncestor(header).TransformBounds(new Rect(title.RenderSize));
        Require(title.ActualWidth > 0 && title.ActualWidth + tolerance >= title.DesiredSize.Width
            && titleBox.Right <= boxes[0].Value.Left + tolerance, "Title is clipped/overlaps actions: " + context);
        var indicator = (FrameworkElement)flyout.FindName("StatusIndicator");
        var indicatorBox = indicator.TransformToAncestor(header).TransformBounds(new Rect(indicator.RenderSize));
        Require(titleBox.Right <= indicatorBox.Left + tolerance && indicatorBox.Right <= boxes[0].Value.Left + tolerance,
            "Status indicator overlaps title/actions: " + context);
        Require(((TextBlock)flyout.FindName("FlyoutZoomPercentText")).Text == zoomText(flyout.ZoomPercent), "Saved zoom percentage disappeared.");
        static string zoomText(int percent) => percent + "%";
    }

    private static string TooltipText(object? tooltip) => tooltip switch
    {
        ToolTip tip => TooltipText(tip.Content),
        TextBlock text => text.Text,
        string text => text,
        _ => ""
    };

    private static void CheckTooltip(FlyoutWindow flyout, string expected)
    {
        var indicator = (FrameworkElement)flyout.FindName("StatusIndicator");
        var tip = indicator.ToolTip as ToolTip ?? new ToolTip { Content = indicator.ToolTip };
        tip.PlacementTarget = indicator;
        try
        {
            tip.IsOpen = true; Pump(); tip.UpdateLayout();
            var text = AccountUiChecks.Descendants<TextBlock>(tip).FirstOrDefault(block => block.Text.Contains(expected, StringComparison.Ordinal));
            Require(tip.ActualWidth > 0 && tip.ActualHeight > 0 && text is not null && text.TextWrapping == TextWrapping.Wrap
                && text.TextTrimming == TextTrimming.None, "Long status tooltip cannot be read in its actual WPF popup.");
            if (text!.ActualHeight > tip.ActualHeight)
            {
                var scroll = AccountUiChecks.Descendants<ScrollViewer>(tip).Single();
                Require(scroll.ScrollableHeight > 0, "Long status tooltip is clipped without scrolling.");
                scroll.ScrollToEnd(); Pump();
                Require(scroll.VerticalOffset > 0, "Remaining long status text is inaccessible.");
            }
        }
        finally { tip.IsOpen = false; }
    }

    private static void CaptureHeader(FlyoutWindow flyout, string directory, string suffix, double scale, string prefix = "header-injected")
    {
        var header = (FrameworkElement)flyout.FindName("FlyoutHeaderGrid");
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle((Brush)flyout.FindResource("BgBrush"), null, new Rect(header.RenderSize));
            dc.DrawRectangle(new VisualBrush(header), null, new Rect(header.RenderSize));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(header.ActualWidth * scale), (int)Math.Ceiling(header.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, prefix + "-" + suffix.ToLowerInvariant() + ".png")); encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump()
    {
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
