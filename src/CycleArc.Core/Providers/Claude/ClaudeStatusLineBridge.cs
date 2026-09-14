using System.Diagnostics;
using System.Text;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

/// <summary>Preserves an existing status line while projecting usage for an authenticated local binding.</summary>
public static class ClaudeStatusLineBridge
{
    public const string Argument = "--claude-statusline-bridge";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(ClaudeBridgeOptions options, Stream input, TextWriter output,
        CodexAccountStore accounts, IClaudeCli? cli = null, IClock? clock = null, CancellationToken token = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(Deadline);
        var receivedAt = (clock ?? SystemClock.Instance).UtcNow;
        var bytes = await ClaudeStatusLineCommand.ReadBoundedAsync(input, bounded.Token).ConfigureAwait(false);
        var oldCommand = options.PreviousStatusLine?["command"]?.GetValue<string>();
        var previous = oldCommand is null ? Task.FromResult<string?>(null) : ForwardAsync(oldCommand, bytes, bounded.Token);
        var text = "Claude | CycleArc unavailable";
        var exit = 1;
        ClaudeConnectionBinding? observedBinding = null;
        try
        {
            if (accounts.ContainsClaude(options.ProfileId))
            {
                var connections = new ClaudeConnectionStore(accounts, options.ProfileId);
                observedBinding = connections.Read().Binding;
                if (observedBinding is { Disconnected: false } binding
                    && string.Equals(binding.ConfigDirectory, options.ConfigDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var auth = await (cli ?? new ClaudeCli()).AuthenticateAsync(binding.CliExecutable,
                        binding.UseDefaultConfig ? null : binding.ConfigDirectory, false, bounded.Token).ConfigureAwait(false);
                    var generationMatches = string.Equals(binding.BindingGeneration, options.BindingGeneration, StringComparison.Ordinal);
                    var matches = auth.Status == ClaudeAuthStatus.SignedIn && auth.Fingerprint == binding.IdentityFingerprint
                        && generationMatches && connections.Read().Binding == binding && accounts.ContainsClaude(options.ProfileId);
                    if (!matches)
                    {
                        // Keep the quota receipt/cache unchanged while recording why this
                        // verified binding cannot receive a new sample.
                        var kind = auth.Status == ClaudeAuthStatus.SignedOut ? ClaudeFailureKind.AuthRequired
                            : auth.Status == ClaudeAuthStatus.SignedIn ? ClaudeFailureKind.IdentityMismatch
                            : ClaudeFailureKind.BridgeUnavailable;
                        await RecordFailureAsync(accounts, options, binding, kind, receivedAt, bounded.Token).ConfigureAwait(false);
                    }
                    var parsed = matches ? ClaudeStatusLineParser.Parse(bytes) : new ClaudeStatusLineResult(ClaudeInputStatus.Missing);
                    await new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(options.ProfileId), options.ProfileId)
                        .RecordAsync(parsed, receivedAt, bounded.Token).ConfigureAwait(false);
                    text = matches ? ClaudeStatusLineCommand.Format(parsed) : "Claude | Reconnect in CycleArc";
                    exit = matches && parsed.Status != ClaudeInputStatus.Malformed ? 0 : 1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (observedBinding is { Disconnected: false } binding)
            {
                try { await RecordFailureAsync(accounts, options, binding, ClaudeFailureKind.BridgeUnavailable, receivedAt, bounded.Token).ConfigureAwait(false); }
                catch (Exception recordEx) when (recordEx is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException) { }
            }
        }
        try
        {
            var previousOutput = await previous.ConfigureAwait(false);
            if (previousOutput is not null) await output.WriteAsync(previousOutput).ConfigureAwait(false);
            else await output.WriteLineAsync(text).ConfigureAwait(false);
        }
        finally { bounded.Cancel(); }
        return exit;
    }

    private static async Task RecordFailureAsync(CodexAccountStore accounts, ClaudeBridgeOptions options,
        ClaudeConnectionBinding binding, ClaudeFailureKind kind, DateTimeOffset observedAt, CancellationToken token)
    {
        if (binding.BindingGeneration is null || options.BindingGeneration is null
            || !string.Equals(binding.BindingGeneration, options.BindingGeneration, StringComparison.Ordinal)) return;
        await new ClaudeFailureStore(accounts).RecordAsync(options.ProfileId, binding.BindingGeneration,
            kind, observedAt, token).ConfigureAwait(false);
    }
    private static async Task<string?> ForwardAsync(string command, byte[] bytes, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var start = ShellStartInfo(command);
            var result = await ClaudeCli.RunAsync(start, 65536, timeout.Token, bytes).ConfigureAwait(false);
            return result.Output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return ""; }
    }

    public static ProcessStartInfo ShellStartInfo(string command)
    {
        var bash = FindGitBash();
        var start = new ProcessStartInfo
        {
            FileName = bash ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (bash is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(command); }
        else
        {
            foreach (var flag in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" }) start.ArgumentList.Add(flag);
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); "
                + "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
                + "$OutputEncoding = [Console]::OutputEncoding; " + command)));
        }
        return start;
    }

    private static string? FindGitBash()
    {
        var candidates = new List<string?> { Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe") };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (ClaudeConnectionPaths.Normalize(entry.Trim(' ', '"')) is not { } path) continue;
            if (File.Exists(Path.Combine(path, "git.exe"))) candidates.Add(Path.GetFullPath(Path.Combine(path, "..", "bin", "bash.exe")));
        }
        return candidates.FirstOrDefault(candidate => ClaudeConnectionPaths.Normalize(candidate) is not null
            && Path.GetFileName(candidate!).Equals("bash.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate));
    }
}
