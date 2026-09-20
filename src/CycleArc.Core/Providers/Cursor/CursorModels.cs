using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>Authentication material projected from Cursor's local state database.</summary>
/// <remarks>The access token is process-only and is never serialized, logged, or included in
/// a diagnostic string. The record exists as a narrow seam for isolated tests.</remarks>
public sealed record CursorAuthRead([property: JsonIgnore] string? AccessToken, string? Failure = null)
{
    public override string ToString() => Failure ?? (AccessToken is null ? "empty" : "available");
}

public interface ICursorAuthSource
{
    CursorAuthRead Read();
}

public sealed record CursorIdentity(string? Email, string? AccountId, string StableFingerprint)
{
    public static string? Fingerprint(string? email, string? accountId)
    {
        // Prefer the immutable server subject. Emails can be shared or changed and are only
        // a compatibility fallback for older /api/auth/me responses.
        var value = NormalizeIdentifier(accountId) ?? Normalize(email);
        return value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length is > 320 or 0 || normalized.Any(char.IsControl) ? null : normalized;
    }

    public static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length is > 320 or 0 || normalized.Any(char.IsControl) ? null : normalized;
    }

    public static bool Matches(string? expectedFingerprint, CursorIdentity identity) =>
        expectedFingerprint is not null
        && string.Equals(expectedFingerprint, identity.StableFingerprint, StringComparison.Ordinal);
}

/// <summary>
/// The persisted Cursor connection contains only a profile binding. Cursor credentials and
/// account email remain owned by Cursor and are never written by CycleArc.
/// </summary>
public sealed record CursorConnectionBinding(
    int Version,
    string ProfileId,
    string IdentityFingerprint,
    DateTimeOffset ConnectedAt,
    string Generation,
    bool Disconnected = false);

public sealed record CursorConnectionRead(CursorConnectionBinding? Binding, bool Unavailable = false);

public sealed class CursorConnectionStore
{
    private const int CurrentVersion = 1;
    private readonly string _profileId;
    private readonly string _path;
    private readonly object _gate;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public CursorConnectionStore(CodexAccountStore accounts, string profileId)
    {
        ValidateProfileId(profileId);
        _profileId = profileId;
        _path = System.IO.Path.GetFullPath(System.IO.Path.Combine(accounts.RootDirectory, "accounts", profileId,
            "cursor-connection.json"));
        _gate = Gates.GetOrAdd(_path, static _ => new object());
    }

    public CursorConnectionRead Read()
    {
        lock (_gate)
        {
            var hasPrimary = TryRead(_path, out var primary);
            var hasBackup = TryRead(_path + ".bak", out var backup);
            // Save publishes both copies. During either a disconnect or reconnect crash
            // window, a valid disconnected copy wins until both connected copies commit.
            if (hasPrimary && primary!.Disconnected) return new(primary);
            if (hasBackup && backup!.Disconnected) return new(backup);
            if (hasPrimary) return new(primary);
            if (hasBackup) return new(backup);
            return new(null, File.Exists(_path) || File.Exists(_path + ".bak"));
        }
    }

    public void Save(CursorConnectionBinding binding)
    {
        if (!Valid(binding)) throw new InvalidDataException("Invalid Cursor connection binding.");
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, binding, Options);
                    stream.Flush(true);
                }

                // Read gives a disconnected copy precedence on either side of this commit.
                WriteBackup(binding);
                File.Move(temp, _path, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak");
        }
    }

    public string Path => _path;
    internal object SyncRoot => _gate;

    private bool Valid(CursorConnectionBinding binding) =>
        binding.Version == CurrentVersion
        && string.Equals(binding.ProfileId, _profileId, StringComparison.Ordinal)
        && ValidProfileId(binding.ProfileId)
        && IsFingerprint(binding.IdentityFingerprint)
        && Guid.TryParseExact(binding.Generation, "N", out _)
        && binding.ConnectedAt > DateTimeOffset.UnixEpoch;

    private bool TryRead(string path, out CursorConnectionBinding? binding)
    {
        binding = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 8192) return false;
            binding = JsonSerializer.Deserialize<CursorConnectionBinding>(stream, Options);
            return binding is not null && Valid(binding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NotSupportedException)
        {
            binding = null;
            return false;
        }
    }

    private void WriteBackup(CursorConnectionBinding binding)
    {
        var backupTemp = _path + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
        try
        {
            using (var stream = new FileStream(backupTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, binding, Options);
                stream.Flush(true);
            }
            File.Move(backupTemp, _path + ".bak", true);
        }
        finally
        {
            if (File.Exists(backupTemp)) File.Delete(backupTemp);
        }
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool ValidProfileId(string value) => value == CodexAccountStore.LegacyProfileId
        || Guid.TryParseExact(value, "N", out _);
    private static void ValidateProfileId(string value)
    {
        if (!ValidProfileId(value)) throw new ArgumentException("Invalid Cursor profile id.", nameof(value));
    }
}

public sealed record CursorConnectionResult(
    bool Success,
    string? Failure = null,
    string? IdentityFingerprint = null,
    string? Email = null,
    CursorConnectionBinding? Binding = null);

public sealed record CursorUsageSample(
    DateTimeOffset ObservedAt,
    IReadOnlyList<CodexQuotaWindow> Windows,
    DateTimeOffset? BillingCycleStart = null,
    DateTimeOffset? BillingCycleEnd = null,
    string? MembershipType = null,
    string? LimitType = null);

public sealed record CursorUsageResponse(
    CursorUsageSample? Sample,
    string? Failure = null,
    DateTimeOffset? RetryAfter = null,
    string? Email = null,
    string? IdentityFingerprint = null,
    DateTimeOffset? AttemptedAt = null);

public interface ICursorUsageClient
{
    Task<CursorConnectionResult> ReadIdentityAsync(CancellationToken token);
    Task<CursorUsageResponse> FetchAsync(CursorConnectionBinding binding, CancellationToken token);
}

public interface ICursorUsageSource
{
    CursorUsageResponse ReadCached(CursorConnectionBinding? binding);
    Task<CursorUsageResponse> RefreshAsync(CursorConnectionBinding? binding, CancellationToken token);
}

public interface ICursorAccountOperations
{
    string? BoundIdentityFingerprint { get; }
    Task<CursorConnectionResult> ConnectCurrentAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
