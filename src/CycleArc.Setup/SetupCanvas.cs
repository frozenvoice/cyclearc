namespace CycleArc.Setup;

/// <summary>
/// A 32-bit offscreen surface. Shapes are drawn with signed distance fields, so every edge is
/// anti-aliased at any DPI without GDI+ or Direct2D; text is drawn by GDI with ClearType on
/// the same surface. The result is copied to the screen in one blit, so nothing flickers.
///
/// Coordinates are window client pixels. A canvas that only backs one child control keeps
/// that control's position as its origin, so the same drawing code serves the whole window
/// and each control.
/// </summary>
internal sealed unsafe class SetupCanvas : IDisposable
{
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previous;
    private readonly uint* _bits;

    public int Width { get; }
    public int Height { get; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public IntPtr Dc { get; }

    public SetupCanvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            biWidth = Width,
            biHeight = -Height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        Dc = Native.CreateCompatibleDC(IntPtr.Zero);
        _bitmap = Native.CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (Dc == IntPtr.Zero || _bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            if (Dc != IntPtr.Zero) Native.DeleteDC(Dc);
            throw new InvalidOperationException("Could not create a drawing surface.");
        }
        _bits = (uint*)bits;
        _previous = Native.SelectObject(Dc, _bitmap);
        Native.SetBkMode(Dc, Native.TRANSPARENT);
    }

    /// <summary>Raw rows, for the backdrop. GDI must have finished before pixels are touched.</summary>
    public Span<uint> Pixels
    {
        get
        {
            Native.GdiFlush();
            return new Span<uint>(_bits, Width * Height);
        }
    }

    public void CopyTo(IntPtr hdc, int x, int y) =>
        Native.BitBlt(hdc, x, y, Width, Height, Dc, 0, 0, Native.SRCCOPY);

    /// <summary>Fills the canvas with the window backdrop under it.</summary>
    public void Fill(SetupBackdrop backdrop)
    {
        var target = Pixels;
        for (var row = 0; row < Height; row++)
        {
            var sourceY = Math.Clamp(row + OriginY, 0, backdrop.Height - 1);
            var sourceX = Math.Clamp(OriginX, 0, backdrop.Width);
            var count = Math.Clamp(backdrop.Width - sourceX, 0, Width);
            backdrop.Row(sourceY).Slice(sourceX, count).CopyTo(target.Slice(row * Width, count));
        }
    }

    // --- Shapes --------------------------------------------------------------------------

    public void FillRoundRect(float x, float y, float width, float height, float radius, Rgb color, float alpha = 1f) =>
        Shade(x, y, width, height, color, alpha, (px, py) =>
            Coverage(RoundRect(px, py, x, y, width, height, radius)));

    /// <summary>A stroke that lies inside the rectangle, so it never spills past its box.</summary>
    public void StrokeRoundRect(float x, float y, float width, float height, float radius, float thickness,
        Rgb color, float alpha = 1f) =>
        Shade(x, y, width, height, color, alpha, (px, py) =>
        {
            var d = RoundRect(px, py, x, y, width, height, radius);
            return Coverage(d) - Coverage(d + thickness);
        });

    public void FillCircle(float cx, float cy, float radius, Rgb color, float alpha = 1f) =>
        FillRoundRect(cx - radius, cy - radius, radius * 2, radius * 2, radius, color, alpha);

    public void StrokeCircle(float cx, float cy, float radius, float thickness, Rgb color, float alpha = 1f) =>
        StrokeRoundRect(cx - radius, cy - radius, radius * 2, radius * 2, radius, thickness, color, alpha);

    /// <summary>A line with round caps.</summary>
    public void Line(float x0, float y0, float x1, float y1, float thickness, Rgb color, float alpha = 1f)
    {
        var half = thickness / 2;
        Shade(MathF.Min(x0, x1) - half, MathF.Min(y0, y1) - half,
            MathF.Abs(x1 - x0) + thickness, MathF.Abs(y1 - y0) + thickness, color, alpha,
            (px, py) => Coverage(Segment(px, py, x0, y0, x1, y1) - half));
    }

