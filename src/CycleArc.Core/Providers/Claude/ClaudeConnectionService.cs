using System.Collections.Concurrent;
using System.Diagnostics;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed record ClaudeConnectionOverview(ClaudeConnectionBinding? Binding, ClaudeAuthentication Authentication,
    bool Installed, string ConfigDirectory, ClaudeFailureKind FailureKind = ClaudeFailureKind.None);
public sealed record ClaudeConnectionResult(bool Success, ClaudeAuthentication Authentication,
    ClaudeConnectionBinding? Binding = null, ClaudeSetupFailure? Failure = null);

public interface IClaudeConnectionActions
{
    Task<ClaudeConnectionOverview> InspectAsync(string profileId, CancellationToken token);
    Task<ClaudeConnectionResult> ConnectAsync(string profileId, string executable, bool login, string? directory, CancellationToken token);
    Task<ClaudeConnectionResult> ReauthenticateAsync(string profileId, string executable, CancellationToken token);
    Task DisconnectAsync(string profileId, CancellationToken token);
    void OpenClaude(string profileId, string workingDirectory);
}

public sealed class ClaudeConnectionService(CodexAccountStore accounts, IClaudeCli? cli = null, IClock? clock = null) : IClaudeConnectionActions
{
    private readonly IClaudeCli _cli = cli ?? new ClaudeCli();
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ClaudeAuthentication> _identities = new(StringComparer.Ordinal);

    public string? Email(string profileId, string? fingerprint) => fingerprint is not null
        && _identities.TryGetValue(profileId, out var auth) && auth.Status == ClaudeAuthStatus.SignedIn
        && (auth.StableFingerprint == fingerprint
            || (auth.Email is { } email
                && ClaudeIdentity.LegacyFingerprint(email, auth.OrganizationId, auth.Plan) == fingerprint))
            ? auth.Email : null;

