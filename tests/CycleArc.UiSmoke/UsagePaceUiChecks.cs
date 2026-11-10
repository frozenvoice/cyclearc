using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// The popup's even-pace ring mark and the sentence under the ring, on synthetic accounts:
/// both for a healthy server sample with a known reset, hidden for a Desktop history receipt
/// and a stale sample, in EN/KO and every theme, without clipping at 80/100/150% zoom.
/// </summary>
internal static class UsagePaceUiChecks
{
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
            foreach (var (name, used, tone) in new[]
                     {
                         ("runs-out", 90d, UsagePaceTone.RunsOut),
                         ("tight", 55d, UsagePaceTone.Tight),
                         ("ahead", 30d, UsagePaceTone.Ahead)
                     })
            foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Claude })
            {
                var flyout = new FlyoutWindow();
                try
                {
                    Bind(flyout, Live(provider, used));
                    Check(flyout.Pace?.Tone == tone, $"{provider} {name}: expected {tone}, got {flyout.Pace?.Tone}.");
                    Check(Element(flyout, "PaceNotice").Visibility == Visibility.Visible
                        && Element(flyout, "CodexRingPaceTick").Visibility == Visibility.Visible,
                        $"{provider} {name}: pace sentence or ring mark hidden.");
                    Check(((TextBlock)flyout.FindName("PaceText")).Text == flyout.Pace!.Text,
                        $"{provider} {name}: shown sentence differs from the projection.");
                    Check(System.Windows.Automation.AutomationProperties.GetHelpText(
                            (Button)flyout.FindName("CyclePeriodButton")) == flyout.Pace!.MarkerText,
                        $"{provider} {name}: ring does not name its mark.");
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        var preview = directory is not null && zoom == 100 && theme != AppTheme.System
                            && provider == UsageProviderId.Claude
                            ? System.IO.Path.Combine(directory, $"usage-pace-{name}-{language}-{theme}.png") : null;
                        AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, preview);
                        CheckLayout(flyout, $"{provider} {name} {language} {theme} {zoom}%");
                        count++;
                    }

                    // The same account without a trustworthy rate hides both parts together.
                    Bind(flyout, DesktopHistory(used));
                    CheckHidden(flyout, $"{name} Desktop history");
                    Bind(flyout, Live(provider, used) with { Status = CodexQuotaStatus.Stale });
                    CheckHidden(flyout, $"{provider} {name} stale");
                }
                finally { flyout.Close(); }
            }
        }
        Console.WriteLine($"[ui-smoke] usage pace checks passed: {count} layouts");
    }

    private static void Bind(FlyoutWindow flyout, CodexQuotaSnapshot snapshot)
    {
        var account = new CodexAccountView(new CodexAccountProfile("pace-fixture",
            @"C:/CycleArc-Samples/pace", UiText.T("Pace sample", "페이스 예시"), true)
            { Provider = snapshot.Provider }, snapshot, "pace@example.invalid") { IsConnected = true };
        var overview = UsageAccountOverview.Create([account], account.Profile.Id, UsagePeriodPreference.FiveHour);
        flyout.BindAccounts(overview.Accounts, overview.SelectedId, false, overview.Preference);
    }

    private static void CheckHidden(FlyoutWindow flyout, string label)
    {
        Check(flyout.Pace is null, $"{label}: a pace was projected.");
        Check(Element(flyout, "PaceNotice").Visibility == Visibility.Collapsed
            && Element(flyout, "CodexRingPaceTick").Visibility == Visibility.Collapsed,
            $"{label}: pace sentence or ring mark stayed visible.");
    }

    private static void CheckLayout(FlyoutWindow flyout, string label)
    {
        var notice = Element(flyout, "PaceNotice");
        var text = (TextBlock)flyout.FindName("PaceText");
        var card = Element(flyout, "CodexCard");
        var noticeBox = notice.TransformToAncestor(card).TransformBounds(new Rect(notice.RenderSize));
        Check(noticeBox.Left >= 0 && noticeBox.Right <= card.ActualWidth + 0.5,
            $"{label}: pace sentence overflows the card.");
        Check(text.ActualWidth <= notice.ActualWidth && text.ActualHeight > 0,
            $"{label}: pace sentence is clipped.");
        var tick = (Line)flyout.FindName("CodexRingPaceTick");
        var host = Element(flyout, "CodexRingHost");
        foreach (var point in new[] { new Point(tick.X1, tick.Y1), new Point(tick.X2, tick.Y2) })
            Check(point.X >= -4 && point.Y >= -4 && point.X <= host.ActualWidth + 4 && point.Y <= host.ActualHeight + 4,
                $"{label}: ring mark lies outside the ring.");
    }

    // Three hours into a five-hour window with the reset two hours away.
    private static CodexQuotaSnapshot Live(UsageProviderId provider, double used)
    {
        var checkedAt = DateTimeOffset.Now;
        return new(CodexQuotaStatus.Available, null, checkedAt, checkedAt, null, null, null,
            [new("five_hour", used, 300, checkedAt.AddHours(2), CodexWindowKind.FiveHour),
             new("seven_day", 20, 10080, checkedAt.AddDays(5), CodexWindowKind.Weekly)],
            provider == UsageProviderId.Claude ? ClaudeUsagePresentation.LiveDetail : null)
        { Provider = provider };
    }

    private static CodexQuotaSnapshot DesktopHistory(double used)
    {
        var receivedAt = DateTimeOffset.Now.AddMinutes(-12);
        return new(CodexQuotaStatus.Available, null, receivedAt, receivedAt, null, null, null,
            [new("five_hour", used, 300, null, CodexWindowKind.FiveHour),
             new("seven_day", 20, 10080, null, CodexWindowKind.Weekly)], "claude-desktop-history")
        { Provider = UsageProviderId.Claude, LastSuccessfulObservationWasServer = false };
    }

    private static FrameworkElement Element(Window window, string name) => (FrameworkElement)window.FindName(name);

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