    /// <summary>
    /// A rounded bar whose opacity is shaped along its length, for the progress indicator.
    /// <paramref name="opacity"/> receives the pixel's x in window coordinates.
    /// </summary>
    public void FillBar(float x, float y, float width, float height, Rgb color, float alpha, Func<float, float> opacity) =>
        Shade(x, y, width, height, color, alpha, (px, py) =>
            Coverage(RoundRect(px, py, x, y, width, height, height / 2)) * opacity(px));

    /// <summary>
    /// Any shape: <paramref name="shade"/> returns the coverage of one pixel, given its centre
    /// in window coordinates. Only the shape's bounding box, clipped to the canvas, is visited.
    /// </summary>
    public void Shade(float x, float y, float width, float height, Rgb color, float alpha, Func<float, float, float> shade)
    {
        if (alpha <= 0) return;
        var left = Math.Max(0, (int)MathF.Floor(x - 1) - OriginX);
        var top = Math.Max(0, (int)MathF.Floor(y - 1) - OriginY);
        var right = Math.Min(Width, (int)MathF.Ceiling(x + width + 1) - OriginX);
        var bottom = Math.Min(Height, (int)MathF.Ceiling(y + height + 1) - OriginY);
        if (left >= right || top >= bottom) return;
        var pixels = Pixels;
        for (var row = top; row < bottom; row++)
        {
            var py = row + OriginY + 0.5f;
            var line = pixels.Slice(row * Width, Width);
            for (var column = left; column < right; column++)
            {
                var amount = shade(column + OriginX + 0.5f, py) * alpha;
                if (amount <= 0.002f) continue;
                line[column] = Blend(line[column], color, MathF.Min(amount, 1f));
            }
        }
    }

