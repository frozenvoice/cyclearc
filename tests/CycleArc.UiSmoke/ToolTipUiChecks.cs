using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class ToolTipUiChecks
{
    private static readonly MethodInfo ApplyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var count = 0;
        // WPF propagates application resource changes to popups through a live placement target.
        var host = new Window { Content = new Border(), Width = 1, Height = 1, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System })
            {
                UiText.SetLanguage(language);
                ApplyTheme.Invoke(null, [theme]);
                var now = DateTimeOffset.Now;
                var codex = new CodexAccountView(new("codex-tip", "", "Personal"),
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
                    {
                        Windows = [new CodexQuotaWindow("codex", 13, 10080, now.AddDays(5), CodexWindowKind.Weekly)],
                        LastSuccessfulRefresh = now
                    }) { Email = "person@example.invalid", IsConnected = true };
                var claude = codex with
                {
                    Profile = codex.Profile with { Id = "claude-tip", Provider = UsageProviderId.Claude },
                    Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with
                    {
                        Provider = UsageProviderId.Claude, TechnicalDetail = "claude-connected-waiting"
                    }
                };
                var cards = CreateCards(codex, claude);
                var codexCard = cards[0];
                var claudeCard = cards[1];
                var avatar = AccountUiChecks.Descendants<Border>(codexCard).Single(x => Equals(x.Tag, "AccountAvatar"));
                var longEmail = new string('a', 64) + "@" + string.Join(".", Enumerable.Repeat(new string('b', 40), 4)) + ".invalid";
                var rich = new TextBlock { Text = UiText.T("Existing formatted tooltip", "기존 서식 툴팁"), TextWrapping = TextWrapping.Wrap };
                (string Name, object Content, string Expected)[] cases =
                [
                    ("codex", codexCard.ToolTip, (string)codexCard.ToolTip),
                    ("claude", claudeCard.ToolTip, (string)claudeCard.ToolTip),
                    ("avatar", avatar.ToolTip, (string)avatar.ToolTip),
                    ("long-email", longEmail, longEmail),
                    ("long-message", string.Join("\n", Enumerable.Repeat((string)claudeCard.ToolTip, 10)),
                        string.Join("\n", Enumerable.Repeat((string)claudeCard.ToolTip, 10))),
                    ("rich-content", rich, rich.Text)
                ];
                foreach (var item in cases)
                {
                    var tip = new ToolTip
                    {
                        Content = item.Content, PlacementTarget = (UIElement)host.Content,
                        Placement = PlacementMode.Absolute, HorizontalOffset = 50, VerticalOffset = 50
                    };
                    try
                    {
                        tip.IsOpen = true;
                        Layout(tip);
                        Check(tip, item.Expected, item.Name);
                        if (item.Name is "codex" or "avatar")
                            Require(tip.ActualHeight < 80, "Short tooltip reserves a large empty area.");
                        if (directory is not null && (item.Name is "codex" or "claude" or "long-email" or "rich-content"))
                            Render(tip, Path.Combine(directory, $"tooltip-{item.Name}-{language}-{theme}.png"));
                        // Popups must follow a theme change while still open, not just at construction.
                        ApplyTheme.Invoke(null, [theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light]);
                        Layout(tip);
                        Check(tip, item.Expected, item.Name);
                        ApplyTheme.Invoke(null, [theme]);
                        count++;
                    }
                    finally { tip.IsOpen = false; }
                }
            }
        }
        finally { host.Close(); }
        Console.WriteLine($"PASS: {count} open WPF tooltip renders; Codex/Claude account and avatar text, wrapping, bounded size, contrast, rich content and live theme changes.");
    }

    private static Button[] CreateCards(params CodexAccountView[] accounts)
    {
        var flyout = new FlyoutWindow();
        try
        {
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            AccountUiChecks.Render(flyout, 440, null, null);
            return ((ItemsControl)flyout.FindName("AccountOverview")).Items.Cast<Button>().ToArray();
        }
        finally { flyout.Close(); }
    }

    private static void Layout(ToolTip tip)
    {
        AccountUiChecks.PumpUntil(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task);
        tip.UpdateLayout();
    }

    private static void Check(ToolTip tip, string expected, string name)
    {
        Require(tip.ActualWidth > 0 && tip.ActualHeight > 0, $"Empty tooltip: {name}.");
        var text = AccountUiChecks.Descendants<TextBlock>(tip).SingleOrDefault(x => x.Text == expected);
        Require(text is not null && text.ActualWidth > 0 && text.ActualHeight > 0, $"Tooltip text lost: {name}.");
        var background = ((SolidColorBrush)tip.Background).Color;
        var foreground = ((SolidColorBrush)text!.Foreground).Color;
        Require(Contrast(background, foreground) >= 4.5, $"Unreadable tooltip: {name}, background {background}, foreground {foreground}.");
        Require(background == ((SolidColorBrush)Application.Current.FindResource("CardBrush")).Color,
            $"Tooltip ignored current theme: {name}.");
        Require(tip.ActualWidth <= 360.5 && tip.ActualHeight <= 300.5, $"Oversized tooltip: {name}, {tip.ActualWidth} x {tip.ActualHeight}.");
        Require(text.ActualWidth <= tip.ActualWidth && text.TextTrimming == TextTrimming.None, $"Tooltip text is clipped: {name}.");
        if (name is "claude" or "long-email" or "long-message")
            Require(text.TextWrapping == TextWrapping.Wrap && text.ActualHeight > text.FontSize * 2,
                $"Long tooltip did not wrap: {name}.");
        if (text.ActualHeight > tip.ActualHeight)
        {
            var scroll = AccountUiChecks.Descendants<ScrollViewer>(tip).Single();
            Require(scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "Long tooltip content is inaccessible.");
            scroll.ScrollToEnd();
            Layout(tip);
            Require(scroll.VerticalOffset > 0, "Long tooltip cannot scroll to the remaining text.");
        }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var s = value / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
