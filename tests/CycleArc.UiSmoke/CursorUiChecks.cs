using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class CursorUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("App.ApplyTheme");
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(now);
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            CheckRows(snapshot, now);
            CheckTooltip(snapshot, now);

            var flyout = new FlyoutWindow { ShowActivated = false };
            var widget = new FloatingWidget { ShowActivated = false };
            try
            {
                flyout.Bind(snapshot);
                var flyoutContent = (FrameworkElement)flyout.Content;
                flyoutContent.Measure(new Size(440, 1000));
                flyoutContent.Arrange(new Rect(0, 0, 440, 1000));
                flyoutContent.UpdateLayout();
                Check(((Border)flyout.FindName("ResetCreditsCard")).Visibility == Visibility.Collapsed,
                    "Cursor exposes Codex reset credits.");
                Check(((RadioButton)flyout.FindName("AutoPeriodButton")).Visibility == Visibility.Collapsed
                    && ((RadioButton)flyout.FindName("FiveHourPeriodButton")).Visibility == Visibility.Collapsed
                    && ((RadioButton)flyout.FindName("WeeklyPeriodButton")).Visibility == Visibility.Collapsed,
                    "Cursor exposes the Codex 5-hour/weekly selector.");
                Check(Descendants<TextBlock>(flyoutContent).Any(text => text.Text == UiText.T("Updated", "업데이트")),
                    "Cursor popup omits its update timestamp.");

                var profile = new CodexAccountProfile("cursor-ui", "", UiText.T("Cursor account", "Cursor 계정"))
                {
                    Provider = UsageProviderId.Cursor
                };
                var account = new CodexAccountView(profile, snapshot, "cursor@example.invalid") { IsConnected = true };
                WidgetFixture.BindOne(widget, account);
                var module = WidgetFixture.Module(widget);
                var visibleWindows = snapshot.Windows.Where(IsWidgetAllowance).ToArray();
                Check(module.Periods.Count == visibleWindows.Length && module.Periods.Count <= 3,
                    "Cursor widget did not keep the three compact named allowances.");
                foreach (var (window, index) in visibleWindows.Select((window, index) => (window, index)))
                {
                    var label = CursorUsagePresentation.QuotaLabel(window.LimitId);
                    var line = module.Periods[index];
                    var modelLine = module.Model!.Periods[index];
                    Check(line.PeriodText.Text == label,
                        "Cursor widget changed a named allowance: " + label);
                    Check(modelLine.CadenceLabel == CursorUsagePresentation.QuotaPeriodLabel(window.LimitId),
                        "Cursor widget omits the allowance cadence: " + label);
                    var startsGroup = index == 0
                        || !string.Equals(modelLine.CadenceLabel, module.Model.Periods[index - 1].CadenceLabel,
                            StringComparison.Ordinal);
                    var expectedCadence = modelLine.CadenceLabel + UiText.T(" · Left", " · 남음");
                    Check(line.CadenceText.Visibility == (startsGroup ? Visibility.Visible : Visibility.Collapsed)
                        && (!startsGroup || line.CadenceText.Text == expectedCadence),
                        "Cursor widget does not render the cadence group label: " + label);
                    Check(line.RemainingText.Text == modelLine.DisplayRemainingText,
                        "Cursor widget changed the compact remaining value: " + label);
                    Check(string.IsNullOrEmpty(line.ResetText.Text),
                        "Cursor widget repeated reset text on every compact allowance: " + label);
                    Check(modelLine.ResetTooltip == CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt),
                        "Cursor widget lost the exact reset tooltip: " + label);
                    Check((line.ToolTip as string ?? "").Contains(modelLine.ResetTooltip ?? UiText.ResetNotProvided,
                            StringComparison.Ordinal),
                        "Cursor widget allowance tooltip lost the exact reset time: " + label);
                }
                var widgetTooltip = module.ToolTip!.ToString()!;
                foreach (var window in snapshot.Windows)
                    Check(widgetTooltip.Contains(CursorUsagePresentation.QuotaLabel(window.LimitId), StringComparison.Ordinal),
                        "Cursor widget tooltip omits a full named allowance: " + window.LimitId);
                Check(widgetTooltip.Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
                    "Cursor widget tooltip omits the update timestamp.");
                Check(module.StatusText.Text.Contains(UiText.Updated, StringComparison.Ordinal),
                    "Cursor widget omits its compact updated status.");
                Check((module.StatusText.ToolTip as string ?? "").Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
                    "Cursor widget status tooltip omits the exact update timestamp.");
                var tray = CycleArcPresentation.TrayTooltip(snapshot);
                Check(tray.Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
                    "Cursor tray tooltip omits the update timestamp.");

                if (directory is not null)
                {
                    Save(flyout, Path.Combine(directory, $"cursor-popup-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"), 440, null);
                    Save(widget, Path.Combine(directory, $"cursor-widget-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                        widget.LastLayout?.Width ?? 380, null);
                    var accounts = new AccountsWindow();
                    try
                    {
                        accounts.Bind([account], profile.Id);
                        if (accounts.FindName("ConnectionOptions") is Expander options)
                            options.IsExpanded = true;
                        Save(accounts, Path.Combine(directory, $"cursor-accounts-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"), 700, 800,
                            () => ((ScrollViewer)accounts.FindName("AccountsScroll")).ScrollToBottom());
                    }
                    finally { accounts.Close(); }
                }
            }
            finally
            {
                flyout.Close();
                widget.CloseWithoutActivation();
            }

            CheckPartialSand(snapshot, now, language, theme, directory);
            CheckNamedAllowanceLayout(snapshot, language, theme, directory);
            CheckLargeMonetaryWidget(snapshot, language, theme, directory);
            CheckWidgetPercentRounding(snapshot, language, theme, directory);
        }
        if (directory is not null)
            Console.WriteLine($"PASS: Cursor popup, widget and account controls EN/KO + Dark/Light; previews: {directory}");
    }

    private static void CheckPartialSand(CodexQuotaSnapshot source, DateTimeOffset now,
        UiLanguage language, AppTheme theme, string? directory)
    {
        // Sand/Grok is optional. Keep the reported monthly buckets and their real
        // timestamp when that independent request fails, without inventing a zero row.
        var snapshot = source with
        {
            Windows = source.Windows.Where(window => window.LimitId != "cursor-sand").ToArray(),
            TechnicalDetail = "cursor-sand-unavailable"
        };
        var flyout = new FlyoutWindow { ShowActivated = false };
        var widget = new FloatingWidget { ShowActivated = false };
        try
        {
            flyout.Bind(snapshot);
            var flyoutContent = (FrameworkElement)flyout.Content;
            flyoutContent.Measure(new Size(440, 1000));
            flyoutContent.Arrange(new Rect(0, 0, 440, 1000));
            flyoutContent.UpdateLayout();

            var updated = UiText.T("Updated", "업데이트됨");
            var grokUnavailable = UiText.T("Grok Bot usage unavailable", "Grok Bot 사용량 확인 불가");
            var stale = UiText.T("Stale data", "오래된 데이터");
            Check(snapshot.Status == CodexQuotaStatus.Available,
                "Cursor partial Sand sample was not available.");
            Check(Descendants<TextBlock>(flyoutContent).Any(text => text.Text == updated),
                "Cursor partial popup lost its Updated status or timestamp.");
            Check(((TextBlock)flyout.FindName("CodexStatusText")).Text == grokUnavailable,
                "Cursor partial popup did not keep the optional Grok warning in detail.");
            Check(((TextBlock)flyout.FindName("StatusText")).Text == updated,
                "Cursor partial popup promoted the optional Grok warning to global status.");
            Check(!Descendants<TextBlock>(flyoutContent).Any(text => text.Text.Contains(stale, StringComparison.Ordinal)),
                "Cursor partial popup marked the monthly sample stale.");
            Check(!Descendants<TextBlock>(flyoutContent).Any(text => text.Text.Contains("Grok", StringComparison.Ordinal)
                && text != (TextBlock)flyout.FindName("CodexStatusText")),
                "Cursor partial popup invented a Grok quota row outside its detail warning.");
            Check(!Descendants<TextBlock>(flyoutContent).Any(text => text.Text.Contains("$0", StringComparison.Ordinal)),
                "Cursor partial popup invented a zero Grok allowance.");

            var profile = new CodexAccountProfile("cursor-ui-partial", "",
                UiText.T("Cursor account", "Cursor 계정"))
            {
                Provider = UsageProviderId.Cursor
            };
            var account = new CodexAccountView(profile, snapshot, "cursor@example.invalid") { IsConnected = true };
            WidgetFixture.BindOne(widget, account);
            var module = WidgetFixture.Module(widget);
            var updatedStamp = CursorUsagePresentation.UpdatedText(snapshot);
            var visibleWindows = snapshot.Windows.Where(IsWidgetAllowance).ToArray();
            Check(module.Periods.Count == visibleWindows.Length,
                "Cursor partial widget merged or invented a Sand allowance.");
            Check(module.StatusText.Text.Contains(UiText.Updated, StringComparison.Ordinal),
                "Cursor partial widget lost the monthly update timestamp.");
            Check(!module.StatusText.Text.Contains(stale, StringComparison.Ordinal)
                && !module.StatusText.Text.Contains("Grok", StringComparison.Ordinal),
                "Cursor partial widget exposed an optional Grok failure as stale/global status.");
            var partialTooltip = module.ToolTip as string ?? "";
            Check(partialTooltip.Contains(CursorUsagePresentation.QuotaLabel("cursor-auto"), StringComparison.Ordinal),
                "Cursor partial widget lost the monthly allowance label.");
            Check(partialTooltip.Contains(updatedStamp, StringComparison.Ordinal),
                "Cursor partial widget lost the exact update timestamp in its tooltip.");
            Check(module.Periods.All(period => period.PeriodText.Text != "Grok Bot")
                && !partialTooltip.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"), StringComparison.Ordinal),
                "Cursor partial widget invented a Grok allowance row.");
            Check(partialTooltip.Contains(grokUnavailable, StringComparison.Ordinal),
                "Cursor partial widget tooltip lost the optional Grok failure detail.");
            var tray = CycleArcPresentation.TrayTooltip(snapshot);
            Check(tray.Contains(updatedStamp, StringComparison.Ordinal)
                && tray.Contains(UiText.T("Updated", "업데이트"), StringComparison.Ordinal),
                "Cursor partial tray lost the monthly update status.");
            Check(!tray.Contains("Grok", StringComparison.Ordinal) && !tray.Contains(stale, StringComparison.Ordinal),
                "Cursor partial tray exposed an optional Grok failure as global status.");

            if (directory is not null)
            {
                Save(flyout, Path.Combine(directory,
                    $"cursor-partial-popup-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"), 440, null);
                Save(widget, Path.Combine(directory,
                    $"cursor-partial-widget-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                    widget.LastLayout?.Width ?? 380, null);
            }
        }
        finally
        {
            flyout.Close();
            widget.CloseWithoutActivation();
        }
    }

    private static void CheckNamedAllowanceLayout(CodexQuotaSnapshot source,
        UiLanguage language, AppTheme theme, string? directory)
    {
        // Reorder only the existing production-shaped fixture so the large ring is
        // inspected with each named allowance as its selection.
        foreach (var (name, snapshot, selectedId) in new[]
        {
            ("cursor", source, "cursor-auto"),
            ("other", MoveFirst(source, "cursor-api"), "cursor-api"),
            ("grok", MoveFirst(source, "cursor-sand"), "cursor-sand")
        })
        {
            var ring = CodexRingPresentation.From(snapshot);
            var selectedLabel = CursorUsagePresentation.QuotaLabel(selectedId);
            var selectedDisplay = CursorUsagePresentation.QuotaDisplayLabel(selectedId);
            Check(ring.CenterSubLabel == selectedLabel + Environment.NewLine
                + UiText.T(
                    $"{CursorUsagePresentation.QuotaPeriodLabel(selectedId)} used",
                    $"{CursorUsagePresentation.QuotaPeriodLabel(selectedId)} 사용"),
                "Cursor large ring lost the selected allowance cadence: " + selectedDisplay);

            foreach (var zoom in new[] { 80, 100, 150 })
            {
                var flyout = new FlyoutWindow { ShowActivated = false };
                var widget = new FloatingWidget { ShowActivated = false };
                try
                {
                    flyout.Bind(snapshot);
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                    var flyoutContent = (FrameworkElement)flyout.Content;
                    Arrange(flyoutContent, 440, 1000);
                    var ringText = (TextBlock)flyout.FindName("CodexRingSubLabel");
                    Check(ringText.Text == ring.CenterSubLabel && ringText.Text.Contains(Environment.NewLine),
                        $"Cursor {name} popup ring did not show its selected name and cadence at {zoom}%.");
                    Check(ringText.TextWrapping == TextWrapping.Wrap,
                        $"Cursor {name} popup ring does not wrap its selected name at {zoom}%.");
                    var ringHost = (FrameworkElement)flyout.FindName("CodexRingHost");
                    Check(ringText.ActualHeight > ringText.FontSize + 1
                        && ringText.ActualHeight < ringHost.ActualHeight - 4,
                        $"Cursor {name} popup ring caption is clipped or still one line at {zoom}%.");
                    Check(Descendants<TextBlock>(flyoutContent).Any(text => text.Text == selectedDisplay),
                        $"Cursor {name} popup lost the combined allowance label at {zoom}%.");
                    var popupLabel = Descendants<TextBlock>(flyoutContent)
                        .FirstOrDefault(text => text.Text == selectedDisplay);
                    Check(popupLabel is not null && popupLabel.TextWrapping == TextWrapping.Wrap,
                        $"Cursor {name} popup allowance label cannot reflow at {zoom}%.");
                    if (popupLabel is not null)
                    {
                        var popupRow = Ancestor<Grid>(popupLabel);
                        var popupValue = popupRow?.Children.OfType<StackPanel>().FirstOrDefault();
                        Check(popupRow is not null && popupValue is not null,
                            $"Cursor {name} popup allowance row lost its value column at {zoom}%.");
                        if (popupRow is not null && popupValue is not null)
                            CheckNoOverlap(popupLabel, popupValue, popupRow,
                                $"Cursor {name} popup allowance overlaps its value at {zoom}%.");
                    }

                    var profile = new CodexAccountProfile("cursor-ui-named-" + name, "",
                        UiText.T("Cursor account", "Cursor 계정"))
                    {
                        Provider = UsageProviderId.Cursor
                    };
                    // The widget keeps its canonical Cursor priority order. Make the earlier
                    // candidates unknown in this widget-only source so the same production
                    // selection reaches Other Models and Grok Bot without changing the popup
                    // fixture above (which still checks the complete source snapshot).
                    var widgetSnapshot = WidgetSelectionSnapshot(snapshot, selectedId);
                    var account = new CodexAccountView(profile, widgetSnapshot, "cursor@example.invalid")
                    {
                        IsConnected = true
                    };
                    widget.SetZoom(zoom, notify: false);
                    WidgetFixture.BindOne(widget, account);
                    WidgetFixture.RenderWidget(widget, null);
                    var module = WidgetFixture.Module(widget);
                    var ringTarget = module.RingTargetText;
                    var widgetModel = module.Model ?? throw new InvalidOperationException(
                        $"Cursor {name} widget has no model at {zoom}%.");
                    var compactTarget = CompactRingTarget(selectedLabel);
                    Check(widgetModel.RingTargetLabel == selectedLabel
                        && ringTarget.Text == compactTarget
                        && ringTarget.TextWrapping == TextWrapping.NoWrap
                        && Math.Abs(ringTarget.FontSize - 10.5) < 0.01,
                        $"Cursor {name} widget ring target is missing or not compact at {zoom}%.");
                    var widgetRingHost = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
                    var ringCentre = VisualTreeHelper.GetParent(module.RingValueText);
                    Check(ReferenceEquals(ringCentre, VisualTreeHelper.GetParent(ringTarget))
                        && ringCentre is Panel centrePanel
                        && centrePanel.Children.IndexOf(ringTarget) < centrePanel.Children.IndexOf(module.RingValueText),
                        $"Cursor {name} widget ring target is not above the percentage at {zoom}%.");
                    var content = (FrameworkElement)widget.Content;
                    var targetBounds = Bounds(ringTarget, content);
                    var ringBounds = Bounds(widgetRingHost, content);
                    Check(targetBounds.Left >= ringBounds.Left - 1
                        && targetBounds.Right <= ringBounds.Right + 1
                        && targetBounds.Top >= ringBounds.Top - 1
                        && targetBounds.Bottom <= ringBounds.Bottom + 1,
                        $"Cursor {name} widget ring target is clipped at {zoom}%.");
                    Check(ringTarget.ToolTip is string targetTooltip
                        && targetTooltip.Contains(widgetModel.Ring.CenterSubLabel, StringComparison.Ordinal),
                        $"Cursor {name} widget ring target tooltip lost the full name at {zoom}%.");
                    Check((module.ToolTip as string ?? "").Contains(selectedLabel, StringComparison.Ordinal),
                        $"Cursor {name} widget module tooltip lost the full name at {zoom}%.");
                    var widgetIndex = module.Periods
                        .Select((period, index) => (period, index))
                        .Where(item => item.period.PeriodText.Text == selectedLabel)
                        .Select(item => item.index)
                        .DefaultIfEmpty(-1)
                        .First();
                    var widgetLabel = widgetIndex >= 0 && widgetIndex < module.Periods.Count
                        ? module.Periods[widgetIndex] : null;
                    Check(widgetLabel is not null && widgetLabel.PeriodText.Text == selectedLabel,
                        $"Cursor {name} widget allowance label is missing at {zoom}%.");
                    if (widgetLabel is not null)
                    {
                        var modelLine = module.Model!.Periods[widgetIndex];
                        Check(modelLine.CadenceLabel == CursorUsagePresentation.QuotaPeriodLabel(selectedId),
                            $"Cursor {name} widget allowance cadence is missing at {zoom}%.");
                        var widgetRow = Ancestor<Grid>(widgetLabel.PeriodText);
                        Check(widgetRow is not null,
                            $"Cursor {name} widget allowance lost its measured row at {zoom}%.");
                        var widgetValue = widgetRow?.Children.OfType<TextBlock>()
                            .FirstOrDefault(text => ReferenceEquals(text, widgetLabel.RemainingText));
                        Check(widgetValue is not null,
                            $"Cursor {name} widget allowance lost its value column at {zoom}%.");
                        if (widgetRow is not null && widgetValue is not null)
                            CheckNoOverlap(widgetLabel.PeriodText, widgetValue, widgetRow,
                                $"Cursor {name} widget allowance overlaps its value at {zoom}%.");
                        Check(widgetLabel.PeriodText.Text == selectedLabel
                            && widgetLabel.PeriodText.ActualWidth + 1 >= widgetLabel.PeriodText.DesiredSize.Width
                            && widgetLabel.RemainingText.ActualWidth + 1 >= widgetLabel.RemainingText.DesiredSize.Width,
                            $"Cursor {name} widget full allowance name or value is clipped at {zoom}%.");
                    }

                    if (directory is not null && zoom == 100)
                    {
                        Save(flyout, Path.Combine(directory,
                            $"cursor-named-{name}-popup-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"), 440, null);
                        Save(widget, Path.Combine(directory,
                            $"cursor-named-{name}-widget-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                            widget.LastLayout?.Width ?? 380, null);
                    }
                }
                finally
                {
                    flyout.Close();
                    widget.CloseWithoutActivation();
                }
            }

            var accountWindow = new AccountsWindow();
            try
            {
                var profile = new CodexAccountProfile("cursor-ui-card-" + name, "",
                    UiText.T("Cursor account", "Cursor 계정"))
                {
                    Provider = UsageProviderId.Cursor
                };
                var account = new CodexAccountView(profile, snapshot, "cursor@example.invalid")
                {
                    IsConnected = true
                };
                accountWindow.Bind([account], profile.Id);
                var accountContent = (FrameworkElement)accountWindow.Content;
                Arrange(accountContent, 700, 800);
                var cardLabel = Descendants<TextBlock>(accountContent)
                    .FirstOrDefault(text => text.Text == selectedDisplay);
                Check(cardLabel is not null && cardLabel.TextWrapping == TextWrapping.Wrap,
                    $"Cursor {name} account card allowance label cannot reflow.");
                if (cardLabel is not null)
                {
                    var cardRow = Ancestor<Grid>(cardLabel);
                    var cardValue = cardRow?.Children.OfType<TextBlock>()
                        .FirstOrDefault(text => !ReferenceEquals(text, cardLabel));
                    Check(cardRow is not null && cardValue is not null,
                        $"Cursor {name} account card allowance lost its value column.");
                    if (cardRow is not null && cardValue is not null)
                        CheckNoOverlap(cardLabel, cardValue, cardRow,
                            $"Cursor {name} account card allowance overlaps its value.");
                }
                if (directory is not null)
                    Save(accountWindow, Path.Combine(directory,
                        $"cursor-named-{name}-account-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"), 700, 800);
            }
            finally { accountWindow.Close(); }
        }
    }

    private static CodexQuotaSnapshot MoveFirst(CodexQuotaSnapshot source, string limitId)
    {
        var selected = source.Windows.Single(window => window.LimitId == limitId);
        return source with
        {
            Windows = source.Windows.Where(window => !ReferenceEquals(window, selected))
                .Prepend(selected).ToArray()
        };
    }

    private static CodexQuotaSnapshot WidgetSelectionSnapshot(CodexQuotaSnapshot source, string selectedId) =>
        source with
        {
            Windows = source.Windows.Select(window => selectedId switch
            {
                "cursor-api" when window.LimitId == "cursor-auto" => window with { UsedPercent = null },
                "cursor-sand" when window.LimitId is "cursor-auto" or "cursor-api"
                    => window with { UsedPercent = null },
                _ => window
            }).ToArray()
        };

    private static void CheckWidgetPercentRounding(CodexQuotaSnapshot source,
        UiLanguage language, AppTheme theme, string? directory)
    {
        var widget = new FloatingWidget { ShowActivated = false };
        var flyout = new FlyoutWindow { ShowActivated = false };
        try
        {
            foreach (var used in new[] { 76.91, 99.6 })
            {
                var snapshot = source with
                {
                    Windows = source.Windows.Select(window => window.LimitId switch
                    {
                        "cursor-auto" => window with { UsedPercent = used },
                        "cursor-api" => window with { UsedPercent = 2.9 },
                        _ => window
                    }).ToArray()
                };
                WidgetFixture.BindOne(widget, WidgetFixture.Synthetic("cursor-rounding",
                    UiText.T("Cursor account", "Cursor 계정"), snapshot));
                WidgetFixture.RenderWidget(widget, null);
                var module = WidgetFixture.Module(widget);
                Check(module.RingValueText.Text == (used == 76.91 ? "77%" : ">99%"),
                    "Cursor widget ring did not round only its visible percent.");
                Check(module.Periods.Select(line => line.RemainingText.Text)
                    .SequenceEqual(used == 76.91 ? new[] { "23%", "97%", "87%" } : new[] { "<1%", "97%", "87%" }),
                    "Cursor widget remaining percentages did not apply the boundary and complement rules.");

                var ringHost = (Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
                var exactUsed = CodexDisplayFormatting.PercentText(used, UsageProviderId.Cursor);
                var exactLeft = CodexDisplayFormatting.PercentText(100 - used, UsageProviderId.Cursor);
                Check((ringHost.ToolTip as string ?? "").Contains(exactUsed, StringComparison.Ordinal)
                    && (module.Periods[0].ToolTip as string ?? "").Contains(exactLeft, StringComparison.Ordinal)
                    && (module.ToolTip as string ?? "").Contains(exactLeft, StringComparison.Ordinal),
                    "Cursor widget tooltip lost the original decimal precision.");
                var arc = ringHost.Children.OfType<System.Windows.Shapes.Path>().Single();
                var segment = (ArcSegment)((PathGeometry)arc.Data).Figures[0].Segments[0];
                var expectedArc = RingGeometry.ComputeUsedArc(used, 32, 32, 29);
                Check(arc.Visibility == Visibility.Visible
                    && Math.Abs(segment.Point.X - expectedArc.End.X) < 0.0001
                    && Math.Abs(segment.Point.Y - expectedArc.End.Y) < 0.0001
                    && ReferenceEquals(arc.Stroke, module.FindResource("AccentBrush"))
                    && ringHost.Children.OfType<System.Windows.Shapes.Ellipse>().Last().Visibility == Visibility.Collapsed,
                    "Rounding changed the Cursor widget arc, full-circle state or warning color.");

                flyout.Bind(snapshot);
                Arrange((FrameworkElement)flyout.Content, 440, 1000);
                Check(((TextBlock)flyout.FindName("CodexRingValueText")).Text == exactUsed
                    && Descendants<TextBlock>((FrameworkElement)flyout.Content)
                        .Any(text => text.Text.Contains(exactLeft, StringComparison.Ordinal)),
                    "Cursor detail popup lost its decimal used/remaining percentages.");
                if (directory is not null && used == 76.91)
                {
                    Save(widget, Path.Combine(directory, $"cursor-rounded-widget-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                        widget.LastLayout!.Width, null);
                    Save(flyout, Path.Combine(directory, $"cursor-rounded-popup-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                        440, null);
                }
            }
        }
        finally { widget.CloseWithoutActivation(); flyout.Close(); }
    }

    private static string CompactRingTarget(string targetLabel) => targetLabel switch
    {
        "Cursor Models" => "Cursor",
        "Other Models" => "Other",
        "Grok Bot" => "Grok Bot",
        _ => targetLabel
    };

    private static void CheckLargeMonetaryWidget(CodexQuotaSnapshot source,
        UiLanguage language, AppTheme theme, string? directory)
    {
        var snapshot = source with
        {
            Windows = source.Windows.Select(window => window.LimitId == "cursor-auto"
                ? window with
                {
                    UsedPercent = 10,
                    RemainingAmount = 123456.78m,
                    LimitAmount = 150000m,
                    Unit = "USD"
                } : window).ToArray()
        };
        var profile = new CodexAccountProfile("cursor-ui-large-money", "", UiText.T("Cursor account", "Cursor 계정"))
        {
            Provider = UsageProviderId.Cursor
        };
        var account = new CodexAccountView(profile, snapshot, "cursor@example.invalid") { IsConnected = true };
        var widget = new FloatingWidget { ShowActivated = false };
        try
        {
            widget.BindAccounts([account], profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, source.LastSuccessfulRefresh);
            WidgetFixture.RenderWidget(widget, null);
            var module = WidgetFixture.Module(widget);
            var auto = module.Periods.Single(period => period.PeriodText.Text == "Cursor Models");
            Check(auto.RemainingText.Text == "$123456.78",
                "Cursor widget lost a large monetary remaining value.");
            Check(auto.PeriodText.ActualWidth + 1 >= auto.PeriodText.DesiredSize.Width
                && auto.RemainingText.ActualWidth + 1 >= auto.RemainingText.DesiredSize.Width,
                "Cursor widget clipped a full monetary allowance name or value.");
            var row = Ancestor<Grid>(auto.PeriodText);
            Check(row is not null, "Cursor monetary allowance lost its measured row.");
            if (row is not null)
            {
                CheckNoOverlap(auto.PeriodText, auto.RemainingText, row,
                    "Cursor monetary allowance overlaps its value.");
            }
            Check((module.ToolTip as string ?? "").Contains("$123456.78", StringComparison.Ordinal),
                "Cursor widget monetary tooltip lost the exact remaining value.");
            if (directory is not null)
                Save(widget, Path.Combine(directory,
                    $"cursor-large-money-widget-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"),
                    widget.LastLayout?.Width ?? 380, null);
        }
        finally { widget.CloseWithoutActivation(); }
    }

    private static void CheckRows(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var rows = CodexDisplayFormatting.Rows(snapshot, now, includeResetCredits: true);
        foreach (var window in snapshot.Windows)
        {
            var label = CursorUsagePresentation.QuotaDisplayLabel(window.LimitId);
            Check(rows.Any(row => row.Label == label
                && row.Value.Contains(CursorUsagePresentation.RemainingText(window), StringComparison.Ordinal)),
                "Cursor popup row lost a named remaining value: " + label);
        }
        var disabled = snapshot.Windows.Single(window => window.LimitId == "cursor-on-demand");
        Check(rows.Any(row => row.Label == CursorUsagePresentation.QuotaDisplayLabel(disabled.LimitId)
            && row.Value.Contains(UiText.T("Off", "꺼짐"), StringComparison.Ordinal)),
            "Cursor disabled allowance was shown as an unknown or zero value.");
        var unknown = new CodexQuotaWindow("cursor-team-pool", null, null, null, CodexWindowKind.Other);
        var budget = new CodexQuotaWindow("cursor-on-demand", null, null, now.AddDays(2), CodexWindowKind.Other)
            { UsedAmount = 4.5m, LimitAmount = 20m, RemainingAmount = 15.5m, Unit = "USD", IsEnabled = true };
        var budgetRows = CodexDisplayFormatting.Rows(snapshot with { Windows = [unknown, budget] }, now);
        Check(budgetRows.Any(row => row.Label == CursorUsagePresentation.QuotaDisplayLabel(unknown.LimitId)
            && row.Value.Contains("?", StringComparison.Ordinal)),
            "Cursor unknown allowance was hidden or shown as zero.");
        Check(budgetRows.Any(row => row.Value.Contains("$15.5", StringComparison.Ordinal)),
            "Cursor separate spending budget lost its remaining amount.");
        Check(rows.Any(row => row.Label == UiText.T("Updated", "업데이트")),
            "Cursor popup has no Updated row.");
    }

    private static void CheckTooltip(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var tooltip = CycleArcPresentation.Tooltip(snapshot);
        foreach (var window in snapshot.Windows)
            Check(tooltip.Contains(CursorUsagePresentation.QuotaDisplayLabel(window.LimitId), StringComparison.Ordinal),
                "Cursor tooltip lost a named allowance.");
        Check(tooltip.Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
            "Cursor tooltip lost its update timestamp.");
    }

    private static CodexQuotaSnapshot Snapshot(DateTimeOffset now)
    {
        // Percentage Auto/API buckets, disabled on-demand with no cap and a separate
        // Grok allowance. Monetary and unknown cases are checked separately above.
        CodexQuotaWindow[] windows =
        [
            new("cursor-auto", 76.9, null, now.AddHours(12), CodexWindowKind.Other),
            new("cursor-api", 0, null, now.AddHours(12), CodexWindowKind.Other),
            new("cursor-on-demand", null, null, null, CodexWindowKind.Other) { IsEnabled = false },
            new("cursor-sand", 12.5, null, now.AddHours(6), CodexWindowKind.Other)
        ];
        return new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now,
            null, null, null, windows, CursorUsagePresentation.LiveDetail)
        {
            Provider = UsageProviderId.Cursor
        };
    }

    private static bool IsWidgetAllowance(CodexQuotaWindow window) =>
        window.IsEnabled != false
        && window.LimitId is "cursor-auto" or "cursor-api" or "cursor-sand";

    private static string LanguageSuffix(UiLanguage language) => language == UiLanguage.Korean ? "ko" : "en";
    private static string ThemeSuffix(AppTheme theme) => theme.ToString().ToLowerInvariant();

    private static void Save(Window window, string path, double width, double? height, Action? afterLayout = null)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        afterLayout?.Invoke();
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2),
            (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Arrange(FrameworkElement content, double width, double height)
    {
        content.UpdateLayout();
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(new Point(), new Size(width, height)));
        content.UpdateLayout();
    }

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private static void CheckNoOverlap(TextBlock label, FrameworkElement value,
        FrameworkElement row, string message)
    {
        Check(label.ActualWidth > 0 && value.ActualWidth > 0,
            message + " (zero-sized column)");
        var labelEnd = label.TranslatePoint(
            new Point(label.ActualWidth + label.Margin.Right, label.ActualHeight + label.Margin.Bottom), row);
        var valueStart = value.TranslatePoint(new Point(0, 0), row);
        Check(labelEnd.X <= valueStart.X + 1 || labelEnd.Y <= valueStart.Y + 1,
            message + $" (label ends {labelEnd}, value starts {valueStart})");
        Check(valueStart.X >= -1 && valueStart.X + value.ActualWidth <= row.ActualWidth + 1,
            message + " (value extends outside its row)");
        Check(label.ActualHeight <= row.ActualHeight + 1,
            message + $" (label height {label.ActualHeight:0.##} > row {row.ActualHeight:0.##})");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
}
