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
                Check(module.Periods.Count == snapshot.Windows.Count,
                    "Cursor widget merged or dropped named allowances.");
                foreach (var window in snapshot.Windows)
                {
                    var label = CursorUsagePresentation.QuotaLabel(window.LimitId);
                    Check(module.ToolTip!.ToString()!.Contains(label, StringComparison.Ordinal),
                        "Cursor widget tooltip omits a named allowance: " + label);
                }
                Check(module.ToolTip!.ToString()!.Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
                    "Cursor widget tooltip omits the update timestamp.");
                Check(module.StatusText.Text.Contains(CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
                    "Cursor widget omits the visible update timestamp.");
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
        }
        if (directory is not null)
            Console.WriteLine($"PASS: Cursor popup, widget and account controls EN/KO + Dark/Light; previews: {directory}");
    }

    private static void CheckRows(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var rows = CodexDisplayFormatting.Rows(snapshot, now, includeResetCredits: true);
        foreach (var window in snapshot.Windows)
        {
            var label = CursorUsagePresentation.QuotaLabel(window.LimitId);
            Check(rows.Any(row => row.Label == label
                && row.Value.Contains(CursorUsagePresentation.RemainingText(window), StringComparison.Ordinal)),
                "Cursor popup row lost a named remaining value: " + label);
        }
        var disabled = snapshot.Windows.Single(window => window.LimitId == "cursor-on-demand");
        Check(rows.Any(row => row.Label == CursorUsagePresentation.QuotaLabel(disabled.LimitId)
            && row.Value.Contains(UiText.T("Off", "꺼짐"), StringComparison.Ordinal)),
            "Cursor disabled allowance was shown as an unknown or zero value.");
        var unknown = new CodexQuotaWindow("cursor-team-pool", null, null, null, CodexWindowKind.Other);
        var budget = new CodexQuotaWindow("cursor-on-demand", null, null, now.AddDays(2), CodexWindowKind.Other)
            { UsedAmount = 4.5m, LimitAmount = 20m, RemainingAmount = 15.5m, Unit = "USD", IsEnabled = true };
        var budgetRows = CodexDisplayFormatting.Rows(snapshot with { Windows = [unknown, budget] }, now);
        Check(budgetRows.Any(row => row.Label == CursorUsagePresentation.QuotaLabel(unknown.LimitId)
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
            Check(tooltip.Contains(CursorUsagePresentation.QuotaLabel(window.LimitId), StringComparison.Ordinal),
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
}
