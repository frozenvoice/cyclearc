using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public enum ClaudeUninstallStatus
{
    /// <summary>No callback in this configuration directory belongs to the installation being removed.</summary>
    NotOwned,
    /// <summary>An owned callback was found and the previous Claude settings were written back.</summary>
    Restored,
    /// <summary>The connection record could not be read, so no configuration directory is proven.</summary>
    ConnectionUnavailable,
    /// <summary>Settings were locked, unreadable, malformed or changed concurrently; nothing was rewritten.</summary>
    CleanupFailed,
}

public sealed record ClaudeUninstallCleanupEntry(string ProfileId, string? ConfigDirectory, ClaudeUninstallStatus Status);

/// <summary>Why a cleanup run could not finish. Never guessed: each value has a definite cause.</summary>
public enum ClaudeUninstallIncompleteReason
{
    /// <summary>Every Claude profile was inspected.</summary>
    None,
    /// <summary>The installation being removed did not resolve to a usable path.</summary>
    InstallationRootUnusable,
    /// <summary>The account registry exists but could not be read, so the profile list is unknown.</summary>
    AccountsUnavailable,
    /// <summary>The run stopped before every profile was inspected, through its budget or an error.</summary>
    Interrupted,
}

/// <summary>
/// The result of one uninstall cleanup attempt. <c>Completed</c> is true only when every Claude
/// profile was inspected; otherwise <c>IncompleteReason</c> names what stopped it, so an
/// unconfirmed account list is never recorded as "nothing to clean up".
/// Contains no credentials, callback payloads, e-mail addresses or Claude settings content.
/// </summary>
public sealed record ClaudeUninstallCleanupReport(int Version, string InstallationRoot, DateTimeOffset CompletedAt,
    bool Completed, ClaudeUninstallIncompleteReason IncompleteReason, int Inspected, int Restored, int Failed,
    IReadOnlyList<ClaudeUninstallCleanupEntry> Profiles);

/// <summary>
/// Removes this installation's Claude callbacks before its files are deleted.
///
/// Velopack stops the app, runs the uninstall hook from the installed executable with a bounded
/// budget, ignores its result and then deletes the installation root. Uninstall can therefore be
/// neither cancelled nor retried from here: the cleanup is time-boxed, never throws, changes only
/// callbacks this installation owns, and records what it actually did. Accounts, settings, quota
/// caches, connection bindings and Claude/Codex credentials are never read for content or deleted.
/// </summary>
public static class ClaudeUninstallCleanup
{
    public const string ReportFileName = "claude-uninstall-cleanup.json";
    /// <summary>Receipt schema version. 2 added <see cref="ClaudeUninstallCleanupReport.IncompleteReason"/>.</summary>
    public const int ReportVersion = 2;
    /// <summary>Well under Velopack's 60-second hook timeout, leaving room for process start and exit.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions ReportJson =
        new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string ReportPath(CodexAccountStore accounts) => Path.Combine(accounts.RootDirectory, ReportFileName);

    /// <summary>
    /// Restores the Claude settings owned by <paramref name="installationRoot"/> for every Claude
    /// profile in <paramref name="accounts"/>. Never throws and never blocks uninstall.
    /// </summary>
    public static async Task<ClaudeUninstallCleanupReport> RunAsync(CodexAccountStore accounts,
        string installationRoot, IClock? clock = null, TimeSpan? budget = null)
    {
        var now = (clock ?? SystemClock.Instance).UtcNow;
        var root = ClaudeConnectionPaths.Normalize(installationRoot);
        var entries = new List<ClaudeUninstallCleanupEntry>();
        var reason = ClaudeUninstallIncompleteReason.InstallationRootUnusable;
        if (root is not null)
        {
            using var timeout = new CancellationTokenSource(budget ?? DefaultBudget);
            try
            {
                // An unreadable registry leaves the profile list unknown. Inspect nothing, change
                // nothing, and record why: a dead callback is better than a guessed repair, but it
                // must not look like a clean removal either.
                var profiles = accounts.ReadClaudeProfiles();
                reason = profiles.Available
                    ? ClaudeUninstallIncompleteReason.None
                    : ClaudeUninstallIncompleteReason.AccountsUnavailable;
                if (profiles.Available)
                {
                    foreach (var profileId in profiles.Ids)
                    {
                        if (timeout.IsCancellationRequested) break;
                        entries.Add(await CleanAsync(accounts, profileId, root, timeout.Token).ConfigureAwait(false));
                    }
                    if (timeout.IsCancellationRequested) reason = ClaudeUninstallIncompleteReason.Interrupted;
                }
            }
            // Reading the profile list cannot throw; anything escaping per-profile handling stopped
            // the run early, leaving the remaining profiles uninspected.
            catch (Exception ex) when (IsExpected(ex)) { reason = ClaudeUninstallIncompleteReason.Interrupted; }
        }

        var completed = reason == ClaudeUninstallIncompleteReason.None;
        var report = new ClaudeUninstallCleanupReport(ReportVersion, root ?? "", now, completed, reason, entries.Count,
            entries.Count(entry => entry.Status == ClaudeUninstallStatus.Restored),
            entries.Count(entry => entry.Status is ClaudeUninstallStatus.CleanupFailed or ClaudeUninstallStatus.ConnectionUnavailable),
            entries);
        if (entries.Count > 0 || !completed) Write(accounts, report);
        return report;
    }

    /// <summary>
    /// A callback belongs to this installation when its executable is the installation root itself
    /// or a file inside it. Those are exactly the paths uninstall is about to delete; a wrapper that
    /// names another installation's executable stays untouched.
    /// </summary>
    public static bool IsInsideInstallation(string installationRoot, string executable)
    {
        var root = ClaudeConnectionPaths.Normalize(installationRoot);
        var candidate = ClaudeConnectionPaths.Normalize(executable);
        if (root is null || candidate is null) return false;
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ClaudeUninstallCleanupEntry> CleanAsync(CodexAccountStore accounts, string profileId,
        string installationRoot, CancellationToken token)
    {
        string? directory = null;
        try
        {
            // The binding is the only evidence of which configuration directory this profile uses.
            // It is read, never rewritten: a reinstall must find the same connection record.
            var read = new ClaudeConnectionStore(accounts, profileId).Read();
            if (read.Unavailable) return new(profileId, null, ClaudeUninstallStatus.ConnectionUnavailable);
            if (read.Binding is not { } binding) return new(profileId, null, ClaudeUninstallStatus.NotOwned);
            directory = binding.ConfigDirectory;
            var restored = await ClaudeStatusLineInstaller.RestoreOwnedAsync(directory, profileId,
                executable => IsInsideInstallation(installationRoot, executable), token).ConfigureAwait(false);
            return new(profileId, directory,
                restored ? ClaudeUninstallStatus.Restored : ClaudeUninstallStatus.NotOwned);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new(profileId, directory, ClaudeUninstallStatus.CleanupFailed);
        }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ClaudeSetupException or InvalidDataException or OperationCanceledException or ArgumentException
        or JsonException or NotSupportedException;

    private static void Write(CodexAccountStore accounts, ClaudeUninstallCleanupReport report)
    {
        var path = ReportPath(accounts);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(accounts.RootDirectory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, report, ReportJson);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // The receipt is diagnostic. Uninstall proceeds and the removed files are gone either way.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
    }
}
