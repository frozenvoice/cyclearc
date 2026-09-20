using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Keeps only projected Cursor windows and binding-scoped failure metadata. A transient live
/// failure retains the last good sample; an identity mismatch clears it until reconnected.
/// </summary>
public sealed class CursorUsageCollector : ICursorUsageSource
{
    private readonly CodexAccountStore _accounts;
    private readonly string _profileId;
    private readonly string _path;
    private readonly string _identityMismatchPath;
    private readonly ICursorUsageClient _client;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private CursorConnectionBinding? _binding;
    private Cache? _state;
    private string? _email;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public CursorUsageCollector(CodexAccountStore accounts, string profileId, ICursorUsageClient client,
        IClock? clock = null)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (!ValidProfileId(profileId)) throw new ArgumentException("Invalid Cursor profile.", nameof(profileId));
        _accounts = accounts;
        _profileId = profileId;
        _path = Path.Combine(accounts.RootDirectory, "accounts", profileId, "cursor-usage.json");
        _identityMismatchPath = _path + ".identity-mismatch";
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? SystemClock.Instance;
    }

    public string CachePath => _path;

    public CursorUsageResponse ReadCached(CursorConnectionBinding? binding)
    {
        lock (_stateGate)
        {
            if (!Equals(binding, _binding))
            {
                _binding = binding;
                _email = null;
                _state = null;
                if (Eligible(binding) && IsCurrent(binding!))
                {
                    var marker = ReadMismatchMarker(binding!);
                    if (marker.Blocked || marker.Matches)
                    {
                        if (TryRead(_path, binding!, out var mismatched))
                            _state = mismatched! with { LastGood = null, Failure = "cursor-live-identity-mismatch", RetryAfter = null };
                        else
                            _state = new Cache(1, _profileId, BindingKey(binding!), null,
                                marker.AttemptedAt ?? _clock.UtcNow, "cursor-live-identity-mismatch", null);
                    }
                    else if (TryRead(_path, binding!, out var saved)) _state = saved;
                    else if (TryRead(_path + ".bak", binding!, out saved))
                        _state = saved! with { Failure = saved.Failure ?? "cursor-live-unavailable" };
                }
            }

            if (!Eligible(binding) || !IsCurrent(binding!))
            {
                // The binding may have been disconnected or rebound by another operation while
                // this source instance was idle. Do not leave the previous account's quota or
                // email available to a direct cache caller while the provider reconciles it.
                _binding = null;
                _state = null;
                _email = null;
                return new(null, "cursor-live-identity-mismatch");
            }
            return Project(_state);
        }
    }

    public async Task<CursorUsageResponse> RefreshAsync(CursorConnectionBinding? binding, CancellationToken token)
    {
        if (!Eligible(binding) || !IsCurrent(binding!))
        {
            lock (_stateGate)
            {
                _binding = null;
                _state = null;
                _email = null;
            }
            return new(null, "cursor-live-identity-mismatch");
        }
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            return ReadCached(binding) with { Failure = "cursor-live-busy" };

        try
        {
            ReadCached(binding);
            if (!Eligible(binding) || !IsCurrent(binding!)) return new(null, "cursor-live-identity-mismatch");
            Cache? previous;
            lock (_stateGate) previous = _state;
            var now = _clock.UtcNow;
            if (previous?.RetryAfter > now) return Project(previous);

            CursorUsageResponse response;
            try
            {
                response = await _client.FetchAsync(binding!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                or InvalidDataException or UnauthorizedAccessException)
            { response = new(null, "cursor-live-unavailable"); }
            token.ThrowIfCancellationRequested();

            if (!IsCurrent(binding!))
            {
                lock (_stateGate)
                {
                    _binding = null;
                    _state = null;
                    _email = null;
                }
                return new(null, "cursor-live-identity-mismatch", AttemptedAt: now);
            }
            var attempted = response.AttemptedAt ?? _clock.UtcNow;
            var failure = response.Failure;
            var sample = response.Sample;
            var verifiedIdentity = response.IdentityFingerprint is not null
                && string.Equals(response.IdentityFingerprint, binding!.IdentityFingerprint, StringComparison.Ordinal);
            if (sample is not null && !verifiedIdentity)
            {
                sample = null;
                failure = "cursor-live-identity-mismatch";
            }
            if (failure == "cursor-live-identity-mismatch") sample = null;
            if (sample is not null && !ValidSample(sample, _clock.UtcNow))
            {
                sample = null;
                failure = "cursor-live-unavailable";
            }
            if (sample is null && failure is null) failure = "cursor-live-unavailable";
            if (failure == "cursor-live-identity-mismatch") previous = null;
            var good = sample ?? previous?.LastGood;
            DateTimeOffset? retry = IsRetryableFailure(failure) && response.RetryAfter is { } retryAt
                ? ClampRetry(retryAt, _clock.UtcNow) : null;
            var verifiedSuccess = verifiedIdentity && sample is not null;
            var next = new Cache(1, _profileId, BindingKey(binding!), good, attempted, failure, retry);
            try
            {
                next = await SaveAsync(binding!, next, verifiedSuccess, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException)
            {
                // A write failure must not turn an identity mismatch into a generic failure:
                // callers and restart recovery must continue to hide the previous quota.
                next = next with
                {
                    Failure = next.Failure == "cursor-live-identity-mismatch"
                        ? "cursor-live-identity-mismatch" : "cursor-live-unavailable",
                    LastGood = next.Failure == "cursor-live-identity-mismatch" ? null : next.LastGood
                };
            }
            lock (_stateGate)
            {
                if (!IsCurrent(binding!))
                {
                    _binding = null;
                    _state = null;
                    _email = null;
                    return new(null, "cursor-live-identity-mismatch", attempted);
                }
                _binding = binding;
                _state = next;
                if (failure == "cursor-live-identity-mismatch") _email = null;
                else if (response.IdentityFingerprint == binding!.IdentityFingerprint && response.Email is not null)
                    _email = response.Email;
                return Project(next);
            }
        }
        finally { _gate.Release(); }
    }

    public static bool ValidSample(CursorUsageSample sample, DateTimeOffset now)
    {
        if (sample.ObservedAt <= DateTimeOffset.UnixEpoch || sample.ObservedAt > now.AddMinutes(1)
            || sample.Windows is null || sample.Windows.Count == 0) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in sample.Windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.LimitId) || !ids.Add(window.LimitId)) return false;
            if (window.UsedPercent is { } percent
                && (!double.IsFinite(percent) || percent is < 0 or > 100)) return false;
            if (window.UsedAmount is { } used && used < 0
                || window.LimitAmount is { } limit && limit < 0
                || window.RemainingAmount is { } remaining && remaining < 0) return false;
            if (window.ResetsAt is { } reset && reset <= DateTimeOffset.UnixEpoch) return false;
        }
        if (sample.BillingCycleStart is { } start && start <= DateTimeOffset.UnixEpoch) return false;
        if (sample.BillingCycleEnd is { } end && end <= DateTimeOffset.UnixEpoch) return false;
        return true;
    }

    private bool Eligible(CursorConnectionBinding? binding) => binding is { Disconnected: false }
        && binding.ProfileId == _profileId;

    private bool IsCurrent(CursorConnectionBinding binding) =>
        new CursorConnectionStore(_accounts, _profileId).Read() is { Unavailable: false, Binding: { } current }
        && current == binding;

    private CursorUsageResponse Project(Cache? state) => new(state?.Failure == "cursor-live-identity-mismatch"
        ? null : state?.LastGood, state?.Failure, state?.RetryAfter, Email: _email,
        AttemptedAt: state?.LastAttempted);

    private MarkerRead ReadMismatchMarker(CursorConnectionBinding binding)
    {
        try
        {
            using var stream = new FileStream(_identityMismatchPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 8192) return new(false, true, null);
            var marker = JsonSerializer.Deserialize<MismatchMarker>(stream, Options);
            if (marker is not { Version: 1 } || marker.ProfileId != _profileId
                || string.IsNullOrWhiteSpace(marker.BindingKey) || marker.BindingKey.Length != 64
                || marker.BindingKey.Any(c => c is not (
                    >= '0' and <= '9' or >= 'A' and <= 'F'))
                || marker.MarkedAt <= DateTimeOffset.UnixEpoch || marker.MarkedAt > _clock.UtcNow.AddMinutes(1))
                return new(false, true, null);
            return new(string.Equals(marker.BindingKey, BindingKey(binding), StringComparison.Ordinal), false,
                marker.MarkedAt);
        }
        catch (FileNotFoundException) { return new(false, false, null); }
        catch (DirectoryNotFoundException) { return new(false, false, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NotSupportedException)
        {
            // A present but unreadable marker is fail-closed; it must not let a backup quota
            // become visible after an identity mismatch.
            return new(false, true, null);
        }
    }

    private bool TryRead(string path, CursorConnectionBinding binding, out Cache? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 512 * 1024) return false;
            var value = JsonSerializer.Deserialize<Cache>(stream, Options);
            var now = _clock.UtcNow;
            if (value is not { Version: 1 } || value.ProfileId != _profileId
                || value.BindingKey != BindingKey(binding)
                || !ValidFailure(value.Failure)
                || value.LastAttempted <= DateTimeOffset.UnixEpoch || value.LastAttempted > now.AddMinutes(1)
                || (value.LastGood is { } sample && !ValidSample(sample, now))
                || (value.RetryAfter is { } retry && (!IsRetryableFailure(value.Failure)
                    || retry <= DateTimeOffset.UnixEpoch || retry > value.LastAttempted.AddDays(1)))) return false;
            state = value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private async Task<Cache> SaveAsync(CursorConnectionBinding binding, Cache state,
        bool clearMismatchMarker, CancellationToken token)
    {
        if (!IsCurrent(binding)) throw new InvalidDataException("Cursor binding changed.");
        if (state.Failure != "cursor-live-identity-mismatch"
            && TryRead(_path, binding, out var existing) && existing!.LastAttempted > state.LastAttempted)
            return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            var connection = new CursorConnectionStore(_accounts, _profileId);
            lock (connection.SyncRoot)
            {
                // Connection Save/Delete and this final cache commit share the same profile
                // lease. The binding check immediately precedes the atomic replace.
                if (!IsCurrent(binding)) throw new InvalidDataException("Cursor binding changed.");
                if (state.Failure != "cursor-live-identity-mismatch"
                    && TryRead(_path, binding, out var latest) && latest!.LastAttempted > state.LastAttempted)
                    return latest;

                var mismatch = state.Failure == "cursor-live-identity-mismatch";
                if (mismatch) WriteMismatchMarker(binding, state.LastAttempted);
                if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
                else File.Move(temporary, _path, true);

                // Keep both cache generations fail-closed. The marker is written first, so a
                // crash between these operations still prevents an old backup from surfacing.
                if (mismatch) File.Copy(_path, _path + ".bak", true);
                else if (clearMismatchMarker) DeleteMismatchMarker();
                return state;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void WriteMismatchMarker(CursorConnectionBinding binding, DateTimeOffset markedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_identityMismatchPath)!);
        var temporary = _identityMismatchPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new MismatchMarker(1, _profileId, BindingKey(binding), markedAt), Options);
                stream.Flush(true);
            }

            if (File.Exists(_identityMismatchPath)) File.Replace(temporary, _identityMismatchPath, null, true);
            else File.Move(temporary, _identityMismatchPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteMismatchMarker()
    {
        if (File.Exists(_identityMismatchPath)) File.Delete(_identityMismatchPath);
    }

    private static bool ValidFailure(string? failure) => failure is null or "cursor-live-auth-required"
        or "cursor-live-request-failed" or "cursor-live-rate-limited" or "cursor-live-identity-mismatch"
        or "cursor-live-unavailable" or "cursor-sand-unavailable" or "cursor-live-busy";

    private static bool IsRetryableFailure(string? failure) => failure is "cursor-live-rate-limited"
        or "cursor-sand-unavailable";

    private static DateTimeOffset ClampRetry(DateTimeOffset retry, DateTimeOffset now) =>
        retry < now.AddSeconds(5) ? now.AddSeconds(5)
        : retry > now.AddHours(24) ? now.AddHours(24) : retry;

    private static string BindingKey(CursorConnectionBinding binding) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding)));

    private static bool ValidProfileId(string value) => value == CodexAccountStore.LegacyProfileId
        || Guid.TryParseExact(value, "N", out _);

    private sealed record Cache(int Version, string ProfileId, string BindingKey, CursorUsageSample? LastGood,
        DateTimeOffset LastAttempted, string? Failure, DateTimeOffset? RetryAfter);
    private sealed record MismatchMarker(int Version, string ProfileId, string BindingKey, DateTimeOffset MarkedAt);
    private readonly record struct MarkerRead(bool Matches, bool Blocked, DateTimeOffset? AttemptedAt);
}
