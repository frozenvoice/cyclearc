using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

public sealed record ClaudeResetTimingRead(ClaudeResetTimingState? State, bool Unavailable = false);

/// <summary>Small binding-scoped continuity ledger. Backups never restore potentially revoked evidence.</summary>
public sealed class ClaudeResetTimingStore(CodexAccountStore accounts, string profileId)
{
    private const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Envelope(int Version, string BindingHash, ClaudeResetTimingState State);
    private string? _expectedStamp;
    private bool _loaded;
    private string? _expectedBindingHash;
    public string PathName => Path.Combine(Path.GetDirectoryName(accounts.ClaudeStatusLinePath(profileId))!, "claude-reset-timing.json");
    private static string Hash(ClaudeConnectionBinding binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding))));

    public ClaudeResetTimingRead Read(ClaudeConnectionBinding binding, DateTimeOffset now)
    {
        _loaded = false;
        if (!accounts.ContainsClaude(profileId) || binding.ProfileId != profileId || binding.Disconnected) return new(null, true);
        _expectedBindingHash = Hash(binding);
        if (!File.Exists(PathName))
        { _expectedStamp = null; _loaded = true; return new(null, File.Exists(PathName + ".bak")); }
        try
        {
            using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > MaximumBytes)
            { _expectedStamp = SizeStamp(stream.Length); _loaded = true; return new(null, true); }
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            _expectedStamp = Convert.ToHexString(SHA256.HashData(bytes)); _loaded = true;
            var saved = JsonSerializer.Deserialize<Envelope>(bytes, Json);
            if (saved?.Version != 1 || saved.BindingHash != Hash(binding)) return new(null, true);
            return Valid(saved.State, binding, now) ? new(saved.State) : new(null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(null, true); }
    }

    public async Task SaveAsync(ClaudeConnectionBinding binding, ClaudeResetTimingState state, CancellationToken token)
    {
        var connection = new ClaudeConnectionStore(accounts, profileId);
        using var lease = await connection.AcquireLeaseAsync(token).ConfigureAwait(false);
        if (!accounts.ContainsClaude(profileId) || connection.Read() != new ClaudeConnectionRead(binding))
            throw new IOException("Claude reset timing binding changed.");
        if (!_loaded || Hash(binding) != _expectedBindingHash || Stamp() != _expectedStamp)
            throw new IOException("Claude reset timing changed since it was read.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Hash(binding), state), Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Claude reset timing exceeds its bound.");
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, PathName, true);
            _expectedStamp = Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SizeStamp(long size) => "invalid-size:" + size;
    private string? Stamp()
    {
        if (!File.Exists(PathName)) return null;
        using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length is 0 or > MaximumBytes ? SizeStamp(stream.Length)
            : Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool Valid(ClaudeResetTimingState? state, ClaudeConnectionBinding binding, DateTimeOffset now) => state is not null
        && Clock(state.BlockedThrough, binding, now) && Window(state.FiveHour, binding, now, 300)
        && Window(state.SevenDay, binding, now, 10080);
    private static bool Clock(DateTimeOffset? at, ClaudeConnectionBinding binding, DateTimeOffset now) => at is null
        || at >= binding.ConnectedAt && at <= now;
    private static bool Usage(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100;
    private static bool Window(ClaudeResetWindowEvidence? value, ClaudeConnectionBinding binding, DateTimeOffset now, int minutes) => value is not null
        && Clock(value.ResetObservedAt, binding, now) && Clock(value.ProgressAt, binding, now)
        && Clock(value.InvalidatedAt, binding, now) && Clock(value.GrantsObservedAt, binding, now)
        && Usage(value.Used) && value.GrantsRemaining is null or >= 0
        && value.Invalidated == (value.InvalidatedAt is not null)
        && (value.ResetAt is null ? value.ResetObservedAt is null && value.ResetSource is null
            : value.ResetAt > DateTimeOffset.UnixEpoch && value.ResetObservedAt is { } origin
                && value.ResetAt <= origin.AddMinutes(minutes + 5) && value.ResetSource is ClaudeResetSource.Server or ClaudeResetSource.StatusLine)
        && value.Seen is { Count: <= 3 } && value.Seen.All(s => s is not null)
        && value.Seen.Select(s => s.Source).Distinct().Count() == value.Seen.Count
        && value.Seen.All(s => Enum.IsDefined(s.Source) && Clock(s.At, binding, now) && Usage(s.Used)
            && (s.ResetAt is null || s.ResetAt > DateTimeOffset.UnixEpoch && s.ResetAt <= s.At.AddMinutes(minutes + 5)));
}
