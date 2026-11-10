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
/// Claude's per-model weekly limit in the popup, on a synthetic account: its usage and reset rows
/// sit below the ring with the other limit, in EN/KO and every theme, without overlapping or
/// leaving the card at 80/100/150% zoom, and disappear with a sample that does not carry one.
/// </summary>
internal static class ModelLimitUiChecks
{
    // The longest name the model accepts, to hold the layout to its worst case as well.
    private static readonly string LongName = new('M', ClaudeModelLimit.MaxNameLength);

    public static void Run(App app, string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            foreach (var model in new[] { "Fable", LongName })
            {
                var flyout = new FlyoutWindow();
                try
                {
                    Bind(flyout, Live() with { ClaudeModelLimits = [new(model, 2, DateTimeOffset.Now.AddDays(2), 50)] });
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        var preview = directory is not null && zoom == 100 && theme != AppTheme.System && model == "Fable"
                            ? System.IO.Path.Combine(directory, $"model-limit-{language}-{theme}.png") : null;
                        AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, preview);
                        CheckRows(flyout, model, $"{model.Length}-char name {language} {theme} {zoom}%");
                        count++;
                    }

                    Bind(flyout, Live());
                    AccountUiChecks.Render(flyout, 440, null, null);
                    Check(!Texts(Element(flyout, "CodexCard")).Any(text => text.Text.Contains(CodexDisplayFormatting.ModelLimitName(model), StringComparison.Ordinal)),
                        $"{language} {theme}: a model row stayed after a sample without one.");
                }
                finally { flyout.Close(); }
            }
        }
        Console.WriteLine($"[ui-smoke] model limit checks passed: {count} layouts");
    }

    private static void CheckRows(FlyoutWindow flyout, string model, string label)
    {
        var card = Element(flyout, "CodexCard");
        var shown = CodexDisplayFormatting.ModelLimitName(model);
        var below = Texts(Element(flyout, "CodexSecondaryRows")).ToArray();
        var beside = Texts(Element(flyout, "CodexRows"));
        Check(!beside.Any(text => text.Text.Contains(shown, StringComparison.Ordinal)), $"{label}: model row sits beside the ring.");
        var usage = below.SingleOrDefault(text => text.Text == $"{UiText.ModelWeeklyUsed(shown)} / {UiText.T("left", "남음")}");
        var reset = below.SingleOrDefault(text => text.Text == UiText.ModelReset(shown));
        Check(usage is not null && reset is not null, $"{label}: usage or reset row missing.");
        Check(below.Any(text => text.Text == "2% / 98%") && below.Any(text => text.Text == UiText.ModelWeeklyShare(50)),
            $"{label}: value or weekly share missing.");
        foreach (var name in new[] { usage!, reset! })
        {
            var row = (Grid)VisualTreeHelper.GetParent(name);
            var rowBox = row.TransformToAncestor(card).TransformBounds(new Rect(row.RenderSize));
            Check(rowBox.Left >= 0 && rowBox.Right <= card.ActualWidth + 0.5, $"{label}: row leaves the card.");
            var nameBox = name.TransformToAncestor(row).TransformBounds(new Rect(name.RenderSize));
            Check(name.ActualWidth > 0 && nameBox.Right <= row.ActualWidth + 0.5, $"{label}: label is clipped.");
            foreach (var value in Texts(row).Where(text => !ReferenceEquals(text, name)))
            {
                var valueBox = value.TransformToAncestor(row).TransformBounds(new Rect(value.RenderSize));
                Check(value.ActualWidth > 0 && valueBox.Left >= nameBox.Right - 0.5 && valueBox.Right <= row.ActualWidth + 0.5,
                    $"{label}: \"{value.Text}\" overlaps its label or is clipped.");
            }
        }
    }

    private static void Bind(FlyoutWindow flyout, CodexQuotaSnapshot snapshot)
    {
        var account = new CodexAccountView(new CodexAccountProfile("model-limit-fixture",
            @"C:/CycleArc-Samples/model-limit", UiText.T("Model limit sample", "모델 한도 예시"), true)
            { Provider = snapshot.Provider }, snapshot, "model-limit@example.invalid") { IsConnected = true };
        var overview = UsageAccountOverview.Create([account], account.Profile.Id, UsagePeriodPreference.FiveHour);
        flyout.BindAccounts(overview.Accounts, overview.SelectedId, false, overview.Preference);
    }

    private static CodexQuotaSnapshot Live()
    {
        var checkedAt = DateTimeOffset.Now;
        return new(CodexQuotaStatus.Available, null, checkedAt, checkedAt, null, null, null,
            [new("five_hour", 5, 300, checkedAt.AddHours(2), CodexWindowKind.FiveHour),
             new("seven_day", 35, 10080, checkedAt.AddDays(5), CodexWindowKind.Weekly)],
            ClaudeUsagePresentation.LiveDetail)
        { Provider = UsageProviderId.Claude };
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) yield return text;
            foreach (var nested in Texts(child)) yield return nested;
        }
    }

    private static FrameworkElement Element(Window window, string name) => (FrameworkElement)window.FindName(name);

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
