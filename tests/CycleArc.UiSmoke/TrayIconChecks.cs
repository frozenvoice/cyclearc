using System.Drawing;
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
                foreach (var lightTaskbar in style == TrayIconStyle.RemainingNumber ? new[] { false, true } : new[] { false })
                {
                    using var icon = TrayIconRenderer.Render(
                        snapshot, style, size, awaiting, lightTaskbar: lightTaskbar);
                    using var bitmap = icon.ToBitmap();
                    var themeLabel = lightTaskbar ? "light" : "dark";
                    CheckBitmap(bitmap, size, style, $"{name}/{themeLabel}");
                    if (style == TrayIconStyle.RemainingNumber)
                    {
                        var foreground = lightTaskbar ? Color.FromArgb(24, 24, 24) : Color.White;
                        CheckMonochromeNumber(bitmap, foreground, name, size);
                        if (CodexRingPresentation.From(snapshot).IsAvailable)
                            CheckPercentageInk(bitmap, foreground, name, size);
                    }
                    else if (CodexRingPresentation.From(snapshot).IsAvailable)
                    {
                        CheckColor(bitmap, expected, name, size);
                    }
                    count++;
                }

                foreach (var value in values)
                foreach (var lightTaskbar in style == TrayIconStyle.RemainingNumber ? new[] { false, true } : new[] { false })
                {
                    using var icon = TrayIconRenderer.Render(
                        Snapshot(CodexQuotaStatus.Available, value), style, size, lightTaskbar: lightTaskbar);
                    using var bitmap = icon.ToBitmap();
                    var themeLabel = lightTaskbar ? "light" : "dark";
                    CheckBitmap(bitmap, size, style, $"{value:0}/{themeLabel}");
                    if (style == TrayIconStyle.RemainingNumber)
                    {
                        var foreground = lightTaskbar ? Color.FromArgb(24, 24, 24) : Color.White;
                        CheckMonochromeNumber(bitmap, foreground, value.ToString("0"), size);
                        CheckPercentageInk(bitmap, foreground, value.ToString("0"), size);
                    }
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
        var minimumHeight = bitmap.Height / 2;
        var minimumWidth = style == TrayIconStyle.RemainingNumber ? bitmap.Width / 3 : bitmap.Width / 2;
        if (maxX - minX + 1 < minimumWidth || maxY - minY + 1 < minimumHeight)
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

        if (matches < Math.Max(3, size / 2))
            throw new InvalidOperationException($"Tray icon state color is missing: {label}/{size}.");
    }

    private static void CheckMonochromeNumber(Bitmap bitmap, Color expected, string label, int size)
    {
        var visible = 0;
        var opaque = 0;
        var matching = 0;
        var nonMonochrome = 0;
        foreach (var y in Enumerable.Range(0, bitmap.Height))
        foreach (var x in Enumerable.Range(0, bitmap.Width))
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A == 0) continue;
            visible++;
            if (pixel.A >= 220) opaque++;
            if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > 8)
                nonMonochrome++;
            if (MatchesForeground(pixel, expected))
                matching++;
        }

        if (visible == 0 || matching < Math.Max(3, size / 2))
            throw new InvalidOperationException($"Tray number foreground is missing: {label}/{size} (visible={visible}, matching={matching}, opaque={opaque}, nonMonochrome={nonMonochrome}).");
        if (nonMonochrome > Math.Max(1, visible / 25))
            throw new InvalidOperationException($"Tray number contains colored pixels: {label}/{size}.");
        if (opaque > bitmap.Width * bitmap.Height / 2)
            throw new InvalidOperationException($"Tray number has an opaque backing: {label}/{size}.");
    }

    private static void CheckPercentageInk(Bitmap bitmap, Color expected, string label, int size)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (!IsForeground(bitmap.GetPixel(x, y), expected)) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (maxX < minX || maxY < minY)
            throw new InvalidOperationException($"Tray number has no measurable ink: {label}/{size}.");

        var glyphWidth = maxX - minX + 1;
        var bandStart = Math.Max(minX + 1, maxX - Math.Max(2, (int)Math.Ceiling(size * 0.2)));
        var rightInk = 0;
        var lowerRightInk = 0;
        var rightMinY = bitmap.Height;
        var rightMaxY = -1;
        var lowerEdge = minY + (maxY - minY) * 0.45;
        for (var y = minY; y <= maxY; y++)
        for (var x = bandStart; x <= maxX; x++)
        {
            if (!IsForeground(bitmap.GetPixel(x, y), expected)) continue;
            rightInk++;
            rightMinY = Math.Min(rightMinY, y);
            rightMaxY = Math.Max(rightMaxY, y);
            if (y >= lowerEdge) lowerRightInk++;
        }

        var rightHeight = rightMaxY - rightMinY + 1;
        if (glyphWidth < Math.Max(4, (int)Math.Ceiling(size * 0.52))
            || rightInk < Math.Max(3, size / 5)
            || rightHeight < Math.Max(2, (int)Math.Ceiling(size * 0.15))
            || lowerRightInk < Math.Max(1, size / 10))
            throw new InvalidOperationException($"Tray number has no visible right-side percent mark: {label}/{size} (width={glyphWidth}, rightInk={rightInk}, rightHeight={rightHeight}, lower={lowerRightInk}).");
    }

    private static bool MatchesForeground(Color pixel, Color expected)
    {
        if (pixel.A < 80) return false;
        if (expected == Color.White)
        {
            var normalized = pixel.R * 255 / pixel.A;
            return normalized >= 170 && Math.Abs(pixel.R - pixel.G) <= 8 && Math.Abs(pixel.R - pixel.B) <= 8;
        }

        return pixel.R <= 100 && Math.Abs(pixel.R - pixel.G) <= 8 && Math.Abs(pixel.R - pixel.B) <= 8;
    }

    private static bool IsForeground(Color pixel, Color expected) =>
        pixel.A >= 32
            && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) <= 8;

    private static void CheckUnknownIsNotZero()
    {
        using var unknown = TrayIconRenderer.Render(Snapshot(CodexQuotaStatus.Unavailable, null), TrayIconStyle.RemainingNumber, 32, lightTaskbar: false);
        using var zero = TrayIconRenderer.Render(Snapshot(CodexQuotaStatus.Stale, 0), TrayIconStyle.RemainingNumber, 32, lightTaskbar: false);
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
                using var icon = TrayIconRenderer.Render(sample.Snapshot, style, size, sample.Awaiting, lightTaskbar: !dark);
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
