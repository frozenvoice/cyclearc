using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed record ClaudeDesktopUsageUpdate(ClaudeDesktopUsageSample? Sample, string? Failure = null);

public interface IClaudeDesktopUsageSource
{
    Task<ClaudeDesktopUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token);
}

/// <summary>Reads Desktop's quota-only history after verifying the connected personal subscription.
/// Never opens credentials, conversation history or a network quota endpoint.</summary>
public sealed class ClaudeDesktopUsageCollector : IClaudeDesktopUsageSource
{
    private readonly CodexAccountStore _accounts;
    private readonly string _profileId;
    private readonly string _path;
    private readonly Func<ClaudeConnectionBinding, CancellationToken, Task<ClaudeAuthentication>> _authenticate;
    private readonly Func<string, DateTimeOffset, ClaudeDesktopUsageRead> _read;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeConnectionBinding? _binding;
    private ClaudeAuthentication? _identity;
    private ClaudeDesktopUsageSample? _lastGood;
    private DateTimeOffset? _verifiedSample;
    private DateTimeOffset _retryAfter;
    private bool _cacheUnavailable;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ClaudeDesktopUsageCollector(CodexAccountStore accounts, string profileId,
        Func<ClaudeConnectionBinding, CancellationToken, Task<ClaudeAuthentication>> authenticate,
        IClock? clock = null, Func<string, DateTimeOffset, ClaudeDesktopUsageRead>? read = null)
    {
        if (!Guid.TryParseExact(profileId, "N", out _)) throw new ArgumentException("Invalid Claude profile.");
        _accounts = accounts;
        _profileId = profileId;
        _path = Path.Combine(accounts.RootDirectory, "accounts", profileId, "claude-desktop-usage.json");
        _authenticate = authenticate;
        _clock = clock ?? SystemClock.Instance;
        _read = read ?? new ClaudeDesktopUsageReader().Read;
    }

    public async Task<ClaudeDesktopUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (binding != _binding)
            {
                _binding = binding;
                _identity = null;
                _lastGood = null;
                _verifiedSample = null;
                _retryAfter = default;
                _cacheUnavailable = false;
            }
            if (binding is null || binding.Disconnected || binding.ProfileId != _profileId) return new(null);
            var now = _clock.UtcNow;
            var authenticated = false;
            if (_identity is null)
            {
                if (now < _retryAfter) return new(_lastGood, "claude-desktop-identity-unverified");
                if (!await VerifyAsync(binding, token).ConfigureAwait(false))
                    return new(_lastGood, "claude-desktop-identity-unverified");
                authenticated = true;
                if (_lastGood is null) _lastGood = ReadCache(binding, now);
            }

