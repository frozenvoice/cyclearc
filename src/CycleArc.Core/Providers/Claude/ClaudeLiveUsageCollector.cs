using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed record ClaudeLiveUsageWindow(double UsedPercentage, DateTimeOffset? ResetsAt);
public sealed record ClaudeLiveUsageSample(DateTimeOffset ObservedAt, ClaudeLiveUsageWindow? FiveHour, ClaudeLiveUsageWindow? SevenDay);
public sealed record ClaudeLiveUsageUpdate(ClaudeLiveUsageSample? Sample, string? Failure = null, DateTimeOffset? AttemptedAt = null);

public interface IClaudeLiveUsageSource
{
    ClaudeLiveUsageUpdate ReadCached(ClaudeConnectionBinding? binding);
    Task<ClaudeLiveUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token);
}

public sealed record ClaudeLiveUsageResponse(ClaudeLiveUsageSample? Sample, string? Failure = null, DateTimeOffset? RetryAfter = null);
public interface IClaudeLiveUsageClient
{
    Task<ClaudeLiveUsageResponse> FetchAsync(ClaudeConnectionBinding binding, CancellationToken token);
}

/// <summary>Stores projected server quota only. Authentication material never reaches the cache.</summary>
public sealed class ClaudeLiveUsageCollector : IClaudeLiveUsageSource
{
    private readonly CodexAccountStore _accounts;
    private readonly string _profileId;
    private readonly string _path;
    private readonly IClaudeLiveUsageClient _client;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private ClaudeConnectionBinding? _binding;
    private Cache? _state;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public ClaudeLiveUsageCollector(CodexAccountStore accounts, string profileId,
        IClaudeLiveUsageClient client, IClock? clock = null)
    {
        if (!Guid.TryParseExact(profileId, "N", out _)) throw new ArgumentException("Invalid Claude profile.");
        _accounts = accounts;
        _profileId = profileId;
        _path = Path.Combine(accounts.RootDirectory, "accounts", profileId, "claude-live-usage.json");
        _client = client;
        _clock = clock ?? SystemClock.Instance;
    }

    public ClaudeLiveUsageUpdate ReadCached(ClaudeConnectionBinding? binding)
    {
        lock (_stateGate)
        {
            if (binding != _binding)
            {
                _binding = binding;
                _state = null;
                if (Eligible(binding) && IsCurrent(binding!))
                {
                    if (TryRead(_path, binding!, out var saved)) _state = saved;
                    else if (TryRead(_path + ".bak", binding!, out saved))
                        _state = saved! with { Failure = "claude-live-unavailable" };
                }
            }
            if (!Eligible(binding) || !IsCurrent(binding!)) return new(null);
            return Project(_state);
        }
    }

    public async Task<ClaudeLiveUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ReadCached(binding);
            if (!Eligible(binding) || !IsCurrent(binding!)) return new(null);
            Cache? previous;
            lock (_stateGate) previous = _state;
            if (previous?.RetryAfter > _clock.UtcNow) return Project(previous);
            var attempted = _clock.UtcNow;
            ClaudeLiveUsageResponse response;
            try { response = await _client.FetchAsync(binding!, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                or JsonException or OperationCanceledException)
            { response = new(null, "claude-live-request-failed"); }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(binding!)) return new(null, "claude-live-identity-mismatch", attempted);

            var sample = response.Sample;
            var failure = response.Failure;
            if (!ValidFailure(failure) || (sample is not null && (!ValidSample(sample, _clock.UtcNow)
                || sample.ObservedAt < binding!.ConnectedAt)) || (sample is null && failure is null))
            { sample = null; failure = "claude-live-unavailable"; }
            if (failure is not null) sample = null;
            var good = sample ?? previous?.LastGood;
            var retry = failure == "claude-live-rate-limited" ? response.RetryAfter : null;
            if (retry is not null) retry = ClampRetry(retry.Value, _clock.UtcNow);
            var next = new Cache(1, _profileId, BindingKey(binding!), good, attempted, failure, retry);
            try { next = await SaveAsync(binding!, next, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { next = next with { Failure = "claude-live-unavailable" }; }
            token.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                if (_binding != binding || !IsCurrent(binding!)) return new(null, "claude-live-identity-mismatch", attempted);
                _state = next;
                return Project(next);
            }
        }
        finally { _gate.Release(); }
    }

    private bool Eligible(ClaudeConnectionBinding? binding) => binding is { Disconnected: false }
        && binding.ProfileId == _profileId;
    private bool IsCurrent(ClaudeConnectionBinding binding) => _accounts.ContainsClaude(_profileId)
        && new ClaudeConnectionStore(_accounts, _profileId).Read() is { Unavailable: false } current
        && current.Binding == binding;
    private static ClaudeLiveUsageUpdate Project(Cache? state) => new(
        state?.Failure == "claude-live-identity-mismatch" ? null : state?.LastGood, state?.Failure, state?.LastAttempted);

    private bool TryRead(string path, ClaudeConnectionBinding binding, out Cache? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 8192) return false;
            var value = JsonSerializer.Deserialize<Cache>(stream, Options);
            var now = _clock.UtcNow;
            if (value is not { Version: 1 } || value.ProfileId != _profileId || value.BindingKey != BindingKey(binding)
                || !ValidFailure(value.Failure) || value.LastAttempted <= DateTimeOffset.UnixEpoch || value.LastAttempted > now
                || (value.LastGood is { } sample && (!ValidSample(sample, now) || sample.ObservedAt < binding.ConnectedAt))
                || (value.RetryAfter is { } retry && (value.Failure != "claude-live-rate-limited"
                    || retry <= DateTimeOffset.UnixEpoch || retry > value.LastAttempted.AddDays(1)))) return false;
            state = value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private async Task<Cache> SaveAsync(ClaudeConnectionBinding binding, Cache state, CancellationToken token)
    {
        using var lease = await new ClaudeConnectionStore(_accounts, _profileId).AcquireLeaseAsync(token).ConfigureAwait(false);
        if (!IsCurrent(binding)) throw new InvalidDataException("Claude binding changed.");
        if (TryRead(_path, binding, out var existing) && existing!.LastAttempted > state.LastAttempted) return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (TryRead(_path, binding, out _)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path, true);
            return state;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool ValidSample(ClaudeLiveUsageSample sample, DateTimeOffset now) =>
        sample.ObservedAt > DateTimeOffset.UnixEpoch && sample.ObservedAt <= now
        && (sample.FiveHour is not null || sample.SevenDay is not null)
        && ValidWindow(sample.FiveHour) && ValidWindow(sample.SevenDay);
    private static bool ValidWindow(ClaudeLiveUsageWindow? window) => window is null
        || (double.IsFinite(window.UsedPercentage) && window.UsedPercentage is >= 0 and <= 100
            && (window.ResetsAt is null || window.ResetsAt > DateTimeOffset.UnixEpoch));
    private static bool ValidFailure(string? failure) => failure is null or "claude-live-auth-required"
        or "claude-live-request-failed" or "claude-live-rate-limited" or "claude-live-identity-mismatch" or "claude-live-unavailable";
    private static DateTimeOffset ClampRetry(DateTimeOffset retry, DateTimeOffset now) =>
        retry < now.AddSeconds(5) ? now.AddSeconds(5) : retry > now.AddHours(23) ? now.AddHours(23) : retry;
    private static string BindingKey(ClaudeConnectionBinding binding) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding)));
    private sealed record Cache(int Version, string ProfileId, string BindingKey, ClaudeLiveUsageSample? LastGood,
        DateTimeOffset LastAttempted, string? Failure, DateTimeOffset? RetryAfter);
}
