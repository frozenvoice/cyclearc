using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CycleArc.Services;

/// <summary>
/// Drains redirected child stdout/stderr so a full pipe cannot stall the child,
/// and keeps a bounded snapshot for diagnostics. Never waits indefinitely.
/// </summary>
internal sealed class ChildProcessOutput : IDisposable
{
    public const int DefaultMaxChars = 32_768;
    internal static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _stdoutDrain;
    private readonly Task _stderrDrain;
    private readonly int _maxChars;

    public ChildProcessOutput(Process process, int maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(process);
        _maxChars = maxChars > 0 ? maxChars : DefaultMaxChars;
        _stdoutDrain = DrainAsync(process.StandardOutput, _stdout);
        _stderrDrain = DrainAsync(process.StandardError, _stderr);
    }

    public string StandardOutput
    {
        get { lock (_gate) return _stdout.ToString(); }
    }

    public string StandardError
    {
        get { lock (_gate) return _stderr.ToString(); }
    }

    public void CollectRemaining(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
        try
        {
            _ = Task.WaitAll(new[] { _stdoutDrain, _stderrDrain }, timeout);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose() => CollectRemaining(TimeSpan.FromMilliseconds(200));

    private async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count <= 0) break;
                lock (_gate)
                {
                    var remaining = _maxChars - sink.Length;
                    if (remaining > 0)
                        sink.Append(buffer, 0, Math.Min(count, remaining));
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal static class ChildProcessReportWait
{
    public static T WaitFor<T>(
        Func<T?> tryMatch,
        Process process,
        ChildProcessOutput output,
        string reportPath,
        TimeSpan timeout,
        string expectedDescription)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(tryMatch);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(output);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var match = tryMatch();
            if (match is not null) return match;
            if (TryHasExited(process, out var exited) && exited)
            {
                throw new InvalidOperationException(
                    Describe(process, output, reportPath,
                        $"Desktop instance child exited before {expectedDescription}"));
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            Describe(process, output, reportPath, $"Timed out waiting for {expectedDescription}"));
    }

    public static string Describe(Process process, ChildProcessOutput output, string reportPath, string heading)
    {
        output.CollectRemaining(TimeSpan.FromMilliseconds(250));
        var hasExited = TryHasExited(process, out var exited) && exited;
        var exit = "n/a";
        if (hasExited && TryExitCode(process, out var code))
            exit = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var report = TryReadReport(reportPath);
        return string.Join(Environment.NewLine, new[]
        {
            heading + ".",
            $"pid={TryId(process)} HasExited={hasExited} exit={exit}",
            "command: " + TryCommandLine(process),
            "stdout: " + FormatBlock(output.StandardOutput),
            "stderr: " + FormatBlock(output.StandardError),
            "report: " + FormatBlock(report)
        });
    }

    private static string FormatBlock(string text) =>
        string.IsNullOrEmpty(text) ? "(empty)" : text;

    private static string TryReadReport(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath)) return string.Empty;
        try
        {
            if (!File.Exists(reportPath)) return "(missing)";
            var text = File.ReadAllText(reportPath);
            return text.Length <= ChildProcessOutput.DefaultMaxChars
                ? text
                : text[..ChildProcessOutput.DefaultMaxChars];
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
        catch (UnauthorizedAccessException)
        {
            return "(unreadable)";
        }
    }

    private static string TryId(Process process)
    {
        try { return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private static string TryCommandLine(Process process)
    {
        try
        {
            var info = process.StartInfo;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.FileName)) parts.Add(info.FileName);
            if (info.ArgumentList.Count > 0)
            {
                parts.AddRange(info.ArgumentList);
            }
            else if (!string.IsNullOrWhiteSpace(info.Arguments))
            {
                parts.Add(info.Arguments);
            }

            return parts.Count == 0 ? "unknown" : string.Join(" ", parts);
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static bool TryHasExited(Process process, out bool exited)
    {
        try
        {
            exited = process.HasExited;
            return true;
        }
        catch (InvalidOperationException)
        {
            exited = false;
            return false;
        }
    }

    private static bool TryExitCode(Process process, out int code)
    {
        try
        {
            code = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            code = 0;
            return false;
        }
    }
}
