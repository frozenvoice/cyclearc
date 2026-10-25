namespace CycleArc.Setup;

/// <summary>
/// Build-time checks of the drawn window, without opening it or installing anything: text
/// must fit its box in both languages at every supported scale, and every page can be
/// written out as an image to look at.
/// </summary>
internal static class SetupPreview
{
    public static readonly uint[] Scales = { 96, 120, 144, 168, 192, 240 };

    private const string SamplePath = @"C:\Users\Example\AppData\Local\Programs\CycleArc";

    public static IReadOnlyList<string> TextProblems()
    {
        var problems = new List<string>();
        var korean = Strings.Korean;
        try
        {
            foreach (var language in new[] { false, true })
            {
                Strings.Korean = language;
                foreach (var dpi in Scales)
                {
                    using var scene = new SetupScene(dpi, SetupPalette.Dark, reducedMotion: false, drawBorder: false);
                    problems.AddRange(scene.TextProblems());
                }
            }
        }
        finally
        {
            Strings.Korean = korean;
        }
        return problems;
    }

    /// <summary>Writes every page, both languages, at 100/125/150/200% as BMP files.</summary>
    public static int Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var korean = Strings.Korean;
        var count = 0;
        try
        {
            foreach (var language in new[] { false, true })
            {
                Strings.Korean = language;
                foreach (var dpi in new uint[] { 96, 120, 144, 192 })
                foreach (var (page, update, name) in new[]
                {
                    (SetupPage.Confirm, false, "1-confirm"),
                    (SetupPage.Confirm, true, "1-confirm-update"),
                    (SetupPage.Progress, false, "2-progress"),
                    (SetupPage.Done, false, "3-done"),
                    (SetupPage.Failed, false, "4-failed"),
                })
                {
                    using var scene = new SetupScene(dpi, SetupPalette.Dark, reducedMotion: false, drawBorder: false)
                    {
                        Page = page,
                        IsUpdate = update,
                        Location = SamplePath,
                        Stage = InstallStage.Installing,
                        Phase = 0.75,
                        Detail = Strings.LogLabel + @": C:\Users\Example\AppData\Local\Temp\CycleArc-setup-logs\CycleArc-install.log"
                            + "\r\n\r\n" + Strings.FailedExitCode + ": 1\r\n"
                            + "[sample] Error applying package: access to the path is denied (0x80070005)",
                    };
                    using var canvas = new SetupCanvas(scene.Width, scene.Height);
                    scene.DrawPage(canvas, element => new ControlState(
                        Enabled: true,
                        Checked: element == SetupElement.Run));
                    var file = Path.Combine(directory, $"setup-{name}-{(language ? "ko" : "en")}-{dpi * 100 / 96}.bmp");
                    WriteBitmap(file, canvas);
                    count++;
                }
            }
        }
        finally
        {
            Strings.Korean = korean;
        }

        Console.Out.WriteLine($"wrote {count} previews to {directory}");
        return 0;
    }

    private static void WriteBitmap(string path, SetupCanvas canvas)
    {
        var pixels = canvas.Pixels;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var imageSize = canvas.Width * canvas.Height * 4;
        writer.Write((ushort)0x4D42);
        writer.Write(14 + 40 + imageSize);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(canvas.Width);
        writer.Write(-canvas.Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        foreach (var pixel in pixels) writer.Write(pixel | 0xFF000000);
    }
}
