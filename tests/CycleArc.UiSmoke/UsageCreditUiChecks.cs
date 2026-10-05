using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class UsageCreditUiChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);
    private static readonly string AccountId = new Guid("11111111-1111-1111-1111-111111111111").ToString("N");

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var zoom in new[] { 80, 100, 150 })
        foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var settings = new AppSettings { FlyoutZoomPercent = zoom };
            var flyout = new FlyoutWindow { ShowActivated = false };
            try
            {
                flyout.ApplyWindowSettings(settings);
                flyout.UsageCardExpansionChanged += (id, value) =>
                {
                    if (value) settings.UsageCardExpandedAccounts[id] = true;
                    else settings.UsageCardExpandedAccounts.Remove(id);
                };
                var account = Sample(provider, "normal");
                var accounts = Enumerable.Range(0, zoom == 80 ? 5 : zoom == 100 ? 1 : 3)
                    .Select(index => index == 0 ? account : account with
                    {
                        Profile = account.Profile with { Id = index.ToString("x32"), Label = "Synthetic other " + index }
                    }).ToArray();
                flyout.BindAccounts(accounts, AccountId, false);
                Render("collapsed");
                Require(Details().Visibility == Visibility.Collapsed, "Card did not start collapsed.");
                var toggle = (Button)flyout.FindName("UsageCreditsExpandButton");
                Require(toggle.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(toggle), "Toggle cannot receive keyboard focus.");
                Require(AutomationProperties.GetName(toggle).Length > 0, "Toggle lacks accessible name.");
                // WPF's standard Button peer invokes the same action exposed to keyboard/assistive input.
                var peer = new ButtonAutomationPeer(toggle);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Pump();
                Require(Details().Visibility == Visibility.Visible, "Accessible toggle did not expand card.");
                Render("expanded");
                flyout.BindAccounts(accounts, AccountId, true);
                Require(Details().Visibility == Visibility.Visible, "Refresh reset expansion.");
                if (accounts.Length > 1)
                {
                    flyout.BindAccounts(accounts, accounts[1].Profile.Id, false);
                    Require(Details().Visibility == Visibility.Collapsed, "Another account inherited expansion.");
                    flyout.BindAccounts(accounts, AccountId, false);
                    Require(Details().Visibility == Visibility.Visible, "Account switch lost expansion.");
                }
                foreach (var state in new[] { "off", "missing", "failed", "large", "identity" })
                {
                    flyout.BindAccounts([Sample(provider, state)], AccountId, false);
                    Render(state);
                    if (state == "missing")
                    {
                        var auxiliary = (Border)flyout.FindName("UsageCreditsCard");
                        // Expansion remains account-owned; the healthy missing folded row is compact.
                        ((Button)flyout.FindName("UsageCreditsExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(auxiliary.BorderThickness == new Thickness(0)
                            && ((TextBlock)flyout.FindName("UsageCreditsSummary")).Text == UiText.T("Not provided", "미제공")
                            && AutomationProperties.GetName(auxiliary).Contains(UiText.T("Not provided", "미제공")),
                            "Healthy missing credit data lost its compact, explicit, accessible state.");
                        ((Button)flyout.FindName("UsageCreditsExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    if (state is "failed" or "identity")
                        Require(((Border)flyout.FindName("UsageCreditsCard")).BorderThickness == new Thickness(1)
                            && ((System.Windows.Shapes.Path)flyout.FindName("UsageCreditsWarning")).Visibility == Visibility.Visible,
                            "Credit failure or reconnect requirement was compacted away.");
                    if (state == "identity")
                    {
                        Require(flyout.DetailRows.Count == 0, "Identity mismatch retained quota rows.");
                        Require(((TextBlock)flyout.FindName("CodexRingValueText")).Text == "?", "Identity mismatch retained quota ring.");
                        Require(!((TextBlock)flyout.FindName("ResetCreditsCount")).Text.Contains('2'), "Identity mismatch retained reset credits.");
                    }
                    count++;
                }
                using var recreated = new WindowCloser(new FlyoutWindow { ShowActivated = false });
                recreated.Window.ApplyWindowSettings(settings);
                recreated.Window.BindAccounts([account], AccountId, false);
                Require(((StackPanel)recreated.Window.FindName("UsageCreditsDetails")).Visibility == Visibility.Visible,
                    "Window recreation lost expansion.");
                if (provider == UsageProviderId.Cursor)
                    Require(!flyout.DetailRows.SelectMany(AccountUiChecks.Descendants<TextBlock>)
                        .Any(text => text.Text == UiText.T("On-demand", "온디맨드")), "On-demand repeats in quota rows.");

                StackPanel Details() => (StackPanel)flyout.FindName("UsageCreditsDetails");
                void Render(string state)
                {
                    var path = directory is not null && zoom == 100
                        ? Path.Combine(directory, $"usage-card-{provider.ToString().ToLowerInvariant()}-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}-{state}.png") : null;
                    AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, path);
                    var card = (Border)flyout.FindName("UsageCreditsCard");
                    Require((string?)card.Tag == AccountId, "Card bound to wrong profile.");
                    var title = (TextBlock)flyout.FindName("UsageCreditsTitle");
                    Require(title.TextWrapping == TextWrapping.NoWrap && title.ActualWidth > 0, "Title wraps or disappears.");
                    var summary = (TextBlock)flyout.FindName("UsageCreditsSummary");
                    var bounds = summary.TransformToAncestor(card).TransformBounds(new Rect(summary.RenderSize));
                    Require(bounds.Right <= card.ActualWidth && bounds.Left >= 0, "Folded summary escapes card.");
                    foreach (var row in ((ItemsControl)flyout.FindName("UsageCreditsRows")).Items.Cast<Grid>())
                    {
                        var label = (TextBlock)row.Children[0];
                        var value = (TextBlock)row.Children[1];
                        Require(label.ActualHeight <= 20, "Label broke into characters.");
                        Require(value.TextWrapping == TextWrapping.NoWrap, "Date or value wraps into characters.");
                        if (Details().Visibility == Visibility.Visible)
                            Require(value.ActualWidth + 1 >= new FormattedText(value.Text,
                                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                new Typeface(value.FontFamily, value.FontStyle, value.FontWeight, value.FontStretch),
                                value.FontSize, Brushes.Black, 1).Width, "Expanded value is clipped: " + value.Text);
                    }
                }
            }
            finally { flyout.Close(); }
        }
        CheckNativeExpansionSnap();
        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine($"PASS: {count} usage-card WPF states in EN/KO Dark/Light at 80/100/150%; account/refresh/recreation persistence, native expansion snap and small-work-area scrolling.");
    }

    internal static CodexAccountView Sample(UsageProviderId provider, string state)
    {
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now, Now,
            null, null, 2, [new(provider == UsageProviderId.Cursor ? "cursor-auto" : "five_hour", 25,
                300, Now.AddHours(2), CodexWindowKind.FiveHour)], provider == UsageProviderId.Claude ? "claude-live" : null)
        {
            Provider = provider,
            UsageCredits = new(true, false, state == "large" ? 1250500123.45m : 1250.5m, "codex", Now),
            ExtraUsage = new(true, state == "large" ? 1250500.45m : 3.20m, 10m, false, "USD", 32, Now)
        };
        if (provider == UsageProviderId.Cursor) snapshot = snapshot with
        {
            Windows = snapshot.Windows.Append(new CodexQuotaWindow("cursor-on-demand", null, null, Now.AddDays(15), CodexWindowKind.Other)
            { IsEnabled = state != "off", UsedAmount = state == "large" ? 1250500.45m : 4.50m,
                LimitAmount = 20m, RemainingAmount = 15.50m, Unit = "USD", AmountObservedAt = Now }).ToArray()
        };
        if (state == "off") snapshot = snapshot with
        {
            UsageCredits = new(false, false, 0, "codex", Now), ExtraUsage = new(false, null, null, false, null, null, Now)
        };
        if (state == "missing") snapshot = snapshot with
        {
            UsageCredits = null, ExtraUsage = null, Windows = snapshot.Windows.Where(window => window.LimitId != "cursor-on-demand").ToArray()
        };
        if (state == "failed") snapshot = snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "timeout" };
        if (state == "identity") snapshot = snapshot with
        {
            Status = CodexQuotaStatus.Unavailable, TechnicalDetail = provider.ToString().ToLowerInvariant()
                + (provider == UsageProviderId.Codex ? "-identity-mismatch" : "-live-identity-mismatch")
        };
        return new(new CodexAccountProfile(AccountId, "", "Synthetic · " + provider
            + (state == "large" ? " very long account name for layout" : "")) { Provider = provider },
            snapshot, "synthetic@example.invalid") { IsConnected = true };
    }

    private static void CheckNativeExpansionSnap()
    {
        var flyout = new FlyoutWindow { ShowActivated = false };
        try
        {
            var account = Sample(UsageProviderId.Claude, "normal");
            flyout.ApplyWindowSettings(new AppSettings { FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Right,
                FlyoutVerticalAnchor = VerticalEdgeAnchor.Bottom });
            flyout.BindAccounts([account], AccountId, false);
            flyout.Show();
            flyout.RefreshWorkArea();
            Pump();
            for (var index = 0; index < 2; index++)
            {
                ((Button)flyout.FindName("UsageCreditsExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(flyout).Handle);
                var transform = PresentationSource.FromVisual(flyout)!.CompositionTarget!.TransformFromDevice;
                var bottomRight = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
                Require(Math.Abs(flyout.Left + flyout.Width - bottomRight.X + 2) <= 1.5
                    && Math.Abs(flyout.Top + flyout.ActualHeight - bottomRight.Y + 2) <= 1.5,
                    "Card expansion changed 2 DIP snapped margin.");
            }
            var fit = typeof(FlyoutWindow).GetMethod("FitContentToWorkArea", BindingFlags.NonPublic | BindingFlags.Instance)!;
            fit.Invoke(flyout, [new ScreenRect(0, 0, 460, 400)]);
            flyout.UpdateLayout();
            Require(((ScrollViewer)flyout.FindName("FlyoutContentScroll")).ScrollableHeight > 0,
                "Small work area no longer scrolls.");
        }
        finally { flyout.Close(); }
    }

    private sealed class WindowCloser(FlyoutWindow window) : IDisposable
    {
        public FlyoutWindow Window { get; } = window;
        public void Dispose() => Window.Close();
    }
    private static void Pump() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
