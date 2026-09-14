using System.Text.Json;
using CycleArc.Providers.Usage;

namespace CycleArc.Codex;

public sealed record CodexAccountConfiguration(int Version, string SelectedId, IReadOnlyList<CodexAccountProfile> Profiles)
{
    public IReadOnlyList<string> IgnoredHomes { get; init; } = [];
}

public sealed class CodexAccountStore
{
    public const string LegacyProfileId = "default";
    private readonly string _root;
    private readonly object _gate = new();
    private string RegistryPath => Path.Combine(_root, "codex-accounts.json");
    public bool RecoveredFromBackup { get; private set; }
    public string RootDirectory => _root;

    public CodexAccountStore(string? root = null) => _root = Path.GetFullPath(root ?? Services.AppPaths.Root);

    public CodexAccountConfiguration LoadOrMigrate(string defaultHome)
    {
        lock (_gate)
        {
            RecoveredFromBackup = false;
            if (TryRead(RegistryPath, out var state)) return state!;
            if (TryRead(RegistryPath + ".bak", out state))
            {
                RecoveredFromBackup = true;
                return state!;
            }
            // Do not overwrite an unsupported/damaged registry with a new account collection.
            if (File.Exists(RegistryPath) || File.Exists(RegistryPath + ".bak"))
                throw new InvalidDataException("Account registry could not be loaded.");
            var home = CodexHomeDiscovery.Normalize(defaultHome) ?? throw new ArgumentException("Invalid Codex home.");
            state = new(1, LegacyProfileId, [new(LegacyProfileId, home, "")]);
            Save(state);
            return state;
        }
    }

    public CodexAccountProfile NewManaged(string label)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(id, Path.Combine(_root, "accounts", id, "codex-home"), CleanLabel(label), true);
    }

    public CodexAccountProfile NewClaude(string label) => new(Guid.NewGuid().ToString("N"), "", CleanLabel(label))
        { Provider = UsageProviderId.Claude };

    // A collector may only target a previously created Claude profile. Do not migrate/create
    // accounts from a statusLine invocation, and never inspect a Claude authentication home.
    public bool ContainsClaude(string id)
    {
        lock (_gate)
            return (TryRead(RegistryPath, out var state) || TryRead(RegistryPath + ".bak", out state))
                && state!.Profiles.Any(p => p.Id == id && p.Provider == UsageProviderId.Claude);
    }

    public string ClaudeStatusLinePath(string id) =>
        Path.Combine(_root, "accounts", RequireId(id), "claude-statusline.json");

    public string ClaudeConnectionPath(string id) =>
        Path.Combine(_root, "accounts", RequireId(id), "claude-connection.json");

    public string ClaudeFailurePath(string id) =>
        Path.Combine(_root, "accounts", RequireId(id), "claude-failure.json");

    public string ManagedClaudeDirectory(string id) =>
        Path.Combine(_root, "accounts", RequireId(id), "claude-home");

    public string SnapshotPath(CodexAccountProfile profile) => profile.Id == LegacyProfileId
        ? Path.Combine(_root, "codex-snapshot.json")
        : Path.Combine(_root, "accounts", RequireId(profile.Id), "quota.json");


    // A detected linked-account conflict requires explicit recovery, even if another profile
    // is later removed. Presence is deliberately fail-closed; no account data goes in this marker.
    public bool HasIdentityConflict(CodexAccountProfile profile)
    {
        var path = SnapshotPath(profile) + ".identity-conflict";
        return File.Exists(path) || Directory.Exists(path);
    }

    public bool RememberIdentityConflict(CodexAccountProfile profile)
    {
        var path = SnapshotPath(profile) + ".identity-conflict";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var marker = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            marker.Flush(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HasIdentityConflict(profile);
        }
    }

    public void Save(CodexAccountConfiguration state)
    {
        if (!IsValid(state)) throw new InvalidDataException("Invalid account registry.");
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var temp = RegistryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var hasPrimary = TryRead(RegistryPath, out var previous);
                if (!hasPrimary) TryRead(RegistryPath + ".bak", out previous);
                if (previous?.Version == 2 && state.Version == 1)
                    throw new InvalidDataException("Account registry cannot be downgraded.");
                WriteDocument(temp, state);
                if (state.Version == 2 && previous?.Version == 1)
                {
                    // Older builds try the backup after rejecting an unfamiliar primary.
                    // Upgrade that previous-good document FIRST, preserving its profiles,
                    // so a downgrade cannot fall back to v1 and erase Claude references.
                    var backupTemp = RegistryPath + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
                    try
                    {
                        WriteDocument(backupTemp, previous with { Version = 2 });
                        File.Move(backupTemp, RegistryPath + ".bak", true);
                    }
                    finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
                    File.Move(temp, RegistryPath, true);
                }
                else if (hasPrimary) File.Replace(temp, RegistryPath, RegistryPath + ".bak", true);
                else File.Move(temp, RegistryPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void WriteDocument(string path, CodexAccountConfiguration state)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, state);
        stream.Flush(true);
    }

    public static string CleanLabel(string? label) => new((label ?? "").Trim().Where(c => !char.IsControl(c)).Take(80).ToArray());
    private static string RequireId(string id) => id == LegacyProfileId || Guid.TryParseExact(id, "N", out _)
        ? id : throw new InvalidDataException("Invalid local profile identifier.");

    private bool TryRead(string path, out CodexAccountConfiguration? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            state = JsonSerializer.Deserialize<CodexAccountConfiguration>(stream);
            return state is not null && IsValid(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    private bool IsValid(CodexAccountConfiguration state)
    {
        if (state.Version is not (1 or 2) || state.Profiles is null || state.Profiles.Any(p => p is null)
            || state.IgnoredHomes is null || state.IgnoredHomes.Any(p => CodexHomeDiscovery.Normalize(p) is null)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in state.Profiles)
        {
            if (profile.Id is null || (profile.Id != LegacyProfileId && !Guid.TryParseExact(profile.Id, "N", out _))
                || !ids.Add(profile.Id) || !Enum.IsDefined(profile.Provider)
                || profile.Label != CleanLabel(profile.Label)) return false;
            if (profile.Provider == UsageProviderId.Claude)
            {
                if (state.Version != 2 || profile.Id == LegacyProfileId || profile.HomePath != "" || profile.IsManaged) return false;
                continue;
            }
            if (CodexHomeDiscovery.Normalize(profile.HomePath) is not { } home || !homes.Add(home)) return false;
            if (profile.IsManaged && (profile.Id == LegacyProfileId || !string.Equals(home,
                    Path.Combine(_root, "accounts", profile.Id, "codex-home"), StringComparison.OrdinalIgnoreCase))) return false;
        }
        return state.Profiles.Count == 0 ? state.SelectedId == "" : ids.Contains(state.SelectedId);
    }
}
