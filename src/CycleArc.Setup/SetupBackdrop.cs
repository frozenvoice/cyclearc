namespace CycleArc.Setup;

/// <summary>
/// The window's background, rendered once per size and DPI: a charcoal surface with three
/// soft light pools rising from the bottom edge - amber on the left, red-magenta in the
/// middle and teal-blue on the right. They stay low and dim so nothing above them competes
/// with the text. High contrast gets the plain system window colour instead.
/// </summary>
internal sealed class SetupBackdrop
{
    private readonly uint[] _pixels;

    public int Width { get; }
    public int Height { get; }

    public SetupBackdrop(int width, int height, SetupPalette palette, bool drawBorder)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _pixels = new uint[Width * Height];

        var glows = palette.HighContrast
            ? Array.Empty<Glow>()
            : new[]
            {
                // Centre x, centre y below the window, horizontal and vertical radius - all as
                // fractions of the window - then colour and strength.
                new Glow(0.15f, 1.10f, 0.42f, 0.62f, Rgb.Hex(0xE79B3A), 0.30f),
                new Glow(0.50f, 1.16f, 0.38f, 0.60f, Rgb.Hex(0xD9346C), 0.26f),
                new Glow(0.86f, 1.10f, 0.42f, 0.62f, Rgb.Hex(0x1FB3D6), 0.30f),
            };

        for (var y = 0; y < Height; y++)
        {
            var fy = (y + 0.5f) / Height;
            var surface = Rgb.Lerp(palette.Surface, palette.SurfaceBottom, fy);
            for (var x = 0; x < Width; x++)
            {
                var fx = (x + 0.5f) / Width;
                float r = surface.R, g = surface.G, b = surface.B;
                foreach (var glow in glows)
                {
                    var dx = (fx - glow.X) / glow.RadiusX;
                    var dy = (fy - glow.Y) / glow.RadiusY;
                    var strength = MathF.Exp(-2.4f * (dx * dx + dy * dy)) * glow.Strength;
                    if (strength < 0.0005f) continue;
                    // Screen blend: light adds without ever clipping to a flat colour.
                    r = 255 - (255 - r) * (1 - strength * glow.Color.R / 255f);
                    g = 255 - (255 - g) * (1 - strength * glow.Color.G / 255f);
                    b = 255 - (255 - b) * (1 - strength * glow.Color.B / 255f);
                }

                // Ordered dither of under one level, so the gradients never band.
                var dither = (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
                _pixels[y * Width + x] = Pack(r + dither, g + dither, b + dither);
            }
        }

        // Windows 11 draws its own rounded border; earlier versions get a faint inner line.
        if (drawBorder && !palette.HighContrast)
        {
            var line = Rgb.Lerp(palette.Surface, palette.Overlay, 0.10f);
            for (var x = 0; x < Width; x++)
            {
                _pixels[x] = Pack(line.R, line.G, line.B);
                _pixels[(Height - 1) * Width + x] = Pack(line.R, line.G, line.B);
            }
            for (var y = 0; y < Height; y++)
            {
                _pixels[y * Width] = Pack(line.R, line.G, line.B);
                _pixels[y * Width + Width - 1] = Pack(line.R, line.G, line.B);
            }
        }
    }

    public ReadOnlySpan<uint> Row(int y) => new(_pixels, y * Width, Width);

    /// <summary>The backdrop's colour at one point, for text faded in over it.</summary>
    public Rgb At(int x, int y)
    {
        var pixel = _pixels[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];
        return new((byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);
    }

    private static uint Pack(float r, float g, float b) =>
        ((uint)Math.Clamp(r + 0.5f, 0, 255) << 16) | ((uint)Math.Clamp(g + 0.5f, 0, 255) << 8) | (uint)Math.Clamp(b + 0.5f, 0, 255);

    private static readonly float[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    private readonly record struct Glow(float X, float Y, float RadiusX, float RadiusY, Rgb Color, float Strength);
}
