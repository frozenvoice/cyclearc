using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class CodexWindowUiChecks
{
    private sealed record WindowData(int Minutes, double? Used);
    private sealed record Scenario(string Name, string? Plan, WindowData Primary, WindowData? Secondary,
        bool CompactFiveHour);

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var scenarios = new[]
        {
            new Scenario("five-hour-only", "plus", new(300, 42), null, true),
            new Scenario("both-windows", "plus", new(300, 42), new(10080, 31), false),
            new Scenario("reversed-windows", "pro", new(10080, 31), new(300, 42), false),
            new Scenario("unknown-weekly", null, new(10080, null), new(300, 42), true),
            new Scenario("weekly-only", "pro", new(10080, 31), null, false),
            new Scenario("unknown-five-hour", "plus", new(300, null), new(10080, 31), false)
        };
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var flyout = new FlyoutWindow();
            var widget = new FloatingWidget();
            try
            {
                // Rebind the same views so removing an optional window cannot leave an old row behind.
                foreach (var scenario in scenarios)
                {
                    var snapshot = Parse(scenario);
                    var account = new CodexAccountView(new CodexAccountProfile("quota-fixture",
                        @"C:\CycleArc-Samples\codex-windows", UiText.T("Window sample", "한도 예시"), true),
                        snapshot, "windows@example.invalid");
                    var other = account with { Profile = account.Profile with { Id = "other-fixture",
                        Label = UiText.T("Other account", "다른 계정") },
                        Snapshot = Parse(scenarios[4]) };
                    flyout.BindAccounts([account, other], account.Profile.Id, false);
                    widget.BindAccount(account);
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        var preview = directory is not null && zoom == 100 && theme != AppTheme.System
                            ? Path.Combine(directory, $"codex-{scenario.Name}-{language}-{theme}.png") : null;
                        AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, preview);
                        Check(flyout, widget, scenario);
                        count++;
                    }
                    AccountUiChecks.Render(widget, 245, null, directory is not null && theme != AppTheme.System
                        && scenario.Name == "five-hour-only"
                        ? Path.Combine(directory, $"codex-five-hour-widget-{language}-{theme}.png") : null);
                    count++;
                }
            }
            finally { flyout.Close(); widget.Close(); }
        }
        Console.WriteLine($"PASS: {count} Codex window WPF renders; five-hour/weekly optional windows, plan-independent display, reversed slots, unknown values, rebinds and compact fallback.");
    }

    private static CodexQuotaSnapshot Parse(Scenario scenario)
    {
        var now = DateTimeOffset.Now;
        object? Window(WindowData? data) => data is null ? null : new
        {
            usedPercent = data.Used,
            windowDurationMins = data.Minutes,
            resetsAt = now.AddMinutes(data.Minutes).ToUnixTimeSeconds()
        };
        var response = JsonSerializer.SerializeToNode(new
        {
            result = new { rateLimitsByLimitId = new { codex = new
            {
                limitId = "codex", planType = scenario.Plan,
                primary = Window(scenario.Primary), secondary = Window(scenario.Secondary)
            } } }
        });
        var parsed = CodexRateLimitParser.Parse(null, response);
        if (parsed.Status != CodexQuotaStatus.Available || parsed.Windows.Count != (scenario.Secondary is null ? 1 : 2))
            throw new InvalidOperationException($"Official Codex window fixture was not accepted: {scenario.Name}.");
        return new CodexQuotaSnapshot(parsed.Status, parsed.PlanType, now, now,
            parsed.OrdinaryUsageAllowed, parsed.RateLimitReachedType, parsed.ResetCreditsAvailable,
            parsed.Windows, parsed.Detail);
    }

    private static void Check(FlyoutWindow flyout, FloatingWidget widget, Scenario scenario)
    {
        var expectedValue = scenario.CompactFiveHour ? "42%" : "31%";
        var expectedCaption = scenario.CompactFiveHour ? UiText.T("5-hour used", "5시간 사용")
            : UiText.T("Weekly used", "주간 사용");
        if (((TextBlock)flyout.FindName("CodexRingValueText")).Text != expectedValue
            || ((TextBlock)flyout.FindName("CodexRingSubLabel")).Text != expectedCaption
            || ((TextBlock)widget.FindName("CodexValue")).Text != expectedValue
            || ((TextBlock)widget.FindName("CodexLabel")).Text != expectedCaption)
            throw new InvalidOperationException($"Codex compact window is incorrect: {scenario.Name}.");

        var inputs = new[] { scenario.Primary, scenario.Secondary }.OfType<WindowData>().ToArray();
        var summary = (StackPanel)((Button)((ItemsControl)flyout.FindName("AccountOverview")).Items[0]).Content;
        var summaryRows = summary.Children.OfType<Grid>().Skip(1).ToArray();
        var details = ((ItemsControl)flyout.FindName("CodexRows")).Items.Cast<Border>().Select(b => (Grid)b.Child).ToArray();
        if (summaryRows.Length != inputs.Length || details.Length != inputs.Length * 2 + 1)
            throw new InvalidOperationException($"Codex optional window was omitted or invented: {scenario.Name}.");
        for (var i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i];
            var label = input.Minutes == 300 ? UiText.T("Codex 5-hour usage", "Codex 5시간 사용")
                : UiText.T("Codex weekly usage", "Codex 주간 사용");
            var used = input.Used is { } value ? $"{value:0}%" : "?";
            var left = input.Used is { } known ? $"{100 - known:0}%" : "?";
            if (((TextBlock)summaryRows[i].Children[0]).Text != label
                || ((TextBlock)summaryRows[i].Children[1]).Text != UiText.T($"Used {used} · Left {left}", $"사용 {used} · 잔여 {left}")
                || ((TextBlock)((StackPanel)details[i * 2].Children[1]).Children[0]).Text != (input.Used is null ? "?" : $"{used} / {left}")
                || ((TextBlock)((StackPanel)details[i * 2 + 1].Children[1]).Children[0]).Text == "?")
                throw new InvalidOperationException($"Codex window values or reset time are incorrect: {scenario.Name}.");
        }
        foreach (var row in summaryRows.Concat(details))
        {
            var label = (FrameworkElement)row.Children[0];
            var value = (FrameworkElement)row.Children[1];
            if (label.TranslatePoint(new Point(label.ActualWidth, 0), row).X
                    > value.TranslatePoint(new Point(), row).X + 1
                || value.TranslatePoint(new Point(value.ActualWidth, 0), row).X > row.ActualWidth + 1)
                throw new InvalidOperationException($"Codex window text overlaps: {scenario.Name}.");
        }
    }
}
