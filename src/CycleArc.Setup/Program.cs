namespace CycleArc.Setup;

/// <summary>Distinguishes success, a person cancelling, and a real installation failure.</summary>
internal enum SetupExitCode
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
}

internal static class Program
{
    /// <summary>
    /// The distributable installer's entry point. Opening it shows the confirmation screen;
    /// nothing is installed, stopped or changed until the person chooses Install.
    ///
    /// --silent runs the embedded engine with no window at all, for CI and scripted installs.
    /// It is deliberately opt-in: a person who double-clicks the file always gets the screens.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // A build-time check of the scaled layout: no control may fall outside the client
        // area or overlap another at any supported scale. Installs nothing.
        if (args.Any(argument => argument.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest();

        var silent = args.Any(argument =>
            argument.Equals("--silent", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("-s", StringComparison.OrdinalIgnoreCase));
        var directory = ReadValue(args, "--installto", "-t");
        // Honoured rather than ignored: a parent that passes --log must find the log there.
        var logPath = ReadValue(args, "--log", "-l");

        var target = directory is null
            ? InstallTargets.Resolve()
            : new InstallTarget(directory, InstallTargets.Looks(directory), "explicit");

        if (!EngineRunner.HasEngine)
        {
            if (!silent)
            {
                Native.MessageBox(IntPtr.Zero, Strings.NoEngineBody, Strings.NoEngineHeading,
                    Native.MB_OK | Native.MB_ICONERROR);
            }

            return (int)SetupExitCode.Failed;
        }

        if (silent)
        {
            SetupState.Report(SetupState.Installing);
            var result = EngineRunner.Install(target.Directory, CancellationToken.None, logPath);
            SetupState.Report(result.Succeeded ? SetupState.Done : SetupState.Failed);
            return (int)(result.Succeeded ? SetupExitCode.Succeeded : SetupExitCode.Failed);
        }

        return (int)new SetupWindow(target, logPath).Run();
    }

    private static int SelfTest()
    {
        var failures = new List<string>();
        foreach (var dpi in new uint[] { 96, 120, 144, 168, 192, 240 })
            failures.AddRange(SetupLayout.Problems(dpi));
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.Out.WriteLine(failures.Count == 0
            ? "setup layout ok at 96-240 dpi"
            : $"setup layout has {failures.Count} problem(s)");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads one of the engine's own option pairs, so a caller that already uses --installto
    /// or --log keeps working. Interactive runs never ask for a path: there is no location page.
    /// </summary>
    private static string? ReadValue(string[] args, string longName, string shortName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var isFlag = args[i].Equals(longName, StringComparison.OrdinalIgnoreCase)
                || args[i].Equals(shortName, StringComparison.OrdinalIgnoreCase);
            if (!isFlag || i + 1 >= args.Length) continue;
            var value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value)) continue;
            try { return Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        return null;
    }
}
