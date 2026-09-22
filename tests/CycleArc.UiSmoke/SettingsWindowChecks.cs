using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
                var window = new SettingsWindow(new AppSettings { UiLanguage = language, Theme = theme });
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

                    foreach (var name in new[] { "GeneralTab", "WidgetTab", "ConnectionTab" })
                    {
                        var tab = (TabItem)window.FindName(name);
                        tabs.SelectedItem = tab;
                        content.UpdateLayout();
                        content.Measure(new Size(width, height));
                        content.Arrange(new Rect(new Point(), new Size(width, height)));
                        content.UpdateLayout();

                        var scroller = (ScrollViewer)tab.Content;
                        var overflow = scroller.ScrollableHeight;
                        worst = Math.Max(worst, overflow);
                        var where = $"{name} at {language}/{theme}";

                        // Whatever a tab's height, its content must be reachable by scrolling.
                        Check(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            $"{where} cannot scroll, so overflowing content would be unreachable.");
                        checks++;

                        if (name != "GeneralTab") continue;
                        // Collected across every language and theme, so one failure reports the
                        // whole picture instead of the first combination that happens to overflow.
                        firstTab.Add(($"{language}/{theme}", overflow, width, height));
                        if (directory is not null)
                        {
                            var suffix = $"{(language == UiLanguage.English ? "en" : "ko")}-{theme.ToString().ToLowerInvariant()}";
                            DocumentationScreenshots.Save(window, Path.Combine(directory, $"settings-{suffix}.png"), width, height);
                        }
                        Check(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            $"{where} cannot scroll.");
                        checks++;

                        // Nothing in the first tab may be cut off horizontally either.
                        foreach (var text in Descendants<TextBlock>(scroller).Where(t => t.Text.Length > 0))
                        {
                            Check(text.ActualWidth + 0.5 >= Math.Min(text.DesiredSize.Width, scroller.ActualWidth),
                                $"{where}: a line is clipped horizontally.");
                        }
                        checks++;
                    }
                    tabs.SelectedItem = window.FindName("GeneralTab");
                    content.Measure(new Size(window.MinWidth, window.MinHeight));
                    content.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
                    content.UpdateLayout();
                    var compactScroll = (ScrollViewer)((TabItem)tabs.SelectedItem).Content;
                    snap.BringIntoView();
                    content.UpdateLayout();
                    var label = (TextBlock)window.FindName("EdgeSnapLabel");
                    Check(label.ActualHeight + 0.5 >= label.DesiredSize.Height && snap.ActualWidth >= 44,
                        "The edge option clips at the minimum settings size.");
                    Check(compactScroll.ScrollableHeight >= 0 && compactScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                        "Compact settings must keep every option reachable.");
                    checks += 2;
                }
                finally { window.Close(); }
            }
            CheckSavingSnapOption();
            checks += 9;
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

        Console.WriteLine($"PASS: {checks} settings window checks; the General tab fits the window's own "
            + $"declared size in both languages and themes (worst tab overflow {worst:n0} DIP), and every "
            + "tab can still scroll.");
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
