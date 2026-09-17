using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class WidgetDpiChecks
{
    // Synthetic quota only. Neither these renders nor the native windows start the production app.
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var now = DateTimeOffset.Now;
        // Both periods and a long nickname, so scaling is tested on the widest real content.
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now,
            null, null, 3,
            [
                new("five", 85, CodexWindowClassifier.FiveHourMinutes, now.AddMinutes(35), CodexWindowKind.FiveHour),
                new("codex", 25, CodexWindowClassifier.WeeklyMinutes, now.AddDays(7), CodexWindowKind.Weekly)
            ], null);
        CodexAccountView[] accounts =
        [
            WidgetFixture.Synthetic("dpi-main", "Main", snapshot),
            WidgetFixture.Synthetic("dpi-long", "a-very-long-synthetic-widget-nickname", snapshot),
            WidgetFixture.Synthetic("dpi-claude", "Work Claude", snapshot with { Provider = UsageProviderId.Claude })
        ];
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var widget = new FloatingWidget();
            try
            {
                // Change the visual's layout DPI, not just the output bitmap resolution.
                var content = (FrameworkElement)widget.Content;
                Bind(widget, accounts, snapshot.Status);
                VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                foreach (var status in new[] { CodexQuotaStatus.Available, CodexQuotaStatus.Stale,
                             CodexQuotaStatus.Available })
                {
                    Bind(widget, accounts, status);
                    content.InvalidateMeasure();
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var desired = content.DesiredSize;
                    // Native window constraints can allocate more height than content requests.
                    // Reproduce this even on CI hosts with different window metrics.
                    foreach (var extraHeight in new[] { 0.0, 6.0 })
                    {
                        content.Arrange(new Rect(0, 0, desired.Width, desired.Height + extraHeight));
                        content.UpdateLayout();
                        AssertModuleLayout(widget, $"{language}/{theme}/{scale}/{status}/extra={extraHeight}");
                        var bitmap = Render(content, scale);
                        if (directory is not null && status == CodexQuotaStatus.Available)
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(directory,
                                $"{language}-{theme}-{scale * 100:0}-{extraHeight:0}.png"));
                            encoder.Save(stream);
                        }
                        count++;
                    }
                }
            }
            finally { widget.Close(); }
        }

        UiText.SetLanguage(UiLanguage.Korean);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        var screens = System.Windows.Forms.Screen.AllScreens;
        foreach (var screen in screens)
        {
            var widget = new FloatingWidget { ShowActivated = false };
            try
            {
                Bind(widget, accounts, CodexQuotaStatus.Available);
                widget.Apply(new AppSettings { WidgetPixelLeft = screen.WorkingArea.Left + 100,
                    WidgetPixelTop = screen.WorkingArea.Top + 100 });
                widget.Show();
                foreach (var status in new[] { CodexQuotaStatus.Available, CodexQuotaStatus.Stale,
                             CodexQuotaStatus.Available })
                {
                    Bind(widget, accounts, status);
                    for (var i = 0; i < 3; i++)
                    {
                        var frame = new DispatcherFrame();
                        widget.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                            new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                    }
                    AssertModuleLayout(widget, $"native {screen.DeviceName}/{status}");
                }
            }
            finally { widget.Close(); }
        }
        Console.WriteLine($"PASS: {count} widget DPI/layout renders and centered native windows on {screens.Length} monitor(s).");
    }

    private static void Bind(FloatingWidget widget, IReadOnlyList<CodexAccountView> accounts, CodexQuotaStatus status) =>
        widget.BindAccounts(accounts.Select(account =>
            account with { Snapshot = account.Snapshot with { Status = status } }).ToArray(),
            accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop);

    // Every module keeps its fixed width and readable text at each scale: nothing overlaps,
    // nothing is clipped, and the header stays above the grid.
    private static void AssertModuleLayout(FloatingWidget widget, string context)
    {
        var content = (FrameworkElement)widget.Content;
        var scale = VisualTreeHelper.GetDpi(content).DpiScaleY;
        var header = (FrameworkElement)widget.FindName("WidgetHeader");
        var title = (TextBlock)widget.FindName("ProductTitle");
        if (title.Text != "CycleArc") throw new InvalidOperationException($"Widget product title is incorrect ({context}).");
        if (widget.Modules.Count == 0) throw new InvalidOperationException($"Widget rendered no account module ({context}).");
        var layout = widget.LastLayout ?? throw new InvalidOperationException($"Widget produced no layout ({context}).");
        foreach (var module in widget.Modules)
        {
            if (Math.Abs(module.ActualWidth - WidgetGridLayout.ModuleWidth) > 0.51)
                throw new InvalidOperationException(
                    $"Module width drifted from the fixed {WidgetGridLayout.ModuleWidth} DIP ({context}): {module.ActualWidth}.");
            var moduleTop = module.TranslatePoint(new Point(), content).Y;
            var headerBottom = header.TranslatePoint(new Point(0, header.ActualHeight), content).Y;
            if (moduleTop + 0.01 < headerBottom)
                throw new InvalidOperationException($"An account module overlaps the header ({context}).");
            var name = module.NameText;
            var badge = module.Badge;
            var nameRight = name.TranslatePoint(new Point(name.ActualWidth, 0), module).X;
            var badgeLeft = badge.TranslatePoint(new Point(), module).X;
            if (nameRight > badgeLeft + 0.01)
                throw new InvalidOperationException($"Account name overlaps its provider badge ({context}).");
            if (name.ActualWidth <= 0 || badge.ActualWidth <= 0)
                throw new InvalidOperationException($"Account identity collapsed to nothing ({context}).");
            foreach (var line in module.Periods)
            {
                var periodRight = line.PeriodText.TranslatePoint(new Point(line.PeriodText.ActualWidth, 0), module).X;
                var remainingLeft = line.RemainingText.TranslatePoint(new Point(), module).X;
                if (periodRight > remainingLeft + 0.01)
                    throw new InvalidOperationException($"Period label overlaps its remaining value ({context}).");
                if (line.RemainingText.ActualWidth + 0.5 < line.RemainingText.DesiredSize.Width
                    || line.ResetText.ActualWidth + 0.5 < line.ResetText.DesiredSize.Width)
                    throw new InvalidOperationException($"A quota or reset value is clipped ({context}).");
            }
            if (Math.Abs(VisualTreeHelper.GetDpi(name).DpiScaleY - scale) > 0.001)
                throw new InvalidOperationException("Widget text did not inherit the tested DPI.");
        }
        // The wrapped grid must never exceed what the widget said it would take, beyond
        // 1 DIP hairlines snapping to whole device pixels (150% DPI: 720 → 721⅓).
        var slack = Math.Max(1.01, layout.HairlineRoundingSlack(VisualTreeHelper.GetDpi(content).DpiScaleX) + 0.01);
        if (content.DesiredSize.Width > layout.Width + slack)
            throw new InvalidOperationException(
                $"Rendered widget is wider than its layout ({context}): {content.DesiredSize.Width} > {layout.Width}.");
    }

    private static RenderTargetBitmap Render(FrameworkElement content, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
            (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }
}
