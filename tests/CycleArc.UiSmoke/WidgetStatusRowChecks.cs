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
/// Exercises the production widget's header status indicator. The fixtures are deliberately
/// offline and date-stable: they cover the exact server projections as well as the local
/// Claude receipts that must continue to retain their freshness details.
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
            CheckStandalone("codex", CodexAccount("codex-status", "Codex account", CodexSnapshot()), language, theme,
                expectHealthyCollapsed, directory, suffix);
            CheckStandalone("cursor", CursorAccount(CursorSnapshot()), language, theme,
                expectHealthyCollapsed, directory, suffix);
            CheckStandalone("claude", ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail)), language, theme,
                expectHealthyCollapsed, directory, suffix);
            CheckMixed(language, theme, expectHealthyCollapsed, directory, suffix);
            if (expectHealthyCollapsed)
            {
                CheckTransitions(language, theme, expectHealthyCollapsed, directory);
                CheckClaudeSources(language, theme, directory);
                CheckCommonWarnings(language, theme, directory);
            }
        }

        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine(expectHealthyCollapsed
            ? "PASS: compact widget header indicators, stable refresh/failure recovery, Claude source preservation and EN/KO Dark/Light 80/100/150 layouts."
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
        foreach (var zoom in new[] { 80, 100, 150 })
        foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Cursor, UsageProviderId.Claude })
            CheckProviderTransitions(provider, language, theme, zoom, expectHealthyCollapsed, directory);
    }

    private static void CheckProviderTransitions(UsageProviderId provider,
        UiLanguage language, AppTheme theme, int zoom, bool expectHealthyCollapsed, string? directory)
    {
        var healthy = provider == UsageProviderId.Cursor
            ? CursorAccount(CursorSnapshot())
            : provider == UsageProviderId.Claude ? ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail))
                : CodexAccount("codex-status", "Codex account", CodexSnapshot());
        var widget = CreateWidget();
        try
        {
            widget.SetZoom(zoom, notify: false);
            widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                WidgetFixture.Desktop, Now);
            var healthyModule = WidgetFixture.Module(widget);
            var healthyHeight = LayoutHeight(widget, healthyModule);
            Check(healthyHeight <= (provider == UsageProviderId.Cursor ? 150 : 112),
                provider + " healthy module contains excess vertical space.");
            var healthyPeriodCount = healthyModule.Periods.Count;
            CheckHealthyModule(healthyModule, healthy.Snapshot, expectHealthyCollapsed,
                provider + " transition healthy");

            var visibleHeight = MeasureWithStatusVisible(widget, healthyModule);
            Check(Math.Abs(visibleHeight - healthyHeight) < 0.01,
                provider + " header indicator changed the compact healthy height.");

            foreach (var (label, account) in FailureStates(provider))
            {
                widget.BindAccounts([account], account.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                var module = WidgetFixture.Module(widget);
                Check(module.StatusArea.Visibility == Visibility.Visible
                    && module.StatusText.Visibility == Visibility.Visible
                    && !string.IsNullOrWhiteSpace(module.StatusText.Text),
                    provider + " " + label + " hid its status indicator or tooltip summary.");
                var active = !module.Model!.StatusPresentation!.IsWarning
                    && (account.Snapshot.Status == CodexQuotaStatus.Refreshing || account.IsSigningIn);
                Check(module.StatusActivityIcon.Visibility == (active ? Visibility.Visible : Visibility.Collapsed),
                    provider + " " + label + " displayed the wrong activity indicator.");
                var withFooter = LayoutHeight(widget, module);
                module.StatusArea.Visibility = Visibility.Collapsed;
                var withoutFooter = LayoutHeight(widget, module);
                module.StatusArea.Visibility = Visibility.Visible;
                Check(Math.Abs(withFooter - withoutFooter) < 0.01,
                    provider + " " + label + " changed height when hiding status content.");
                if (module.Periods.Count == healthyPeriodCount)
                    Check(Math.Abs(withFooter - healthyHeight) < 0.01,
                        provider + " " + label + " changed height without changing quota rows.");
                LayoutHeight(widget, module);
                WidgetFixture.RenderWidget(widget, directory is null ? null : Path.Combine(directory,
                    $"widget-status-{provider.ToString().ToLowerInvariant()}-{label}-{LanguageSuffix(language)}-{ThemeSuffix(theme)}-{zoom}.png"));
                CheckModuleShape(widget, module, account.Snapshot, provider + " " + label);

                widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                CheckHealthyModule(WidgetFixture.Module(widget), healthy.Snapshot, expectHealthyCollapsed,
                    provider + " recovered from " + label);
                Check(Math.Abs(LayoutHeight(widget, WidgetFixture.Module(widget)) - healthyHeight) < 0.01,
                    provider + " recovery changed the compact healthy height.");
            }

            // Rebinding the latest server result must hide the content again, after every
            // failure state, without losing the exact tooltip/detail metadata.
            widget.BindAccounts([healthy], healthy.Profile.Id, UsagePeriodPreference.Auto,
                WidgetFixture.Desktop, Now);
            var recovered = WidgetFixture.Module(widget);
            CheckHealthyModule(recovered, healthy.Snapshot, expectHealthyCollapsed,
                provider + " transition recovered");
            Check(LayoutHeight(widget, recovered) == healthyHeight,
                provider + " recovery changed the compact healthy height.");
        }
        finally { widget.CloseWithoutActivation(); }
    }

    private static IEnumerable<(string Label, CodexAccountView Account)> FailureStates(UsageProviderId provider)
    {
        if (provider == UsageProviderId.Codex)
        {
            yield return ("refreshing", CodexAccount("codex-status", "Codex account", CodexSnapshot().AsRefreshing()));
            var stale = CodexSnapshot() with { Status = CodexQuotaStatus.Stale,
                LastSuccessfulRefresh = Now.AddMinutes(-12), TechnicalDetail = null };
            yield return ("stale", CodexAccount("codex-status", "Codex account", stale));
            yield return ("retry", CodexAccount("codex-status", "Codex account", stale.AsRefreshing()));
            yield return ("request-failed", CodexAccount("codex-status", "Codex account", stale with { TechnicalDetail = "timed-out" }));
            yield return ("identity", CodexAccount("codex-status", "Codex account", stale with { TechnicalDetail = "codex-identity-mismatch" }));
            yield break;
        }
        if (provider == UsageProviderId.Cursor)
        {
            yield return ("refreshing", CursorAccount(CursorSnapshot().AsRefreshing()));
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

        yield return ("refreshing", ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail).AsRefreshing()));
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

    private static void CheckCommonWarnings(UiLanguage language, AppTheme theme, string? directory)
    {
        var baseAccounts = new[]
        {
            CodexAccount("warning-codex", "Synthetic Codex with a long account nickname", CodexSnapshot()),
            ClaudeAccount(ClaudeSnapshot(ClaudeUsagePresentation.LiveDetail), "warning-claude", "Synthetic Claude"),
            CursorAccount(CursorSnapshot(), "warning-cursor", "Synthetic Cursor")
        };
        foreach (var zoom in new[] { 80, 100, 150 })
        foreach (var count in new[] { 1, 3, 5 })
        {
            var accounts = Enumerable.Range(0, count).Select(index =>
            {
                var account = baseAccounts[index % baseAccounts.Length];
                return account with
                {
                    Profile = account.Profile with { Id = account.Profile.Id + "-" + index },
                    Snapshot = account.Snapshot with { Status = CodexQuotaStatus.Stale,
                        TechnicalDetail = null, LastSuccessfulRefresh = Now.AddMinutes(-12) }
                };
            }).ToArray();
            var widget = CreateWidget();
            try
            {
                widget.SetZoom(zoom, notify: false);
                widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto,
                    WidgetFixture.Desktop, Now);
                WidgetFixture.RenderWidget(widget, directory is null ? null : Path.Combine(directory,
                    $"widget-common-warning-synthetic-{count}-{LanguageSuffix(language)}-{ThemeSuffix(theme)}-{zoom}.png"));
                foreach (var module in widget.Modules)
                {
                    Check(module.StatusText.Text == UiText.T("Previous data", "이전 데이터"),
                        "Equivalent stale states did not use the same concise summary.");
                    Check(module.StatusText.FontWeight == FontWeights.SemiBold
                        && module.StatusText.Foreground == (Brush)Application.Current.FindResource("StaleBrush"),
                        "Equivalent stale states did not use the shared warning color and emphasis.");
                    Check(module.StatusWarningIcon.Visibility == Visibility.Visible
                        && module.StatusWarningIcon.Data is not null,
                        "The status warning lost its font-independent vector icon.");
                    Check(module.StatusAgeText.Visibility == Visibility.Visible
                        && module.StatusAgeText.Text.Contains(UiText.T("12m ago", "12분 전"), StringComparison.Ordinal),
                        "The second status line lost the original valid observation age.");
                    Check(module.StatusText.TextWrapping == TextWrapping.NoWrap
                        && module.StatusAgeText.TextWrapping == TextWrapping.NoWrap,
                        "The status region can grow beyond two lines.");
                    Check(module.RingValueText.Foreground == (Brush)Application.Current.FindResource("TextBrush"),
                        "A status warning recolored the quota number.");
                    Check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(module))
                        && ReferenceEquals(module.StatusArea.ToolTip, module.StatusTooltip)
                        && module.StatusDetailText.Text.Length > 0,
                        "The status region lost its detailed tooltip/accessibility explanation.");
                    var area = (FrameworkElement)module.StatusTooltip.Content;
                    Arrange(area, 300, 300);
                    var summary = module.StatusText.TransformToAncestor(area).TransformBounds(new Rect(module.StatusText.RenderSize));
                    var age = module.StatusAgeText.TransformToAncestor(area).TransformBounds(new Rect(module.StatusAgeText.RenderSize));
                    Check(summary.Right <= area.ActualWidth + 0.5 && age.Right <= area.ActualWidth + 0.5
                        && age.Bottom <= area.ActualHeight + 0.5,
                        "The status tooltip text escapes its measured region.");
                    var indicator = module.StatusArea.TransformToAncestor(module)
                        .TransformBounds(new Rect(module.StatusArea.RenderSize));
                    var name = module.NameText.TransformToAncestor(module)
                        .TransformBounds(new Rect(module.NameText.RenderSize));
                    Check(indicator.Bottom <= name.Bottom + 4 && indicator.Top >= name.Top - 4,
                        "A warning icon moved outside the existing identity line.");
                }
                WidgetMultiAccountChecks.CheckAlignment(widget, "common warning " + count);
            }
            finally { widget.CloseWithoutActivation(); }
        }
    }

    private static void CheckHealthyModule(WidgetAccountModuleView module,
        CodexQuotaSnapshot snapshot, bool expectHealthyCollapsed, string label)
    {
        var expected = expectHealthyCollapsed ? Visibility.Collapsed : Visibility.Visible;
        Check(module.StatusText.Visibility == expected,
            label + " did not use the expected healthy server status-row visibility.");
        if (expectHealthyCollapsed)
            Check(module.StatusArea.Visibility == Visibility.Collapsed,
                label + " retained a healthy status indicator or reserved space.");
        Check(module.StatusText.Text.Contains(UiText.T("Updated", "업데이트됨"), StringComparison.Ordinal)
            || module.StatusText.Text.Contains(UiText.T("Updated from", "Claude 서버에서"), StringComparison.Ordinal),
            label + " lost the normal updated status text in its retained model/view.");
        Check((module.ToolTip as string ?? "").Contains(
            snapshot.Provider == UsageProviderId.Claude
                ? ClaudeUsagePresentation.LastReceivedText(snapshot)
                : snapshot.Provider == UsageProviderId.Cursor ? CursorUsagePresentation.UpdatedText(snapshot)
                    : WidgetStatusPresentation.From(snapshot, Now).DetailText, StringComparison.Ordinal),
            label + " tooltip lost its exact successful refresh timestamp.");
    }

    private static void CheckModuleShape(FloatingWidget widget, WidgetAccountModuleView module,
        CodexQuotaSnapshot snapshot, string label)
    {
        // The module's Width is the unscaled production column. ActualWidth includes the
        // widget's 80/100/150% LayoutTransform, so it is intentionally different at zoom.
        Check(Math.Abs(module.Width - WidgetGridLayout.ModuleWidth) <= 0.51,
            label + " changed the fixed module width.");
        var protectedIdentity = WidgetStatusPresentation.HidesQuota(snapshot);
        var expectedPeriods = CodexDisplayFormatting.ShowsQuotaWindows(snapshot) && !protectedIdentity
            ? snapshot.Windows.Count : 0;
        Check(module.Periods.Count == expectedPeriods,
            label + " changed the reported quota row count.");
        Check(module.Periods.All(line => line.RemainingText.Text.Length > 0),
            label + " lost a visible quota value.");
        var content = (FrameworkElement)widget.Content;
        ObservationRemovalUiChecks.AssertNoTrend(module, label);
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
        module.StatusArea.Visibility = Visibility.Visible;
        module.StatusText.Visibility = Visibility.Visible;
        var height = LayoutHeight(widget, module);
        module.StatusArea.Visibility = Visibility.Collapsed;
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
