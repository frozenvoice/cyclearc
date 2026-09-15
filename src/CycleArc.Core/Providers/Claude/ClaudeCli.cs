using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CycleArc.Providers.Claude;

public enum ClaudeAuthStatus { SignedIn, SignedOut, NotInstalled, Unsupported, InvalidResponse, Failed, TimedOut, Cancelled }

/// <summary>
/// Hashes the non-secret identity metadata returned by Claude's official CLI.
/// SubscriptionType is deliberately excluded from the stable identity: a plan can
/// change while the email and organization remain the same account.
/// </summary>
public static class ClaudeIdentity
{
    // Bounded allowlist for plan metadata that may be persisted or used as
    // historical migration evidence. Unknown values remain memory-only.
    public static IReadOnlyList<string?> KnownPlanValues { get; } =
        [null, "", "free", "pro", "max", "max_5x", "max_20x", "team", "enterprise", "business", "education"];

    public static string StableFingerprint(string email, string? organizationId) =>
        Hash(NormalizeEmail(email) + "\n" + NormalizeOrganization(organizationId));

    // v1 binding files used this shape. Keep it available only for an evidence-backed
    // migration; it must never become the current identity comparison again.
    public static string LegacyFingerprint(string email, string? organizationId, string? plan) =>
        Hash(email.ToLowerInvariant() + "\n" + organizationId + "\n" + plan);

    public static bool IsFingerprint(string? fingerprint) => fingerprint is { Length: 64 } value
        && value.All(Uri.IsHexDigit);

    public static bool IsSafePersistedPlan(string? plan) =>
        KnownPlanValues.Contains(plan, StringComparer.Ordinal);

    public static string? SafePersistedPlan(string? plan) => IsSafePersistedPlan(plan) ? plan : null;

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    // Organization IDs are opaque; preserving their bytes avoids collapsing two
    // distinct values during an identity check.
    private static string NormalizeOrganization(string? organizationId) => organizationId ?? string.Empty;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

// Authentication metadata comes from the official CLI. Credentials never enter CycleArc.
public sealed record ClaudeAuthentication(ClaudeAuthStatus Status, string? Email = null,
    string? Plan = null, string? Fingerprint = null, string? OrganizationId = null,
    string? LegacyFingerprint = null)
{
    // Recompute from the structured fields when they are available. This keeps
    // identity checks fail-closed even if a caller passes a stale record copy.
    public string? StableFingerprint => Status == ClaudeAuthStatus.SignedIn
        && Email is { Length: > 0 } email ? ClaudeIdentity.StableFingerprint(email, OrganizationId) : Fingerprint;

    public static ClaudeAuthentication Parse(string json, int exitCode)
    {
        try
        {
            if (json.Length > 16384) return new(ClaudeAuthStatus.InvalidResponse);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)
                || !root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(ClaudeAuthStatus.InvalidResponse);
            if (!loggedIn.GetBoolean()) return new(exitCode is 0 or 1 ? ClaudeAuthStatus.SignedOut : ClaudeAuthStatus.Failed);
            if (exitCode != 0) return new(ClaudeAuthStatus.Failed);
            var method = String(root, "authMethod", 80);
            var provider = String(root, "apiProvider", 80);
            if (method is null || provider is null) return new(ClaudeAuthStatus.InvalidResponse);
            if (method != "claude.ai" || provider != "firstParty") return new(ClaudeAuthStatus.Unsupported);
            var email = String(root, "email", 320);
            var org = String(root, "orgId", 200, optional: true);
            var plan = String(root, "subscriptionType", 80, optional: true);
            if (string.IsNullOrWhiteSpace(email)) return new(ClaudeAuthStatus.InvalidResponse);
            var stable = ClaudeIdentity.StableFingerprint(email, org);
            var legacy = ClaudeIdentity.LegacyFingerprint(email, org, plan);
            return new(ClaudeAuthStatus.SignedIn, email, plan, stable, org, legacy);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        { return new(ClaudeAuthStatus.InvalidResponse); }
    }

    private static string? String(JsonElement root, string name, int max, bool optional = false)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return optional ? null : throw new InvalidDataException();
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var text = value.GetString()!;
        if (text.Length > max || text.Any(char.IsControl)) throw new InvalidDataException();
        return text;
    }
}

public interface IClaudeCli
{
    string? FindExecutable();
    Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token);
}

