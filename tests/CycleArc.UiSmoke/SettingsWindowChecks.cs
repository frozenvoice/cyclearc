using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// The settings window at the size it actually opens at.
///
/// Each tab scrolls when it has to, so long content is never unreachable. What must not happen
/// is the first tab arriving already scrolled: the window opens on General, and a person who
/// does not notice a scrollbar there simply does not see the rest of it. The documentation
/// preview is rendered at the same declared size, so a General tab that overflows also produces
/// a screenshot cut through the middle of a line.
/// </summary>
internal static class SettingsWindowChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousLanguage = UiText.Language;
        var checks = 0;
        var worst = 0d;
        var firstTab = new List<(string Where, double Overflow, double Width, double Height)>();
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var window = new SettingsWindow(new AppSettings
                {
                    UiLanguage = language,
                    Theme = theme,
                    FloatingWidgetEnabled = true
                });
                try
                {
                    // The size the window declares is the size it opens at, and the size the
                    // documentation exporter renders it at. Read it rather than repeating it.
                    var width = window.Width;
                    var height = window.Height;
                    var content = (FrameworkElement)window.Content;
                    var tabs = (TabControl)window.FindName("SettingsTabs");
                    var snap = (CheckBox)window.FindName("EdgeSnapBox");
                    Check(snap.IsChecked == true && snap.IsEnabled, "Edge snapping must default on independently of widget visibility.");
                    Check(AutomationProperties.GetName(snap) == UiText.T("Snap windows to screen edges", "화면 가장자리에 자동 정렬"),
                        "Edge snapping needs the localized accessible name.");
                    checks += 2;

                    var assembly = typeof(SettingsWindow).Assembly;
                    var buildVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? assembly.GetName().Version?.ToString() ?? "?";
                    var versionCaption = (TextBlock)window.FindName("VersionCaption");
                    var versionText = (TextBlock)window.FindName("VersionText");
                    Check(versionCaption.Text == UiText.T("Current version", "현재 실행 버전")
                        && versionText.Text == "v" + buildVersion.Split('+')[0]
                        && Equals(versionText.ToolTip, buildVersion)
                        && AutomationProperties.GetName(versionText) == versionCaption.Text + " " + buildVersion,
                        "The settings version must match the running assembly and expose its full build version.");
                    checks++;

                    foreach (var name in new[] { "GeneralTab", "WidgetTab", "ConnectionTab" })
                    {
                        var tab = (TabItem)window.FindName(name);
                        var scroller = ArrangeTab(content, tabs, tab, new Size(width, height));
                        var overflow = scroller.ScrollableHeight;
                        worst = Math.Max(worst, overflow);
                        var where = $"{name} at {language}/{theme}";

                        // Whatever a tab's height, its content must be reachable by scrolling.
                        Check(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            $"{where} cannot scroll, so overflowing content would be unreachable.");
                        checks++;

                        CheckTextNotClipped(scroller, where);
                        checks++;

                        if (directory is not null)
                        {
                            var suffix = $"{(language == UiLanguage.English ? "en" : "ko")}-{theme.ToString().ToLowerInvariant()}";
                            DocumentationScreenshots.Save(window,
                                Path.Combine(directory, $"{DefaultPreviewName(name)}-{suffix}.png"), width, height);
                        }

                        if (name == "GeneralTab")
                        {
                            // Collected across every language and theme, so one failure reports the
                            // whole picture instead of the first combination that happens to overflow.
                            firstTab.Add(($"{language}/{theme}", overflow, width, height));
                        }
                    }

                    var compactSize = new Size(window.MinWidth, window.MinHeight);
                    foreach (var name in new[] { "GeneralTab", "WidgetTab", "ConnectionTab" })
                    {
                        var tab = (TabItem)window.FindName(name);
                        var scroller = ArrangeTab(content, tabs, tab, compactSize);
                        var where = $"{name} at compact {language}/{theme}";
                        Check(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            $"{where} cannot scroll, so overflowing content would be unreachable.");
                        CheckTextNotClipped(scroller, where);
                        checks += 2;
                        if (directory is not null)
                        {
                            var suffix = $"{(language == UiLanguage.English ? "en" : "ko")}-{theme.ToString().ToLowerInvariant()}";
                            DocumentationScreenshots.Save(window,
                                Path.Combine(directory, $"{CompactPreviewName(name)}-{suffix}.png"),
                                compactSize.Width, compactSize.Height);
                        }
                    }

                    tabs.SelectedItem = window.FindName("GeneralTab");
                    ArrangeTab(content, tabs, (TabItem)window.FindName("GeneralTab"), compactSize);
                    var compactScroll = (ScrollViewer)((TabItem)tabs.SelectedItem).Content;
                    snap.BringIntoView();
                    content.UpdateLayout();
                    var label = (TextBlock)window.FindName("EdgeSnapLabel");
                    Check(label.ActualHeight + 0.5 >= label.DesiredSize.Height && snap.ActualWidth >= 44,
                        "The edge option clips at the minimum settings size.");
                    Check(compactScroll.ScrollableHeight >= 0 && compactScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                        "Compact settings must keep every option reachable.");
                    checks += 2;

                    CheckWidgetOptionEnablement(window, content);
                    checks += 2;
                    checks += CheckHelpExpander(window, content, tabs, "GeneralTab", "TrayDetails", language, theme);
                    checks += CheckHelpExpander(window, content, tabs, "ConnectionTab", "ConnectionHelp", language, theme);
                }
                finally { window.Close(); }
            }
            CheckSavingSnapOption();
            checks += 9;
            CheckCancelPreservesSettings();
            checks++;
        }
        finally
        {
            UiText.SetLanguage(previousLanguage);
            applyTheme.Invoke(null, [AppTheme.Dark]);
        }

        var overflowing = firstTab.Where(entry => entry.Overflow > 0.5).ToArray();
        Check(overflowing.Length == 0,
            "The settings window opens on General, so that tab must fit the size the window "
            + "declares. It does not: "
            + string.Join("; ", overflowing.Select(entry =>
                $"{entry.Where} overflows by {entry.Overflow:n0} DIP at {entry.Width:n0}x{entry.Height:n0}")));

        Console.WriteLine($"PASS: {checks} settings window checks; General fits its declared size in both "
            + $"languages and themes (worst tab overflow {worst:n0} DIP), every tab scrolls, and the "
            + "default/compact EN/KO and Dark/Light previews were rendered.");
    }

    private static ScrollViewer ArrangeTab(FrameworkElement content, TabControl tabs, TabItem tab, Size size)
    {
        tabs.SelectedItem = tab;
        content.UpdateLayout();
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        var scroller = (ScrollViewer)tab.Content;
        scroller.ScrollToTop();
        content.UpdateLayout();
        return scroller;
    }

    private static string DefaultPreviewName(string tabName) => tabName switch
    {
        "GeneralTab" => "settings",
        "WidgetTab" => "settings-widget",
        "ConnectionTab" => "settings-connection",
        _ => throw new ArgumentOutOfRangeException(nameof(tabName))
    };

    private static string CompactPreviewName(string tabName) => tabName switch
    {
        "GeneralTab" => "settings-compact",
        "WidgetTab" => "settings-widget-compact",
        "ConnectionTab" => "settings-connection-compact",
        _ => throw new ArgumentOutOfRangeException(nameof(tabName))
    };

    private static void CheckTextNotClipped(ScrollViewer scroller, string where)
    {
        foreach (var text in Descendants<TextBlock>(scroller).Where(t =>
                     t.Text.Length > 0 && t.RenderSize.Width > 0 && IsVisibleWithin(t, scroller)))
        {
            var bounds = text.TransformToAncestor(scroller).TransformBounds(new Rect(text.RenderSize));
            // DesiredSize includes Margin; ActualWidth measures only the text element.
            var margin = text.Margin.Left + text.Margin.Right;
            var desiredWidth = Math.Max(0, text.DesiredSize.Width - margin);
            Check(text.ActualWidth + 0.5 >= Math.Min(desiredWidth, Math.Max(0, scroller.ActualWidth - margin))
                && bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5,
                $"{where}: '{text.Text}' is clipped horizontally ({text.ActualWidth:n1} < {desiredWidth:n1} DIP).");
        }
    }

    private static bool IsVisibleWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && visual.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static void CheckWidgetOptionEnablement(SettingsWindow window, FrameworkElement content)
    {
        var enabled = (CheckBox)window.FindName("WidgetBox");
        var alwaysOnTop = (CheckBox)window.FindName("WidgetTopBox");
        var clickThrough = (CheckBox)window.FindName("WidgetClickThroughBox");
        var opacity = (Slider)window.FindName("WidgetOpacityBox");

        enabled.IsChecked = false;
        content.UpdateLayout();
        Check(!alwaysOnTop.IsEnabled && !clickThrough.IsEnabled && !opacity.IsEnabled,
            "Disabling the widget must disable its dependent options.");

        enabled.IsChecked = true;
        content.UpdateLayout();
        Check(alwaysOnTop.IsEnabled && clickThrough.IsEnabled && opacity.IsEnabled,
            "Enabling the widget must enable its dependent options.");
    }

    private static int CheckHelpExpander(SettingsWindow window, FrameworkElement content, TabControl tabs,
        string tabName, string expanderName, UiLanguage language, AppTheme theme)
    {
        var tab = (TabItem)window.FindName(tabName);
        var scroller = ArrangeTab(content, tabs, tab, new Size(window.Width, window.Height));
        var expander = (Expander)window.FindName(expanderName);
        var where = $"{expanderName} at {language}/{theme}";
        Check(!expander.IsExpanded, $"{where} must be collapsed by default.");
        var checks = 1;

        expander.IsExpanded = true;
        content.UpdateLayout();
        scroller.UpdateLayout();
        var helpContent = expander.Content as FrameworkElement;
        Check(expander.IsExpanded && helpContent is not null
            && helpContent.Visibility == Visibility.Visible
            && helpContent.DesiredSize.Height > 0 && helpContent.RenderSize.Height > 0,
            $"{where} did not reveal its help content.");
        checks++;

        CheckTextNotClipped(scroller, $"{where} expanded");
        checks++;

        scroller.ScrollToEnd();
        content.UpdateLayout();
        scroller.UpdateLayout();
        var expandedContent = helpContent!;
        var bounds = expandedContent.TransformToAncestor(scroller).TransformBounds(new Rect(expandedContent.RenderSize));
        Check(expandedContent.Visibility == Visibility.Visible && expandedContent.RenderSize.Height > 0
            && bounds.Top >= -1 && bounds.Bottom <= scroller.ActualHeight + 1,
            $"{where} help content cannot be reached by scrolling its tab.");
        checks++;

        expander.IsExpanded = false;
        content.UpdateLayout();
        Check(!expander.IsExpanded, $"{where} could not return to its collapsed state.");
        return checks + 1;
    }

    private static void CheckSavingSnapOption()
    {
        var settings = new AppSettings
        {
            WidgetLeft = 600, WidgetTop = 500, FlyoutLeft = 200, FlyoutTop = 300,
            WidgetHorizontalAnchor = HorizontalEdgeAnchor.Right, WidgetVerticalAnchor = VerticalEdgeAnchor.Bottom,
            FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Left, FlyoutVerticalAnchor = VerticalEdgeAnchor.Top
        };
        // Drive the production Save button, without App startup or the user's settings store.
        foreach (var enabled in new[] { true, false, true })
        {
            var window = new SettingsWindow(settings);
            AppSettings? saved = null;
            window.Saved += value => saved = value;
            ((CheckBox)window.FindName("EdgeSnapBox")).IsChecked = enabled;
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(settings, saved) && settings.SnapWindowsToScreenEdges == enabled,
                "The settings save path dropped the edge option.");
            if (enabled && settings.WidgetHorizontalAnchor == HorizontalEdgeAnchor.Right)
                Check(settings.FlyoutHorizontalAnchor == HorizontalEdgeAnchor.Left, "Unrelated Save lost an independent anchor.");
            else
                Check(settings.WidgetHorizontalAnchor == HorizontalEdgeAnchor.None
                    && settings.WidgetVerticalAnchor == VerticalEdgeAnchor.None
                    && settings.FlyoutHorizontalAnchor == HorizontalEdgeAnchor.None
                    && settings.FlyoutVerticalAnchor == VerticalEdgeAnchor.None, "Disabled anchors reattached on Save.");
            Check(settings.WidgetLeft == 600 && settings.FlyoutLeft == 200, "Changing the option moved a saved window.");
        }
    }

    private static void CheckCancelPreservesSettings()
    {
        var settings = new AppSettings
        {
            Theme = AppTheme.Light,
            UiLanguage = UiLanguage.Korean,
            TrayIconStyle = TrayIconStyle.ProgressRing,
            StartWithWindows = false,
            SnapWindowsToScreenEdges = true,
            FloatingWidgetEnabled = true,
            WidgetOpacity = 0.7,
            WidgetAlwaysOnTop = true,
            WidgetClickThrough = false,
            CodexExePath = @"C:\CycleArc-original\codex.exe",
            CodexRefreshIntervalMinutes = 10,
            WidgetLeft = 600,
            WidgetTop = 500,
            FlyoutLeft = 200,
            FlyoutTop = 300,
            WidgetHorizontalAnchor = HorizontalEdgeAnchor.Right,
            WidgetVerticalAnchor = VerticalEdgeAnchor.Bottom,
            FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Left,
            FlyoutVerticalAnchor = VerticalEdgeAnchor.Top
        };
        var window = new SettingsWindow(settings);
        try
        {
            ((Selector)window.FindName("ThemeBox")).SelectedIndex = (int)AppTheme.Dark;
            ((Selector)window.FindName("LanguageBox")).SelectedIndex = 1;
            ((Selector)window.FindName("IconBox")).SelectedIndex = (int)TrayIconStyle.RemainingNumber;
            ((CheckBox)window.FindName("StartupBox")).IsChecked = true;
            ((CheckBox)window.FindName("EdgeSnapBox")).IsChecked = false;
            ((CheckBox)window.FindName("WidgetBox")).IsChecked = false;
            ((CheckBox)window.FindName("WidgetTopBox")).IsChecked = false;
            ((CheckBox)window.FindName("WidgetClickThroughBox")).IsChecked = true;
            ((Slider)window.FindName("WidgetOpacityBox")).Value = 0.25;
            ((TextBox)window.FindName("CodexExeBox")).Text = @"C:\CycleArc-changed\codex.exe";
            var refresh = (Selector)window.FindName("RefreshIntervalBox");
            refresh.SelectedIndex = (refresh.SelectedIndex + 1) % refresh.Items.Count;

            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Check(settings.Theme == AppTheme.Light
                && settings.UiLanguage == UiLanguage.Korean
                && settings.TrayIconStyle == TrayIconStyle.ProgressRing
                && !settings.StartWithWindows
                && settings.SnapWindowsToScreenEdges
                && settings.FloatingWidgetEnabled
                && Math.Abs(settings.WidgetOpacity - 0.7) < 0.001
                && settings.WidgetAlwaysOnTop
                && !settings.WidgetClickThrough
                && settings.CodexExePath == @"C:\CycleArc-original\codex.exe"
                && settings.CodexRefreshIntervalMinutes == 10
                && settings.WidgetLeft == 600 && settings.WidgetTop == 500
                && settings.FlyoutLeft == 200 && settings.FlyoutTop == 300
                && settings.WidgetHorizontalAnchor == HorizontalEdgeAnchor.Right
                && settings.WidgetVerticalAnchor == VerticalEdgeAnchor.Bottom
                && settings.FlyoutHorizontalAnchor == HorizontalEdgeAnchor.Left
                && settings.FlyoutVerticalAnchor == VerticalEdgeAnchor.Top,
                "Cancel changed in-memory preferences or saved window placement.");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
