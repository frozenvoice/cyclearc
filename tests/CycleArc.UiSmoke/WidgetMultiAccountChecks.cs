using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// The multi-account widget across account counts, wrapping, period shapes and states.
/// Production views with synthetic accounts only: no account store, settings or network is read,
/// and every name here is invented for the fixture.
/// </summary>
internal static class WidgetMultiAccountChecks
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

    // The narrow work area used for the wrapped preview; wide enough for three modules only.
    private static readonly IReadOnlyList<ScreenRect> Narrow = [new ScreenRect(0, 0, 760, 1040)];

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
            count += Scenarios(directory, suffix);
        }
        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine($"PASS: {count} multi-account widget renders; 1/3/5 accounts, wrapped rows, mixed period shapes, "
            + "long names, missing resets, stale data and authentication failure in both languages and themes; synthetic accounts only.");
    }

    private static int Scenarios(string? directory, string suffix)
    {
        var count = 0;
        count += Render(directory, $"widget-1-account-{suffix}", [Codex("one", "Main", Both(28, 61))], "one", WidgetFixture.Desktop,
            expectColumns: 1, expectRows: 1);
        count += Render(directory, $"widget-3-accounts-{suffix}", Three(), "three-c", WidgetFixture.Desktop,
            expectColumns: 3, expectRows: 1);
        count += Render(directory, $"widget-5-accounts-{suffix}", Five(), "five-c1", WidgetFixture.Desktop,
            expectColumns: 5, expectRows: 1);
        count += Render(directory, $"widget-5-accounts-wrapped-{suffix}", Five(), "five-c1", Narrow,
            expectColumns: 3, expectRows: 2);
        count += Render(directory, $"widget-mixed-periods-{suffix}", MixedPeriods(), "mixed-both", WidgetFixture.Desktop,
            expectColumns: 3, expectRows: 1);
        count += Render(directory, $"widget-states-{suffix}", States(), "state-ok", WidgetFixture.Desktop,
            expectColumns: 4, expectRows: 1);
        return count;
    }

    private static int Render(string? directory, string name, IReadOnlyList<CodexAccountView> accounts, string selectedId,
        IReadOnlyList<ScreenRect> workAreas, int expectColumns, int expectRows)
    {
        var widget = new FloatingWidget();
        try
        {
            widget.BindAccounts(accounts, selectedId, UsagePeriodPreference.Auto, workAreas, Now);
            var layout = widget.LastLayout!;
            Check(layout.Columns == expectColumns && layout.Rows == expectRows,
                $"{name}: expected {expectColumns}x{expectRows}, produced {layout.Columns}x{layout.Rows}.");
            Check(((System.Windows.Controls.TextBlock)widget.FindName("AccountCountText")).Text
                == UiText.WidgetAccountsConnected(accounts.Count),
                $"{name}: the header is not the short account-count label.");
            Check(layout.Width <= workAreas[0].Width - (2 * WidgetGridLayout.EdgeMargin),
                $"{name}: the widget is wider than the work area ({layout.Width} in {workAreas[0].Width}).");
            Check(widget.Modules.Count == accounts.Count, $"{name}: an account module is missing.");
            Check(widget.Modules.Select(module => module.ProfileId)
                .SequenceEqual(accounts.Select(account => account.Profile.Id)), $"{name}: account order changed.");

            WidgetFixture.RenderWidget(widget, directory is null ? null : Path.Combine(directory, name + ".png"));
            CheckReadable(widget, name);
            return 1;
        }
        finally { widget.Close(); }
    }

    private static void CheckReadable(FloatingWidget widget, string name)
    {
        CheckAlignment(widget, name);
        var content = (FrameworkElement)widget.Content;
        var header = (FrameworkElement)widget.FindName("WidgetHeader");
        Check(AccountUiChecks.Descendants<TextBlock>(content)
            .Count(text => text.Text == UiText.ProductName) == 1, $"{name}: the header is repeated per account.");
        foreach (var module in widget.Modules)
        {
            var model = module.Model!;
            Check(Math.Abs(module.ActualWidth - WidgetGridLayout.ModuleWidth) <= 0.51,
                $"{name}: a module drifted from the fixed width ({module.ActualWidth}).");
            Check(module.Periods.Count == model.Periods.Count, $"{name}: a period line is missing.");
            foreach (var line in module.Periods)
            {
                Check(line.RemainingText.ActualWidth + 0.5 >= line.RemainingText.DesiredSize.Width
                    && line.ResetText.ActualWidth + 0.5 >= line.ResetText.DesiredSize.Width,
                    $"{name}: a quota or reset value is clipped.");
                Check(!line.ResetText.Text.Contains('-'), $"{name}: a reset countdown went negative.");
            }
            var bounds = module.TransformToAncestor(content).TransformBounds(new Rect(module.RenderSize));
            Check(bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1,
                $"{name}: a module is clipped by the widget panel.");
            Check(module.ToolTip is string tip && tip.StartsWith(model.DisplayName, StringComparison.Ordinal),
                $"{name}: the full identity is not in the module tooltip.");
            Check(module.TranslatePoint(new Point(), content).Y + 0.01
                >= header.TranslatePoint(new Point(0, header.ActualHeight), content).Y,
                $"{name}: a module overlaps the header.");
        }
    }

    internal static void CheckAlignment(FloatingWidget widget, string name)
    {
        var content = (FrameworkElement)widget.Content;
        var tolerance = Math.Max(1, widget.ZoomScale);
        var rowCenters = new Dictionary<int, double>();
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content)
            .TransformBounds(new Rect(element.RenderSize));

        for (var index = 0; index < widget.Modules.Count; index++)
        {
            var module = widget.Modules[index];
            var ring = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
            var ringBounds = Bounds(ring);
            var ringCenter = ringBounds.Top + ringBounds.Height / 2;
            var row = index / widget.LastLayout!.Columns;
            if (rowCenters.TryGetValue(row, out var rowCenter))
                Check(Math.Abs(ringCenter - rowCenter) <= tolerance,
                    $"{name}: rings in the same account row are vertically misaligned.");
            else rowCenters.Add(row, ringCenter);

            if (module.Periods.Count == 0) continue;
            var periodBounds = Rect.Empty;
            var labelStart = Bounds(module.Periods[0].PeriodText).Left;
            foreach (var line in module.Periods)
            {
                periodBounds.Union(Bounds(line));
                Check(Math.Abs(Bounds(line.PeriodText).Left - labelStart) <= tolerance,
                    $"{name}: the representative marker shifts a period label out of its column.");
                var labelBaseline = line.PeriodText.TranslatePoint(new Point(0, line.PeriodText.BaselineOffset), content).Y;
                var remainingBaseline = line.RemainingText.TranslatePoint(new Point(0, line.RemainingText.BaselineOffset), content).Y;
                Check(Math.Abs(labelBaseline - remainingBaseline) <= tolerance,
                    $"{name}: a period label and remaining value have different baselines.");
                Check(Math.Abs(Bounds(line.RemainingText).Right - Bounds(line.ResetText).Right) <= tolerance,
                    $"{name}: remaining and reset values have different right edges.");
            }
            Check(Math.Abs(periodBounds.Top + periodBounds.Height / 2 - ringCenter) <= tolerance,
                $"{name}: the period block is not centered beside its ring.");
            Check(periodBounds.Top >= ringBounds.Top - tolerance && periodBounds.Bottom <= ringBounds.Bottom + tolerance,
                $"{name}: period lines extend above or below their ring.");
        }
    }

    private static CodexQuotaSnapshot Both(double fiveHourUsed, double weeklyUsed) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
        [
            new("five", fiveHourUsed, CodexWindowClassifier.FiveHourMinutes, Now.AddMinutes(35), CodexWindowKind.FiveHour),
            new("week", weeklyUsed, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(2).AddHours(4), CodexWindowKind.Weekly)
        ], null);

    private static CodexQuotaSnapshot WeeklyOnly(double used) =>
        Both(0, used) with { Windows = [new("week", used, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(1).AddHours(9), CodexWindowKind.Weekly)] };

    private static CodexQuotaSnapshot FiveHourOnly(double used) =>
        Both(used, 0) with { Windows = [new("five", used, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(1).AddMinutes(20), CodexWindowKind.FiveHour)] };

    private static CodexAccountView Codex(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label), snapshot);

    private static CodexAccountView Claude(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label) { Provider = UsageProviderId.Claude },
            snapshot with { Provider = UsageProviderId.Claude, TechnicalDetail = ClaudeUsagePresentation.LiveDetail })
        { IsConnected = true };

    // Synthetic nicknames only; never a real account's address.
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

    private static CodexAccountView[] MixedPeriods() =>
    [
        Claude("mixed-both", UiText.T("Personal Claude", "개인 Claude"), Both(85, 23)),
        Codex("mixed-weekly", UiText.T("Kakao", "카카오"), WeeklyOnly(41)),
        Codex("mixed-five", UiText.T("Lab", "실험용"), FiveHourOnly(62))
    ];

    private static CodexAccountView[] States()
    {
        var noReset = Both(0, 23) with
        {
            Windows =
            [
                new("five", 0, CodexWindowClassifier.FiveHourMinutes, null, CodexWindowKind.FiveHour),
                new("week", 23, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(5).AddHours(21), CodexWindowKind.Weekly)
            ]
        };
        return
        [
            Codex("state-ok", UiText.T("a-very-long-synthetic-widget-nickname", "아주 긴 합성 위젯 별명입니다"), Both(97, 44)),
            Claude("state-noreset", UiText.T("Lab", "실험용"), noReset),
            Claude("state-stale", UiText.T("Work", "작업용"), Both(62, 18) with { Status = CodexQuotaStatus.Stale }),
            Claude("state-auth", UiText.T("Personal Claude", "개인 Claude"),
                Both(85, 23) with { TechnicalDetail = "claude-live-auth-required" })
        ];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
