using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.UI;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class TrayIconChecks
{
    public static void Run(string? directory = null)
    {
        var cases = new[]
        {
            ("available", Snapshot(CodexQuotaStatus.Available, 63), Color.FromArgb(37, 99, 235), false),
            ("refreshing", Snapshot(CodexQuotaStatus.Refreshing, 63), Color.FromArgb(37, 99, 235), false),
            ("stale", Snapshot(CodexQuotaStatus.Stale, 63, "codex-refresh-failed"), Color.FromArgb(251, 191, 36), false),
            ("unknown", Snapshot(CodexQuotaStatus.Unavailable, null), Color.FromArgb(251, 191, 36), false),
            ("danger", Snapshot(CodexQuotaStatus.Available, 100), Color.FromArgb(220, 38, 38), false),
            ("claude-waiting", Snapshot(CodexQuotaStatus.Unavailable, null, "claude-connected-waiting",
                UsageProviderId.Claude), Color.FromArgb(107, 114, 128), true)
        };
        var values = new[] { 0d, 9d, 63d, 99d, 100d };
        var count = 0;

        foreach (var size in new[] { 16, 24, 32 })
        {
            foreach (var style in Enum.GetValues<TrayIconStyle>())
            {
                foreach (var (name, snapshot, expected, awaiting) in cases)
                {
                    using var icon = TrayIconRenderer.Render(snapshot, style, size, awaiting);
                    using var bitmap = icon.ToBitmap();
                    CheckBitmap(bitmap, size, style, name);
                    if (style == TrayIconStyle.RemainingNumber)
                    {
                        CheckColor(bitmap, expected, name, size);
                        CheckContrast(bitmap, name, size);
                    }
                    count++;
                }

                foreach (var value in values)
                {
                    using var icon = TrayIconRenderer.Render(Snapshot(CodexQuotaStatus.Available, value), style, size);
                    using var bitmap = icon.ToBitmap();
                    CheckBitmap(bitmap, size, style, value.ToString("0"));
                    if (style == TrayIconStyle.RemainingNumber)
                        CheckGlyph(bitmap, size, value.ToString("0"));
                    count++;
                }
            }
        }

        CheckUnknownIsNotZero();
        if (directory is not null)
            ExportContactSheet(directory);
        Console.WriteLine($"PASS: {count} tray icon renders across sizes, styles, values and states.");
    }

    private static CodexQuotaSnapshot Snapshot(
        CodexQuotaStatus status,
        double? used,
        string? detail = null,
        UsageProviderId provider = UsageProviderId.Codex)
    {
        var window = new CodexQuotaWindow("smoke", used, 10_080, null, CodexWindowKind.Weekly);
        return new CodexQuotaSnapshot(status, "pro", DateTimeOffset.Now, DateTimeOffset.Now,
            null, null, null, [window], detail) with { Provider = provider };
    }

    private static void CheckBitmap(Bitmap bitmap, int requestedSize, TrayIconStyle style, string label)
    {
        if (bitmap.Width != requestedSize || bitmap.Height != requestedSize)
            throw new InvalidOperationException($"Tray icon has no pixels: {style}/{label}/{requestedSize}.");

        var visible = 0;
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A == 0) continue;
            visible++;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (visible == 0 || maxX < minX || maxY < minY)
            throw new InvalidOperationException($"Tray icon is fully transparent: {style}/{label}/{requestedSize}.");
        if (maxX - minX + 1 < bitmap.Width / 2 || maxY - minY + 1 < bitmap.Height / 2)
            throw new InvalidOperationException($"Tray icon bounds are too small: {style}/{label}/{requestedSize}.");
        if (bitmap.GetPixel(0, 0).A != 0 && style == TrayIconStyle.RemainingNumber)
            throw new InvalidOperationException($"Number icon lost transparent corner: {label}/{requestedSize}.");
    }

    private static void CheckColor(Bitmap bitmap, Color expected, string label, int size)
    {
        var matches = 0;
        for (var y = 1; y < bitmap.Height - 1; y++)
        for (var x = 1; x < bitmap.Width - 1; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A > 180 && Distance(pixel, expected) <= 8)
                matches++;
        }

        if (matches < Math.Max(4, size * size / 8))
            throw new InvalidOperationException($"Tray icon state color is missing: {label}/{size}.");
    }

    private static void CheckContrast(Bitmap bitmap, string label, int size)
    {
        var minimum = 255d;
        var maximum = 0d;
        for (var y = 1; y < bitmap.Height - 1; y++)
        for (var x = 1; x < bitmap.Width - 1; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 160) continue;
            var luminance = pixel.R * 0.2126 + pixel.G * 0.7152 + pixel.B * 0.0722;
            minimum = Math.Min(minimum, luminance);
            maximum = Math.Max(maximum, luminance);
        }

        if (maximum - minimum < 80)
            throw new InvalidOperationException($"Tray icon lacks glyph/background contrast: {label}/{size}.");
    }

    private static void CheckGlyph(Bitmap bitmap, int size, string value)
    {
        var glyphColor = Color.White;
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 1; y < bitmap.Height - 1; y++)
        for (var x = 1; x < bitmap.Width - 1; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 160 || Distance(pixel, glyphColor) > 85) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        if (maxX < minX || width < Math.Max(2, size / 5) || height < Math.Max(4, Math.Floor(size * (value.Length >= 3 ? 0.34 : 0.55))))
            throw new InvalidOperationException($"Tray glyph is too small: {value}/{size} ({width}x{height}).");
    }

    private static void CheckUnknownIsNotZero()
    {
        using var unknown = TrayIconRenderer.Render(Snapshot(CodexQuotaStatus.Unavailable, null), TrayIconStyle.RemainingNumber, 32);
        using var zero = TrayIconRenderer.Render(Snapshot(CodexQuotaStatus.Stale, 0), TrayIconStyle.RemainingNumber, 32);
        using var unknownBitmap = unknown.ToBitmap();
        using var zeroBitmap = zero.ToBitmap();
        var difference = 0;
        for (var y = 0; y < Math.Min(unknownBitmap.Height, zeroBitmap.Height); y++)
        for (var x = 0; x < Math.Min(unknownBitmap.Width, zeroBitmap.Width); x++)
            if (Distance(unknownBitmap.GetPixel(x, y), zeroBitmap.GetPixel(x, y)) > 20) difference++;
        if (difference < 8)
            throw new InvalidOperationException("Unknown tray value rendered like zero.");
    }

    private static void ExportContactSheet(string directory)
    {
        Directory.CreateDirectory(directory);
        var samples = new (string Label, CodexQuotaSnapshot Snapshot, bool Awaiting)[]
        {
            ("0", Snapshot(CodexQuotaStatus.Available, 0), false),
            ("9", Snapshot(CodexQuotaStatus.Available, 9), false),
            ("63", Snapshot(CodexQuotaStatus.Available, 63), false),
            ("99", Snapshot(CodexQuotaStatus.Available, 99), false),
            ("100", Snapshot(CodexQuotaStatus.Available, 100), false),
            ("Stale", Snapshot(CodexQuotaStatus.Stale, 63), false),
            ("Unknown", Snapshot(CodexQuotaStatus.Unavailable, null), false),
            ("Waiting", Snapshot(CodexQuotaStatus.Unavailable, null, "claude-connected-waiting", UsageProviderId.Claude), true)
        };
        using var sheet = new Bitmap(576, 664);
        using var graphics = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.Clear(Color.FromArgb(27, 31, 39));
        for (var i = 0; i < samples.Length; i++)
            graphics.DrawString(samples[i].Label, font, Brushes.White, 82 + i * 60, 8);
        var row = 0;
        foreach (var dark in new[] { true, false })
        foreach (var size in new[] { 16, 24, 32 })
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            var y = 32 + row++ * 52;
            using var background = new SolidBrush(dark ? Color.FromArgb(27, 31, 39) : Color.FromArgb(245, 247, 250));
            graphics.FillRectangle(background, 0, y, sheet.Width, 52);
            graphics.DrawString($"{size}px {(style == TrayIconStyle.ProgressRing ? "ring" : "number")}", font,
                dark ? Brushes.White : Brushes.Black, 6, y + 20);
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                using var icon = TrayIconRenderer.Render(sample.Snapshot, style, size, sample.Awaiting);
                using var bitmap = icon.ToBitmap();
                graphics.DrawImageUnscaled(bitmap, 82 + i * 60 + (32 - size) / 2, y + (52 - size) / 2);
            }
        }
        sheet.Save(Path.Combine(directory, "tray-icons.png"), System.Drawing.Imaging.ImageFormat.Png);

        // Render the affected production settings hint, including its compact scroll layout.
        var applyTheme = typeof(App).GetMethod("ApplyTheme", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            foreach (var compact in new[] { false, true })
            {
                var settings = new SettingsWindow(new AppSettings { UiLanguage = language, Theme = theme });
                try
                {
                    AccountUiChecks.Render(settings, compact ? 470 : 610, compact ? 400 : 580,
                        Path.Combine(directory, $"tray-settings-{language}-{theme}-{(compact ? "compact" : "normal")}.png"));
                    var scroll = (System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)settings.FindName("GeneralTab")).Content;
                    scroll.ScrollToEnd();
                    ((System.Windows.FrameworkElement)settings.Content).UpdateLayout();
                    var hint = (System.Windows.Controls.TextBlock)settings.FindName("TrayHint");
                    var hintBounds = hint.TransformToAncestor(scroll).TransformBounds(new System.Windows.Rect(hint.RenderSize));
                    if (hintBounds.Top < -1 || hintBounds.Bottom > scroll.ActualHeight + 1)
                        throw new InvalidOperationException($"Tray legend is clipped after scrolling: {language}/{theme}/{compact}.");
                    AccountUiChecks.Render(settings, compact ? 470 : 610, compact ? 400 : 580,
                        Path.Combine(directory, $"tray-settings-{language}-{theme}-{(compact ? "compact" : "normal")}-scrolled.png"));
                }
                finally { settings.Close(); }
            }
        }
    }
    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
