using CycleArc.Codex;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using DrawingColor = System.Drawing.Color;
using Matrix = System.Drawing.Drawing2D.Matrix;
using Pen = System.Drawing.Pen;
using SolidBrush = System.Drawing.SolidBrush;

namespace CycleArc.UI;

public static class TrayIconRenderer
{
    public static Icon Render(CodexQuotaSnapshot snapshot, TrayIconStyle style, int size, bool claudeAwaitingUsage = false, bool lightTaskbar = false)
    {
        size = Math.Max(8, size);
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // ClearType assumes an opaque desktop background and produces colored fringes
        // after Windows scales the native notification icon. Grayscale AA keeps the
        // small glyph crisp on both light and dark taskbars.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(DrawingColor.Transparent);

        var ring = CodexRingPresentation.From(snapshot);
        var exact = ring.IsAvailable;
        var ratio = (ring.UsedPercent ?? 0) / 100;
        var palette = Palette(snapshot, exact, ring.IsDangerLevel, claudeAwaitingUsage);
        var text = exact ? CodexDisplayFormatting.PercentText(ring.UsedPercent).TrimEnd('%') : "?";

        if (style == TrayIconStyle.ProgressRing)
        {
            // An opaque center keeps the number legible on light and dark Windows taskbars.
            using var center = new SolidBrush(DrawingColor.FromArgb(27, 31, 39));
            graphics.FillEllipse(center, 1, 1, size - 2, size - 2);
            using var bg = new Pen(DrawingColor.FromArgb(60, 255, 255, 255), Math.Max(2f, size / 8f));
            using var fg = new Pen(palette.Fill, Math.Max(2f, size / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var pad = size / 8f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            graphics.DrawArc(bg, rect, -90, 360);
            if (exact)
            {
                graphics.DrawArc(fg, rect, -90, (float)(360 * ratio));
            }


            DrawGlyph(graphics, text, size, DrawingColor.White, ringStyle: true);
        }
        else
        {
            var foreground = lightTaskbar ? DrawingColor.FromArgb(24, 24, 24) : DrawingColor.White;
            if (exact)
                DrawPercentage(graphics, text, size, foreground);
            else
                DrawGlyph(graphics, "?", size, foreground, ringStyle: false);
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

    private static IconPalette Palette(CodexQuotaSnapshot snapshot, bool exact, bool danger, bool claudeAwaitingUsage)
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
            return danger
                ? new IconPalette(DrawingColor.FromArgb(220, 38, 38), DrawingColor.White)
                : new IconPalette(DrawingColor.FromArgb(37, 99, 235), DrawingColor.White);
        }

        return new IconPalette(
            DrawingColor.FromArgb(251, 191, 36),
            DrawingColor.FromArgb(23, 27, 34));
    }

    private static void DrawPercentage(Graphics graphics, string digits, int size, DrawingColor color)
    {
        using var family = new System.Drawing.FontFamily("Segoe UI");
        using var number = new GraphicsPath();
        number.AddString(digits, family, (int)System.Drawing.FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        var numberInk = number.GetBounds();
        using (var normalize = new Matrix(1, 0, 0, 1, -numberInk.X, -numberInk.Y))
            number.Transform(normalize);

        // Keep the value prominent while fitting its unit into the native square slot.
        using var unit = new GraphicsPath();
        unit.AddString("%", family, (int)System.Drawing.FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        var unitInk = unit.GetBounds();
        var unitScale = numberInk.Height * 0.58f / unitInk.Height;
        using (var placement = new Matrix(unitScale, 0, 0, unitScale,
            numberInk.Width + numberInk.Height * 0.06f - unitInk.X * unitScale,
            numberInk.Height - unitInk.Height * unitScale - unitInk.Y * unitScale))
            unit.Transform(placement);
        number.AddPath(unit, false);
        FillGlyphPath(graphics, number, size, color, size - 1f, size * 0.70f);
    }

    private static void DrawGlyph(Graphics graphics, string text, int size, DrawingColor color, bool ringStyle)
    {
        using var family = new System.Drawing.FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        var emSize = size * (ringStyle ? 0.86f : 1.08f);
        path.AddString(text, family, (int)System.Drawing.FontStyle.Bold, emSize, PointF.Empty, StringFormat.GenericTypographic);
        FillGlyphPath(graphics, path, size, color,
            size * (ringStyle ? 0.62f : 0.86f), size * (ringStyle ? 0.52f : 0.78f));
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
