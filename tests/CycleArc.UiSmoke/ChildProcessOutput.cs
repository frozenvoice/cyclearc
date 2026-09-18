using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

/// <summary>
/// Shared access to a child's JSON-lines report file. The child appends records while the
/// parent polls the same file, so every open here has to admit the other side's handle:
/// <see cref="File.ReadLines(string)"/> takes <see cref="FileShare.Read"/>, which denies the
/// child's write open and makes an append fail for as long as the parent is reading.
/// A record that is only half on disk stays invisible until its newline lands, so a torn
/// read is never mistaken for a malformed record or for a missing one.
/// </summary>
internal static class ChildReportFile
{
    internal const int AppendAttempts = 12;
    internal static readonly TimeSpan AppendRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Opens the report for reading without locking out the appending child.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Opens the report for appending without locking out a reading parent.</summary>
    public static FileStream OpenAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Returns only the newline-terminated records. Bytes after the last newline are a record
    /// still being written, so they are withheld rather than parsed and rejected.
    /// </summary>
    public static string[] ReadCompleteLines(string path)
    {
        var text = ReadCompletedText(path);
        if (text.Length == 0) return Array.Empty<string>();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    /// <summary>The report text up to and including its last newline, or an empty string.</summary>
    public static string ReadCompletedText(string path)
    {
        var bytes = ReadAllBytesShared(path);
        // Decode only up to the last newline: a truncated trailing UTF-8 sequence would
        // otherwise decode to a replacement character and corrupt an otherwise good record.
        var end = Array.LastIndexOf(bytes, (byte)'\n');
        return end < 0 ? string.Empty : Encoding.UTF8.GetString(bytes, 0, end + 1);
    }

    /// <summary>The whole report, including any partial trailing record, for diagnostics only.</summary>
    public static string ReadAllTextShared(string path)
    {
        var bytes = ReadAllBytesShared(path);
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Appends one record. A parent that is mid-read still briefly denies this handle on
    /// Windows, so a sharing violation is retried within a bounded budget; anything left
    /// after that is a real failure and is thrown to the caller, never swallowed.
    /// </summary>
    public static void Append(string path, string line)
    {
        var payload = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = OpenAppend(path);
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: true);
                return;
            }
            catch (IOException) when (attempt < AppendAttempts)
            {
                Thread.Sleep(AppendRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < AppendAttempts)
            {
                Thread.Sleep(AppendRetryDelay);
            }
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
            var text = ChildReportFile.ReadAllTextShared(reportPath);
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
