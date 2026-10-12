using System.Diagnostics;
using System.IO;

namespace CycleArc.UiSmoke;

internal static class InstallerHookProcessChecks
{
    public static void Run(string executable)
    {
        foreach (var hook in new[] { "--veloapp-install", "--veloapp-obsolete", "--veloapp-updated",
            "--veloapp-uninstall", "--squirrel-obsolete", "--VELOAPP-UPDATED" })
            Verify(executable, hook, "0.5.9", expectedSuccess: true);
        Verify(executable, "--veloapp-obsolete", "invalid-version", expectedSuccess: false);
        Console.WriteLine("PASS: production installer hooks exit without desktop startup in an unmanaged layout; malformed versions fail.");
    }

    private static void Verify(string executable, string hook, string version, bool expectedSuccess)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add(hook);
        start.ArgumentList.Add(version);
        // A routing regression must report its parser error instead of opening a modal dialog.
        start.ArgumentList.Add("--quiet");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Installer hook did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(10_000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5_000);
            throw new TimeoutException($"Installer hook {hook} did not exit.");
        }
        if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Installer hook output did not close.");
        if ((child.ExitCode == 0) != expectedSuccess
            || stderr.Result.Contains("Unknown or incomplete CycleArc launch argument.", StringComparison.Ordinal)
            || stdout.Result.Length != 0)
            throw new InvalidOperationException($"Installer hook {hook} failed its fast-exit contract (exit {child.ExitCode}).");
    }
}
