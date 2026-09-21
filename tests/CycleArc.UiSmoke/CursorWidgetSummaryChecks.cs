using System.IO;
using System.Reflection;
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
/// Synthetic widget captures and layout checks for reviewing Cursor's summary beside the other
/// providers. It never loads the account store or reaches a provider endpoint.
/// </summary>
internal static class CursorWidgetSummaryChecks
{
    private static readonly DateTimeOffset Now = new(2035, 1, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthlyReset = Now.AddDays(12);
    private static readonly DateTimeOffset WeeklyReset = Now.AddDays(3);

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("App.ApplyTheme");
        var count = 0;

        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var prefix = $"{language}-{theme}".ToLowerInvariant();
            foreach (var zoom in new[] { 80, 100, 150 })
            {
                count += Render(directory, $"cursor-widget-{prefix}-{zoom}", CursorOnly(), "cursor-fixture", zoom);
                count += Render(directory, $"cursor-widget-mixed-{prefix}-{zoom}", Mixed(), "cursor-fixture", zoom);
            }

            count += Render(directory, $"cursor-widget-mixed-wrapped-{prefix}-100", MixedWrapped(),
                "cursor-fixture", 100, [new ScreenRect(0, 0, 760, 1040)]);

            foreach (var (name, snapshot) in StatusCases())
                count += Render(directory, $"cursor-widget-{name}-{prefix}-100", [CursorAccount(snapshot)],
                    "cursor-fixture", 100);
            count += Render(directory, $"cursor-widget-long-name-{prefix}-100",
                [CursorAccount(CursorSnapshot(), UiText.T("A very long Cursor account nickname", "아주 긴 Cursor 계정 이름입니다"))],
                "cursor-fixture", 100);
        }

        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine(directory is null
            ? $"PASS: {count} synthetic Cursor widget summary layouts validated; no files, account store or network access."
            : $"PASS: {count} synthetic Cursor widget summary previews exported and validated; no account store or network access.");
    }

    private static int Render(string? directory, string name, IReadOnlyList<CodexAccountView> accounts,
        string selectedId, int zoom, IReadOnlyList<ScreenRect>? workAreas = null)
    {
        var widget = new FloatingWidget();
        try
        {
            widget.SetZoom(zoom, notify: false);
            widget.BindAccounts(accounts, selectedId, UsagePeriodPreference.Auto, workAreas ?? WidgetFixture.Desktop, Now);
            var path = directory is null ? null : Path.Combine(directory, name + ".png");
            WidgetFixture.RenderWidget(widget, path);

            var content = (FrameworkElement)widget.Content;
            content.UpdateLayout();
            CheckLayout(widget, content, accounts, name, zoom);
            if (widget.Modules.Count > 0)
            {
                for (var index = 0; index < widget.Modules.Count; index++)
                {
                    var module = widget.Modules[index];
                    var namePoint = module.NameText.TranslatePoint(new Point(), content);
                    var ring = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
                    var ringBounds = ring.TransformToAncestor(content).TransformBounds(new Rect(ring.RenderSize));
                    Console.WriteLine($"CURSOR-WIDGET {name} module={index} zoom={zoom}% "
                        + $"size={content.ActualWidth:0}x{content.ActualHeight:0} nameTop={namePoint.Y:0.0} "
                        + $"ringCenter={ringBounds.Top + ringBounds.Height / 2:0.0}");
                }
            }
            else
            {
                Console.WriteLine($"CURSOR-WIDGET {name} zoom={zoom}% size={content.ActualWidth:0}x{content.ActualHeight:0} empty");
            }
            return 1;
        }
        finally { widget.Close(); }
    }

    private static void CheckLayout(FloatingWidget widget, FrameworkElement content,
        IReadOnlyList<CodexAccountView> accounts, string name, int zoom)
    {
        Check(widget.Modules.Count == accounts.Count, $"{name}: account module count changed.");
        var layout = widget.LastLayout ?? throw new InvalidOperationException($"{name}: widget layout was not produced.");
        var viewport = (FrameworkElement)widget.FindName("ModuleScroller")!;
        var viewportBounds = viewport.TransformToAncestor(content).TransformBounds(new Rect(viewport.RenderSize));
        var tolerance = Math.Max(1, widget.ZoomScale);
        var rows = new Dictionary<int, (double NameTop, double RingCenter)>();
        var compactStandard = accounts.Any(account => account.Profile.Provider == UsageProviderId.Cursor
            && account.Snapshot.Status == CodexQuotaStatus.Available)
            && layout.Rows == 1
            && accounts.All(account => account.Snapshot.Status is CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing)
            && accounts.All(account => account.Snapshot.Windows.All(window => window.RemainingAmount is null));
        if (compactStandard)
            Check(content.ActualHeight <= 192 + tolerance,
                $"{name}: standard Cursor summary grew beyond 192 DIP ({content.ActualHeight:0.##}).");

        for (var index = 0; index < widget.Modules.Count; index++)
        {
            var module = widget.Modules[index];
            var model = module.Model ?? throw new InvalidOperationException($"{name}: module {index} has no model.");
            Check(Math.Abs(module.ActualWidth - WidgetGridLayout.ModuleWidth) <= 0.51,
                $"{name}: module {index} changed width from {WidgetGridLayout.ModuleWidth}.");

            var moduleBounds = Bounds(module, content);
            Check(moduleBounds.Left >= viewportBounds.Left - tolerance
                && moduleBounds.Right <= viewportBounds.Right + tolerance,
                $"{name}: module {index} falls outside the widget viewport.");
            Check(moduleBounds.Top >= viewportBounds.Top - tolerance
                && (layout.Scrolls || moduleBounds.Bottom <= viewportBounds.Bottom + tolerance),
                $"{name}: module {index} is clipped by the widget viewport.");

            var nameTop = module.NameText.TranslatePoint(new Point(), content).Y;
            var ringHost = RingHost(module);
            var ringBounds = Bounds(ringHost, content);
            Check(Math.Abs(ringHost.ActualWidth - 64) <= 0.51
                && Math.Abs(ringHost.ActualHeight - 64) <= 0.51
                && module.RingValueText.FontSize >= 15
                && module.NameText.FontSize >= 12,
                $"{name}: module {index} shrank its ring or primary text.");
            var ringCenter = ringBounds.Top + ringBounds.Height / 2;
            var row = index / layout.Columns;
            if (rows.TryGetValue(row, out var baseline))
            {
                Check(Math.Abs(nameTop - baseline.NameTop) <= tolerance,
                    $"{name}: module {index} name top is not aligned with its row.");
                Check(Math.Abs(ringCenter - baseline.RingCenter) <= tolerance,
                    $"{name}: module {index} ring center is not aligned with its row.");
            }
            else rows[row] = (nameTop, ringCenter);

            Check(module.NameText.ActualWidth + tolerance >= module.NameText.DesiredSize.Width,
                $"{name}: module {index} name is clipped.");
            Check(Bounds(module.NameText, content).Right <= moduleBounds.Right + tolerance,
                $"{name}: module {index} name leaves its module.");

            if (module.RingTargetText.Visibility == Visibility.Visible)
            {
                var ringCentre = VisualTreeHelper.GetParent(module.RingValueText);
                Check(ReferenceEquals(ringCentre, VisualTreeHelper.GetParent(module.RingTargetText)),
                    $"{name}: ring target left the ring centre stack.");
                Check(ringCentre is Panel panel
                    && panel.Children.IndexOf(module.RingTargetText) < panel.Children.IndexOf(module.RingValueText),
                    $"{name}: ring target is not above the percentage in the ring centre stack.");
                Check(module.RingTargetText.TextWrapping == TextWrapping.NoWrap,
                    $"{name}: ring target unexpectedly wraps or shrinks its compact caption.");
                var targetBounds = Bounds(module.RingTargetText, content);
                Check(targetBounds.Left >= ringBounds.Left - tolerance
                    && targetBounds.Right <= ringBounds.Right + tolerance
                    && targetBounds.Top >= ringBounds.Top - tolerance
                    && targetBounds.Bottom <= ringBounds.Bottom + tolerance,
                    $"{name}: ring target is clipped outside the 64-DIP ring.");
            }

            if (model.Provider == UsageProviderId.Cursor)
                CheckCursorModule(module, model, accounts[index].Snapshot, content, name, zoom);
            else
            {
                Check(module.RingTargetText.Visibility == Visibility.Collapsed,
                    $"{name}: non-Cursor module {index} shows a Cursor ring target.");
                Check(module.Periods.All(period => period.CadenceText.Visibility == Visibility.Collapsed),
                    $"{name}: non-Cursor module {index} shows a Cursor cadence.");
            }

            CheckPeriods(module, model, content, name, zoom);
        }
    }

    private static void CheckCursorModule(WidgetAccountModuleView module, WidgetAccountModel model, CodexQuotaSnapshot source,
        FrameworkElement content, string name, int zoom)
    {
        if (model.Periods.Count == 0)
        {
            Check(model.RingTargetLabel is null && module.RingTargetText.Visibility == Visibility.Collapsed,
                $"{name}: Cursor unavailable state exposed a quota ring target.");
            Check(module.StatusText.Visibility == Visibility.Visible
                && !string.IsNullOrWhiteSpace(module.StatusText.Text),
                $"{name}: Cursor unavailable state has no visible status.");
            Check((module.ToolTip as string ?? "").Contains("Cursor", StringComparison.OrdinalIgnoreCase),
                $"{name}: Cursor unavailable state has no account tooltip.");
            if (source.TechnicalDetail is "cursor-live-identity-mismatch" or "cursor-identity-mismatch")
                Check(module.RingValueText.Text == "?"
                    && string.IsNullOrEmpty(module.RingTargetText.Text)
                    && !((module.ToolTip as string ?? "").Contains(
                        CursorUsagePresentation.RemainingText(source.Windows[0]), StringComparison.Ordinal)),
                    $"{name}: Cursor identity mismatch exposed cached quota values.");
            return;
        }

        var targetLabel = model.RingTargetLabel
            ?? throw new InvalidOperationException($"{name}: Cursor ring target model is missing.");
        var compactTarget = CompactRingTarget(targetLabel);
        Check(module.RingTargetText.Visibility == Visibility.Visible && module.RingTargetText.Text == compactTarget,
            $"{name}: Cursor ring target is missing or does not show the compact selected allowance.");
        Check(module.RingTargetText.FontSize >= 10.5 - 0.01,
            $"{name}: Cursor ring target was reduced below the shared caption size.");
        Check(module.RingTargetText.ToolTip is string targetTooltip
            && targetTooltip.Contains(model.Ring.CenterSubLabel, StringComparison.Ordinal),
            $"{name}: Cursor ring target tooltip lost the full selected allowance.");
        Check((module.ToolTip as string ?? "").Contains(targetLabel, StringComparison.Ordinal),
            $"{name}: Cursor module tooltip lost the full selected allowance.");
        Check(Bounds(module.RingTargetText, content).Right <= Bounds(module, content).Right + Math.Max(1, zoom / 100d),
            $"{name}: Cursor ring target overflows its module.");

        var visibleNames = model.Periods.Select(period => period.PeriodLabel).ToArray();
        Check(visibleNames.Length == 3, $"{name}: Cursor widget does not show exactly three allowance rows.");
        Check(visibleNames.SequenceEqual(["Cursor Models", "Other Models", "Grok Bot"]),
            $"{name}: Cursor rows are not the three official visible allowances.");
        Check(!visibleNames.Contains("On-demand", StringComparer.OrdinalIgnoreCase),
            $"{name}: disabled on-demand allowance leaked into a widget row.");

        var tooltip = module.ToolTip as string ?? "";
        Check(tooltip.Contains(CursorUsagePresentation.QuotaLabel("cursor-on-demand"), StringComparison.Ordinal)
            && tooltip.Contains(CursorUsagePresentation.QuotaLabel("cursor-team-pool"), StringComparison.Ordinal),
            $"{name}: omitted Cursor allowances are missing from the account tooltip.");
        Check(source.Windows.Where(window => window.ResetsAt is not null)
            .All(window => tooltip.Contains(CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt)!, StringComparison.Ordinal)),
            $"{name}: Cursor tooltip omitted an exact reset timestamp.");
        Check(source.Windows.Where(window => window.LimitId is "cursor-auto" or "cursor-api" or "cursor-sand")
            .All(window => tooltip.Contains(CursorUsagePresentation.RemainingText(window), StringComparison.Ordinal)),
            $"{name}: Cursor tooltip omitted a raw remaining value.");
        if (source.Windows.Where(window => window.LimitId is "cursor-auto" or "cursor-api" or "cursor-sand")
            .All(window => window.UsedPercent is null))
            Check(module.RingValueText.Text == "?" && module.Periods.All(period => period.RemainingText.Text == "?"),
                $"{name}: unknown Cursor values were hidden or replaced with zero.");
        if (source.Status == CodexQuotaStatus.Available
            && source.Windows.All(window => window.RemainingAmount is null))
            Check(module.ActualHeight <= 150, $"{name}: Cursor summary expanded beyond its compact module height.");
        if (model.IsStale)
            Check(module.StatusText.Visibility == Visibility.Visible
                && (module.StatusText.Text.Contains("Stale", StringComparison.Ordinal)
                    || module.StatusText.Text.Contains("오래된", StringComparison.Ordinal)),
                $"{name}: stale Cursor state is not visible.");
        if (model.IsStale && source.TechnicalDetail is "cursor-auth-required" or "cursor-live-auth-required")
            Check(module.StatusText.Text.Contains(UiText.T("Cursor sign-in required", "Cursor 로그인 필요"),
                    StringComparison.Ordinal),
                $"{name}: stale Cursor authentication failure is not visible.");

    }

    private static void CheckPeriods(WidgetAccountModuleView module, WidgetAccountModel model,
        FrameworkElement content, string name, int zoom)
    {
        Check(module.Periods.Count == model.Periods.Count,
            $"{name}: period view count differs from its model.");
        var tolerance = Math.Max(1, zoom / 100d);
        for (var index = 0; index < module.Periods.Count; index++)
        {
            var view = module.Periods[index];
            var period = model.Periods[index];
            Check(view.PeriodText.Text == period.PeriodLabel,
                $"{name}: period {index} changed its official name.");
            Check(view.RemainingText.Text == period.RemainingText,
                $"{name}: period {index} changed its raw remaining value.");

            var labelBounds = Bounds(view.PeriodText, content);
            var valueBounds = Bounds(view.RemainingText, content);
            Check(labelBounds.Right <= valueBounds.Left + tolerance,
                $"{name}: period {index} label and value overlap.");
            Check(valueBounds.Right <= Bounds(module, content).Right + tolerance,
                $"{name}: period {index} value is clipped by its module.");
            Check(labelBounds.Left >= Bounds(module, content).Left - tolerance
                && labelBounds.Bottom <= Bounds(module, content).Bottom + tolerance
                && valueBounds.Bottom <= Bounds(module, content).Bottom + tolerance,
                $"{name}: period {index} full name or value leaves its module.");
            Check(view.PeriodText.ActualWidth + tolerance >= view.PeriodText.DesiredSize.Width,
                $"{name}: period {index} label is clipped.");
            Check(view.RemainingText.ActualWidth + tolerance >= view.RemainingText.DesiredSize.Width,
                $"{name}: period {index} value is clipped.");

            if (model.Provider == UsageProviderId.Cursor)
            {
                Check(view.PeriodText.FontSize >= 12 && view.RemainingText.FontSize >= 12,
                    $"{name}: Cursor period {index} reduced its label or value font.");
                Check(view.PeriodText.FontWeight == (period.IsRepresentative ? FontWeights.SemiBold : FontWeights.Normal),
                    $"{name}: Cursor period {index} does not identify the ring's full allowance name.");
                var cadence = period.CadenceLabel
                    ?? throw new InvalidOperationException($"{name}: Cursor period {index} cadence is missing.");
                var startsGroup = index == 0 || cadence != model.Periods[index - 1].CadenceLabel;
                Check(view.CadenceText.Visibility == (startsGroup ? Visibility.Visible : Visibility.Collapsed),
                    $"{name}: cadence visibility is wrong for Cursor period {index}.");
                if (startsGroup)
                    Check(view.CadenceText.Text == cadence + UiText.T(" · Left", " · 남음"),
                        $"{name}: cadence caption is wrong for Cursor period {index}.");
                Check(!view.RemainingText.Text.Contains("Left ", StringComparison.Ordinal)
                    && !view.RemainingText.Text.Contains("남음 ", StringComparison.Ordinal),
                    $"{name}: Cursor period {index} embedded a Left label in its raw value.");
                Check(string.IsNullOrEmpty(view.ResetText.Text) && view.ResetText.Visibility == Visibility.Collapsed,
                    $"{name}: Cursor period {index} shows a separate reset row.");
                var periodTooltip = period.Tooltip;
                Check(!string.IsNullOrWhiteSpace(periodTooltip)
                    && (view.ToolTip as string ?? "").Contains(periodTooltip, StringComparison.Ordinal),
                    $"{name}: Cursor period {index} tooltip lost its exact reset detail.");
            }
            else
            {
                Check(view.CadenceText.Visibility == Visibility.Collapsed,
                    $"{name}: non-Cursor period {index} shows a cadence caption.");
            }
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IReadOnlyList<CodexAccountView> CursorOnly() => [CursorAccount(CursorSnapshot())];

    private static IReadOnlyList<CodexAccountView> Mixed() =>
    [
        CodexAccount(),
        CursorAccount(CursorSnapshot()),
        ClaudeAccount()
    ];

    private static IReadOnlyList<CodexAccountView> MixedWrapped() =>
    [
        CodexAccount(),
        CursorAccount(CursorSnapshot(), UiText.T("Cursor wrapped", "Cursor 줄바꿈")),
        ClaudeAccount(),
        new(new CodexAccountProfile("codex-fixture-2", "", UiText.T("Second Codex", "두 번째 Codex")),
            CodexSnapshot(), "codex-2@example.invalid", HasMatchingIdentity: true) { IsConnected = true }
    ];

    private static CodexAccountView CursorAccount(CodexQuotaSnapshot snapshot, string nickname = "Cursor") =>
        new(new CodexAccountProfile("cursor-fixture", "", nickname) { Provider = UsageProviderId.Cursor },
            snapshot, "cursor@example.invalid", HasMatchingIdentity: true) { IsConnected = true };

    private static CodexAccountView CodexAccount() =>
        new(new CodexAccountProfile("codex-fixture", "", "Codex"), CodexSnapshot(),
            "codex@example.invalid", HasMatchingIdentity: true) { IsConnected = true };

    private static CodexAccountView ClaudeAccount() =>
        new(new CodexAccountProfile("claude-fixture", "", "Claude") { Provider = UsageProviderId.Claude },
            ClaudeSnapshot(), "claude@example.invalid", HasMatchingIdentity: true) { IsConnected = true };

    private static CodexQuotaSnapshot CursorSnapshot(CodexQuotaStatus status = CodexQuotaStatus.Available,
        string? detail = CursorUsagePresentation.LiveDetail)
    {
        var windows = status == CodexQuotaStatus.SignedOut ? [] : CursorWindows();
        return new CodexQuotaSnapshot(status, "pro", Now.AddMinutes(-4), Now.AddMinutes(-3), null, null, null,
            windows, detail) with { Provider = UsageProviderId.Cursor };
    }

    private static IReadOnlyList<CodexQuotaWindow> CursorWindows() =>
    [
        new("cursor-auto", 76.9, null, MonthlyReset, CodexWindowKind.Other),
        new("cursor-api", 41.2, null, MonthlyReset, CodexWindowKind.Other),
        new("cursor-on-demand", null, null, null, CodexWindowKind.Other) { IsEnabled = false },
        new("cursor-sand", 12.5, null, WeeklyReset, CodexWindowKind.Other),
        new("cursor-team-pool", 22.5, null, MonthlyReset, CodexWindowKind.Other)
    ];

    private static CodexQuotaSnapshot CodexSnapshot() =>
        new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-4), Now.AddMinutes(-3), null, null, null,
            [new("codex-weekly", 63, CodexWindowClassifier.WeeklyMinutes, MonthlyReset, CodexWindowKind.Weekly)], null);

    private static CodexQuotaSnapshot ClaudeSnapshot() =>
        new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-4), Now.AddMinutes(-3), null, null, null,
            [new("claude-weekly", 34, CodexWindowClassifier.WeeklyMinutes, MonthlyReset, CodexWindowKind.Weekly)],
            ClaudeUsagePresentation.LiveDetail) with { Provider = UsageProviderId.Claude };

    private static IEnumerable<(string Name, CodexQuotaSnapshot Snapshot)> StatusCases()
    {
        yield return ("unknown", CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "cursor-live-unavailable")
            with { Provider = UsageProviderId.Cursor });
        yield return ("unknown-values", CursorSnapshot() with
        {
            Windows = CursorWindows().Select(window => window.LimitId == "cursor-team-pool" ? window
                : window with { UsedPercent = null, ResetsAt = null }).ToArray()
        });
        yield return ("budget-first", CursorSnapshot() with
        {
            Windows = CursorWindows().OrderByDescending(window => window.LimitId == "cursor-team-pool").ToArray()
        });
        yield return ("enabled-on-demand", CursorSnapshot() with
        {
            Windows = CursorWindows().Select(window => window.LimitId == "cursor-on-demand"
                ? window with
                {
                    IsEnabled = true, UsedPercent = 8, ResetsAt = Now.AddDays(2),
                    RemainingAmount = 123456.78m, LimitAmount = 150000m, Unit = "USD"
                } : window).ToArray()
        });
        yield return ("other-selected-unknown", CursorSnapshot() with
        {
            Windows = CursorWindows().Select(window => window.LimitId == "cursor-auto"
                ? window with { UsedPercent = null } : window).ToArray()
        });
        yield return ("grok-selected-unknown", CursorSnapshot() with
        {
            Windows = CursorWindows().Select(window => window.LimitId is "cursor-auto" or "cursor-api"
                ? window with { UsedPercent = null } : window).ToArray()
        });
        yield return ("large-money", CursorSnapshot() with
        {
            Windows = CursorWindows().Select(window => window.LimitId == "cursor-auto"
                ? window with
                {
                    UsedPercent = 10,
                    RemainingAmount = 123456.78m,
                    LimitAmount = 150000m,
                    Unit = "USD"
                } : window).ToArray()
        });
        yield return ("identity-mismatch", CursorSnapshot() with
        {
            TechnicalDetail = "cursor-live-identity-mismatch",
            IdentityFingerprint = "old-cached-account"
        });
        yield return ("stale-auth", CursorSnapshot(CodexQuotaStatus.Stale, "cursor-auth-required"));
        yield return ("stale", CursorSnapshot(CodexQuotaStatus.Stale, "cursor-request-failed"));
        yield return ("auth", CursorSnapshot(CodexQuotaStatus.SignedOut, "cursor-auth-required"));
    }

    private static FrameworkElement RingHost(WidgetAccountModuleView module) =>
        (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));

    private static string CompactRingTarget(string targetLabel) => targetLabel switch
    {
        "Cursor Models" => "Cursor",
        "Other Models" => "Other",
        "Grok Bot" => "Grok Bot",
        _ => targetLabel
    };
}
