using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>Real production views, bound to identical synthetic sources without app startup.</summary>
internal static class UsagePercentUiChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);
    private sealed record Sample(double Used, string WidgetUsed, string WidgetLeft, string DetailUsed, string DetailLeft);
    private static readonly Sample[] Cases =
    [
        new(76.91, "77%", "23%", "76.91%", "23.09%"),
        new(76.5, "77%", "23%", "76.5%", "23.5%"),
        new(76.499, "76%", "24%", "76.5%", "23.5%"),
        new(12.5, "13%", "87%", "12.5%", "87.5%"),
        new(99.6, ">99%", "<1%", "99.6%", "0.4%"),
        new(0.5, "<1%", ">99%", "0.5%", "99.5%"),
        new(1, "1%", "99%", "1%", "99%"),
        new(0, "0%", "100%", "0%", "100%"),
        new(100, "100%", "0%", "100%", "0%"),
        new(0.009, "<1%", ">99%", "<0.01%", ">99.99%"),
        new(99.991, ">99%", "<1%", ">99.99%", "<0.01%")
    ];

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in Enum.GetValues<AppTheme>())
            foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                foreach (var zoom in new[] { 80, 100, 150 })
                {
                    // Full contract once per language/provider; layout checks use the
                    // fractional and longest boundary strings at every theme/scale.
                    var samples = theme == AppTheme.Dark && zoom == 100
                        ? Cases : new[] { Cases[0], Cases[4], Cases[5], Cases[9], Cases[10] };
                    var widget = new FloatingWidget { ShowActivated = false };
                    var flyout = new FlyoutWindow { ShowActivated = false };
                    try
                    {
                        widget.SetZoom(zoom, notify: false);
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        foreach (var sample in samples)
                        {
                            var account = Account(provider, "target", sample);
                            var other = Account(provider, "other", Cases[3]);
                            var originalWindow = account.Snapshot.Windows[0];
                            flyout.BindAccounts([other, account], account.Profile.Id, false);
                            widget.BindAccounts([account], account.Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
                            WidgetFixture.RenderWidget(widget, null);
                            AccountUiChecks.Render(flyout, 440 * zoom / 100d, null, null);
                            var label = $"{provider}/{language}/{theme}/{zoom}/{sample.Used.ToString(CultureInfo.InvariantCulture)}";
                            CheckViews(widget, flyout, account, sample, label);
                            Check(ReferenceEquals(originalWindow, account.Snapshot.Windows[0])
                                && originalWindow.UsedPercent == sample.Used, label + ": source changed.");

                            if (directory is not null && zoom == 100
                                && (sample == Cases[0] || sample == Cases[4] || sample == Cases[10]))
                            {
                                var suffix = sample == Cases[0] ? "" : sample == Cases[4] ? "-boundary" : "-tiny";
                                var path = Path.Combine(directory,
                                    $"usage-percent-{provider.ToString().ToLowerInvariant()}-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}{suffix}.png");
                                SavePair(widget, flyout, path);
                            }
                            count++;
                        }
                    }
                    finally { flyout.Close(); widget.CloseWithoutActivation(); }
                }
            }
            // Also exercise native, simultaneously visible windows. OfflineApp has no
            // production startup; these controls have no refresh/settings subscribers.
            foreach (var provider in new[] { UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor })
                CheckShownPair(provider);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
            applyTheme.Invoke(null, [AppTheme.Dark]);
        }
        Console.WriteLine($"PASS: {count} all-provider widget/detail/account-summary WPF cases, EN/KO, Dark/Light/System, 80/100/150; three simultaneous native pairs.");
    }

    private static CodexAccountView Account(UsageProviderId provider, string id, Sample sample) =>
        new(new CodexAccountProfile(id, "", provider.Name() + (id == "target" ? " · Sample" : " · Other")) { Provider = provider },
            new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now, Now, null, null, null,
                [new CodexQuotaWindow(provider == UsageProviderId.Cursor ? "cursor-auto" : "five_hour",
                    sample.Used, 300, Now.AddHours(2), CodexWindowKind.FiveHour)],
                provider == UsageProviderId.Claude ? "claude-live" : null) { Provider = provider },
            "sample@example.invalid") { IsConnected = true };

    private static void CheckViews(FloatingWidget widget, FlyoutWindow flyout, CodexAccountView account, Sample sample, string label)
    {
        var cursor = account.Profile.Provider == UsageProviderId.Cursor;
        var module = WidgetFixture.Module(widget);
        Check(module.RingValueText.Text == sample.WidgetUsed, label + ": widget ring.");
        Check(module.Periods.Single().RemainingText.Text == (cursor ? sample.WidgetLeft : UiText.WidgetLeft(sample.WidgetLeft)),
            label + ": widget remaining.");
        Check(module.StatusText.Visibility == Visibility.Collapsed, label + ": healthy footer reappeared.");
        Check(module.Model!.Ring.UsedPercent == sample.Used && module.Model.Ring.IsDangerLevel == (sample.Used >= 100),
            label + ": geometry/danger input changed.");
        var widgetRing = (Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
        CheckFits(module.RingValueText, widgetRing, 6, label + ": widget ring text");
        var arc = widgetRing.Children.OfType<System.Windows.Shapes.Path>().Single();
        var expectedArc = RingGeometry.ComputeUsedArc(sample.Used, 32, 32, 29);
        var actualSegment = (ArcSegment)((PathGeometry)arc.Data).Figures[0].Segments[0];
        Check(arc.Visibility == (expectedArc.Visible ? Visibility.Visible : Visibility.Collapsed), label + ": arc visibility.");
        if (expectedArc.Visible)
            Check(Math.Abs(actualSegment.Point.X - expectedArc.End.X) < 0.0001
                && Math.Abs(actualSegment.Point.Y - expectedArc.End.Y) < 0.0001, label + ": fractional arc.");
        Check(ReferenceEquals(arc.Stroke, module.FindResource(sample.Used >= 100 ? "DangerBrush" : "AccentBrush")),
            label + ": original warning color.");

        var ringText = (TextBlock)flyout.FindName("CodexRingValueText");
        Check(ringText.Text == sample.DetailUsed, label + ": detail ring.");
        CheckFits(ringText, (FrameworkElement)flyout.FindName("CodexRingHost"), 10, label + ": detail ring text");
        var expectedLabel = cursor ? CursorUsagePresentation.QuotaDisplayLabel("cursor-auto")
            : UiText.FiveHourUsed + " / " + UiText.T("left", "남음");
        var row = ((ItemsControl)flyout.FindName("CodexRows")).Items.Cast<Border>().Select(border => (Grid)border.Child)
            .Single(grid => ((TextBlock)grid.Children[0]).Text == expectedLabel);
        var value = (TextBlock)row.Children.OfType<StackPanel>().Single().Children[0];
        var remaining = UiText.T($"Remaining {sample.DetailLeft}", $"잔여 {sample.DetailLeft}");
        Check(value.Text == (cursor ? remaining : $"{sample.DetailUsed} / {sample.DetailLeft}"), label + ": detail row.");
        Check(value.ActualWidth + 1 >= value.DesiredSize.Width, label + ": detail row clipped.");
        var card = ((ItemsControl)flyout.FindName("AccountOverview")).Items.Cast<Button>()
            .Single(button => (string)button.Tag == account.Profile.Id);
        var summary = cursor ? remaining : UiText.T($"Used {sample.DetailUsed} · Left {sample.DetailLeft}",
            $"사용 {sample.DetailUsed} · 잔여 {sample.DetailLeft}");
        Check(AccountUiChecks.Descendants<TextBlock>(card).Any(text => text.Text == summary), label + ": account summary.");
    }

    private static void CheckFits(FrameworkElement text, FrameworkElement host, double inset, string label)
    {
        var bounds = text.TransformToAncestor(host).TransformBounds(new Rect(text.RenderSize));
        Check(bounds.Left >= inset - 1 && bounds.Right <= host.ActualWidth - inset + 1
            && bounds.Top >= -1 && bounds.Bottom <= host.ActualHeight + 1, label + " clipped.");
    }

    private static void CheckShownPair(UsageProviderId provider)
    {
        var widget = new FloatingWidget { ShowActivated = false, Left = 40, Top = 40 };
        var flyout = new FlyoutWindow { ShowActivated = false, Left = 340, Top = 40 };
        try
        {
            var target = Account(provider, "target", Cases[0]);
            widget.BindAccounts([target], target.Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
            flyout.BindAccounts([Account(provider, "other", Cases[3]), target], target.Profile.Id, false);
            widget.Show();
            flyout.Show();
            flyout.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(widget.IsVisible && flyout.IsVisible, provider + ": native pair not visible.");
            CheckViews(widget, flyout, target, Cases[0], provider + "/native");
        }
        finally { flyout.Close(); widget.CloseWithoutActivation(); }
    }

    private static void SavePair(FloatingWidget widget, FlyoutWindow flyout, string path)
    {
        // Export actual production visuals at 100% on one canvas; no reconstructed UI.
        var left = (FrameworkElement)widget.Content;
        var right = (FrameworkElement)flyout.Content;
        var leftSize = left.RenderSize;
        var rightSize = right.RenderSize;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new VisualBrush(left), null, new Rect(new Point(), leftSize));
            drawing.DrawRectangle(new VisualBrush(right), null, new Rect(new Point(leftSize.Width + 18, 0), rightSize));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((leftSize.Width + 18 + rightSize.Width) * 2),
            (int)Math.Ceiling(Math.Max(leftSize.Height, rightSize.Height) * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
