using System.Diagnostics;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class DesktopInstanceProcessWaitTests
{
    private static readonly TimeSpan FormerReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CurrentReadyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void DesktopInstanceProcessChecks_SeparatesReadyTimeoutFromIpcRequestTimeout()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "tests", "CycleArc.UiSmoke", "DesktopInstanceProcessChecks.cs"));
        Assert.Contains("ProcessTimeout = TimeSpan.FromSeconds(30)", text, StringComparison.Ordinal);
        Assert.Contains("RequestTimeout = TimeSpan.FromSeconds(3)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTimeout = TimeSpan.FromSeconds(10)", text, StringComparison.Ordinal);
        var helper = File.ReadAllText(Path.Combine(RepoRoot, "tests", "CycleArc.UiSmoke", "ChildProcessOutput.cs"));
        Assert.DoesNotContain("ReadToEnd()", helper, StringComparison.Ordinal);
        Assert.Contains("Task.WaitAll", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitFor_SucceedsWhenReadyArrivesAfterFormerTenSecondBudget()
    {
        Assert.True(CurrentReadyTimeout > FormerReadyTimeout);
        var delay = FormerReadyTimeout + TimeSpan.FromMilliseconds(500);
        using var run = SyntheticChild.Start(
            stdout: "delayed-ready-stdout",
            stderr: "delayed-ready-stderr",
            writeReadyAfter: delay,
            hangAfterReady: true);
        var started = Stopwatch.StartNew();
        var ready = ChildProcessReportWait.WaitFor(
            () => TryReady(run.ReportPath),
            run.Process,
            run.Output,
            run.ReportPath,
            CurrentReadyTimeout,
            "desktop instance report");
        started.Stop();
        Assert.Equal("ready", ready);
        Assert.True(started.Elapsed > FormerReadyTimeout,
            $"Ready arrived after {started.Elapsed.TotalSeconds:n1}s; the former 10s budget was not exceeded.");
        Assert.True(started.Elapsed < CurrentReadyTimeout);
        Assert.Contains("delayed-ready-stdout", run.Output.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitFor_TimeoutIncludesPidStreamsAndReport()
    {
        using var run = SyntheticChild.Start(
            stdout: "timeout-child-stdout",
            stderr: "timeout-child-stderr",
            writeReadyAfter: null,
            hangAfterReady: true);
        var streamsVisible = Stopwatch.StartNew();
        while (streamsVisible.Elapsed < TimeSpan.FromSeconds(8)
            && !run.Output.StandardOutput.Contains("timeout-child-stdout", StringComparison.Ordinal))
        {
            Thread.Sleep(25);
        }

        var error = Assert.Throws<TimeoutException>(() =>
            ChildProcessReportWait.WaitFor(
                () => TryReady(run.ReportPath),
                run.Process,
                run.Output,
                run.ReportPath,
                TimeSpan.FromMilliseconds(400),
                $"desktop instance report '{run.ReportPath}'"));
        var text = error.Message;
        Assert.Contains($"pid={run.Process.Id}", text, StringComparison.Ordinal);
        Assert.Contains("HasExited=False", text, StringComparison.Ordinal);
        Assert.Contains("exit=n/a", text, StringComparison.Ordinal);
        Assert.Contains("timeout-child-stdout", text, StringComparison.Ordinal);
        Assert.Contains("timeout-child-stderr", text, StringComparison.Ordinal);
        Assert.Contains(run.ReportPath, text, StringComparison.Ordinal);
        Assert.Contains("report: (missing)", text, StringComparison.Ordinal);
        Assert.False(run.Process.HasExited, "Timeout diagnostics must not kill a child the test did not stop.");
    }

    [Fact]
    public void WaitFor_DrainsStderrBeyondBudgetWithoutBlocking()
    {
        using var run = SyntheticChild.Start(
            stdout: "flood-stdout",
            stderr: new string('e', 64 * 1024),
            writeReadyAfter: TimeSpan.Zero,
            hangAfterReady: true);
        var ready = ChildProcessReportWait.WaitFor(
            () => TryReady(run.ReportPath),
            run.Process,
            run.Output,
            run.ReportPath,
            TimeSpan.FromSeconds(8),
            "desktop instance report");
        Assert.Equal("ready", ready);
        Assert.Equal(ChildProcessOutput.DefaultMaxChars, run.Output.StandardError.Length);
        Assert.Contains("flood-stdout", run.Output.StandardOutput, StringComparison.Ordinal);
    }

    private static string? TryReady(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath)) return null;
            var text = File.ReadAllText(reportPath);
            return text.Contains("\"state\":\"ready\"", StringComparison.Ordinal) ? "ready" : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CycleArc.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (CycleArc.sln).");
    }

    private sealed class SyntheticChild : IDisposable
    {
        private readonly string _work;

        private SyntheticChild(string work, Process process, ChildProcessOutput output, string reportPath)
        {
            _work = work;
            Process = process;
            Output = output;
            ReportPath = reportPath;
        }

        public Process Process { get; }
        public ChildProcessOutput Output { get; }
        public string ReportPath { get; }

        public static SyntheticChild Start(string stdout, string stderr, TimeSpan? writeReadyAfter, bool hangAfterReady)
        {
            var work = Directory.CreateTempSubdirectory("cyclearc-report-wait-");
            var reportPath = Path.Combine(work.FullName, "child.jsonl");
            File.WriteAllText(Path.Combine(work.FullName, "stdout.txt"), stdout);
            File.WriteAllText(Path.Combine(work.FullName, "stderr.txt"), stderr);
            var start = CreateStartInfo(work.FullName, reportPath, writeReadyAfter, hangAfterReady);
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start the synthetic report child.");
            return new SyntheticChild(work.FullName, process, new ChildProcessOutput(process), reportPath);
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                Output.CollectRemaining(ChildProcessOutput.DrainBudget);
                Output.Dispose();
                Process.Dispose();
            }

            try { Directory.Delete(_work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static ProcessStartInfo CreateStartInfo(
            string work, string reportPath, TimeSpan? writeReadyAfter, bool hangAfterReady)
        {
            var stdoutFile = Path.Combine(work, "stdout.txt");
            var stderrFile = Path.Combine(work, "stderr.txt");
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(work, "child.ps1");
                var delayMs = writeReadyAfter is { } delay ? Math.Max(0, (int)delay.TotalMilliseconds) : -1;
                File.WriteAllText(script, string.Join(Environment.NewLine, new[]
                {
                    "$ErrorActionPreference = 'Stop'",
                    $"[Console]::Out.Write([IO.File]::ReadAllText({PsQuote(stdoutFile)}))",
                    "[Console]::Out.WriteLine()",
                    $"[Console]::Error.Write([IO.File]::ReadAllText({PsQuote(stderrFile)}))",
                    "[Console]::Error.WriteLine()",
                    "[Console]::Out.Flush()",
                    "[Console]::Error.Flush()",
                    delayMs >= 0 ? $"Start-Sleep -Milliseconds {delayMs}" : "",
                    delayMs >= 0
                        ? $"Add-Content -LiteralPath {PsQuote(reportPath)} -Value {PsQuote("{\"state\":\"ready\"}")}"
                        : "",
                    hangAfterReady ? "Start-Sleep -Seconds 120" : ""
                }));
                var host = FindOnPath("pwsh") ?? FindOnPath("powershell")
                    ?? throw new InvalidOperationException("PowerShell is required to start the synthetic child.");
                var start = Redirected(host);
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(script);
                return start;
            }

            var shell = Path.Combine(work, "child.sh");
            var sleep = writeReadyAfter is { } unixDelay
                ? Math.Max(0, (int)Math.Ceiling(unixDelay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            File.WriteAllText(shell, string.Join('\n', new[]
            {
                "set -eu",
                $"cat {ShQuote(stdoutFile)}",
                "printf '\\n'",
                $"cat {ShQuote(stderrFile)} >&2",
                "printf '\\n' >&2",
                sleep is null ? "" : $"sleep {sleep}",
                sleep is null ? "" : $"printf '%s\\n' {ShQuote("{\"state\":\"ready\"}")} >> {ShQuote(reportPath)}",
                hangAfterReady ? "sleep 120" : ""
            }));
            var sh = Redirected("/bin/sh");
            sh.ArgumentList.Add(shell);
            return sh;
        }

        private static ProcessStartInfo Redirected(string fileName) => new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private static string ShQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string? FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
                if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe";
            }

            return null;
        }
    }
}
