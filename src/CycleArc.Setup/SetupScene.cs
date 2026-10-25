namespace CycleArc.Setup;

/// <summary>How one child control looks right now, read from the control itself.</summary>
internal readonly record struct ControlState(
    bool Hot = false, bool Pressed = false, bool Focused = false, bool Enabled = true, bool Checked = false);

/// <summary>
/// The installer's visual design, separate from installing. It knows the page, the text and
/// the animation state, and draws them; it never starts, waits for or judges an installation.
/// The window draws its own layer and each child control through this, and the self-test
/// draws whole pages through the same code, so a preview is what a person sees.
/// </summary>
internal sealed class SetupScene : IDisposable
{
    private const float ProgressPeriod = 1.7f;
    private const float PulsePeriod = 2.4f;

    public uint Dpi { get; }
    public SetupPalette Palette { get; }
    public SetupFonts Fonts { get; }
    public SetupBackdrop Backdrop { get; }
    public bool ReducedMotion { get; }

    public SetupPage Page { get; set; } = SetupPage.Confirm;
    public bool IsUpdate { get; set; }
    public string Location { get; set; } = "";
    public InstallStage? Stage { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>0 to 1 while a page fades in; 1 once it has arrived.</summary>
    public float Reveal { get; set; } = 1f;

    /// <summary>Seconds since the progress page appeared, for the indeterminate bar.</summary>
    public double Phase { get; set; }

    /// <summary>
    /// The bar's real fill, or null while the outcome is unknown. Only completion sets it:
    /// the engine reports finishing, not a percentage, so nothing in between is invented.
    /// </summary>
    public float? Fill { get; set; }

    public SetupScene(uint dpi, SetupPalette palette, bool reducedMotion, bool drawBorder)
    {
        Dpi = dpi == 0 ? 96 : dpi;
        Palette = palette;
        ReducedMotion = reducedMotion;
        Fonts = new SetupFonts(Dpi, Strings.Korean);
        Backdrop = new SetupBackdrop(Width, Height, palette, drawBorder);
    }

    public int Width => SetupLayout.Scale(SetupLayout.ClientWidth, Dpi);
    public int Height => SetupLayout.Scale(SetupLayout.ClientHeight, Dpi);

    private float S(float value) => value * Dpi / 96f;

    /// <summary>The element's box on the current page in pixels, or null when the page does not show it.</summary>
    public Native.RECT? Bounds(SetupElement element) => Bounds(Page, element);

    public Native.RECT? Bounds(SetupPage page, SetupElement element)
    {
        if (SetupLayout.Box(page, element) is not { } box) return null;
        return new Native.RECT
        {
            Left = SetupLayout.Scale(box.X, Dpi),
            Top = SetupLayout.Scale(box.Y, Dpi),
            Right = SetupLayout.Scale(box.Right, Dpi),
            Bottom = SetupLayout.Scale(box.Bottom, Dpi),
        };
    }

    public static string Version
    {
        get
        {
            var version = typeof(SetupScene).Assembly.GetName().Version;
            return version is null ? "" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    /// <summary>The text an element carries, which is also what the accessibility tree reads.</summary>
    public string Text(SetupElement element) => element switch
    {
        SetupElement.Heading => Page switch
        {
            SetupPage.Confirm => IsUpdate ? Strings.ConfirmHeadingUpdate : Strings.ConfirmHeadingInstall,
            SetupPage.Progress => IsUpdate ? Strings.ProgressHeadingUpdate : Strings.ProgressHeadingInstall,
            SetupPage.Done => IsUpdate ? Strings.DoneHeadingUpdate : Strings.DoneHeadingInstall,
            _ => IsUpdate ? Strings.FailedHeadingUpdate : Strings.FailedHeading,
        },
        SetupElement.Body => Page switch
        {
            SetupPage.Confirm => IsUpdate ? Strings.ConfirmBodyUpdate : Strings.ConfirmBodyInstall,
            SetupPage.Done => Strings.DoneBody,
            SetupPage.Failed => Strings.FailedBodyPrefix,
            _ => "",
        },
        SetupElement.Location => Strings.LocationLabel + ": " + Location,
        SetupElement.Status => StageText(Stage),
        SetupElement.Hint => Strings.ProgressBody,
        SetupElement.Detail => Detail,
        SetupElement.DesktopShortcut => Strings.DesktopShortcutCheckbox,
        SetupElement.Run => Strings.RunCheckbox,
        SetupElement.Secondary => Strings.CancelButton,
        SetupElement.Primary => Page switch
        {
            SetupPage.Done => Strings.FinishButton,
            SetupPage.Failed => Strings.CloseButton,
            _ => Strings.InstallButton,
        },
        SetupElement.Minimize => Strings.MinimizeButton,
        SetupElement.Close => Strings.CloseButton,
        _ => "",
    };

    public static string StageText(InstallStage? stage) => stage switch
    {
        InstallStage.PreparingDesktop => Strings.ProgressClosing,
        InstallStage.Extracting => Strings.ProgressPreparing,
        InstallStage.Finishing => Strings.ProgressFinishing,
        _ => Strings.ProgressCopying,
    };

    // --- The window's own layer ----------------------------------------------------------

    /// <summary>Backdrop, brand, page icon, progress bar and the failure panel.</summary>
    public void DrawWindow(SetupCanvas canvas)
    {
        canvas.Fill(Backdrop);
        DrawBrand(canvas);
        if (Bounds(SetupElement.Icon) is { } icon) DrawIcon(canvas, icon);
        if (Bounds(SetupElement.Progress) is { } bar) DrawProgress(canvas, bar);
        if (Bounds(SetupElement.Detail) is { } panel)
        {
            var radius = S(10);
            canvas.FillRoundRect(panel.Left, panel.Top, Wd(panel), Ht(panel), radius, Palette.Panel, Reveal);
            canvas.StrokeRoundRect(panel.Left, panel.Top, Wd(panel), Ht(panel), radius, S(1), Palette.Overlay, 0.07f * Reveal);
        }
    }

    /// <summary>The area the progress bar repaints each frame, glow included.</summary>
    public Native.RECT ProgressInvalidation()
    {
        var bar = Bounds(SetupPage.Progress, SetupElement.Progress)!.Value;
        var pad = (int)MathF.Ceiling(S(8));
        return new Native.RECT { Left = bar.Left - pad, Top = bar.Top - pad, Right = bar.Right + pad, Bottom = bar.Bottom + pad };
    }

    private void DrawBrand(SetupCanvas canvas)
    {
        var box = Bounds(SetupElement.Brand)!.Value;
        var logo = S(18);
        var top = box.Top + (Ht(box) - logo) / 2;
        canvas.Logo(box.Left, top, logo);
        var nameX = box.Left + logo + S(9);
        const string name = "CycleArc";
        var nameWidth = canvas.Measure(name, Fonts.Brand, Wd(box), Native.DT_SINGLELINE).Width;
        canvas.Text(name, nameX, box.Top, nameWidth + S(2), Ht(box), Fonts.Brand, Palette.Text,
            Native.DT_SINGLELINE | Native.DT_VCENTER);
        var versionX = nameX + nameWidth + S(8);
        canvas.Text(Version, versionX, box.Top, box.Right - versionX, Ht(box), Fonts.Small, Palette.TextMuted,
            Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
    }

    private void DrawIcon(SetupCanvas canvas, Native.RECT box)
    {
        var size = (float)Wd(box);
        var cx = box.Left + size / 2;
        var cy = box.Top + Ht(box) / 2f;
        // Icons settle into place as the page arrives.
        var grow = 0.86f + 0.14f * Reveal;
        switch (Page)
        {
            case SetupPage.Done:
            {
                var r = (size / 2 - S(1)) * grow;
                canvas.FillCircle(cx, cy, r, Palette.Accent, 0.13f * Reveal);
                canvas.StrokeCircle(cx, cy, r, S(1.5f), Palette.Accent, 0.85f * Reveal);
                var t = S(2.4f);
                canvas.Line(cx - 0.36f * r, cy + 0.02f * r, cx - 0.10f * r, cy + 0.28f * r, t, Palette.Accent, Reveal);
                canvas.Line(cx - 0.10f * r, cy + 0.28f * r, cx + 0.38f * r, cy - 0.24f * r, t, Palette.Accent, Reveal);
                break;
            }
            case SetupPage.Failed:
            {
                var r = (size / 2 - S(1)) * grow;
                canvas.FillCircle(cx, cy, r, Palette.Danger, 0.13f * Reveal);
                canvas.StrokeCircle(cx, cy, r, S(1.5f), Palette.Danger, 0.85f * Reveal);
                canvas.Line(cx, cy - 0.42f * r, cx, cy + 0.10f * r, S(2.4f), Palette.Danger, Reveal);
                canvas.FillCircle(cx, cy + 0.38f * r, S(1.6f), Palette.Danger, Reveal);
                break;
            }
            default:
            {
                var logo = size * grow;
                canvas.Logo(cx - logo / 2, cy - logo / 2, logo, Reveal);
                break;
            }
        }
    }

    private void DrawProgress(SetupCanvas canvas, Native.RECT bar)
    {
        float x = bar.Left, y = bar.Top, w = Wd(bar), h = Ht(bar);
        canvas.FillRoundRect(x, y, w, h, h / 2, Palette.Track, Reveal);
        if (Fill is { } fill)
        {
            var width = MathF.Max(h, w * Math.Clamp(fill, 0f, 1f));
            canvas.FillRoundRect(x, y - S(3), width, h + S(6), (h + S(6)) / 2, Palette.Accent, 0.10f * Reveal);
            canvas.FillRoundRect(x, y, width, h, h / 2, Palette.Accent, Reveal);
            return;
        }

        if (ReducedMotion || Palette.HighContrast)
        {
            // No travelling motion: a slow change of brightness says "working" instead.
            var pulse = 0.5f + 0.5f * MathF.Sin((float)(Phase * 2 * Math.PI / PulsePeriod));
            canvas.FillRoundRect(x, y, w, h, h / 2, Palette.Accent, (0.30f + 0.35f * pulse) * Reveal);
            return;
        }

        // An honest indeterminate sweep: a soft-edged segment that eases across the track.
        var u = (float)(Phase % ProgressPeriod / ProgressPeriod);
        var eased = 0.5f - 0.5f * MathF.Cos(u * MathF.PI);
        var segment = w * 0.34f;
        var left = x - segment + eased * (w + segment);
        float Window(float px)
        {
            var t = (px - left) / segment;
            if (t <= 0 || t >= 1) return 0;
            var edge = MathF.Min(t, 1 - t) / 0.3f;
            return edge >= 1 ? 1 : edge * edge * (3 - 2 * edge);
        }
        canvas.FillBar(x, y - S(3), w, h + S(6), Palette.Accent, 0.12f * Reveal, Window);
        canvas.FillBar(x, y, w, h, Palette.Accent, Reveal, Window);
    }

    // --- Child controls ------------------------------------------------------------------

    /// <summary>Draws one control onto a canvas whose origin is that control's position.</summary>
    public void DrawControl(SetupCanvas canvas, SetupElement element, ControlState state)
    {
        canvas.Fill(Backdrop);
        if (Bounds(element) is not { } box) return;
        var text = Text(element);
        var fade = element is SetupElement.Minimize or SetupElement.Close ? 1f : Reveal;
        var alpha = fade * (state.Enabled ? 1f : 0.4f);
        float x = box.Left, y = box.Top, w = Wd(box), h = Ht(box);

        switch (element)
        {
            case SetupElement.Heading:
                canvas.Text(text, x, y, w, h, Fonts.Heading, Faded(Palette.Text, fade),
                    Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
                break;

            case SetupElement.Body:
                canvas.Text(canvas.Wrap(text, Fonts.Body, (int)w), x, y, w, h, Fonts.Body,
                    Faded(Palette.TextSecondary, fade), Native.DT_CENTER);
                break;

            case SetupElement.Status:
                canvas.Text(text, x, y, w, h, Fonts.Body, Faded(Palette.TextSecondary, fade),
                    Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
                break;

            case SetupElement.Hint:
                canvas.Text(text, x, y, w, h, Fonts.Small, Faded(Palette.TextMuted, fade),
                    Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
                break;

            case SetupElement.Location:
                DrawLocation(canvas, x, y, w, h, fade);
                break;

            case SetupElement.Detail:
                // Only previews come here; the live window shows a real, selectable edit box.
                var pad = S(SetupLayout.DetailPadding);
                canvas.Text(text, x + pad, y + pad, w - 2 * pad, h - 2 * pad, Fonts.Mono, Palette.PanelText,
                    Native.DT_WORDBREAK | Native.DT_EDITCONTROL);
                break;

            case SetupElement.DesktopShortcut:
            case SetupElement.Run:
                DrawCheckbox(canvas, text, x, y, w, h, state, alpha);
                break;

            case SetupElement.Primary:
                DrawButton(canvas, text, x, y, w, h, state, alpha, primary: true);
                break;

            case SetupElement.Secondary:
                DrawButton(canvas, text, x, y, w, h, state, alpha, primary: false);
                break;

            case SetupElement.Minimize:
            case SetupElement.Close:
                DrawCaptionButton(canvas, element, x, y, w, h, state, alpha);
                break;
        }
    }

    private void DrawLocation(SetupCanvas canvas, float x, float y, float w, float h, float fade)
    {
        var radius = S(9);
        canvas.FillRoundRect(x, y, w, h, radius, Palette.Overlay, (Palette.HighContrast ? 0 : 0.045f) * fade);
        canvas.StrokeRoundRect(x, y, w, h, radius, S(1), Palette.Overlay, (Palette.HighContrast ? 1 : 0.09f) * fade);
        var inset = S(14);
        var label = Strings.LocationLabel;
        var labelWidth = canvas.Measure(label, Fonts.Small, (int)w, Native.DT_SINGLELINE).Width;
        canvas.Text(label, x + inset, y, labelWidth + S(2), h, Fonts.Small, Faded(Palette.TextMuted, fade),
            Native.DT_SINGLELINE | Native.DT_VCENTER);
        var pathX = x + inset + labelWidth + S(12);
        canvas.Text(Location, pathX, y, x + w - inset - pathX, h, Fonts.Path,
            Faded(Rgb.Lerp(Palette.TextSecondary, Palette.Text, 0.55f), fade),
            Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_PATH_ELLIPSIS);
    }

    private void DrawCheckbox(SetupCanvas canvas, string text, float x, float y, float w, float h,
        ControlState state, float alpha)
    {
        var size = S(18);
        var bx = x + S(2);
        var by = y + (h - size) / 2;
        var radius = S(5);
        if (state.Checked)
        {
            var fill = state.Pressed ? Palette.AccentPressed : state.Hot ? Palette.AccentHover : Palette.Accent;
            canvas.FillRoundRect(bx, by, size, size, radius, fill, alpha);
            var t = S(2f);
            canvas.Line(bx + 0.26f * size, by + 0.52f * size, bx + 0.43f * size, by + 0.69f * size, t, Palette.OnAccent, alpha);
            canvas.Line(bx + 0.43f * size, by + 0.69f * size, bx + 0.75f * size, by + 0.33f * size, t, Palette.OnAccent, alpha);
        }
        else
        {
            canvas.FillRoundRect(bx, by, size, size, radius, Palette.Overlay,
                (state.Pressed ? 0.10f : state.Hot ? 0.07f : 0.03f) * alpha);
            canvas.StrokeRoundRect(bx, by, size, size, radius, S(1.5f), Palette.Overlay,
                (Palette.HighContrast ? 1f : state.Hot ? 0.55f : 0.36f) * alpha);
        }
        if (state.Focused)
        {
            var ring = S(3);
            canvas.StrokeRoundRect(bx - ring, by - ring, size + 2 * ring, size + 2 * ring, radius + ring,
                S(1.5f), Palette.Focus, 0.9f * alpha);
        }

        var color = state.Hot ? Palette.Text : Rgb.Lerp(Palette.TextSecondary, Palette.Text, 0.45f);
        canvas.Text(text, bx + size + S(10), y, x + w - (bx + size + S(10)), h, Fonts.Body,
            Faded(color, alpha), Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
    }

    private void DrawButton(SetupCanvas canvas, string text, float x, float y, float w, float h,
        ControlState state, float alpha, bool primary)
    {
        // Inset, so the keyboard focus ring fits inside the control's own box.
        var inset = S(2.5f);
        float bx = x + inset, by = y + inset, bw = w - 2 * inset, bh = h - 2 * inset;
        var radius = S(9);
        Rgb label;
        if (primary)
        {
            var fill = state.Pressed ? Palette.AccentPressed : state.Hot ? Palette.AccentHover : Palette.Accent;
            canvas.FillRoundRect(bx, by, bw, bh, radius, fill, alpha);
            label = Palette.OnAccent;
        }
        else
        {
            canvas.FillRoundRect(bx, by, bw, bh, radius, Palette.Overlay,
                (Palette.HighContrast ? 0 : state.Pressed ? 0.05f : state.Hot ? 0.12f : 0.07f) * alpha);
            canvas.StrokeRoundRect(bx, by, bw, bh, radius, S(1), Palette.Overlay,
                (Palette.HighContrast ? 1f : state.Hot ? 0.16f : 0.10f) * alpha);
            label = Palette.Text;
        }
        if (state.Focused)
            canvas.StrokeRoundRect(x, y, w, h, radius + inset, S(1.5f), Palette.Focus, 0.9f * alpha);

        var textColor = primary ? Rgb.Lerp(Palette.Accent, label, alpha) : Faded(label, alpha);
        canvas.Text(text, bx, by + (state.Pressed ? S(0.5f) : 0), bw, bh, Fonts.Button, textColor,
            Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_END_ELLIPSIS);
    }

    private void DrawCaptionButton(SetupCanvas canvas, SetupElement element, float x, float y, float w, float h,
        ControlState state, float alpha)
    {
        var close = element == SetupElement.Close;
        var radius = S(7);
        if (state.Enabled && (state.Hot || state.Pressed))
        {
            if (close) canvas.FillRoundRect(x, y, w, h, radius, Palette.CloseHover, state.Pressed ? 0.75f : 0.9f);
            else canvas.FillRoundRect(x, y, w, h, radius, Palette.Overlay, state.Pressed ? 0.05f : 0.09f);
        }
        if (state.Focused)
            canvas.StrokeRoundRect(x, y, w, h, radius, S(1.5f), Palette.Focus, 0.9f);

        var glyph = close && state.Enabled && (state.Hot || state.Pressed) && !Palette.HighContrast
            ? Rgb.Hex(0xFFFFFF)
            : state.Hot ? Palette.Text : Palette.TextSecondary;
        var cx = x + w / 2;
        var cy = y + h / 2;
        var half = S(5);
        var stroke = MathF.Max(1f, S(1.1f));
        if (close)
        {
            canvas.Line(cx - half, cy - half, cx + half, cy + half, stroke, glyph, alpha);
            canvas.Line(cx - half, cy + half, cx + half, cy - half, stroke, glyph, alpha);
        }
        else
        {
            canvas.Line(cx - half, cy, cx + half, cy, stroke, glyph, alpha);
        }
    }

    /// <summary>Text fading in: GDI text has no opacity, so its colour moves from the surface instead.</summary>
    private Rgb Faded(Rgb color, float amount) =>
        amount >= 1f ? color : Rgb.Lerp(Palette.Surface, color, amount);

    private static int Wd(Native.RECT rect) => rect.Right - rect.Left;
    private static int Ht(Native.RECT rect) => rect.Bottom - rect.Top;

    // --- Previews and checks -------------------------------------------------------------

    /// <summary>A whole page as one picture, controls included, in their resting state.</summary>
    public void DrawPage(SetupCanvas canvas, Func<SetupElement, ControlState> state)
    {
        canvas.OriginX = 0;
        canvas.OriginY = 0;
        DrawWindow(canvas);
        foreach (var (element, _) in SetupLayout.Page(Page))
        {
            if (element is SetupElement.Brand or SetupElement.Icon or SetupElement.Progress) continue;
            var box = Bounds(element)!.Value;
            using var part = new SetupCanvas(Wd(box), Ht(box)) { OriginX = box.Left, OriginY = box.Top };
            if (element == SetupElement.Detail)
            {
                // The panel belongs to the window layer; copy it rather than the plain backdrop.
                Native.BitBlt(part.Dc, 0, 0, Wd(box), Ht(box), canvas.Dc, box.Left, box.Top, Native.SRCCOPY);
                var pad = S(SetupLayout.DetailPadding);
                part.Text(Text(element), box.Left + pad, box.Top + pad, Wd(box) - 2 * pad, Ht(box) - 2 * pad,
                    Fonts.Mono, Palette.PanelText, Native.DT_WORDBREAK | Native.DT_EDITCONTROL);
            }
            else
            {
                DrawControl(part, element, state(element));
            }
            part.CopyTo(canvas.Dc, box.Left, box.Top);
        }
    }

    /// <summary>
    /// Text that does not fit its box at this DPI, in the current language. Paths and the
    /// failure detail are excluded: the path is drawn with a middle ellipsis by design and the
    /// detail box scrolls.
    /// </summary>
    public IReadOnlyList<string> TextProblems()
    {
        var problems = new List<string>();
        using var measure = new SetupCanvas(1, 1);
        var language = Strings.Korean ? "ko" : "en";
        var page = Page;
        var update = IsUpdate;
        try
        {
            foreach (var candidatePage in SetupLayout.Pages)
            foreach (var candidateUpdate in new[] { false, true })
            {
                Page = candidatePage;
                IsUpdate = candidateUpdate;
                foreach (var (element, _) in SetupLayout.Page(candidatePage))
                {
                    var box = Bounds(element)!.Value;
                    var width = Wd(box);
                    var height = Ht(box);
                    void Check(string text, IntPtr font, int room, bool wraps, int roomHeight)
                    {
                        var size = wraps
                            ? measure.Measure(measure.Wrap(text, font, room), font, room, 0)
                            : measure.Measure(text, font, 100000, Native.DT_SINGLELINE);
                        if (size.Width > room || size.Height > roomHeight)
                            problems.Add($"{Dpi} dpi {language}: '{text}' needs {size.Width}x{size.Height} but the {candidatePage} page's {element} has {room}x{roomHeight}.");
                    }

                    switch (element)
                    {
                        case SetupElement.Heading:
                            Check(Text(element), Fonts.Heading, width, false, height);
                            break;
                        case SetupElement.Body:
                            Check(Text(element), Fonts.Body, width, true, height);
                            break;
                        case SetupElement.Status:
                            foreach (var stage in Enum.GetValues<InstallStage>())
                                Check(StageText(stage), Fonts.Body, width, false, height);
                            break;
                        case SetupElement.Hint:
                            Check(Text(element), Fonts.Small, width, false, height);
                            break;
                        case SetupElement.Location:
                            // Leave the path at least a third of the field.
                            var labelWidth = measure.Measure(Strings.LocationLabel, Fonts.Small, width, Native.DT_SINGLELINE).Width;
                            if (labelWidth + S(40) > width * 2 / 3f)
                                problems.Add($"{Dpi} dpi {language}: the location label leaves too little room for the path.");
                            break;
                        case SetupElement.DesktopShortcut:
                        case SetupElement.Run:
                            Check(Text(element), Fonts.Body, width - (int)MathF.Ceiling(S(32)), false, height);
                            break;
                        case SetupElement.Primary:
                        case SetupElement.Secondary:
                            Check(Text(element), Fonts.Button, width - (int)MathF.Ceiling(S(24)), false, height);
                            break;
                    }
                }
            }
        }
        finally
        {
            Page = page;
            IsUpdate = update;
        }

        return problems;
    }

    public void Dispose() => Fonts.Dispose();
}
