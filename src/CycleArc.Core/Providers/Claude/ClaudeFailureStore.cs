using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

public enum ClaudeFailureKind
{
    None,
    AuthRequired,
    RequestFailed,
    IdentityMismatch,
    BridgeUnavailable
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeFailureState(int Version, string ProfileId, string BindingGeneration,
    DateTimeOffset ObservedAt, ClaudeFailureKind Kind);

public sealed record ClaudeFailureRead(ClaudeFailureState? State, bool Unavailable = false);

/// <summary>Stores only a bounded, projected Claude hook failure category.</summary>
public sealed class ClaudeFailureStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly CodexAccountStore _accounts;

    public ClaudeFailureStore(CodexAccountStore accounts) => _accounts = accounts;

    public ClaudeFailureRead Read(string profileId)
    {
        ValidateProfile(profileId);
        var path = _accounts.ClaudeFailurePath(profileId);
        if (TryRead(path, profileId, out var state)) return new(state);
        if (TryRead(path + ".bak", profileId, out state)) return new(state, true);
        return new(null, File.Exists(path) || File.Exists(path + ".bak"));
    }

    public async Task<ClaudeFailureState> RecordAsync(string profileId, string bindingGeneration,
        ClaudeFailureKind kind, DateTimeOffset observedAt, CancellationToken token = default)
    {
        ValidateProfile(profileId);
        if (!ValidGeneration(bindingGeneration) || !Enum.IsDefined(kind) || observedAt <= DateTimeOffset.UnixEpoch)
            throw new InvalidDataException("Invalid projected Claude failure.");

        var path = _accounts.ClaudeFailurePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = await AcquireAsync(path, token).ConfigureAwait(false);
        var currentBinding = new ClaudeConnectionStore(_accounts, profileId).Read().Binding;
        if (currentBinding is not { Disconnected: false }
            || currentBinding.BindingGeneration != bindingGeneration) throw new InvalidDataException("Claude binding changed.");
        var previous = Read(profileId).State;
        if (previous is not null && previous.BindingGeneration == bindingGeneration)
        {
            if (observedAt < previous.ObservedAt) return previous;
            if (previous.Kind is ClaudeFailureKind.AuthRequired or ClaudeFailureKind.IdentityMismatch
                && kind is not (ClaudeFailureKind.None or ClaudeFailureKind.AuthRequired or ClaudeFailureKind.IdentityMismatch))
                return previous;
        }

        var state = new ClaudeFailureState(1, profileId, bindingGeneration, observedAt, kind);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                               4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (TryRead(path, profileId, out _)) File.Replace(temp, path, path + ".bak", true);
            else File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return state;
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private bool TryRead(string path, string profileId, out ClaudeFailureState? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 8192) return false;
            state = JsonSerializer.Deserialize<ClaudeFailureState>(stream, Options);
            return state is not null && Valid(state, profileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private static bool Valid(ClaudeFailureState state, string profileId) => state.Version == 1
        && state.ProfileId == profileId && Guid.TryParseExact(profileId, "N", out _)
        && ValidGeneration(state.BindingGeneration) && Enum.IsDefined(state.Kind)
        && state.ObservedAt > DateTimeOffset.UnixEpoch;

    private static bool ValidGeneration(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out _);

    private static void ValidateProfile(string profileId)
    {
        if (!Guid.TryParseExact(profileId, "N", out _)) throw new ArgumentException("Invalid Claude profile.");
    }
}

public static class ClaudeFailureClassification
{
    public static bool IsActive(ClaudeFailureState? state, ClaudeConnectionBinding? binding,
        ClaudeRateLimitSample? lastGood) => IsActive(state, binding, lastGood?.ReceivedAt);

    public static bool IsActive(ClaudeFailureState? state, ClaudeConnectionBinding? binding, DateTimeOffset? lastGoodReceivedAt) =>
        state is not null && state.Kind != ClaudeFailureKind.None && binding is { Disconnected: false }
        && state.ProfileId == binding.ProfileId
        && !string.IsNullOrWhiteSpace(binding.BindingGeneration)
        && string.Equals(state.BindingGeneration, binding.BindingGeneration, StringComparison.Ordinal)
        && (state.Kind is ClaudeFailureKind.AuthRequired or ClaudeFailureKind.IdentityMismatch
            || lastGoodReceivedAt is null || state.ObservedAt > lastGoodReceivedAt.Value);
    public static string? TechnicalDetail(ClaudeFailureKind kind) => kind switch
    {
        ClaudeFailureKind.AuthRequired => "claude-auth-required",
        ClaudeFailureKind.RequestFailed => "claude-request-failed",
        ClaudeFailureKind.IdentityMismatch => "claude-identity-mismatch",
        ClaudeFailureKind.BridgeUnavailable => "claude-bridge-unavailable",
        _ => null
    };

}