    public async Task<ClaudeConnectionOverview> InspectAsync(string profileId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await InspectCoreAsync(profileId, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ClaudeConnectionOverview> InspectCoreAsync(string profileId, CancellationToken token)
    {
        RequireProfile(profileId);
        var store = new ClaudeConnectionStore(accounts, profileId);
        var read = store.Read();
        if (read.Unavailable) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        var binding = read.Binding;
        var directory = binding?.ConfigDirectory ?? ClaudeConnectionPaths.DefaultDirectory;
        var useDefault = binding?.UseDefaultConfig ?? ClaudeConnectionPaths.UsesImplicitDirectory;
        var executable = binding is not null && File.Exists(binding.CliExecutable) ? binding.CliExecutable : _cli.FindExecutable();
        var auth = executable is null ? new ClaudeAuthentication(ClaudeAuthStatus.NotInstalled)
            : await _cli.AuthenticateAsync(executable, useDefault ? null : directory, false, token).ConfigureAwait(false);
        _identities[profileId] = auth;
        ClaudeConnectionBinding? migrationBackup = null;

        // Upgrade a legacy plan-sensitive binding only after the official identity
        // check. If the old plan is not available as evidence, stay fail-closed: a
        // changed hash cannot be recovered into an arbitrary email/org identity.
        if (binding is { Disconnected: false } legacy
            && auth.Status == ClaudeAuthStatus.SignedIn
            && ClaudeIdentityBinding.TryMigrate(auth, legacy, out var upgraded))
        {
            migrationBackup = legacy;
            binding = upgraded with
            {
                BindingGeneration = upgraded.BindingGeneration ?? Guid.NewGuid().ToString("N")
            };
            store.Save(binding);
        }

        // Upgrade an existing CycleArc wrapper only after the official identity check. A
        // cancellation or timeout cannot invalidate a still-working legacy callback.
        if (binding is { Disconnected: false } current
            && auth.Status == ClaudeAuthStatus.SignedIn
            && ClaudeIdentityBinding.Matches(auth, current)
            && ClaudeStatusLineInstaller.TryReadOwnedStatusLine(directory, profileId, out var owned)
            && owned is not null && File.Exists(owned.CycleArcExecutable)
            && ClaudeCli.IsExecutablePath(owned.CycleArcExecutable))
        {
            var oldBinding = current;
            var migrated = current.BindingGeneration is null
                ? current with { BindingGeneration = Guid.NewGuid().ToString("N") } : current;
            if (current != migrated) store.Save(migrated);
            try
            {
                await ClaudeStatusLineInstaller.InstallAsync(accounts, profileId, directory,
                    owned.CycleArcExecutable, token).ConfigureAwait(false);
                binding = migrated;
            }
            catch
            {
                store.Save(migrationBackup ?? oldBinding);
                throw;
            }
        }

        var installed = binding is { Disconnected: false } && ClaudeStatusLineInstaller.IsInstalled(directory, profileId);
        ClaudeFailureKind failureKind = ClaudeFailureKind.None;
        if (binding is { Disconnected: false } active
            && auth.Status is ClaudeAuthStatus.SignedOut or ClaudeAuthStatus.Failed or ClaudeAuthStatus.InvalidResponse or ClaudeAuthStatus.TimedOut or ClaudeAuthStatus.NotInstalled)
        {
            // SignedOut is an explicit authentication result even for legacy bindings. Other
            // legacy failures remain diagnostic-only until a generation already exists.
            if (active.BindingGeneration is null && auth.Status == ClaudeAuthStatus.SignedOut)
            {
                active = active with { BindingGeneration = Guid.NewGuid().ToString("N") };
                store.Save(active);
                binding = active;
            }
            if (active.BindingGeneration is { } generation)
            {
                failureKind = auth.Status == ClaudeAuthStatus.SignedOut ? ClaudeFailureKind.AuthRequired : ClaudeFailureKind.BridgeUnavailable;
                await new ClaudeFailureStore(accounts).RecordAsync(profileId, generation, failureKind,
                    _clock.UtcNow, token).ConfigureAwait(false);
            }
            else if (auth.Status is ClaudeAuthStatus.Failed or ClaudeAuthStatus.InvalidResponse or ClaudeAuthStatus.TimedOut or ClaudeAuthStatus.NotInstalled)
            {
                failureKind = ClaudeFailureKind.BridgeUnavailable;
            }
        }
        else if (binding is { Disconnected: false } activeSignedIn
                 && auth.Status == ClaudeAuthStatus.SignedIn
                 && !ClaudeIdentityBinding.Matches(auth, activeSignedIn))
        {
            if (activeSignedIn.BindingGeneration is null)
            {
                activeSignedIn = activeSignedIn with { BindingGeneration = Guid.NewGuid().ToString("N") };
                store.Save(activeSignedIn);
                binding = activeSignedIn;
            }
            if (activeSignedIn.BindingGeneration is { } generation)
            {
                failureKind = ClaudeFailureKind.IdentityMismatch;
                await new ClaudeFailureStore(accounts).RecordAsync(profileId, generation,
                    failureKind, _clock.UtcNow, token).ConfigureAwait(false);
            }
        }
        return new(binding, auth, installed, directory, failureKind == ClaudeFailureKind.None
            ? ActiveFailure(profileId, binding) : failureKind);
    }
    public async Task<ClaudeConnectionResult> ConnectAsync(string profileId, string executable, bool login, string? directory, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireProfile(profileId);
            var store = new ClaudeConnectionStore(accounts, profileId);
            var read = store.Read();
            if (read.Unavailable) return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable);
            var previous = read.Binding;
            var useDefault = !login && directory is null && (previous?.UseDefaultConfig ?? ClaudeConnectionPaths.UsesImplicitDirectory);
            var managedRoot = accounts.ManagedClaudeDirectory(profileId);
            var target = ClaudeConnectionPaths.Normalize(directory ?? (login
                ? Path.Combine(managedRoot, Guid.NewGuid().ToString("N"))
                : previous?.ConfigDirectory ?? ClaudeConnectionPaths.DefaultDirectory));
            if (target is null) return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.InvalidSettings);
            var cliPath = previous is not null && File.Exists(previous.CliExecutable) ? previous.CliExecutable : _cli.FindExecutable();
            if (cliPath is null) return new(false, new(ClaudeAuthStatus.NotInstalled));
            var auth = await _cli.AuthenticateAsync(cliPath, useDefault ? null : target, login, token).ConfigureAwait(false);
            if (auth.Status != ClaudeAuthStatus.SignedIn)
            {
                _identities[profileId] = auth;
                return new(false, auth);
            }
            RequireProfile(profileId);
            token.ThrowIfCancellationRequested();
            // Repeated "connect current login" resolves the already verified binding.
            // Never merge different configuration folders or identities by email alone.
            var existing = accounts.LoadOrMigrate(CodexHomeDiscovery.DefaultHome).Profiles
                .Where(profile => profile.Provider == Usage.UsageProviderId.Claude && profile.Id != profileId)
                .Select(profile => new ClaudeConnectionStore(accounts, profile.Id).Read())
                .Where(candidate => !candidate.Unavailable).Select(candidate => candidate.Binding)
                .FirstOrDefault(candidate => candidate is { Disconnected: false }
                    && string.Equals(candidate.ConfigDirectory, target, StringComparison.OrdinalIgnoreCase)
                    && candidate.UseDefaultConfig == useDefault && ClaudeIdentityBinding.Matches(auth, candidate));
            if (existing is not null)
            {
                profileId = existing.ProfileId;
                store = new ClaudeConnectionStore(accounts, profileId);
                previous = existing;
                managedRoot = accounts.ManagedClaudeDirectory(profileId);
                RequireProfile(profileId);
            }
            _identities[profileId] = auth;
            var same = previous is { Disconnected: false } && string.Equals(previous.ConfigDirectory, target, StringComparison.OrdinalIgnoreCase)
                && ClaudeIdentityBinding.Matches(auth, previous) && previous.UseDefaultConfig == useDefault;
            var generation = same && previous!.BindingGeneration is { } oldGeneration
                ? oldGeneration : Guid.NewGuid().ToString("N");
            var binding = new ClaudeConnectionBinding(2, profileId, target, cliPath,
                target.Equals(managedRoot, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(managedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                auth.StableFingerprint!, same ? previous!.ConnectedAt : _clock.UtcNow, useDefault, false, generation,
                ClaudeIdentity.SafePersistedPlan(auth.Plan));
            store.Save(binding);
            try
            {
                await ClaudeStatusLineInstaller.InstallAsync(accounts, profileId, target, executable, token).ConfigureAwait(false);
            }
            catch
            {
                if (previous is null) store.Delete(); else store.Save(previous);
                throw;
            }
            if (previous is not null && !string.Equals(previous.ConfigDirectory, target, StringComparison.OrdinalIgnoreCase))
            {
                try { await ClaudeStatusLineInstaller.RestoreAsync(previous.ConfigDirectory, profileId, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClaudeSetupException or OperationCanceledException)
                {
                    // The committed new binding is usable. An old wrapper left in a locked or
                    // edited settings file still preserves its original output.
                }
            }
            return new(true, auth, binding);
        }
        catch (ClaudeSetupException ex) { return new(false, new(ClaudeAuthStatus.Failed), Failure: ex.Failure); }
        catch (OperationCanceledException) { return new(false, new(ClaudeAuthStatus.Cancelled)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable); }
        finally { _gate.Release(); }
    }

    public async Task<ClaudeConnectionResult> ReauthenticateAsync(string profileId, string executable, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireProfile(profileId);
            var store = new ClaudeConnectionStore(accounts, profileId);
            var read = store.Read();
            if (read.Unavailable || read.Binding is not { Disconnected: false } binding)
                return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable);
            var cliPath = File.Exists(binding.CliExecutable) ? binding.CliExecutable : _cli.FindExecutable();
            if (cliPath is null || !ClaudeCli.IsExecutablePath(cliPath))
                return new(false, new(ClaudeAuthStatus.NotInstalled));
            var auth = await _cli.AuthenticateAsync(cliPath, binding.UseDefaultConfig ? null : binding.ConfigDirectory, true, token).ConfigureAwait(false);
            _identities[profileId] = auth;
            if (auth.Status != ClaudeAuthStatus.SignedIn) return new(false, auth);
            if (!ClaudeIdentityBinding.Matches(auth, binding))
            {
                if (binding.BindingGeneration is { } oldGeneration)
                    await new ClaudeFailureStore(accounts).RecordAsync(profileId, oldGeneration,
                        ClaudeFailureKind.IdentityMismatch, _clock.UtcNow, token).ConfigureAwait(false);
                return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.AlreadyLinked);
            }
            token.ThrowIfCancellationRequested();
            var generation = Guid.NewGuid().ToString("N");
            var current = ClaudeIdentityBinding.TryMigrate(auth, binding, out var migrated) ? migrated : binding;
            var updated = current with
            {
                Version = 2,
                IdentityFingerprint = auth.StableFingerprint!,
                Plan = ClaudeIdentity.SafePersistedPlan(auth.Plan),
                CliExecutable = cliPath,
                BindingGeneration = generation
            };
            store.Save(updated);
            try
            {
                await ClaudeStatusLineInstaller.InstallAsync(accounts, profileId, binding.ConfigDirectory, executable, token).ConfigureAwait(false);
            }
            catch
            {
                store.Save(binding);
                throw;
            }
            try
            {
                await new ClaudeFailureStore(accounts).RecordAsync(profileId, generation,
                    ClaudeFailureKind.None, _clock.UtcNow, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Settings and binding are already committed. A diagnostic clear is best effort.
            }
            return new(true, auth, updated);
        }
        catch (ClaudeSetupException ex) { return new(false, new(ClaudeAuthStatus.Failed), Failure: ex.Failure); }
        catch (OperationCanceledException) { return new(false, new(ClaudeAuthStatus.Cancelled)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable); }
        finally { _gate.Release(); }
    }
    public async Task DisconnectAsync(string profileId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = new ClaudeConnectionStore(accounts, profileId);
            var read = store.Read();
            if (read.Unavailable) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
            if (read.Binding is not { Disconnected: false } binding) return;
            await ClaudeStatusLineInstaller.RestoreAsync(binding.ConfigDirectory, profileId, token).ConfigureAwait(false);
            await new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profileId), profileId)
                .RecordAsync(new ClaudeStatusLineResult(ClaudeInputStatus.Missing), _clock.UtcNow, token).ConfigureAwait(false);
            store.Save(binding with { Disconnected = true });
            _identities.TryRemove(profileId, out _);
        }
        finally { _gate.Release(); }
    }

    public void OpenClaude(string profileId, string workingDirectory)
    {
        RequireProfile(profileId);
        var binding = new ClaudeConnectionStore(accounts, profileId).Read().Binding;
        if (binding is not { Disconnected: false }) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        if (!Directory.Exists(workingDirectory) || !File.Exists(binding.CliExecutable))
            throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        var start = ClaudeCli.StartInfo(binding.CliExecutable, binding.UseDefaultConfig ? null : binding.ConfigDirectory, false);
        start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        start.Environment["CYCLEARC_CLAUDE_LAUNCH"] = binding.CliExecutable;
        start.Arguments = "/d /v:off /s /k \"\"%CYCLEARC_CLAUDE_LAUNCH%\"\"";
        start.CreateNoWindow = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = false;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = null;
        start.WorkingDirectory = workingDirectory;
        Process.Start(start)?.Dispose();
    }

    private ClaudeFailureKind ActiveFailure(string profileId, ClaudeConnectionBinding? binding)
    {
        if (binding is not { Disconnected: false, BindingGeneration: not null } current) return ClaudeFailureKind.None;
        var failureRead = new ClaudeFailureStore(accounts).Read(profileId);
        if (failureRead.Unavailable) return ClaudeFailureKind.BridgeUnavailable;
        var lastGood = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profileId), profileId).Read().State?.LastGood;
        return ClaudeFailureClassification.IsActive(failureRead.State, current, lastGood)
            ? failureRead.State!.Kind : ClaudeFailureKind.None;
    }
    private void RequireProfile(string profileId)
    {
        if (!accounts.ContainsClaude(profileId)) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
    }
}
