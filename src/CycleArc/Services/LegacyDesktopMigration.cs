using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CycleArc.Services;

public sealed record LegacyDesktopIdentity(string ExecutablePath, string Sha256);

/// <summary>
/// Performs the one-time cleanup needed when a pre-0.5.8 desktop executable is
/// still running. The normal startup path must use the IPC handoff first; this
/// bounded fallback is intended only for an explicit replacement operation.
/// </summary>
public static class LegacyDesktopMigration
{
    private static readonly Version FirstIpcVersion = new(0, 5, 8);
    private const int HelperTimeoutMilliseconds = 8000;
    private const int HelperReapMilliseconds = 2000;
    private const int DesktopExitTimeoutMilliseconds = 10000;
    private const int MaximumHelperOutputBytes = 64 * 1024;
    private const string PowerShellScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
Get-CimInstance -ClassName Win32_Process -Filter 'Name = ''CycleArc.exe''' |
    Where-Object { [int]$_.SessionId -eq $session } |
    ForEach-Object {
        [pscustomobject]@{
            pid = [int]$_.ProcessId
            session = [int]$_.SessionId
            path = [string]$_.ExecutablePath
            commandLine = [string]$_.CommandLine
        }
    } | ConvertTo-Json -Compress -Depth 2
";

    /// <summary>
    /// Returns the exact executable paths of old desktop instances that were
    /// stopped and verified exited. No headless callback or current executable
    /// is eligible for this fallback.
    /// </summary>
    public static IReadOnlyList<LegacyDesktopIdentity> StopOlderDesktops()
    {
        var rows = QueryProcesses();
        var currentSession = Process.GetCurrentProcess().SessionId;
        var candidates = new List<LegacyProcess>();
        try
        {
            foreach (var row in rows)
            {
                if (!TryOpenLegacyProcess(row, currentSession, out var candidate)) continue;
                candidates.Add(candidate!);
            }

            // Capture every old executable's identity before killing any one of
            // them. A missing hash leaves all retained process handles untouched.
            for (var index = 0; index < candidates.Count; index++)
            {
                if (!TryComputeSha256(candidates[index].Path, out var sha256)) return Array.Empty<LegacyDesktopIdentity>();
                candidates[index] = candidates[index] with { Sha256 = sha256 };
            }

            var stopped = new List<LegacyDesktopIdentity>();
            foreach (var candidate in candidates)
                if (candidate.Sha256 is not null && TryStop(candidate, currentSession))
                    stopped.Add(new LegacyDesktopIdentity(candidate.Path, candidate.Sha256));
            return stopped;
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Process.Dispose();
        }
    }

    private static IReadOnlyList<ProcessRow> QueryProcesses()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return Array.Empty<ProcessRow>();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript));
        using var helper = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        try
        {
            if (!helper.Start()) return Array.Empty<ProcessRow>();
            var outputTask = ReadBoundedAsync(helper.StandardOutput.BaseStream, MaximumHelperOutputBytes);
            var errorTask = ReadBoundedAsync(helper.StandardError.BaseStream, MaximumHelperOutputBytes / 4);
            if (!helper.WaitForExit(HelperTimeoutMilliseconds))
            {
                KillAndReapHelper(helper);
                return Array.Empty<ProcessRow>();
            }

            string output;
            try
            {
                output = outputTask.GetAwaiter().GetResult();
                _ = errorTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                KillAndReapHelper(helper);
                return Array.Empty<ProcessRow>();
            }
            if (helper.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return Array.Empty<ProcessRow>();
            return ParseRows(output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or NotSupportedException or ObjectDisposedException
                                       or System.ComponentModel.Win32Exception)
        {
            KillAndReapHelper(helper);
            return Array.Empty<ProcessRow>();
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > maximumBytes) throw new InvalidDataException("Legacy process query output is too large.");
            bytes.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    }

    private static void KillAndReapHelper(Process helper)
    {
        try { if (!helper.HasExited) helper.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { helper.WaitForExit(HelperReapMilliseconds); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static IReadOnlyList<ProcessRow> ParseRows(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return TryReadRow(document.RootElement, out var one) ? new[] { one! } : Array.Empty<ProcessRow>();
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ProcessRow>();
            var rows = new List<ProcessRow>();
            foreach (var element in document.RootElement.EnumerateArray())
                if (TryReadRow(element, out var row)) rows.Add(row!);
            return rows;
        }
        catch (JsonException) { return Array.Empty<ProcessRow>(); }
    }

    private static bool TryReadRow(JsonElement element, out ProcessRow? row)
    {
        row = null;
        try
        {
            if (!element.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var processId)
                || !element.TryGetProperty("session", out var session) || !session.TryGetInt32(out var sessionId)
                || !element.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("commandLine", out var command) || command.ValueKind != JsonValueKind.String)
                return false;
            row = new ProcessRow(processId, sessionId, path.GetString() ?? string.Empty, command.GetString() ?? string.Empty);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryOpenLegacyProcess(ProcessRow row, int currentSession, out LegacyProcess? candidate)
    {
        candidate = null;
        if (row.ProcessId == Environment.ProcessId || row.SessionId != currentSession
            || string.IsNullOrWhiteSpace(row.Path) || string.IsNullOrWhiteSpace(row.CommandLine)
            || !DesktopLaunchOptions.IsLegacyDesktopCommand(row.CommandLine)) return false;
        string path;
        try { path = Path.GetFullPath(row.Path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
        if (!Path.GetFileName(path).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(info.ProductName, "CycleArc", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(info.OriginalFilename, "CycleArc.dll", StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(info.FileVersion, out var version) || version >= FirstIpcVersion)
                return false;

            var process = Process.GetProcessById(row.ProcessId);
            try
            {
                process.Refresh();
                _ = process.Handle; // retain a process handle before any PID revalidation
                var actualPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actualPath)
                    || !Path.GetFullPath(actualPath).Equals(path, StringComparison.OrdinalIgnoreCase)
                    || process.SessionId != currentSession || process.HasExited) return false;
                var startTime = process.StartTime;
                candidate = new LegacyProcess(process, path, row.ProcessId, currentSession, startTime);
                return true;
            }
            catch
            {
                process.Dispose();
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool TryStop(LegacyProcess candidate, int currentSession)
    {
        try
        {
            var process = candidate.Process;
            process.Refresh();
            _ = process.Handle;
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath)
                || !Path.GetFullPath(actualPath).Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)
                || process.SessionId != currentSession || process.Id != candidate.ProcessId
                || process.HasExited || process.StartTime != candidate.StartTime) return false;
            process.Kill();
            return process.WaitForExit(DesktopExitTimeoutMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool TryComputeSha256(string path, out string sha256)
    {
        sha256 = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        { return false; }
    }

    private sealed record ProcessRow(int ProcessId, int SessionId, string Path, string CommandLine);
    private sealed record LegacyProcess(Process Process, string Path, int ProcessId, int SessionId, DateTime StartTime,
        string? Sha256 = null);
}
