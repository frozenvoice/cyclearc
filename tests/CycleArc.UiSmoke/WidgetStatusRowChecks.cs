using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// Exercises the production widget's account status footer. The fixtures are deliberately
/// offline and date-stable: they cover the exact server projections as well as the local
/// Claude receipts that must continue to retain their freshness row.
/// </summary>
internal static class WidgetStatusRowChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);

    public static void Run(string? directory = null, bool expectHealthyCollapsed = true)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException("App.ApplyTheme");

        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var suffix = $"{LanguageSuffix(language)}-{ThemeSuffix(theme)}";
            CheckStandalone("cursor", CursorAccount(CursorSnapshot()), language, theme,
                expectHealthyCollapsed, directory, suffix);
            CheckStandalone("claude", ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail)), language, theme,
                expectHealthyCollapsed, directory, suffix);
            CheckMixed(language, theme, expectHealthyCollapsed, directory, suffix);
            if (expectHealthyCollapsed)
            {
                CheckTransitions(language, theme, expectHealthyCollapsed, directory);
                CheckClaudeSources(language, theme, directory);
            }
        }

        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine(expectHealthyCollapsed
            ? "PASS: widget healthy status collapse, failure recovery, Claude source preservation and EN/KO Dark/Light 80/100/150 layouts."
            : "PASS: 36 original production WPF status-row baseline captures in EN/KO Dark/Light 80/100/150.");
    }

    private static void CheckStandalone(string kind, CodexAccountView account,
        UiLanguage language, AppTheme theme, bool expectHealthyCollapsed, string? directory, string suffix)
    {
        foreach (var zoom in new[] { 80, 100, 150 })
        {
            var widget = CreateWidget();
            try
            {
                widget.SetZoom(zoom, notify: false);
                widget.BindAccounts([account], account.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                var module = WidgetFixture.Module(widget);
                CheckHealthyModule(module, account.Snapshot, expectHealthyCollapsed, kind + " healthy");
                WidgetFixture.RenderWidget(widget, null);
                CheckModuleShape(widget, module, account.Snapshot, kind + " healthy");
                Console.WriteLine($"WIDGET-STATUS {kind}-{suffix}-{zoom} DIP={widget.LastLayout!.Width:0.##}x{widget.LastLayout.Height:0.##}");
                if (directory is not null)
                {
                    WidgetFixture.RenderWidget(widget, Path.Combine(directory,
                        $"widget-status-{kind}-{suffix}-{zoom}.png"));
                }
            }
            finally { widget.CloseWithoutActivation(); }
        }
    }

    private static void CheckMixed(UiLanguage language, AppTheme theme,
        bool expectHealthyCollapsed, string? directory, string suffix)
    {
        var codex = CodexAccount("mixed-codex", UiText.T("Codex", "Codex"), CodexSnapshot());
        var claude = ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail),
            "mixed-claude", UiText.T("Claude", "Claude"));
        var cursor = CursorAccount(CursorSnapshot(), "mixed-cursor", UiText.T("Cursor", "Cursor"));

        foreach (var zoom in new[] { 80, 100, 150 })
        {
            var widget = CreateWidget();
            try
            {
                var accounts = new[] { codex, claude, cursor };
                widget.SetZoom(zoom, notify: false);
                widget.BindAccounts(accounts, codex.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                WidgetFixture.RenderWidget(widget, null);
                Check(widget.Modules.Count == 3, "Mixed status fixture lost an account module.");
                Check(widget.Modules.Zip(accounts).All(pair => pair.First.StatusText.Visibility
                    == (pair.Second.Profile.Provider == UsageProviderId.Codex || expectHealthyCollapsed
                        ? Visibility.Collapsed : Visibility.Visible)),
                    "Mixed healthy server accounts did not use the expected status-row visibility.");
                WidgetMultiAccountChecks.CheckAlignment(widget, "mixed healthy status " + suffix);
                Console.WriteLine($"WIDGET-STATUS mixed-{suffix}-{zoom} DIP={widget.LastLayout!.Width:0.##}x{widget.LastLayout.Height:0.##}");
                foreach (var (module, account) in widget.Modules.Zip(accounts))
                {
                    CheckModuleShape(widget, module, account.Snapshot, "mixed " + account.Profile.Provider);
                    Check(module.NameText.Text == account.DisplayName,
                        "Mixed status fixture changed account name alignment content.");
                }

                if (directory is not null)
                    WidgetFixture.RenderWidget(widget, Path.Combine(directory,
                        $"widget-status-mixed-{suffix}-{zoom}.png"));
            }
            finally { widget.CloseWithoutActivation(); }
        }
    }

    private static void CheckTransitions(UiLanguage language, AppTheme theme, bool expectHealthyCollapsed, string? directory)
    {
        CheckProviderTransitions(UsageProviderId.Cursor, language, theme, expectHealthyCollapsed, directory);
        CheckProviderTransitions(UsageProviderId.Claude, language, theme, expectHealthyCollapsed, directory);
    }

    private static void CheckProviderTransitions(UsageProviderId provider,
        UiLanguage language, AppTheme theme, bool expectHealthyCollapsed, string? directory)
    {
        var healthy = provider == UsageProviderId.Cursor
            ? CursorAccount(CursorSnapshot())
            : ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail));
        var widget = CreateWidget();
        try
        {
            widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                WidgetFixture.Desktop, Now);
            var healthyModule = WidgetFixture.Module(widget);
            var healthyHeight = LayoutHeight(widget, healthyModule);
            CheckHealthyModule(healthyModule, healthy.Snapshot, expectHealthyCollapsed,
                provider + " transition healthy");

            var visibleHeight = MeasureWithStatusVisible(widget, healthyModule);
            Check(visibleHeight - healthyHeight >= 5 - 0.01,
                provider + " healthy footer did not remove its 5 DIP top margin from layout.");

            foreach (var (label, account) in FailureStates(provider))
            {
                widget.BindAccounts([account], account.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                var module = WidgetFixture.Module(widget);
                Check(module.StatusText.Visibility == Visibility.Visible
                    && !string.IsNullOrWhiteSpace(module.StatusText.Text),
                    provider + " " + label + " hid its recovery/status footer.");
                var withFooter = LayoutHeight(widget, module);
                module.StatusText.Visibility = Visibility.Collapsed;
                var withoutFooter = LayoutHeight(widget, module);
                module.StatusText.Visibility = Visibility.Visible;
                Check(withFooter - withoutFooter >= 5 - 0.01,
                    provider + " " + label + " did not restore footer layout height.");
                LayoutHeight(widget, module);
                WidgetFixture.RenderWidget(widget, directory is null ? null : Path.Combine(directory,
                    $"widget-status-{provider.ToString().ToLowerInvariant()}-{label}-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"));
                CheckModuleShape(widget, module, account.Snapshot, provider + " " + label);

                widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                CheckHealthyModule(WidgetFixture.Module(widget), healthy.Snapshot, expectHealthyCollapsed,
                    provider + " recovered from " + label);
                Check(Math.Abs(LayoutHeight(widget, WidgetFixture.Module(widget)) - healthyHeight) < 0.01,
                    provider + " recovery did not return to the compact healthy height.");
            }

            // Rebinding the latest server result must collapse the row again, after every
            // failure state, without losing the exact tooltip/detail metadata.
            widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                WidgetFixture.Desktop, Now);
            var recovered = WidgetFixture.Module(widget);
            CheckHealthyModule(recovered, healthy.Snapshot, expectHealthyCollapsed,
                provider + " transition recovered");
            Check(LayoutHeight(widget, recovered) == healthyHeight,
                provider + " recovery did not return to the compact healthy height.");
        }
        finally { widget.CloseWithoutActivation(); }
    }

    private static IEnumerable<(string Label, CodexAccountView Account)> FailureStates(UsageProviderId provider)
    {
        if (provider == UsageProviderId.Cursor)
        {
            yield return ("stale", CursorAccount(CursorSnapshot(CodexQuotaStatus.Stale, "cursor-live-request-failed",
                Now.AddHours(-3))));
            yield return ("request-failed", CursorAccount(CursorSnapshot(CodexQuotaStatus.Available,
                "cursor-live-request-failed", Now)));
            yield return ("auth", CursorAccount(CursorSnapshot(CodexQuotaStatus.Stale,
                "cursor-live-auth-required", Now.AddHours(-3))));
            yield return ("identity", CursorAccount(CursorSnapshot(CodexQuotaStatus.Available,
                "cursor-live-identity-mismatch", Now)));
            yield break;
        }

        yield return ("stale", ClaudeAccount(ClaudeSnapshot("claude-live-request-failed",
            CodexQuotaStatus.Stale, Now.AddHours(-3))));
        yield return ("request-failed", ClaudeAccount(ClaudeSnapshot("claude-live-request-failed",
            CodexQuotaStatus.Available, Now)));
        yield return ("auth", ClaudeAccount(ClaudeSnapshot("claude-live-auth-required",
            CodexQuotaStatus.Available, Now)));
        yield return ("identity", ClaudeAccount(ClaudeSnapshot("claude-live-identity-mismatch",
            CodexQuotaStatus.Available, Now)));
        yield return ("awaiting", ClaudeAccount(ClaudeSnapshot("claude-connected-waiting",
            CodexQuotaStatus.Unavailable, null), connected: true));
        yield return ("signing-in", ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail),
            signingIn: true));
    }

    private static void CheckClaudeSources(UiLanguage language, AppTheme theme, string? directory)
    {
        var sources = new[]
        {
            ("server", ClaudeUsagePresentation.LiveDetail),
            ("desktop-history", "claude-desktop-history"),
            ("code-receipt", "claude-statusline"),
            ("awaiting", "claude-connected-waiting")
        };
        foreach (var (label, detail) in sources)
        {
            var awaiting = detail == "claude-connected-waiting";
            var snapshot = ClaudeSnapshot(detail,
                awaiting ? CodexQuotaStatus.Unavailable : CodexQuotaStatus.Available,
                awaiting ? null : Now.AddMinutes(-4));
            var account = ClaudeAccount(snapshot, connected: awaiting);
            var widget = CreateWidget();
            FlyoutWindow? flyout = null;
            try
            {
                widget.BindAccounts([account], account.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                var module = WidgetFixture.Module(widget);
                WidgetFixture.RenderWidget(widget, directory is null ? null : Path.Combine(directory,
                    $"widget-status-claude-source-{label}-{LanguageSuffix(language)}-{ThemeSuffix(theme)}.png"));
                Check(module.StatusText.Visibility == (ClaudeUsagePresentation.IsLive(snapshot)
                        ? Visibility.Collapsed : Visibility.Visible),
                    "Claude " + label + " incorrectly hid its receipt/status row.");
                Check(module.StatusText.Text.Contains(CycleArcPresentation.StatusLabel(snapshot),
                    StringComparison.Ordinal), "Claude " + label + " status text changed.");
                var tooltip = module.ToolTip as string ?? "";
                Check(tooltip.Contains(ClaudeUsagePresentation.SourceText(snapshot), StringComparison.Ordinal),
                    "Claude " + label + " widget tooltip lost its source.");
                Check(tooltip.Contains(ClaudeUsagePresentation.LastReceivedText(snapshot), StringComparison.Ordinal),
                    "Claude " + label + " widget tooltip lost its receipt time.");

                flyout = new FlyoutWindow { ShowActivated = false };
                flyout.Bind(snapshot);
                Arrange((FrameworkElement)flyout.Content, 440, 1000);
                var popup = (FrameworkElement)flyout.Content;
                var popupText = string.Join("\n", Descendants<TextBlock>(popup).Select(text => text.Text));
                Check(popupText.Contains(ClaudeUsagePresentation.StatusText(snapshot), StringComparison.Ordinal),
                    "Claude " + label + " detail popup lost its source.");
                Check(snapshot.LastSuccessfulRefresh is null || Descendants<FrameworkElement>(popup)
                    .Any(element => (element.ToolTip as string ?? "").Contains(
                        ClaudeUsagePresentation.LastReceivedText(snapshot), StringComparison.Ordinal)),
                    "Claude " + label + " detail popup lost its receipt time.");
            }
            finally
            {
                flyout?.Close();
                widget.CloseWithoutActivation();
            }
        }
    }

    private static void CheckHealthyModule(WidgetAccountModuleView module,
        CodexQuotaSnapshot snapshot, bool expectHealthyCollapsed, string label)
    {
        var expected = expectHealthyCollapsed ? Visibility.Collapsed : Visibility.Visible;
        Check(module.StatusText.Visibility == expected,
            label + " did not use the expected healthy server status-row visibility.");
        Check(module.StatusText.Text.Contains(UiText.T("Updated", "업데이트됨"), StringComparison.Ordinal)
            || module.StatusText.Text.Contains(UiText.T("Updated from", "Claude 서버에서"), StringComparison.Ordinal),
            label + " lost the normal updated status text in its retained model/view.");
        Check((module.ToolTip as string ?? "").Contains(
            snapshot.Provider == UsageProviderId.Claude
                ? ClaudeUsagePresentation.LastReceivedText(snapshot)
                : CursorUsagePresentation.UpdatedText(snapshot), StringComparison.Ordinal),
            label + " tooltip lost its exact successful refresh timestamp.");
    }

    private static void CheckModuleShape(FloatingWidget widget, WidgetAccountModuleView module,
        CodexQuotaSnapshot snapshot, string label)
    {
        // The module's Width is the unscaled production column. ActualWidth includes the
        // widget's 80/100/150% LayoutTransform, so it is intentionally different at zoom.
        Check(Math.Abs(module.Width - WidgetGridLayout.ModuleWidth) <= 0.51,
            label + " changed the fixed module width.");
        var protectedIdentity = CodexIdentityPresentation.NeedsReconnection(snapshot)
            || snapshot.TechnicalDetail is "cursor-live-identity-mismatch" or "cursor-identity-mismatch";
        var expectedPeriods = CodexDisplayFormatting.ShowsQuotaWindows(snapshot) && !protectedIdentity
            ? snapshot.Windows.Count : 0;
        Check(module.Periods.Count == expectedPeriods,
            label + " changed the reported quota row count.");
        Check(module.Periods.All(line => line.RemainingText.Text.Length > 0),
            label + " lost a visible quota value.");
        var content = (FrameworkElement)widget.Content;
        var moduleBounds = module.TransformToAncestor(content).TransformBounds(new Rect(module.RenderSize));
        Check(moduleBounds.Right <= content.ActualWidth + 1 && moduleBounds.Bottom <= content.ActualHeight + 1,
            label + " is clipped by the widget layout.");
    }

    private static double LayoutHeight(FloatingWidget widget, WidgetAccountModuleView module)
    {
        var content = (FrameworkElement)widget.Content;
        ((FrameworkElement)module.Child).InvalidateMeasure();
        module.InvalidateMeasure();
        content.UpdateLayout();
        widget.Relayout(WidgetFixture.Desktop);
        WidgetFixture.RenderWidget(widget, null);
        return module.DesiredSize.Height;
    }

    private static double MeasureWithStatusVisible(FloatingWidget widget, WidgetAccountModuleView module)
    {
        module.StatusText.Visibility = Visibility.Visible;
        var height = LayoutHeight(widget, module);
        module.StatusText.Visibility = Visibility.Collapsed;
        LayoutHeight(widget, module);
        return height;
    }

    private static FloatingWidget CreateWidget()
    {
        var widget = new FloatingWidget { ShowActivated = false };
        VisualTreeHelper.SetRootDpi(widget, new DpiScale(1, 1));
        VisualTreeHelper.SetRootDpi((FrameworkElement)widget.Content, new DpiScale(1, 1));
        return widget;
    }

    private static CodexAccountView CodexAccount(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label), snapshot);

    private static CodexAccountView ClaudeAccount(CodexQuotaSnapshot snapshot,
        string id = "claude-status", string label = "Claude account", bool connected = true, bool signingIn = false) =>
        new(new CodexAccountProfile(id, "", label) { Provider = UsageProviderId.Claude }, snapshot with
        { Provider = UsageProviderId.Claude }, "claude@example.invalid", signingIn)
        { IsConnected = connected };

    private static CodexAccountView CursorAccount(CodexQuotaSnapshot snapshot,
        string id = "cursor-status", string label = "Cursor account") =>
        new(new CodexAccountProfile(id, "", label) { Provider = UsageProviderId.Cursor }, snapshot with
        { Provider = UsageProviderId.Cursor }, "cursor@example.invalid") { IsConnected = true };

    private static CodexQuotaSnapshot CodexSnapshot() =>
        new(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null,
        [new("codex-week", 41.2, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(6), CodexWindowKind.Weekly)], null);

    private static CodexQuotaSnapshot CursorSnapshot(CodexQuotaStatus status = CodexQuotaStatus.Available,
        string? detail = null, DateTimeOffset? last = null) =>
        new(status, "pro", last ?? Now, last ?? Now, null, null, null,
        [
            new("cursor-auto", 76.91, null, Now.AddDays(18), CodexWindowKind.Other),
            new("cursor-api", 2.9, null, Now.AddDays(18), CodexWindowKind.Other),
            new("cursor-sand", 12.5, null, Now.AddDays(4), CodexWindowKind.Other)
        ], detail)
        { Provider = UsageProviderId.Cursor };

    private static CodexQuotaSnapshot ClaudeSnapshot(string? detail,
        CodexQuotaStatus status = CodexQuotaStatus.Available, DateTimeOffset? last = null) =>
        new(status, "pro", last ?? (status == CodexQuotaStatus.Available ? Now : null),
        last ?? (status == CodexQuotaStatus.Available ? Now : null), null, null, null,
        [
            new("claude-five", 76.91, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(2), CodexWindowKind.FiveHour),
            new("claude-week", 12.5, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(5), CodexWindowKind.Weekly)
        ], detail)
        { Provider = UsageProviderId.Claude };

    private static string LanguageSuffix(UiLanguage language) => language == UiLanguage.Korean ? "ko" : "en";
    private static string ThemeSuffix(AppTheme theme) => theme.ToString().ToLowerInvariant();

    private static void Arrange(FrameworkElement content, double width, double height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
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
