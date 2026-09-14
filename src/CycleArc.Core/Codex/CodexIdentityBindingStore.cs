using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CycleArc.Codex;

public enum CodexIdentityBindingMatch
{
    Matched,
    FirstSeen,
    MissingIdentity,
    Mismatch,
    Unavailable
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodexIdentityBindingState(int Version, string ProfileId,
    string? AccountFingerprint, string? LegacyIdentityFingerprint);

public sealed record CodexIdentityBindingRead(CodexIdentityBindingState? State, bool Unavailable = false);

/// <summary>Persists only the per-profile Codex identity hashes beside its quota snapshot.</summary>
public sealed class CodexIdentityBindingStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;
    private readonly string _profileId;

    public CodexIdentityBindingStore(CodexSnapshotStore snapshots, string profileId)
        : this(snapshots.StoragePath, profileId) { }

    public CodexIdentityBindingStore(string snapshotPath, string profileId)
    {
        if (!ValidProfileId(profileId)) throw new ArgumentException("Invalid Codex profile.");
        if (CodexHomeDiscovery.Normalize(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))) is null)
            throw new ArgumentException("Invalid Codex snapshot path.");
        _profileId = profileId;
        _path = Path.GetFullPath(snapshotPath) + ".identity.json";
    }

    public string PathName => _path;

    public CodexIdentityBindingRead Read()
    {
        using var lease = Acquire();
        return ReadUnlocked();
    }

    public CodexIdentityBindingRead ReadOrSeedLegacy(string? legacyFingerprint)
    {
        using var lease = Acquire();
        var read = ReadUnlocked();
        if (read.Unavailable || read.State is not null || !ValidFingerprint(legacyFingerprint)) return read;
        SaveUnlocked(new CodexIdentityBindingState(1, _profileId, null, legacyFingerprint));
        return ReadUnlocked();
    }

    public CodexIdentityBindingMatch Match(CodexAccountIdentity identity)
    {
        var stable = identity.StableAccountFingerprint;
        if (identity.Status != CodexQuotaStatus.Available || stable is null)
            return CodexIdentityBindingMatch.MissingIdentity;

        using var lease = Acquire();
        var read = ReadUnlocked();
        if (read.Unavailable) return CodexIdentityBindingMatch.Unavailable;
        if (read.State is null)
        {
            try
            {
                SaveUnlocked(new CodexIdentityBindingState(1, _profileId, stable, identity.Fingerprint));
                return CodexIdentityBindingMatch.FirstSeen;
            }
            catch (IOException) { return CodexIdentityBindingMatch.Unavailable; }
            catch (UnauthorizedAccessException) { return CodexIdentityBindingMatch.Unavailable; }
        }

        var state = read.State;
        if (state.AccountFingerprint is { } expected)
            return string.Equals(expected, stable, StringComparison.Ordinal)
                ? CodexIdentityBindingMatch.Matched : CodexIdentityBindingMatch.Mismatch;
        if (state.LegacyIdentityFingerprint is { } legacy
            && (string.Equals(legacy, identity.Fingerprint, StringComparison.Ordinal)
                || string.Equals(legacy, stable, StringComparison.Ordinal)))
        {
            try
            {
                SaveUnlocked(state with { AccountFingerprint = stable });
                return CodexIdentityBindingMatch.Matched;
            }
            catch (IOException) { return CodexIdentityBindingMatch.Unavailable; }
            catch (UnauthorizedAccessException) { return CodexIdentityBindingMatch.Unavailable; }
        }
        return CodexIdentityBindingMatch.Mismatch;
    }

    /// <summary>Resets an expected identity only after the user's explicit confirmation.</summary>
    public void Reset(CodexAccountIdentity identity)
    {
        if (identity.Status != CodexQuotaStatus.Available || identity.StableAccountFingerprint is not { } stable)
            throw new InvalidDataException("Missing Codex identity.");
        using var lease = Acquire();
        SaveUnlocked(new CodexIdentityBindingState(1, _profileId, stable, identity.Fingerprint));
    }

    private CodexIdentityBindingRead ReadUnlocked()
    {
        var primaryExists = File.Exists(_path);
        var backupExists = File.Exists(_path + ".bak");
        if (!primaryExists && !backupExists) return new(null);
        // Identity safety is fail-closed: never fall back to a possibly stale backup.
        if (!primaryExists || !TryRead(_path, out var primary) || backupExists && !TryRead(_path + ".bak", out _))
            return new(null, true);
        return new(primary);
    }

    private void Save(CodexIdentityBindingState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        using var lease = Acquire();
        SaveUnlocked(state);
    }

    private void SaveUnlocked(CodexIdentityBindingState state)
    {
        if (!Valid(state)) throw new InvalidDataException("Invalid Codex identity binding.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Options);
                stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", true);
            else File.Move(temp, _path, true);

            // Keep both copies valid after an explicit reset or migration. This also
            // repairs a stale/corrupt backup left by an interrupted previous write.
            var backupTemp = _path + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
            try
            {
                File.Copy(_path, backupTemp, true);
                File.Move(backupTemp, _path + ".bak", true);
            }
            finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private FileStream Acquire()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1)) { Thread.Sleep(25); }
        }
    }
    private bool TryRead(string path, out CodexIdentityBindingState? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 8192) return false;
            state = JsonSerializer.Deserialize<CodexIdentityBindingState>(stream, Options);
            return state is not null && Valid(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private bool Valid(CodexIdentityBindingState state) => state.Version == 1 && state.ProfileId == _profileId
        && ValidProfileId(state.ProfileId) && (ValidFingerprint(state.AccountFingerprint) || ValidFingerprint(state.LegacyIdentityFingerprint))
        && (state.AccountFingerprint is null || ValidFingerprint(state.AccountFingerprint))
        && (state.LegacyIdentityFingerprint is null || ValidFingerprint(state.LegacyIdentityFingerprint));

    private static bool ValidProfileId(string? id) => id == CodexAccountStore.LegacyProfileId || Guid.TryParseExact(id, "N", out _);
    private static bool ValidFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}