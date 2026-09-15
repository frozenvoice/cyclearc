using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class DocumentationScreenshots
{
    // Production views, synthetic profiles/quota metadata, no account or local settings access.
    public static void Export(string directory, bool claudeUsageOnly = false, bool usagePeriodOnly = false)
    {
        Directory.CreateDirectory(directory);
        if (claudeUsageOnly)
        {
            ExportAccounts(directory, DateTimeOffset.Now,
                typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!, true);
            Console.WriteLine("Exported 4 Claude usage previews; synthetic data only.");
            return;
        }
        UiText.SetLanguage(UiLanguage.English);
        var now = DateTimeOffset.Now;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-3),
            now.AddMinutes(-3), null, null, 3,
            [new("codex", 28, 10080, now.AddDays(4).AddHours(6), CodexWindowKind.Weekly)], null,
            [now.AddDays(30), now.AddDays(30), now.AddDays(60)]);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            applyTheme.Invoke(null, [theme]);
            var flyout = new FlyoutWindow();
            try
            {
                flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
                flyout.BindAccounts([new(new CodexAccountProfile("default", @"C:\CycleArc-Samples\personal", "Personal"), snapshot)], "default", false);
                Save(flyout, Path.Combine(directory, $"overview-{theme.ToString().ToLowerInvariant()}.png"), 440, null);
            }
            finally { flyout.Close(); }
        }
        if (usagePeriodOnly)
        {
            ExportAccounts(directory, now, applyTheme, usagePeriodOnly: true);
            Console.WriteLine("Exported 14 usage-period detail previews; synthetic data only.");
            return;
        }
        applyTheme.Invoke(null, [AppTheme.Dark]);
        var settings = new SettingsWindow(new AppSettings { UiLanguage = UiLanguage.English, Theme = AppTheme.Dark });
        try { Save(settings, Path.Combine(directory, "settings.png"), 640, 590); }
        finally { settings.Close(); }
        var widget = new FloatingWidget();
        try
        {
            widget.Bind(snapshot);
            Save(widget, Path.Combine(directory, "widget.png"), 220, null);
        }
        finally { widget.Close(); }
        ExportAccounts(directory, now, applyTheme);
        Console.WriteLine("Exported 32 production WPF views: Codex usage, unconnected-profile filtering, account management, connected Claude awaiting usage, Claude authentication failure, mixed usage and automatic connection; synthetic data only.");
    }

    private static void ExportAccounts(string directory, DateTimeOffset now, MethodInfo applyTheme, bool claudeUsageOnly = false, bool usagePeriodOnly = false)
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var accounts = SampleAccounts(now);
            var selected = accounts[1].Profile.Id;
            var suffix = $"{(language == UiLanguage.English ? "en" : "ko")}-{theme.ToString().ToLowerInvariant()}";
            var flyout = new FlyoutWindow();
            var manager = new AccountsWindow();
            var connection = new PreviewClaudeConnection(accounts[2].Profile.Id, now);
            var guide = new ClaudeConnectionWindow(accounts[2].Profile, @"C:\CycleArc-Samples\CycleArc.exe", connection);
            try
            {
                flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
                if (!claudeUsageOnly)
                {
                    flyout.BindAccounts(accounts, selected, false);
                    Save(flyout, Path.Combine(directory, $"accounts-overview-{suffix}.png"), 440, null);
                    if (!usagePeriodOnly)
                    {
                        manager.Bind(accounts, selected);
                        Save(manager, Path.Combine(directory, $"accounts-manage-{suffix}.png"), 700, 800,
                            () => ((ScrollViewer)manager.FindName("AccountsScroll")).ScrollToBottom());
                    }

                    var waiting = accounts[2] with { IsConnected = true, Email = "research@example.invalid",
                        Snapshot = accounts[2].Snapshot with { TechnicalDetail = "claude-connected-waiting" } };
                    flyout.BindAccounts([accounts[0], accounts[1], waiting], waiting.Profile.Id, false);
                    Save(flyout, Path.Combine(directory, $"claude-waiting-{suffix}.png"), 440, null);

                    if (!usagePeriodOnly)
                    {
                        var authFailure = waiting with
                        {
                            Snapshot = waiting.Snapshot with
                            {
                                Status = CodexQuotaStatus.Stale,
                                LastSuccessfulRefresh = null,
                                Windows = [],
                                TechnicalDetail = "claude-auth-required"
                            }
                        };
                        flyout.BindAccounts([accounts[0], accounts[1], authFailure], authFailure.Profile.Id, false);
                        Save(flyout, Path.Combine(directory, $"claude-auth-required-{suffix}.png"), 440, null);
                    }
                }

                var received = accounts[2] with
                {
                    Snapshot = ClaudeQuotaService.ApplyFreshness(new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now.AddMinutes(-12), now.AddMinutes(-12),
                        null, null, null,
                        [new("five_hour", 91, 300, now.AddHours(3), CodexWindowKind.FiveHour),
                         new("seven_day", 47, 10080, now.AddDays(4), CodexWindowKind.Weekly)], null)
                        { Provider = UsageProviderId.Claude }, now),
                    Email = "research@example.invalid"
                };
                flyout.BindAccounts([accounts[0], accounts[1], received], received.Profile.Id, false);
                Save(flyout, Path.Combine(directory, $"claude-overview-{suffix}.png"), 440, null);

                if (claudeUsageOnly || usagePeriodOnly) continue;
                guide.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                AccountUiChecks.PumpUntil(guide.ActiveOperation);
                ((Button)guide.FindName("ConnectExistingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AccountUiChecks.PumpUntil(guide.ActiveOperation);
                Save(guide, Path.Combine(directory, $"claude-connection-{suffix}.png"), 610, 580);
                connection.FailureKind = ClaudeFailureKind.AuthRequired;
                guide.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                AccountUiChecks.PumpUntil(guide.ActiveOperation);
                Save(guide, Path.Combine(directory, $"claude-connection-failure-{suffix}.png"), 610, 580);
            }
            finally { flyout.Close(); manager.Close(); guide.Close(); }
        }
    }

    private static CodexAccountView[] SampleAccounts(DateTimeOffset now)
    {
        // These paths are display-only fixture values. Never create or inspect Codex homes here.
        var names = new[] { UiText.T("Personal", "개인용"), UiText.T("Work", "업무용") };
        var slugs = new[] { "personal", "work" };
        var used = new[] { 18d, 64d };
        var codex = Enumerable.Range(0, names.Length).Select(i => new CodexAccountView(
            new CodexAccountProfile(i == 0 ? "default" : i.ToString("D32"),
                @"C:\CycleArc-Samples\" + slugs[i], names[i], i > 0),
            new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro",
                now.AddMinutes(-3), now.AddMinutes(-3), null, null, 2,
                [new("codex", used[i], 10080, now.AddDays(2 + i).AddHours(6), CodexWindowKind.Weekly)], null,
                [now.AddDays(28), now.AddDays(54)]), $"{slugs[i]}@example.invalid")).ToArray();
        var pending = new CodexAccountView(new CodexAccountProfile(2.ToString("D32"), "", UiText.T("Research", "실험용"))
            { Provider = UsageProviderId.Claude },
            CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = UsageProviderId.Claude });
        return [.. codex, pending];
    }

    private sealed class PreviewClaudeConnection(string profileId, DateTimeOffset now) : IClaudeConnectionActions
    {
        private readonly ClaudeAuthentication _auth = new(ClaudeAuthStatus.SignedIn, "research@example.invalid", "Pro", new string('A', 64));
        private ClaudeConnectionBinding? _binding;
        public ClaudeFailureKind FailureKind { get; set; }
        public Task<ClaudeConnectionOverview> InspectAsync(string id, System.Threading.CancellationToken token) =>
            Task.FromResult(new ClaudeConnectionOverview(_binding, _auth, _binding is not null, @"C:\CycleArc-Samples\claude", FailureKind));
        public Task<ClaudeConnectionResult> ConnectAsync(string id, string executable, bool login, string? directory, System.Threading.CancellationToken token)
        {
            if (login) throw new InvalidOperationException("Documentation previews cannot start browser login.");
            _binding = new(1, profileId, @"C:\CycleArc-Samples\claude", @"C:\CycleArc-Samples\claude.cmd", false, _auth.Fingerprint!, now);
            return Task.FromResult(new ClaudeConnectionResult(true, _auth, _binding));
        }
        public Task<ClaudeConnectionResult> ReauthenticateAsync(string id, string executable, System.Threading.CancellationToken token) =>
            throw new InvalidOperationException("Documentation previews cannot change a connection.");
        public Task DisconnectAsync(string id, System.Threading.CancellationToken token) =>
            throw new InvalidOperationException("Documentation previews cannot change a connection.");
        public void OpenClaude(string id, string workingDirectory) =>
            throw new InvalidOperationException("Documentation previews cannot open a session.");
    }

    private static void Save(Window window, string path, double width, double? height, Action? arranged = null)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        arranged?.Invoke();
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2),
            (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
