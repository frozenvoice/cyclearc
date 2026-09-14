using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

public static class ClaudeConnectionPaths
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    public static string ImplicitDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public static bool UsesImplicitDirectory => Normalize(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) is null;
    public static string DefaultDirectory => Normalize(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) ?? ImplicitDirectory;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeConnectionBinding(int Version, string ProfileId, string ConfigDirectory,
    string CliExecutable, bool Managed, string IdentityFingerprint, DateTimeOffset ConnectedAt, bool UseDefaultConfig = false,
    bool Disconnected = false, string? BindingGeneration = null, string? Plan = null);

public sealed record ClaudeConnectionRead(ClaudeConnectionBinding? Binding, bool Unavailable = false);

/// <summary>Identity comparison and one-way migration for Claude connection bindings.</summary>
public static class ClaudeIdentityBinding
{
    public static bool Matches(ClaudeAuthentication authentication, ClaudeConnectionBinding binding)
    {
        if (binding.Version is not (1 or 2)
            || authentication.Status != ClaudeAuthStatus.SignedIn
            || authentication.StableFingerprint is not { } stable)
            return false;

        if (binding.Version >= 2)
            return string.Equals(stable, binding.IdentityFingerprint, StringComparison.Ordinal);

        // v1 stored the plan-sensitive hash. A stable hash in a hand-created or
        // partially migrated v1 fixture is also safe to accept because it proves
        // the current email/org pair directly.
        if (string.Equals(stable, binding.IdentityFingerprint, StringComparison.Ordinal)) return true;
        if (authentication.Email is not { } email) return false;

        // A v1 file that was written by an intermediate build may have retained the
        // previous plan. This is evidence for migration, rather than a reason to
        // guess at an arbitrary historical plan.
        var candidates = ClaudeIdentity.KnownPlanValues.AsEnumerable();
        if (binding.Plan is { } previousPlan) candidates = candidates.Prepend(previousPlan);
        if (authentication.Plan is { } currentPlan) candidates = candidates.Prepend(currentPlan);
        return candidates.Distinct(StringComparer.Ordinal).Any(plan =>
            string.Equals(ClaudeIdentity.LegacyFingerprint(email, authentication.OrganizationId, plan),
                binding.IdentityFingerprint, StringComparison.Ordinal));
    }

    public static bool TryMigrate(ClaudeAuthentication authentication, ClaudeConnectionBinding binding,
        out ClaudeConnectionBinding migrated)
    {
        migrated = binding;
        if (binding.Version != 1 || authentication.Status != ClaudeAuthStatus.SignedIn
            || authentication.StableFingerprint is not { } stable || !Matches(authentication, binding))
            return false;

        migrated = binding with { Version = 2, IdentityFingerprint = stable,
            Plan = ClaudeIdentity.SafePersistedPlan(authentication.Plan) };
        return true;
    }
}

// Separate provider metadata keeps Codex account/home/cache formats unchanged. No emails or credentials.
public sealed class ClaudeConnectionStore(CodexAccountStore accounts, string profileId)
{
    public string PathName => accounts.ClaudeConnectionPath(profileId);
    public ClaudeConnectionRead Read()
    {
        if (!File.Exists(PathName)) return new(null);
        try
        {
            using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 8192) return new(null, true);
            var binding = JsonSerializer.Deserialize<ClaudeConnectionBinding>(stream);
            return binding is not null && Valid(binding) ? new(binding) : new(null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(null, true); }
    }
    public void Save(ClaudeConnectionBinding binding)
    {
        if (!Valid(binding)) throw new InvalidDataException("Invalid Claude connection.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var lease = AcquireLease();
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, binding); stream.Flush(true);
            }
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete()
    {
        using var lease = AcquireLease();
        if (File.Exists(PathName)) File.Delete(PathName);
    }

    /// <summary>
    /// Serializes a cross-process binding read/commit with statusLine receipt writes.
    /// Callers must dispose the returned stream and must not call Save/Delete while it
    /// is held, because those methods acquire the same lease.
    /// </summary>
    internal async Task<FileStream> AcquireLeaseAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(PathName + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private FileStream AcquireLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(PathName + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(25);
            }
        }
    }

    private bool Valid(ClaudeConnectionBinding binding) => binding.Version is 1 or 2 && binding.ProfileId == profileId
        && Guid.TryParseExact(profileId, "N", out _) && ClaudeConnectionPaths.Normalize(binding.ConfigDirectory) is { } directory
        && directory == binding.ConfigDirectory
        && (!binding.UseDefaultConfig || string.Equals(directory, ClaudeConnectionPaths.ImplicitDirectory, StringComparison.OrdinalIgnoreCase))
        && ClaudeCli.IsExecutablePath(binding.CliExecutable) && binding.ConnectedAt > DateTimeOffset.UnixEpoch
        && ClaudeIdentity.IsFingerprint(binding.IdentityFingerprint)
        && (binding.BindingGeneration is null || Guid.TryParseExact(binding.BindingGeneration, "N", out _))
        && ClaudeIdentity.IsSafePersistedPlan(binding.Plan);
}
