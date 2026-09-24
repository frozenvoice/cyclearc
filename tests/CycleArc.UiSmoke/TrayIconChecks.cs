using System.Drawing;
using System.Globalization;
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
            ("caution", Snapshot(CodexQuotaStatus.Available, 70), Color.FromArgb(245, 158, 11), false),
            ("caution-edge", Snapshot(CodexQuotaStatus.Available, 84.99), Color.FromArgb(245, 158, 11), false),
            ("near-limit", Snapshot(CodexQuotaStatus.Available, 85), Color.FromArgb(234, 88, 12), false),
            ("near-limit-rounded", Snapshot(CodexQuotaStatus.Available, 99.6), Color.FromArgb(234, 88, 12), false),
            ("normal-edge", Snapshot(CodexQuotaStatus.Available, 69.99), Color.FromArgb(37, 99, 235), false),
            ("stale-near-limit", Snapshot(CodexQuotaStatus.Stale, 90, "codex-refresh-failed"), Color.FromArgb(251, 191, 36), false),
            ("claude-near-limit", Snapshot(CodexQuotaStatus.Available, 88, "claude-live", UsageProviderId.Claude),
                Color.FromArgb(234, 88, 12), false),
            ("cursor-caution", Snapshot(CodexQuotaStatus.Available, 72, null, UsageProviderId.Cursor),
                Color.FromArgb(245, 158, 11), false),
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
                    }
                    count++;
                }
            }
        }

        CheckUnknownIsNotZero();
        CheckRoundedNearLimitIsNotExhausted();
        CheckNaturalProportions();
        count += CheckCursorPixelRegression();
        CheckCursorProjectionText();
        if (directory is not null)
        {
            ExportContactSheet(directory);
            ExportCursorContactSheet(directory);
        }
        Console.WriteLine($"PASS: {count} tray icon renders across sizes, styles, values and states.");
    }

    private static CodexQuotaSnapshot Snapshot(
        CodexQuotaStatus status,
        double? used,
        string? detail = null,
        UsageProviderId provider = UsageProviderId.Codex)
    {
        var window = provider == UsageProviderId.Cursor
            ? new CodexQuotaWindow("cursor-auto", used, null, null, CodexWindowKind.Other)
            : new CodexQuotaWindow("smoke", used, 10_080, null, CodexWindowKind.Weekly);
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
        // Three natural-width digits use less height than one or two digits.
        var minimumHeight = style == TrayIconStyle.RemainingNumber
            ? Math.Max(5, (int)Math.Ceiling(bitmap.Height * 0.40)) : bitmap.Height / 2;
        var minimumWidth = style == TrayIconStyle.RemainingNumber ? bitmap.Width / 3 : bitmap.Width / 2;
        if (maxX - minX + 1 < minimumWidth || maxY - minY + 1 < minimumHeight)
            throw new InvalidOperationException($"Tray icon bounds are too small: {style}/{label}/{requestedSize}.");
        if (bitmap.GetPixel(0, 0).A != 0 && style == TrayIconStyle.RemainingNumber)
            throw new InvalidOperationException($"Number icon lost transparent corner: {label}/{requestedSize}.");
        if (style == TrayIconStyle.RemainingNumber)
        {
            // A half-pixel inset allows the antialiased edge, but not a solid clipped stroke.
            for (var i = 0; i < requestedSize; i++)
            {
                if (bitmap.GetPixel(i, 0).A > 160 || bitmap.GetPixel(i, requestedSize - 1).A > 160
                    || bitmap.GetPixel(0, i).A > 160 || bitmap.GetPixel(requestedSize - 1, i).A > 160)
                    throw new InvalidOperationException($"Tray digit is clipped at the icon edge: {label}/{requestedSize}.");
            }
        }
    }

    private static void CheckRoundedNearLimitIsNotExhausted()
    {
        // Codex's tray glyph rounds 99.6 to 100; the ring stays orange rather than red.
        var snapshot = Snapshot(CodexQuotaStatus.Available, 99.6);
        if (CodexDisplayFormatting.PercentText(99.6) != "100%" || CodexRingPresentation.From(snapshot).IsDangerLevel)
            throw new InvalidOperationException("Rounded 99.6% fixture changed meaning.");
        foreach (var size in new[] { 16, 24, 32 })
        {
            using var icon = TrayIconRenderer.Render(snapshot, TrayIconStyle.ProgressRing, size);
            using var bitmap = icon.ToBitmap();
            var red = 0;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) is { A: > 180 } pixel && Distance(pixel, Color.FromArgb(220, 38, 38)) <= 8) red++;
            if (red > 0) throw new InvalidOperationException($"Rounded 99.6% tray ring is red at {size}px.");
        }
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

    private static void CheckNaturalProportions()
    {
        // Bounds catch squeezed digits or a percent suffix taking their space again.
        foreach (var (value, minimum, maximum) in new[] { (0d, 0.55, 0.85), (9d, 0.55, 0.85), (70d, 1.3, 1.65), (99d, 1.3, 1.65), (100d, 1.95, 2.4) })
        {
            using var icon = TrayIconRenderer.Render(
                Snapshot(CodexQuotaStatus.Available, value), TrayIconStyle.RemainingNumber, 32);
            using var bitmap = icon.ToBitmap();
            var left = bitmap.Width;
            var top = bitmap.Height;
            var right = -1;
            var bottom = -1;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A < 32) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            var aspect = (right - left + 1d) / (bottom - top + 1d);
            if (right < left || aspect < minimum || aspect > maximum
                || Math.Max(right - left + 1, bottom - top + 1) < bitmap.Width - 2)
                throw new InvalidOperationException($"Tray digits are undersized or distorted: {value} ({aspect:0.00}).");
        }
    }

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

    private static int CheckCursorPixelRegression()
    {
        var count = 0;
        var values = new[] { 76.4, 76.5, 76.9, 0d, 99.6, 100d };
        foreach (var size in new[] { 16, 24, 32 })
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        foreach (var value in values)
        foreach (var lightTaskbar in new[] { false, true })
        {
            using var cursor = TrayIconRenderer.Render(
                Snapshot(CodexQuotaStatus.Available, value, provider: UsageProviderId.Cursor),
                style, size, lightTaskbar: lightTaskbar);
            using var cursorBitmap = cursor.ToBitmap();
            CheckBitmap(cursorBitmap, size, style, $"cursor/{value:0.0}/{(lightTaskbar ? "light" : "dark")}");
            using var codexFractional = TrayIconRenderer.Render(
                Snapshot(CodexQuotaStatus.Available, value), style, size, lightTaskbar: lightTaskbar);
            using var codexFractionalBitmap = codexFractional.ToBitmap();
            CheckEquivalent(cursorBitmap, codexFractionalBitmap,
                $"Cursor {value.ToString("0.0", CultureInfo.InvariantCulture)} changed the shared tray rendering");
            if (style == TrayIconStyle.RemainingNumber)
            {
                var foreground = lightTaskbar ? Color.FromArgb(24, 24, 24) : Color.White;
                CheckMonochromeNumber(cursorBitmap, foreground, $"cursor/{value:0.0}", size);
                var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
                using var codex = TrayIconRenderer.Render(
                    Snapshot(CodexQuotaStatus.Available, rounded), style, size, lightTaskbar: lightTaskbar);
                using var codexBitmap = codex.ToBitmap();
                CheckEquivalent(cursorBitmap, codexBitmap,
                    $"Cursor {value.ToString("0.0", CultureInfo.InvariantCulture)} did not use the Codex integer glyph");
            }
            else if (value > 0)
            {
                CheckColor(cursorBitmap, TrayIconRenderer.BandColor(UsageRingBands.From(value)),
                    $"cursor/{value:0.0}", size);
            }
            count++;
        }

        // Unknown remains a question mark, including for Cursor and both taskbar tones.
        foreach (var size in new[] { 16, 24, 32 })
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        foreach (var lightTaskbar in new[] { false, true })
        {
            using var unknown = TrayIconRenderer.Render(
                Snapshot(CodexQuotaStatus.Unavailable, null, provider: UsageProviderId.Cursor),
                style, size, lightTaskbar: lightTaskbar);
            using var unknownBitmap = unknown.ToBitmap();
            using var codexUnknown = TrayIconRenderer.Render(
                Snapshot(CodexQuotaStatus.Unavailable, null), style, size, lightTaskbar: lightTaskbar);
            using var codexUnknownBitmap = codexUnknown.ToBitmap();
            CheckEquivalent(unknownBitmap, codexUnknownBitmap, "Unknown Cursor usage lost its question mark");
            CheckBitmap(unknownBitmap, size, style, $"cursor/unknown/{(lightTaskbar ? "light" : "dark")}");
            count++;
        }

        // The fractional input must still drive the ring arc. Its glyph matches the
        // rounded Codex glyph, but its arc must differ from a true 77% sample.
        using var fractionalRing = TrayIconRenderer.Render(
            Snapshot(CodexQuotaStatus.Available, 76.9, provider: UsageProviderId.Cursor),
            TrayIconStyle.ProgressRing, 32);
        using var roundedRing = TrayIconRenderer.Render(
            Snapshot(CodexQuotaStatus.Available, 77, provider: UsageProviderId.Cursor),
            TrayIconStyle.ProgressRing, 32);
        using var fractionalBitmap = fractionalRing.ToBitmap();
        using var roundedBitmap = roundedRing.ToBitmap();
        if (DifferentPixels(fractionalBitmap, roundedBitmap) < 3)
            throw new InvalidOperationException("Cursor ring lost the fractional usage arc.");
        return count;
    }

    private static void CheckCursorProjectionText()
    {
        var snapshot = Snapshot(CodexQuotaStatus.Available, 76.9, provider: UsageProviderId.Cursor);
        Check(CodexDisplayFormatting.PercentText(76.9, UsageProviderId.Cursor) == "76.9%",
            "Cursor detail formatting rounded away the fractional percentage.");
        Check(CodexRingPresentation.From(snapshot).CenterValueText == "76.9%",
            "Cursor popup ring text rounded away the fractional percentage.");
        Check(CycleArcPresentation.CompactText(snapshot).Contains("76.9%", StringComparison.Ordinal),
            "Cursor compact projection rounded away the fractional percentage.");
        Check(CycleArcPresentation.TrayTooltip(snapshot).Contains("23.1%", StringComparison.Ordinal),
            "Cursor tray tooltip rounded away the fractional remaining percentage.");
        Check(CodexDisplayFormatting.Rows(snapshot).Any(row => row.Value.Contains("23.1%", StringComparison.Ordinal)),
            "Cursor popup rows rounded away the fractional remaining percentage.");
        var account = new CodexAccountView(
            new CodexAccountProfile("cursor-tray-text", "", "Cursor") { Provider = UsageProviderId.Cursor }, snapshot);
        Check(WidgetAccountModel.From(account, selected: false).Ring.CenterValueText == "76.9%",
            "Cursor widget shared ring metadata lost the fractional percentage.");
        Check(WidgetAccountModel.From(account, selected: false).RingValueText == "77%",
            "Cursor widget visible ring text did not use whole percentages.");
    }

    private static void CheckEquivalent(Bitmap actual, Bitmap expected, string message)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height
            || DifferentPixels(actual, expected) > Math.Max(1, actual.Width / 8))
            throw new InvalidOperationException(message + ".");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int DifferentPixels(Bitmap first, Bitmap second)
    {
        var different = 0;
        for (var y = 0; y < Math.Min(first.Height, second.Height); y++)
        for (var x = 0; x < Math.Min(first.Width, second.Width); x++)
            if (Distance(first.GetPixel(x, y), second.GetPixel(x, y)) > 8) different++;
        return different + Math.Abs(first.Width - second.Width) + Math.Abs(first.Height - second.Height);
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

    private static void ExportCursorContactSheet(string directory)
    {
        Directory.CreateDirectory(directory);
        var samples = new (string Label, CodexQuotaSnapshot Snapshot)[]
        {
            ("76.4", Snapshot(CodexQuotaStatus.Available, 76.4, provider: UsageProviderId.Cursor)),
            ("76.5", Snapshot(CodexQuotaStatus.Available, 76.5, provider: UsageProviderId.Cursor)),
            ("76.9", Snapshot(CodexQuotaStatus.Available, 76.9, provider: UsageProviderId.Cursor)),
            ("0", Snapshot(CodexQuotaStatus.Available, 0, provider: UsageProviderId.Cursor)),
            ("100", Snapshot(CodexQuotaStatus.Available, 100, provider: UsageProviderId.Cursor)),
            ("?", Snapshot(CodexQuotaStatus.Unavailable, null, provider: UsageProviderId.Cursor))
        };

        const int labelWidth = 82;
        const int sampleWidth = 60;
        using var sheet = new Bitmap(labelWidth + samples.Length * sampleWidth, 664);
        using var graphics = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.Clear(Color.FromArgb(27, 31, 39));
        for (var i = 0; i < samples.Length; i++)
            graphics.DrawString(samples[i].Label, font, Brushes.White, labelWidth + i * sampleWidth, 8);

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
                using var icon = TrayIconRenderer.Render(samples[i].Snapshot, style, size, lightTaskbar: !dark);
                using var bitmap = icon.ToBitmap();
                graphics.DrawImageUnscaled(bitmap, labelWidth + i * sampleWidth + (32 - size) / 2, y + (52 - size) / 2);
            }
        }

        sheet.Save(Path.Combine(directory, "cursor-tray-icons.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
