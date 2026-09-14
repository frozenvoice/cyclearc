using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

/// <summary>Arguments embedded in the exact owned StopFailure command.</summary>
public sealed record ClaudeFailureBridgeOptions(int Version, string ProfileId, string ConfigDirectory,
    string CycleArcExecutable, string DataRoot, string IdentityFingerprint, string BindingGeneration,
    bool CreatedHooks = false, bool CreatedStopFailure = false);

public static class ClaudeFailureCommand
{
    public const string Argument = "--claude-stop-failure-bridge";
    private const string PowerShellPrefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const int MaxPayloadBytes = 8192;

    public static string Command(ClaudeFailureBridgeOptions options)
    {
        Validate(options);
        var path = Path.GetFullPath(options.CycleArcExecutable).Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var payload = Payload(options);
        var script = "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); "
            + "$OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + Argument + "' '" + payload
            + "'; exit $LASTEXITCODE";
        return PowerShellPrefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static string Payload(ClaudeFailureBridgeOptions options)
    {
        Validate(options);
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxPayloadBytes) throw new ArgumentException("Invalid Claude failure command.");
        return Convert.ToBase64String(bytes);
    }

    public static ClaudeFailureBridgeOptions Decode(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxPayloadBytes * 2)
            throw new ArgumentException("Invalid Claude failure command.");
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var options = JsonSerializer.Deserialize<ClaudeFailureBridgeOptions>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            if (options is null) throw new InvalidDataException();
            Validate(options);
            return options;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or ArgumentException)
        { throw new ArgumentException("Invalid Claude failure command.", ex); }
    }

    public static bool TryRead(string command, out ClaudeFailureBridgeOptions? options)
    {
        options = null;
        if (string.IsNullOrWhiteSpace(command) || command.Length > 65536 || !command.StartsWith(PowerShellPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var encoded = command[PowerShellPrefix.Length..].Trim();
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            var marker = "'" + Argument + "' '";
            var start = script.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            start += marker.Length;
            var end = script.IndexOf('\'', start);
            if (end <= start) return false;
            var candidate = script[start..end];
            options = Decode(candidate);
            return string.Equals(Command(options), command, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or ArgumentException or InvalidDataException)
        { return false; }
    }

    private static void Validate(ClaudeFailureBridgeOptions options)
    {
        var config = ClaudeConnectionPaths.Normalize(options.ConfigDirectory);
        var root = CodexHomeDiscovery.Normalize(options.DataRoot);
        var executable = ClaudeConnectionPaths.Normalize(options.CycleArcExecutable);
        if (options.Version != 1 || !Guid.TryParseExact(options.ProfileId, "N", out _)
            || config is null || !string.Equals(config, options.ConfigDirectory, StringComparison.OrdinalIgnoreCase)
            || executable is null || !string.Equals(executable, options.CycleArcExecutable, StringComparison.OrdinalIgnoreCase)
            || !ClaudeCli.IsExecutablePath(options.CycleArcExecutable)
            || !string.Equals(Path.GetFileName(options.CycleArcExecutable), "CycleArc.exe", StringComparison.OrdinalIgnoreCase)
            || root is null || !string.Equals(root, options.DataRoot, StringComparison.OrdinalIgnoreCase)
            || options.IdentityFingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit)
            || options.BindingGeneration is not { Length: 32 } generation
            || !Guid.TryParseExact(generation, "N", out _))
            throw new ArgumentException("Invalid Claude failure command.");
    }
}

/// <summary>Bounded same-executable receiver for Claude's official StopFailure hook.</summary>
public static class ClaudeFailureBridge
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "authentication_failed", "oauth_org_not_allowed", "rate_limit", "overloaded", "account_on_hold",
        "billing_error", "invalid_request", "model_not_found", "server_error", "max_output_tokens",
        "cloud_credential_error", "unknown"
    };

    public static async Task<int> RunAsync(ClaudeFailureBridgeOptions options, Stream input, TextWriter output,
        CodexAccountStore accounts, IClock? clock = null, CancellationToken token = default)
    {
        try
        {
            if (!MatchesBinding(options, accounts)) return 1;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Deadline);
            var bytes = await ClaudeStatusLineCommand.ReadBoundedAsync(input, deadline.Token).ConfigureAwait(false);
            if (!TryParseFailure(bytes, out var kind)) return 1;
            var observedAt = (clock ?? SystemClock.Instance).UtcNow;
            await new ClaudeFailureStore(accounts).RecordAsync(options.ProfileId, options.BindingGeneration,
                kind, observedAt, deadline.Token).ConfigureAwait(false);
            // StopFailure hook output is ignored by Claude. Keep this receiver silent so no
            // hook payload or private error detail can appear in a terminal/log.
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                   or JsonException or InvalidDataException or ArgumentException)
        { return 1; }
    }

    private static bool MatchesBinding(ClaudeFailureBridgeOptions options, CodexAccountStore accounts)
    {
        if (!accounts.ContainsClaude(options.ProfileId)) return false;
        var read = new ClaudeConnectionStore(accounts, options.ProfileId).Read();
        var binding = read.Binding;
        if (read.Unavailable || binding is not { Disconnected: false }) return false;
        var currentConfig = ClaudeConnectionPaths.Normalize(binding.ConfigDirectory);
        var suppliedConfig = ClaudeConnectionPaths.Normalize(options.ConfigDirectory);
        return currentConfig is not null && suppliedConfig is not null
            && string.Equals(currentConfig, suppliedConfig, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CodexHomeDiscovery.Normalize(accounts.RootDirectory), options.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.IdentityFingerprint, options.IdentityFingerprint, StringComparison.Ordinal)
            && string.Equals(binding.BindingGeneration, options.BindingGeneration, StringComparison.Ordinal);
    }

    private static bool TryParseFailure(byte[] bytes, out ClaudeFailureKind kind)
    {
        kind = ClaudeFailureKind.None;
        if (bytes.Length == 0 || bytes.Length > ClaudeStatusLineParser.MaxInputBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? eventName = null;
            string? error = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) return false;
                if (property.Name == "hook_event_name")
                {
                    if (property.Value.ValueKind != JsonValueKind.String) return false;
                    eventName = property.Value.GetString();
                }
                else if (property.Name == "error")
                {
                    if (property.Value.ValueKind != JsonValueKind.String) return false;
                    error = property.Value.GetString();
                }
            }
            if (eventName != "StopFailure" || error is null || !KnownErrors.Contains(error)) return false;
            kind = error is "authentication_failed" or "oauth_org_not_allowed" or "cloud_credential_error"
                ? ClaudeFailureKind.AuthRequired : ClaudeFailureKind.RequestFailed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }
}
