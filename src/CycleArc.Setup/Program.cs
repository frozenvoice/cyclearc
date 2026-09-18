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
        var silent = args.Any(argument =>
            argument.Equals("--silent", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("-s", StringComparison.OrdinalIgnoreCase));
        var directory = ReadInstallTo(args);

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
            var result = EngineRunner.Install(target.Directory, CancellationToken.None);
            return (int)(result.Succeeded ? SetupExitCode.Succeeded : SetupExitCode.Failed);
        }

        return (int)new SetupWindow(target).Run();
    }

    /// <summary>
    /// --installto is accepted so the engine's own contract still works for callers that
    /// already use it. Interactive runs never ask for a path: there is no location page.
    /// </summary>
    private static string? ReadInstallTo(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var isFlag = args[i].Equals("--installto", StringComparison.OrdinalIgnoreCase)
                || args[i].Equals("-t", StringComparison.OrdinalIgnoreCase);
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