public sealed class ClaudeCli : IClaudeCli
{
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);

    public string? FindExecutable()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        foreach (var directory in dirs)
        {
            if (ClaudeConnectionPaths.Normalize(directory.Trim(' ', '"')) is not { } full) continue;
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                var path = Path.Combine(full, name);
                if (File.Exists(path) && IsExecutablePath(path)) return path;
            }
        }
        return null;
    }

    public static bool IsExecutablePath(string path) => ClaudeConnectionPaths.Normalize(path) is not null
        && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd"
        && (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !path.Any(c => c is '"' or '%' or '!' or '^'));

    public static ProcessStartInfo StartInfo(string executable, string? configDirectory, bool login)
    {
        if (!IsExecutablePath(executable) || ClaudeConnectionPaths.Normalize(configDirectory ?? ClaudeConnectionPaths.ImplicitDirectory) is not { } root)
            throw new ArgumentException("Invalid Claude executable or configuration directory.");
        var arguments = login ? "auth login --claudeai" : "auth status --json";
        var batch = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = batch ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable,
            Arguments = batch ? "/d /s /c \"\"" + executable + "\" " + arguments + "\"" : arguments,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = root
        };
        // An unset CLAUDE_CONFIG_DIR is not equivalent to explicitly setting ~/.claude:
        // the official CLI resolves its login metadata differently in those two modes.
        if (configDirectory is null) start.Environment.Remove("CLAUDE_CONFIG_DIR");
        else start.Environment["CLAUDE_CONFIG_DIR"] = root;
        // Scope authentication to this Claude home, without copying or exposing credentials.
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
                     "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            start.Environment.Remove(name);
        return start;
    }

    public async Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
    {
        if (!File.Exists(executable)) return new(ClaudeAuthStatus.NotInstalled);
        var root = configDirectory ?? ClaudeConnectionPaths.ImplicitDirectory;
        if (!Directory.Exists(root))
        {
            if (!login) return new(ClaudeAuthStatus.SignedOut);
            Directory.CreateDirectory(root);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(login ? LoginTimeout : StatusTimeout);
        try
        {
            var result = await RunAsync(StartInfo(executable, configDirectory, login), login ? 0 : 16384, bounded.Token).ConfigureAwait(false);
            if (login)
            {
                if (result.ExitCode != 0) return new(ClaudeAuthStatus.Failed);
                // Browser completion/exit status alone must never imply an authenticated account.
                return await AuthenticateAsync(executable, configDirectory, false, bounded.Token).ConfigureAwait(false);
            }
            return ClaudeAuthentication.Parse(result.Output, result.ExitCode);
        }
        catch (OperationCanceledException) { return new(token.IsCancellationRequested ? ClaudeAuthStatus.Cancelled : ClaudeAuthStatus.TimedOut); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ArgumentException)
        { return new(ClaudeAuthStatus.Failed); }
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, int captureLimit,
        CancellationToken token, ReadOnlyMemory<byte>? input = null)
    {
        using var process = Process.Start(start) ?? throw new IOException("Claude process could not start.");
        // Process.Kill(entireProcessTree: true) does not wait for descendants. A
        // .cmd/.bat Claude installation commonly starts cmd.exe and a child CLI;
        // also track attached descendants in a Windows job and wait for them on
        // cancellation/error. Normal completion must not close a login browser.
        using var processTree = ClaudeProcessTree.TryAttach(process);
        using var cancel = token.Register(() => Kill(process));
        var stdout = DrainAsync(process.StandardOutput, captureLimit, token);
        var stderr = DrainAsync(process.StandardError, 0, token);
        var completed = false;
        try
        {
            if (input is { } bytes)
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await stdout.WaitAsync(token).ConfigureAwait(false);
            await stderr.WaitAsync(token).ConfigureAwait(false);
            completed = process.ExitCode == 0 && !token.IsCancellationRequested;
            return (process.ExitCode, output);
        }
        finally
        {
            Kill(process);
            var treeExited = completed || processTree is null
                || await processTree.TerminateAndWaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            process.StandardInput.Dispose(); process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            if (!treeExited) throw new IOException("Claude process tree cleanup did not complete.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        var tooLong = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (limit == 0) continue; // Login URLs, codes and errors are drained without retaining/logging them.
            if (count > limit - result.Length) tooLong = true;
            result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        }
        return tooLong ? "" : result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Owns a Windows process job for bounded, synchronous descendant cleanup.</summary>
    private sealed class ClaudeProcessTree : IDisposable
    {
        private const uint JobObjectBasicAccountingInformationClass = 1;
        private IntPtr _job;

        private ClaudeProcessTree(IntPtr job) => _job = job;

        public static ClaudeProcessTree? TryAttach(Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;
            try
            {
                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    CloseHandle(job);
                    return null;
                }
                return new ClaudeProcessTree(job);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
            {
                CloseHandle(job);
                return null;
            }
        }

        public async Task<bool> TerminateAndWaitAsync(TimeSpan timeout)
        {
            if (_job == IntPtr.Zero) return true;
            if (!TerminateJobObject(_job, 1)) return false;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                var accounting = new JobObjectBasicAccountingInformation();
                if (!QueryInformationJobObject(_job, JobObjectBasicAccountingInformationClass,
                        ref accounting, (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero))
                    return false;
                if (accounting.ActiveProcesses == 0) return true;
                await Task.Delay(25).ConfigureAwait(false);
            }
            return false;
        }

        public void Dispose()
        {
            if (_job == IntPtr.Zero) return;
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, uint informationClass,
            ref JobObjectBasicAccountingInformation information, uint informationLength,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }
    }
}