    /// <summary>The CycleArc mark from Assets/cyclearc.svg, drawn as vectors at any size.</summary>
    public void Logo(float x, float y, float size, float alpha = 1f)
    {
        var scale = size / 256f;
        var tileTop = Rgb.Hex(0x1D3252);
        var tileBottom = Rgb.Hex(0x0B1425);
        var arcStops = new[] { Rgb.Hex(0x65F0CF), Rgb.Hex(0x38BDF8), Rgb.Hex(0x4B7BFF) };
        var dot = Rgb.Hex(0xD9FFF5);
        // The arc leaves a gap of +-35 degrees around the dot on its right.
        var gap = MathF.Atan2(40.15f, 57.34f);
        var endX = 128 + 70 * MathF.Cos(gap);
        var endY = 70 * MathF.Sin(gap);

        var left = Math.Max(0, (int)MathF.Floor(x) - OriginX);
        var top = Math.Max(0, (int)MathF.Floor(y) - OriginY);
        var right = Math.Min(Width, (int)MathF.Ceiling(x + size) - OriginX);
        var bottom = Math.Min(Height, (int)MathF.Ceiling(y + size) - OriginY);
        var pixels = Pixels;
        for (var row = top; row < bottom; row++)
        {
            var line = pixels.Slice(row * Width, Width);
            for (var column = left; column < right; column++)
            {
                var u = (column + OriginX + 0.5f - x) / scale;
                var v = (row + OriginY + 0.5f - y) / scale;

                var tile = Coverage(RoundRect(u, v, 8, 8, 240, 240, 56) * scale) * alpha;
                if (tile <= 0) continue;
                var pixel = Blend(line[column], Rgb.Lerp(tileTop, tileBottom, (u + v - 16) / 480f), tile);

                var dx = u - 128;
                var dy = v - 128;
                var angle = MathF.Atan2(dy, dx);
                var arc = MathF.Abs(angle) > gap
                    ? MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - 70)
                    : MathF.Min(Distance(u, v, endX, 128 - endY), Distance(u, v, endX, 128 + endY));
                var arcCoverage = Coverage((arc - 12) * scale) * alpha;
                if (arcCoverage > 0)
                {
                    // objectBoundingBox gradient from (0,0) towards (0.65,1) of the arc's box.
                    var bx = (u - 58) / 127.34f;
                    var by = (v - 58) / 140f;
                    var t = Math.Clamp((bx * 0.65f + by) / (0.65f * 0.65f + 1f), 0f, 1f);
                    var color = t < 0.48f
                        ? Rgb.Lerp(arcStops[0], arcStops[1], t / 0.48f)
                        : Rgb.Lerp(arcStops[1], arcStops[2], (t - 0.48f) / 0.52f);
                    pixel = Blend(pixel, color, arcCoverage);
                }

                var dotCoverage = Coverage((Distance(u, v, 201, 128) - 12) * scale) * alpha;
                if (dotCoverage > 0) pixel = Blend(pixel, dot, dotCoverage);
                line[column] = pixel;
            }
        }
    }

    // --- Text ----------------------------------------------------------------------------

    public void Text(string text, float x, float y, float width, float height, IntPtr font, Rgb color, uint format)
    {
        if (string.IsNullOrEmpty(text)) return;
        var rect = new Native.RECT
        {
            Left = (int)MathF.Round(x) - OriginX,
            Top = (int)MathF.Round(y) - OriginY,
            Right = (int)MathF.Round(x + width) - OriginX,
            Bottom = (int)MathF.Round(y + height) - OriginY,
        };
        var previous = Native.SelectObject(Dc, font);
        Native.SetTextColor(Dc, color.ColorRef);
        Native.DrawText(Dc, text, -1, ref rect, format | Native.DT_NOPREFIX);
        Native.SelectObject(Dc, previous);
    }

    /// <summary>
    /// Breaks text into lines only at spaces. GDI's word break splits Hangul between any two
    /// syllables, which leaves a word's last syllable alone on the next line.
    /// </summary>
    public string Wrap(string text, IntPtr font, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(candidate, font, int.MaxValue / 2, Native.DT_SINGLELINE).Width > width)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0) lines.Add(line);
        return string.Join("\n", lines);
    }

    /// <summary>The size <paramref name="text"/> needs, wrapped to <paramref name="width"/> when it wraps.</summary>
    public (int Width, int Height) Measure(string text, IntPtr font, int width, uint format)
    {
        var rect = new Native.RECT { Right = Math.Max(1, width) };
        var previous = Native.SelectObject(Dc, font);
        Native.DrawText(Dc, string.IsNullOrEmpty(text) ? " " : text, -1, ref rect,
            format | Native.DT_CALCRECT | Native.DT_NOPREFIX);
        Native.SelectObject(Dc, previous);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    // --- Geometry ------------------------------------------------------------------------

    private static float Coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);

    private static float Distance(float x0, float y0, float x1, float y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float RoundRect(float px, float py, float x, float y, float width, float height, float radius)
    {
        radius = MathF.Min(radius, MathF.Min(width, height) / 2);
        var qx = MathF.Abs(px - (x + width / 2)) - width / 2 + radius;
        var qy = MathF.Abs(py - (y + height / 2)) - height / 2 + radius;
        var ox = MathF.Max(qx, 0);
        var oy = MathF.Max(qy, 0);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0) - radius;
    }

    private static float Segment(float px, float py, float x0, float y0, float x1, float y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / lengthSquared, 0f, 1f);
        return Distance(px, py, x0 + t * dx, y0 + t * dy);
    }

    private static uint Blend(uint destination, Rgb color, float amount)
    {
        var r = (destination >> 16) & 0xFF;
        var g = (destination >> 8) & 0xFF;
        var b = destination & 0xFF;
        r = (uint)(r + (color.R - (int)r) * amount + 0.5f);
        g = (uint)(g + (color.G - (int)g) * amount + 0.5f);
        b = (uint)(b + (color.B - (int)b) * amount + 0.5f);
        return (r << 16) | (g << 8) | b;
    }

    public void Dispose()
    {
        Native.SelectObject(Dc, _previous);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(Dc);
    }
}
