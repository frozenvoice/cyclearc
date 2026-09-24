using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// Usage ring color bands on the production widget, detail popup and tray renderer, in both
/// themes and languages. Several accounts share one widget so selection and neighbouring
/// accounts are shown not to change a ring's band.
/// </summary>
internal static class RingBandUiChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);

    private sealed record Case(string Id, UsageProviderId Provider, double? Used, bool Stale = false,
        bool Unknown = false);

    private static readonly Case[] Cases =
    [
        new("normal", UsageProviderId.Codex, 69.99),
        new("caution", UsageProviderId.Claude, 70),
        new("caution-edge", UsageProviderId.Cursor, 84.99),
        new("near-limit", UsageProviderId.Codex, 85),
        new("rounded", UsageProviderId.Claude, 99.6),
        new("exhausted", UsageProviderId.Cursor, 100),
        new("stale", UsageProviderId.Claude, 90, Stale: true),
        new("unknown", UsageProviderId.Codex, null, Unknown: true)
    ];

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        var sheets = new List<(string Label, BitmapSource Image)>();
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                CheckPalette(theme);
                var accounts = Cases.Select(Account).ToArray();
                foreach (var selected in new[] { "near-limit", "caution" })
                {
                    var widget = new FloatingWidget { ShowActivated = false };
                    var flyout = new FlyoutWindow { ShowActivated = false };
                    try
                    {
                        widget.BindAccounts(accounts, selected, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
                        WidgetFixture.RenderWidget(widget, null);
                        for (var i = 0; i < Cases.Length; i++)
                        {
                            CheckWidgetModule(WidgetFixture.Module(widget, i), Cases[i], Cases[i].Id == selected,
                                $"{language}/{theme}/{selected}/{Cases[i].Id}");
                            count++;
                        }

                        foreach (var sample in Cases)
                        {
                            flyout.BindAccounts(accounts, sample.Id, false);
                            AccountUiChecks.Render(flyout, 440, null, null);
                            CheckFlyout(flyout, sample, $"{language}/{theme}/detail/{sample.Id}");
                            count++;
                        }

                        if (directory is not null && selected == "near-limit")
                        {
                            var suffix = $"{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}";
                            var widgetImage = Capture((FrameworkElement)widget.Content);
                            // One account per popup so its detail ring is in view, not scrolled away.
                            var details = new List<BitmapSource>();
                            foreach (var id in new[] { "normal", "caution", "rounded", "exhausted" })
                            {
                                var single = accounts.Single(account => account.Profile.Id == id);
                                flyout.BindAccounts([single], id, false);
                                AccountUiChecks.Render(flyout, 440, null, null);
                                CheckFlyout(flyout, Cases.Single(sample => sample.Id == id), $"{language}/{theme}/single/{id}");
                                details.Add(Capture((FrameworkElement)flyout.Content));
                            }
                            var detailImage = Compose(details, horizontal: true, theme);
                            Save(widgetImage, Path.Combine(directory, $"ring-bands-widget-{suffix}.png"));
                            Save(detailImage, Path.Combine(directory, $"ring-bands-detail-{suffix}.png"));
                            if (language == UiLanguage.English)
                                sheets.Add(($"{theme}", Compose([widgetImage, detailImage], horizontal: false, theme)));
                        }
                    }
                    finally { flyout.Close(); widget.CloseWithoutActivation(); }
                }
            }
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
            applyTheme.Invoke(null, [AppTheme.Dark]);
        }

        if (directory is not null)
        {
            var tray = TraySheet();
            Save(tray, Path.Combine(directory, "ring-bands-tray.png"));
            Save(Compose([.. sheets.Select(sheet => sheet.Image), tray], horizontal: false, AppTheme.Dark),
                Path.Combine(directory, "ring-bands-comparison.png"));
        }
        Console.WriteLine($"PASS: {count} ring band cases on widget/detail, EN/KO, Dark/Light, mixed selection; tray bands in the tray icon checks.");
    }

    private static CodexAccountView Account(Case sample)
    {
        var provider = sample.Provider;
        var window = new CodexQuotaWindow(provider == UsageProviderId.Cursor ? "cursor-auto" : "five_hour",
            sample.Used, provider == UsageProviderId.Cursor ? null : 300, Now.AddHours(2),
            provider == UsageProviderId.Cursor ? CodexWindowKind.Other : CodexWindowKind.FiveHour) { IsEnabled = true };
        var snapshot = new CodexQuotaSnapshot(sample.Unknown ? CodexQuotaStatus.Unavailable : CodexQuotaStatus.Available,
            "pro", Now, Now, null, null, null, [window],
            provider == UsageProviderId.Claude ? "claude-live" : null) { Provider = provider };
        if (sample.Stale) snapshot = snapshot.AsStale(Now, "claude-live-request-failed");
        var name = $"{provider.Name()} · {sample.Used?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}";
        return new CodexAccountView(new CodexAccountProfile(sample.Id, "", name) { Provider = provider }, snapshot,
            "sample@example.invalid") { IsConnected = true };
    }

    private static string ExpectedKey(Case sample) =>
        UsageRingBands.ArcBrushKey(sample.Unknown ? UsageRingBand.Normal : UsageRingBands.From(sample.Used), sample.Stale);

    private static void CheckWidgetModule(WidgetAccountModuleView module, Case sample, bool selected, string label)
    {
        var ringGrid = (Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(module.RingValueText));
        var arc = ringGrid.Children.OfType<System.Windows.Shapes.Path>().Single();
        var full = ringGrid.Children.OfType<System.Windows.Shapes.Ellipse>().ToArray();
        var expected = module.FindResource(ExpectedKey(sample));
        Check(ReferenceEquals(arc.Stroke, expected), label + ": widget arc color.");
        Check(full.Any(ellipse => ReferenceEquals(ellipse.Stroke, expected)), label + ": widget full-circle color.");
        Check(arc.Visibility == (RingGeometry.ComputeUsedArc(sample.Unknown ? null : sample.Used, 32, 32, 29).Visible
                ? Visibility.Visible : Visibility.Collapsed), label + ": widget arc visibility.");
        // Only the ring changes: value text, selection border and status keep their resources.
        Check(ReferenceEquals(module.RingValueText.Foreground, module.FindResource(sample.Stale ? "StaleBrush" : "TextBrush")),
            label + ": widget value color changed.");
        Check(selected
                ? ReferenceEquals(module.BorderBrush, module.FindResource("AccentBrush"))
                : module.BorderBrush == Brushes.Transparent,
            label + ": selection border changed.");
        var model = module.Model!;
        Check(model.Ring.IsDangerLevel == (sample.Used is >= 100 && !sample.Unknown), label + ": exhaustion changed.");
        var name = AutomationProperties.GetName(module);
        Check(name.Contains(model.Ring.CenterValueText, StringComparison.Ordinal), label + ": accessible value lost.");
        var bandLabel = UsageRingBands.Label(model.Ring.Band);
        if (!sample.Stale && !sample.Unknown && bandLabel.Length > 0)
            Check(name.Contains(" · " + bandLabel, StringComparison.Ordinal), label + ": accessible band missing.");
        if (sample.Stale || sample.Unknown)
            Check(!name.Contains(" · " + UsageRingBands.Label(UsageRingBand.NearLimit), StringComparison.Ordinal),
                label + ": stale/unknown got a band label.");
    }

    private static void CheckFlyout(FlyoutWindow flyout, Case sample, string label)
    {
        var arc = (System.Windows.Shapes.Path)flyout.FindName("CodexRingArcPath");
        var full = (System.Windows.Shapes.Ellipse)flyout.FindName("CodexRingFullCircle");
        var value = (TextBlock)flyout.FindName("CodexRingValueText");
        var expected = flyout.FindResource(ExpectedKey(sample));
        Check(ReferenceEquals(arc.Stroke, expected) && ReferenceEquals(full.Stroke, expected), label + ": detail arc color.");
        Check(ReferenceEquals(value.Foreground, flyout.FindResource(sample.Stale ? "StaleBrush" : "TextBrush")),
            label + ": detail value color changed.");
        if (sample.Id == "rounded")
            Check(value.Text == "99.6%" && full.Visibility == Visibility.Collapsed, label + ": 99.6% drew as exhausted.");
        if (sample.Id == "exhausted")
            Check(full.Visibility == Visibility.Visible, label + ": 100% is not a full circle.");
        var button = (Button)flyout.FindName("CyclePeriodButton");
        var name = AutomationProperties.GetName(button);
        var band = sample.Unknown ? UsageRingBand.Normal : UsageRingBands.From(sample.Used);
        if (!sample.Stale && !sample.Unknown && band != UsageRingBand.Normal)
            Check(name.Contains(" · " + UsageRingBands.Label(band), StringComparison.Ordinal), label + ": detail band label.");
        Check(name.Contains(value.Text, StringComparison.Ordinal), label + ": detail accessible value lost.");
    }

    private static void CheckPalette(AppTheme theme)
    {
        var app = Application.Current;
        Color Color(string key) => ((SolidColorBrush)app.FindResource(key)).Color;
        var card = Color("CardBrush");
        var background = Color("BgBrush");
        var ring = new[] { "AccentBrush", "RingCautionBrush", "RingNearLimitBrush", "RingExhaustedBrush" };
        foreach (var key in ring)
        {
            Check(Contrast(Color(key), card) >= 3 && Contrast(Color(key), background) >= 2.8,
                $"{theme}: {key} blends into the background.");
        }
        foreach (var a in ring)
        foreach (var b in ring)
        {
            if (string.CompareOrdinal(a, b) >= 0) continue;
            Check(Distance(Color(a), Color(b)) >= 40, $"{theme}: {a} and {b} are too close.");
        }
        // Stale keeps its existing amber and takes priority; it also recolors the value text.
        Check(Distance(Color("StaleBrush"), Color("RingNearLimitBrush")) >= 40
            && Distance(Color("StaleBrush"), Color("RingExhaustedBrush")) >= 40, $"{theme}: stale looks like a band.");
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static BitmapSource TraySheet()
    {
        // Production tray renderer at the native sizes, on dark and light taskbar backdrops.
        var values = new (string Label, CodexQuotaStatus Status, double? Used)[]
        {
            ("69.99", CodexQuotaStatus.Available, 69.99), ("70", CodexQuotaStatus.Available, 70),
            ("84.99", CodexQuotaStatus.Available, 84.99), ("85", CodexQuotaStatus.Available, 85),
            ("99.6", CodexQuotaStatus.Available, 99.6), ("100", CodexQuotaStatus.Available, 100),
            ("stale 90", CodexQuotaStatus.Stale, 90), ("unknown", CodexQuotaStatus.Unavailable, null)
        };
        using var sheet = new System.Drawing.Bitmap(96 + values.Length * 64, 24 + 2 * 3 * 44);
        using (var graphics = System.Drawing.Graphics.FromImage(sheet))
        using (var font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(27, 31, 39));
            for (var i = 0; i < values.Length; i++)
                graphics.DrawString(values[i].Label, font, System.Drawing.Brushes.White, 96 + i * 64, 6);
            var row = 0;
            foreach (var dark in new[] { true, false })
            foreach (var size in new[] { 16, 24, 32 })
            {
                var y = 24 + row++ * 44;
                using var backdrop = new System.Drawing.SolidBrush(dark
                    ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(243, 243, 243));
                graphics.FillRectangle(backdrop, 0, y, sheet.Width, 44);
                graphics.DrawString($"{(dark ? "dark" : "light")} {size}px", font,
                    dark ? System.Drawing.Brushes.White : System.Drawing.Brushes.Black, 6, y + 15);
                for (var i = 0; i < values.Length; i++)
                {
                    var window = new CodexQuotaWindow("smoke", values[i].Used, 300, null, CodexWindowKind.FiveHour);
                    var snapshot = new CodexQuotaSnapshot(values[i].Status, "pro", Now, Now, null, null, null, [window],
                        values[i].Status == CodexQuotaStatus.Stale ? "codex-refresh-failed" : null);
                    using var icon = TrayIconRenderer.Render(snapshot, TrayIconStyle.ProgressRing, size, lightTaskbar: !dark);
                    using var bitmap = icon.ToBitmap();
                    graphics.DrawImageUnscaled(bitmap, 96 + i * 64 + (32 - size) / 2, y + (44 - size) / 2);
                }
            }
        }
        using var stream = new MemoryStream();
        sheet.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // Match the 2x WPF captures so the tray row is legible in the combined sheet.
        return new TransformedBitmap(decoded, new ScaleTransform(2, 2));
    }

    private static BitmapSource Capture(FrameworkElement content)
    {
        content.UpdateLayout();
        var size = content.RenderSize;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2),
            192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Compose(IReadOnlyList<BitmapSource> images, bool horizontal, AppTheme theme)
    {
        const double gap = 24;
        var width = horizontal ? images.Sum(image => image.PixelWidth) + gap * (images.Count - 1) : images.Max(image => image.PixelWidth);
        var height = horizontal ? images.Max(image => image.PixelHeight) : images.Sum(image => image.PixelHeight) + gap * (images.Count - 1);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(theme == AppTheme.Light
                ? Color.FromRgb(245, 247, 250) : Color.FromRgb(18, 20, 24)), null, new Rect(0, 0, width, height));
            double offset = 0;
            foreach (var image in images)
            {
                var rect = horizontal
                    ? new Rect(offset, 0, image.PixelWidth, image.PixelHeight)
                    : new Rect(0, offset, image.PixelWidth, image.PixelHeight);
                drawing.DrawImage(image, rect);
                offset += (horizontal ? image.PixelWidth : image.PixelHeight) + gap;
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
