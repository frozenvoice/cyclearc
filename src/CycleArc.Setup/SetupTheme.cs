namespace CycleArc.Setup;

/// <summary>An opaque colour. GDI wants it as 0x00BBGGRR, the canvas as 0x00RRGGBB.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Rgb FromColorRef(uint colorRef) => new((byte)colorRef, (byte)(colorRef >> 8), (byte)(colorRef >> 16));

    public uint ColorRef => (uint)(R | (G << 8) | (B << 16));

    public static Rgb Lerp(Rgb from, Rgb to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new(
            (byte)(from.R + (to.R - from.R) * amount + 0.5f),
            (byte)(from.G + (to.G - from.G) * amount + 0.5f),
            (byte)(from.B + (to.B - from.B) * amount + 0.5f));
    }
}

/// <summary>
/// The installer's colours. One dark palette by design; Windows high contrast replaces it
/// with the person's own system colours and drops the decorative glow entirely.
/// </summary>
internal sealed record SetupPalette(
    bool HighContrast,
    Rgb Surface,
    Rgb SurfaceBottom,
    Rgb Text,
    Rgb TextSecondary,
    Rgb TextMuted,
    Rgb Accent,
    Rgb AccentHover,
    Rgb AccentPressed,
    Rgb OnAccent,
    Rgb Overlay,
    Rgb Track,
    Rgb Panel,
    Rgb PanelText,
    Rgb Danger,
    Rgb CloseHover,
    Rgb Focus)
{
    /// <summary>
    /// Charcoal surfaces, near-white primary text and one restrained light-blue accent. Text
    /// colours keep at least 4.5:1 against <see cref="Surface"/>; the accent button text is
    /// dark on light for the same reason.
    /// </summary>
    public static SetupPalette Dark { get; } = new(
        HighContrast: false,
        Surface: Rgb.Hex(0x141416),
        SurfaceBottom: Rgb.Hex(0x0E0E10),
        Text: Rgb.Hex(0xF4F4F6),
        TextSecondary: Rgb.Hex(0xA9A9B3),
        TextMuted: Rgb.Hex(0x8A8A95),
        Accent: Rgb.Hex(0x8DBBFF),
        AccentHover: Rgb.Hex(0xA6CBFF),
        AccentPressed: Rgb.Hex(0x7AA8EE),
        OnAccent: Rgb.Hex(0x0A1424),
        Overlay: Rgb.Hex(0xFFFFFF),
        Track: Rgb.Hex(0x2A2A30),
        Panel: Rgb.Hex(0x1B1B1F),
        PanelText: Rgb.Hex(0xC9C9D2),
        Danger: Rgb.Hex(0xFF7B7B),
        CloseHover: Rgb.Hex(0xE5484D),
        Focus: Rgb.Hex(0xE8F1FF));

    public static SetupPalette Current() => IsHighContrast() ? FromSystem() : Dark;

    private static bool IsHighContrast()
    {
        var info = new Native.HIGHCONTRAST { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.HIGHCONTRAST>() };
        return Native.SystemParametersInfo(Native.SPI_GETHIGHCONTRAST, (uint)info.cbSize, ref info, 0)
            && (info.dwFlags & Native.HCF_HIGHCONTRASTON) != 0;
    }

    private static SetupPalette FromSystem()
    {
        Rgb Sys(int index) => Rgb.FromColorRef(Native.GetSysColor(index));
        var window = Sys(Native.COLOR_WINDOW);
        var text = Sys(Native.COLOR_WINDOWTEXT);
        var highlight = Sys(Native.COLOR_HIGHLIGHT);
        var onHighlight = Sys(Native.COLOR_HIGHLIGHTTEXT);
        var gray = Sys(Native.COLOR_GRAYTEXT);
        return new(
            HighContrast: true,
            Surface: window, SurfaceBottom: window,
            Text: text, TextSecondary: text, TextMuted: text,
            Accent: highlight, AccentHover: highlight, AccentPressed: highlight, OnAccent: onHighlight,
            Overlay: text, Track: gray, Panel: window, PanelText: text,
            Danger: text, CloseHover: highlight, Focus: text);
    }
}

/// <summary>
/// The installer's typefaces at one DPI. Korean text uses Malgun Gothic, which Windows ships
/// for Hangul; other languages use Segoe UI Variable where Windows 11 provides it and Segoe UI
/// otherwise. Paths always use the Latin face, where a backslash is drawn as a backslash.
/// </summary>
internal sealed class SetupFonts : IDisposable
{
    public IntPtr Brand { get; }
    public IntPtr Heading { get; }
    public IntPtr Body { get; }
    public IntPtr Small { get; }
    public IntPtr Button { get; }
    public IntPtr Path { get; }
    public IntPtr Mono { get; }

    public SetupFonts(uint dpi, bool korean)
    {
        var probe = Native.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            var display = Pick(probe, "Segoe UI Variable Display", "Segoe UI");
            var text = Pick(probe, "Segoe UI Variable Text", "Segoe UI");
            var hangul = Pick(probe, "Malgun Gothic", text);
            var ui = korean ? hangul : text;

            Brand = Create(dpi, 13, 600, korean ? hangul : text);
            Heading = Create(dpi, korean ? 25 : 26, korean ? 700 : 600, korean ? hangul : display);
            Body = Create(dpi, 13.5f, 400, ui);
            Small = Create(dpi, 12.5f, 400, ui);
            Button = Create(dpi, 13, 600, ui);
            Path = Create(dpi, 12.5f, 400, text);
            Mono = Create(dpi, 12, 400, Pick(probe, "Cascadia Mono", "Consolas"));
        }
        finally
        {
            Native.DeleteDC(probe);
        }
    }

    private static IntPtr Create(uint dpi, float pixels, int weight, string face) =>
        Native.CreateFont(-(int)Math.Round(pixels * dpi / 96.0), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);

    /// <summary>
    /// The preferred face when Windows has it. CreateFont silently substitutes a missing face,
    /// and reports an installed one by its localised name ("맑은 고딕" on Korean Windows), so
    /// a face counts as present when it does not resolve to what a made-up name resolves to.
    /// </summary>
    private static string Pick(IntPtr dc, string preferred, string fallback)
    {
        var substitute = ActualFace(dc, "CycleArc Missing Face");
        var actual = ActualFace(dc, preferred);
        return actual.Length > 0 && !actual.Equals(substitute, StringComparison.OrdinalIgnoreCase) ? preferred : fallback;
    }

    private static unsafe string ActualFace(IntPtr dc, string face)
    {
        var font = Create(96, 12, 400, face);
        var previous = Native.SelectObject(dc, font);
        try
        {
            var buffer = stackalloc char[64];
            buffer[0] = '\0';
            return Native.GetTextFace(dc, 64, buffer) > 0 ? new string(buffer) : "";
        }
        finally
        {
            Native.SelectObject(dc, previous);
            Native.DeleteObject(font);
        }
    }

    public void Dispose()
    {
        foreach (var font in new[] { Brand, Heading, Body, Small, Button, Path, Mono })
            if (font != IntPtr.Zero) Native.DeleteObject(font);
    }
}
