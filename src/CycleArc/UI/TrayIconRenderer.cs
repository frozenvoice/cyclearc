using CycleArc.Codex;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using DrawingColor = System.Drawing.Color;
using Pen = System.Drawing.Pen;
using SolidBrush = System.Drawing.SolidBrush;

namespace CycleArc.UI;

/// <summary>
/// Every input that decides the drawn icon. Equal views draw identical pixels, so a caller may
/// keep its current icon for an equal view while still updating the tooltip and account.
/// </summary>
public readonly record struct TrayIconView(TrayIconStyle Style, int Size, string Text, bool Exact,
    float SweepDegrees, int FillArgb, bool LightTaskbar);

public static class TrayIconRenderer
{
    public static Icon Render(CodexQuotaSnapshot snapshot, TrayIconStyle style, int size, bool claudeAwaitingUsage = false, bool lightTaskbar = false, UsagePeriodPreference preference = UsagePeriodPreference.Auto) =>
        Render(Describe(snapshot, style, size, claudeAwaitingUsage, lightTaskbar, preference));

    public static TrayIconView Describe(CodexQuotaSnapshot snapshot, TrayIconStyle style, int size, bool claudeAwaitingUsage = false, bool lightTaskbar = false, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        size = Math.Max(8, size);
        var ring = CodexRingPresentation.From(snapshot, preference);
        var exact = ring.IsAvailable;
        var ratio = (ring.UsedPercent ?? 0) / 100;
        var palette = Palette(snapshot, exact, ring.Band, claudeAwaitingUsage);
        var text = exact ? CodexDisplayFormatting.PercentText(ring.UsedPercent, snapshot.Provider).TrimEnd('%') : "?";
        // Keep Cursor's tiny tray glyph to whole digits; the source value, arc and
        // detailed views retain their precision. Match Codex's whole-percent rounding.
        if (snapshot.Provider == UsageProviderId.Cursor && ring.UsedPercent is { } cursorUsed)
            text = Math.Round(cursorUsed, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        // The left number complements the whole-percent usage digits, so 67 used reads 33 left.
        if (style == TrayIconStyle.LeftNumber && exact && ring.UsedPercent is { } used)
            text = (100 - Math.Round(Math.Clamp(used, 0, 100), MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        return new(style, size, text, exact, (float)(360 * ratio), palette.Fill.ToArgb(), lightTaskbar);
    }

    public static Icon Render(TrayIconView view)
    {
        var size = view.Size;
        var text = view.Text;
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // ClearType assumes an opaque desktop background and produces colored fringes
        // after Windows scales the native notification icon. Grayscale AA keeps the
        // small glyph crisp on both light and dark taskbars.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(DrawingColor.Transparent);

        if (view.Style == TrayIconStyle.ProgressRing)
        {
            // An opaque center keeps the number legible on light and dark Windows taskbars.
            using var center = new SolidBrush(DrawingColor.FromArgb(27, 31, 39));
            graphics.FillEllipse(center, 1, 1, size - 2, size - 2);
            using var bg = new Pen(DrawingColor.FromArgb(60, 255, 255, 255), Math.Max(2f, size / 8f));
            using var fg = new Pen(DrawingColor.FromArgb(view.FillArgb), Math.Max(2f, size / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var pad = size / 8f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            graphics.DrawArc(bg, rect, -90, 360);
            if (view.Exact)
            {
                graphics.DrawArc(fg, rect, -90, view.SweepDegrees);
            }


            DrawGlyph(graphics, text, size, DrawingColor.White, ringStyle: true);
        }
        else
        {
            var foreground = view.LightTaskbar ? DrawingColor.FromArgb(24, 24, 24) : DrawingColor.White;
            DrawGlyph(graphics, text, size, foreground, ringStyle: false);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static IconPalette Palette(CodexQuotaSnapshot snapshot, bool exact, UsageRingBand band, bool claudeAwaitingUsage)
    {
        // A connected Claude profile without a received sample is a neutral state;
        // it must not look like a usage failure and must not add to attention totals.
        if (claudeAwaitingUsage)
        {
            return new IconPalette(
                DrawingColor.FromArgb(107, 114, 128),
                DrawingColor.White);
        }

        if (exact && snapshot.Status is (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing))
        {
            // The ring sits on the icon's fixed dark center, so one set serves both taskbars.
            // Amber and orange match the dark theme's ring resources.
            return new IconPalette(BandColor(band), DrawingColor.White);
        }

        return new IconPalette(
            DrawingColor.FromArgb(251, 191, 36),
            DrawingColor.FromArgb(23, 27, 34));
    }

    public static DrawingColor BandColor(UsageRingBand band) => band switch
    {
        UsageRingBand.Caution => DrawingColor.FromArgb(245, 158, 11),
        UsageRingBand.NearLimit => DrawingColor.FromArgb(234, 88, 12),
        UsageRingBand.Exhausted => DrawingColor.FromArgb(220, 38, 38),
        _ => DrawingColor.FromArgb(37, 99, 235)
    };

    private static void DrawGlyph(Graphics graphics, string text, int size, DrawingColor color, bool ringStyle)
    {
        using var family = new System.Drawing.FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        var emSize = size * (ringStyle ? 0.86f : 1.08f);
        path.AddString(text, family, (int)System.Drawing.FontStyle.Bold, emSize, PointF.Empty, StringFormat.GenericTypographic);
        FillGlyphPath(graphics, path, size, color,
            ringStyle ? size * 0.62f : size - 1f, ringStyle ? size * 0.52f : size - 1f);
    }

    private static void FillGlyphPath(Graphics graphics, GraphicsPath path, int size, DrawingColor color,
        float maxWidth, float maxHeight)
    {
        var ink = path.GetBounds();
        // Preserve the font proportions instead of squeezing the digits horizontally.
        var scale = Math.Min(maxWidth / Math.Max(ink.Width, 0.01f), maxHeight / Math.Max(ink.Height, 0.01f));
        var x = (size - ink.Width * scale) / 2f - ink.X * scale;
        var y = (size - ink.Height * scale) / 2f - ink.Y * scale;
        using var brush = new SolidBrush(color);
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(x, y);
            graphics.ScaleTransform(scale, scale);
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private readonly record struct IconPalette(DrawingColor Fill, DrawingColor Glyph);
}