            // History contains an organization ID, not a member ID. Limit this source to
            // individual subscriptions; an organization match alone cannot identify a Team member.
            var verifiedIdentity = _identity!;
            if (!Eligible(verifiedIdentity)) return new(null);
            var read = await Task.Run(() => _read(verifiedIdentity.OrganizationId!, now), token).ConfigureAwait(false);
            var candidate = read.Sample;
            if (candidate is not null && (!ValidSample(candidate, now) || candidate.ObservedAt < binding.ConnectedAt))
                candidate = null;
            if (candidate is not null && candidate.ObservedAt != _verifiedSample && !authenticated)
            {
                var organization = verifiedIdentity.OrganizationId;
                if (now < _retryAfter || !await VerifyAsync(binding, token).ConfigureAwait(false)
                    || !Eligible(_identity!) || _identity!.OrganizationId != organization)
                    return new(_lastGood, "claude-desktop-identity-unverified");
            }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(binding)) return new(null, "claude-desktop-identity-unverified");
            if (candidate is not null)
            {
                _verifiedSample = candidate.ObservedAt;
                if (_lastGood is null || candidate.ObservedAt > _lastGood.ObservedAt
                    || (candidate == _lastGood && _cacheUnavailable))
                {
                    try
                    {
                        _lastGood = await SaveAsync(binding, candidate, token).ConfigureAwait(false);
                        _cacheUnavailable = false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    { return new(_lastGood, "claude-desktop-unavailable"); }
                }
            }
            var unavailable = read.Unavailable || _cacheUnavailable || (candidate is null && _lastGood is not null)
                || (candidate is not null && _lastGood is not null && candidate.ObservedAt <= _lastGood.ObservedAt
                    && candidate != _lastGood);
            return new(_lastGood, unavailable ? "claude-desktop-unavailable" : null);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> VerifyAsync(ClaudeConnectionBinding binding, CancellationToken token)
    {
        var auth = await _authenticate(binding, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (ClaudeIdentityBinding.Matches(auth, binding) && IsCurrent(binding))
        {
            _identity = auth;
            _retryAfter = default;
            return true;
        }
        _identity = null;
        _retryAfter = _clock.UtcNow.AddSeconds(30);
        return false;
    }

    private bool IsCurrent(ClaudeConnectionBinding binding) => _accounts.ContainsClaude(_profileId)
        && new ClaudeConnectionStore(_accounts, _profileId).Read() is { Unavailable: false } current
        && current.Binding == binding;

    private ClaudeDesktopUsageSample? ReadCache(ClaudeConnectionBinding binding, DateTimeOffset now)
    {
        if (TryReadCache(_path, binding, now, out var sample)) return sample;
        _cacheUnavailable = File.Exists(_path) || File.Exists(_path + ".bak");
        return TryReadCache(_path + ".bak", binding, now, out sample) ? sample : null;
    }

    private bool TryReadCache(string path, ClaudeConnectionBinding binding, DateTimeOffset now,
        out ClaudeDesktopUsageSample? sample)
    {
        sample = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 4096) return false;
            var cache = JsonSerializer.Deserialize<Cache>(stream, Options);
            if (cache is not { Version: 1 } || cache.ProfileId != _profileId || cache.BindingKey != BindingKey(binding)
                || cache.Sample is null || !ValidSample(cache.Sample, now) || cache.Sample.ObservedAt < binding.ConnectedAt)
                return false;
            sample = cache.Sample;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private async Task<ClaudeDesktopUsageSample> SaveAsync(ClaudeConnectionBinding binding, ClaudeDesktopUsageSample sample, CancellationToken token)
    {
        var connections = new ClaudeConnectionStore(_accounts, _profileId);
        using var lease = await connections.AcquireLeaseAsync(token).ConfigureAwait(false);
        if (!IsCurrent(binding)) throw new InvalidDataException("Claude binding changed.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // The connection lease also serializes multiple collectors and generation changes.
        if (TryReadCache(_path, binding, _clock.UtcNow, out var existing) && existing!.ObservedAt > sample.ObservedAt)
        {
            return existing;
        }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Cache(1, _profileId, BindingKey(binding), sample), Options, token)
                    .ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (TryReadCache(_path, binding, _clock.UtcNow, out _)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path, true);
            return sample;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidSample(ClaudeDesktopUsageSample sample, DateTimeOffset now) =>
        sample.ObservedAt > DateTimeOffset.UnixEpoch && sample.ObservedAt <= now
        && (sample.FiveHour is not null || sample.SevenDay is not null)
        && ValidPercent(sample.FiveHour) && ValidPercent(sample.SevenDay);
    private static bool ValidPercent(double? value) => value is null || (double.IsFinite(value.Value) && value is >= 0 and <= 100);
    private static bool Eligible(ClaudeAuthentication identity) => identity.Plan is "pro" or "max" or "max_5x" or "max_20x"
        && Guid.TryParseExact(identity.OrganizationId, "D", out _);
    private static string BindingKey(ClaudeConnectionBinding binding) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding)));
    private sealed record Cache(int Version, string ProfileId, string BindingKey, ClaudeDesktopUsageSample Sample);
}
