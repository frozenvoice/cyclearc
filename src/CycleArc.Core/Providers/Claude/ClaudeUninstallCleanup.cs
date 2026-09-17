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

/// <summary>
/// The result of one uninstall cleanup attempt. <c>Completed</c> is false when the budget ran
/// out or the installation root was unusable, so an incomplete run is never recorded as a success.
/// Contains no credentials, callback payloads, e-mail addresses or Claude settings content.
/// </summary>
public sealed record ClaudeUninstallCleanupReport(int Version, string InstallationRoot, DateTimeOffset CompletedAt,
    bool Completed, int Inspected, int Restored, int Failed, IReadOnlyList<ClaudeUninstallCleanupEntry> Profiles);

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
        var completed = false;
        if (root is not null)
        {
            using var timeout = new CancellationTokenSource(budget ?? DefaultBudget);
            try
            {
                foreach (var profileId in accounts.ClaudeProfileIds())
                {
                    if (timeout.IsCancellationRequested) break;
                    entries.Add(await CleanAsync(accounts, profileId, root, timeout.Token).ConfigureAwait(false));
                }
                completed = !timeout.IsCancellationRequested;
            }
            catch (Exception ex) when (IsExpected(ex)) { completed = false; }
        }

        var report = new ClaudeUninstallCleanupReport(1, root ?? "", now, completed, entries.Count,
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
