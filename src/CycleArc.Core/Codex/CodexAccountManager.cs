using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;

namespace CycleArc.Codex;

public sealed record CodexDiscoveryResult(int Added, int Failed, int SignedOut);

/// <summary>Owns an extensible collection of isolated services and one shared refresh.</summary>
public sealed class CodexAccountManager
{
    private readonly object _gate = new();
    private readonly CodexAccountStore _store;
    private readonly Func<CodexAccountProfile, IUsageAccountService> _createService;
    private readonly Dictionary<string, IUsageAccountService> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<CodexQuotaSnapshot>> _serviceHandlers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _processSlots = new(2, 2);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly SemaphoreSlim _passiveGate = new(1, 1);
    private CodexAccountConfiguration _configuration;
    private string? _loginProfile;
    private readonly HashSet<string> _identityConflicts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshingProfiles = new(StringComparer.Ordinal);

    public CodexAccountManager(CodexAccountStore store, string defaultHome,
        Func<CodexAccountProfile, CodexQuotaService> createService, Func<string?> configuredPath)
        : this(store, defaultHome, [new CodexUsageProvider(createService, configuredPath)]) { }

    public CodexAccountManager(CodexAccountStore store, string defaultHome, IEnumerable<IUsageProvider> providers)
    {
        _store = store;
        _configuration = store.LoadOrMigrate(defaultHome);
        var registered = providers.ToDictionary(provider => provider.Id);
        _createService = profile => registered.TryGetValue(profile.Provider, out var provider)
            ? provider.Create(profile) : throw new InvalidDataException("Account provider is unavailable.");
        foreach (var profile in _configuration.Profiles)
        {
            AddService(profile);
            if (profile.Provider == UsageProviderId.Codex && !profile.IsManaged && store.HasIdentityConflict(profile))
                _identityConflicts.Add(profile.Id);
        }
        _ = Accounts; // Preserve legacy duplicate conflicts before asynchronous identity migration.
        Refresh = new CodexRefreshCoordinator(RefreshAllAsync);
        Refresh.StateChanged += () => Changed?.Invoke();
    }

    public event Action? Changed;
    public CodexRefreshCoordinator Refresh { get; }
    public string SelectedId { get { lock (_gate) return _configuration.SelectedId; } }
    public bool IsSigningIn { get { lock (_gate) return _loginProfile is not null; } }
    public IReadOnlyList<CodexAccountView> Accounts
    {
        get
        {
            lock (_gate)
            {
                var identities = _configuration.Profiles
                    .Where(profile => profile.Provider == UsageProviderId.Codex)
                    .Select(profile => (profile, service: _services[profile.Id]))
                    .Select(item => (item.profile, fingerprint: IdentityOf(item.service)))
                    .Where(item => item.fingerprint is not null)
                    .ToArray();
                var repeated = identities.Where(item => _services[item.profile.Id].Snapshot.Status
                        is not (CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound))
                    .GroupBy(item => item.fingerprint!, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                var managed = identities.Where(item => item.profile.IsManaged)
                    .Select(item => item.fingerprint!)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var item in identities.Where(item => !item.profile.IsManaged && managed.Contains(item.fingerprint!)))
                    _identityConflicts.Add(item.profile.Id);
                foreach (var profile in _configuration.Profiles.Where(profile => _identityConflicts.Contains(profile.Id)))
                    if (!_store.HasIdentityConflict(profile)) _store.RememberIdentityConflict(profile);
                return _configuration.Profiles.Select(profile =>
                {
                    var service = _services[profile.Id];
                    var fingerprint = IdentityOf(service);
                    var identityConflict = _identityConflicts.Contains(profile.Id);
                    var snapshot = identityConflict
                        ? CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "codex-identity-conflict")
                        : service.Snapshot;
                    var email = identityConflict ? null : service.Email;
                    var hasMatchingIdentity = profile.Provider == UsageProviderId.Codex
                        && fingerprint is not null && repeated.Contains(fingerprint) && !identityConflict;
                    return new CodexAccountView(profile, snapshot, email, _loginProfile == profile.Id, hasMatchingIdentity)
                        { IsConnected = !identityConflict && service.IsConnected };
                }).ToArray();
            }
        }
    }
    public CodexAccountView? Selected => Accounts.FirstOrDefault(a => a.Profile.Id == SelectedId);
    public CodexQuotaSnapshot Snapshot => Selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut);

    public void Select(string id)
    {
        lock (_gate)
        {
            if (!_services.ContainsKey(id) || id == _configuration.SelectedId) return;
            Save(_configuration with { SelectedId = id });
        }
        Changed?.Invoke();
    }

    public void Rename(string id, string label)
    {
        lock (_gate) Save(_configuration with { Profiles = _configuration.Profiles.Select(p =>
            p.Id == id ? p with { Label = CodexAccountStore.CleanLabel(label) } : p).ToArray() });
        Changed?.Invoke();
    }

    public CodexAccountProfile AddClaude(string label)
    {
        CodexAccountProfile profile;
        lock (_gate)
        {
            profile = _store.NewClaude(label);
            var service = _createService(profile);
            // Version 2 makes older Codex-only builds refuse this registry, rather than
            // treating a Claude profile as a Codex login on downgrade.
            Save(_configuration with { Version = 2, Profiles = _configuration.Profiles.Append(profile).ToArray(),
                SelectedId = _configuration.Profiles.Count == 0 ? profile.Id : _configuration.SelectedId });
            AddService(profile, service);
        }
        Changed?.Invoke();
        return profile;
    }

    // The modal flow finishes authentication before returning. Only its own new,
    // never-connected draft may be discarded; existing profiles and data stay intact.
    public void ConfigureNewClaude(string label, Action<CodexAccountProfile> configure)
    {
        var profile = AddClaude(label);
        try { configure(profile); }
        finally
        {
            var connection = new ClaudeConnectionStore(_store, profile.Id).Read();
            var usage = new ClaudeStatusLineStore(_store.ClaudeStatusLinePath(profile.Id), profile.Id).Read();
            if (connection is { Binding: null, Unavailable: false } && usage is { State: null, Unavailable: false })
                Remove(profile.Id, discardClaudeDraft: true);
        }
    }

    public bool Move(string id, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        lock (_gate)
        {
            var profiles = _configuration.Profiles.ToArray();
            var index = Array.FindIndex(profiles, profile => profile.Id == id);
            var destination = index + direction;
            if (index < 0 || destination < 0 || destination >= profiles.Length) return false;
            (profiles[index], profiles[destination]) = (profiles[destination], profiles[index]);
            // Ordering is presentation only; selected identity, services, homes and caches stay bound to their IDs.
            Save(_configuration with { Profiles = profiles });
        }
        Changed?.Invoke();
        return true;
    }

    // Forget only local references. Never delete/log out shared Codex credentials or histories.
    public bool Remove(string id) => Remove(id, discardClaudeDraft: false);

    private bool Remove(string id, bool discardClaudeDraft)
    {
        lock (_gate)
        {
            if (id == _loginProfile || !_services.TryGetValue(id, out var service)) return false;
            var profile = _configuration.Profiles.First(p => p.Id == id);
            // A passive read of an empty Claude inbox can finish after forgetting the
            // draft. It performs no login, settings write or quota collection.
            if (service.IsRefreshing && !(discardClaudeDraft && profile.Provider == UsageProviderId.Claude)) return false;
            var profiles = _configuration.Profiles.Where(p => p.Id != id).ToArray();
            Save(_configuration with { Profiles = profiles,
                SelectedId = _configuration.SelectedId == id ? profiles.FirstOrDefault()?.Id ?? "" : _configuration.SelectedId,
                IgnoredHomes = profile.Provider == UsageProviderId.Codex
                    ? _configuration.IgnoredHomes.Append(profile.HomePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : _configuration.IgnoredHomes });
            if (_serviceHandlers.Remove(id, out var handler)) service.Changed -= handler;
            _identityConflicts.Remove(id);
            _services.Remove(id);
        }
        Changed?.Invoke();
        return true;
    }

    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate) return _services.Any(pair => pair.Key != _loginProfile && pair.Value.ShouldRefresh(now, interval));
    }

    // Passive sources need no remote probe. Poll their projected inbox independently of
    // the Codex interval, without starting Codex or touching shared manual-refresh state.
    public async Task RefreshPassiveAsync(CancellationToken token)
    {
        if (!await _passiveGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            IUsageAccountService[] services;
            lock (_gate) services = _services.Values.Where(service => service.ReceivesPassiveUpdates).ToArray();
            foreach (var service in services)
                await service.RefreshAsync(token).ConfigureAwait(false);
        }
        finally { _passiveGate.Release(); }
    }

    public Task<CodexRefreshResult> RefreshAutomaticallyAsync(TimeSpan interval, CancellationToken token)
    {
        // All entry points join the same coordinator, including automatic checks.
        // Each service has its own failure cooldown; the batch itself never overlaps.
        lock (_gate)
        {
            if (!Refresh.IsRefreshing) _automaticInterval = interval;
            return Refresh.RefreshAsync(token);
        }
    }
    private TimeSpan? _automaticInterval;

    public Task<CodexRefreshResult> RefreshManuallyAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (!Refresh.IsRefreshing) _automaticInterval = null;
            return Refresh.RefreshAsync(token);
        }
    }

    private async Task<CodexRefreshResult> RefreshAllAsync(CancellationToken token)
    {
        CodexAccountProfile[] profiles;
        TimeSpan? interval;
        lock (_gate) { profiles = _configuration.Profiles.ToArray(); interval = _automaticInterval; }
        await Task.WhenAll(profiles.Select(async profile =>
        {
            await _processSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                IUsageAccountService? service;
                lock (_gate)
                {
                    if (profile.Id == _loginProfile || !_services.TryGetValue(profile.Id, out service)) return;
                    if (interval is { } age && !service.ShouldRefresh(DateTimeOffset.Now, age)) return;
                    _refreshingProfiles.Add(profile.Id);
                }
                await service.RefreshAsync(token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _refreshingProfiles.Remove(profile.Id);
                _processSlots.Release();
            }
        })).ConfigureAwait(false);
        return new(Snapshot, Snapshot.Status != CodexQuotaStatus.Available, null);
    }

    public async Task<CodexDiscoveryResult> DiscoverAsync(IEnumerable<string> homes, bool automatic, CancellationToken token)
    {
        await _discoveryGate.WaitAsync(token).ConfigureAwait(false);
        var added = 0;
        var failed = 0;
        var signedOut = 0;
        try
        {
            foreach (var home in CodexHomeDiscovery.Candidates(homes, Directory.Exists))
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                    if (_configuration.Profiles.Any(p => p.Provider == UsageProviderId.Codex && string.Equals(p.HomePath, home, StringComparison.OrdinalIgnoreCase))
                        || (automatic && _configuration.IgnoredHomes.Contains(home, StringComparer.OrdinalIgnoreCase))) continue;
                var profile = new CodexAccountProfile(Guid.NewGuid().ToString("N"), home, "");
                var service = _createService(profile);
                if (service is not ICodexAccountOperations operations) { failed++; continue; }
                await _processSlots.WaitAsync(token).ConfigureAwait(false);
                CodexAccountIdentity identity;
                try { identity = await operations.ProbeAccountAsync(token).ConfigureAwait(false); }
                finally { _processSlots.Release(); }
                if (identity.Status != CodexQuotaStatus.Available)
                {
                    if (identity.Status == CodexQuotaStatus.SignedOut) signedOut++; else failed++;
                    continue;
                }
                lock (_gate)
                {
                    if (_configuration.Profiles.Any(p => p.Provider == UsageProviderId.Codex && string.Equals(p.HomePath, home, StringComparison.OrdinalIgnoreCase))) continue;
                    Save(_configuration with { Profiles = _configuration.Profiles.Append(profile).ToArray(),
                        SelectedId = _configuration.Profiles.Count == 0 ? profile.Id : _configuration.SelectedId,
                        IgnoredHomes = _configuration.IgnoredHomes.Where(p => !string.Equals(p, home, StringComparison.OrdinalIgnoreCase)).ToArray() });
                    AddService(profile, service);
                    added++;
                }
                Changed?.Invoke();
            }
            return new(added, failed, signedOut);
        }
        finally { _discoveryGate.Release(); }
    }

    public async Task<CodexLoginResult> LoginAsync(string? profileId, string label,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token)
    {
        if (!await _loginGate.WaitAsync(0, token).ConfigureAwait(false)) return new(CodexQuotaStatus.Unavailable);
        try
        {
            if (profileId is not null && IsImportedCodex(profileId))
                return await RecoverImportedAsync(profileId, openBrowser, token).ConfigureAwait(false);

            IUsageAccountService service;
            ICodexAccountOperations operations;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (profileId is null)
                {
                    var profile = _store.NewManaged(label);
                    // Persist the reference before Codex logs in, so interrupted login is recoverable.
                    Save(_configuration with { Profiles = _configuration.Profiles.Append(profile).ToArray(),
                        SelectedId = _configuration.Profiles.Count == 0 ? profile.Id : _configuration.SelectedId });
                    service = AddService(profile);
                    profileId = profile.Id;
                }
                else if (!_services.TryGetValue(profileId, out service!)) return new(CodexQuotaStatus.Unavailable);
                if (service is not ICodexAccountOperations codexOperations) return new(CodexQuotaStatus.Unavailable);
                operations = codexOperations;
                _loginProfile = profileId;
            }
            Changed?.Invoke();
            var result = await operations.LoginAsync(openBrowser, token).ConfigureAwait(false);
            if (result.Status == CodexQuotaStatus.Available)
            {
                await _processSlots.WaitAsync(token).ConfigureAwait(false);
                try { await service.RefreshAsync(token).ConfigureAwait(false); }
                finally { _processSlots.Release(); }
                lock (_gate)
                {
                    if (service.Snapshot.Status == CodexQuotaStatus.Available
                        && (!_services.TryGetValue(_configuration.SelectedId, out var selected)
                            || !CodexRingPresentation.From(selected.Snapshot).IsAvailable))
                        Save(_configuration with { SelectedId = profileId });
                }
            }
            return result;
        }
        finally
        {
            lock (_gate) _loginProfile = null;
            _loginGate.Release();
            Changed?.Invoke();
        }
    }

    private async Task<CodexLoginResult> RecoverImportedAsync(string profileId,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token)
    {
        CodexAccountProfile original;
        IUsageAccountService originalService;
        lock (_gate)
        {
            if (_loginProfile is not null || !_services.TryGetValue(profileId, out originalService!)
                || originalService.IsRefreshing || _refreshingProfiles.Contains(profileId))
                return new(CodexQuotaStatus.Unavailable, Detail: "codex-reconnect-busy");
            original = _configuration.Profiles.FirstOrDefault(profile => profile.Id == profileId
                && profile.Provider == UsageProviderId.Codex && !profile.IsManaged)
                ?? throw new InvalidOperationException("Imported Codex profile is unavailable.");
            _loginProfile = profileId;
        }
        Changed?.Invoke();

        try
        {
            var candidate = _store.NewManaged(original.Label);
            var candidateService = _createService(candidate);
            if (candidateService is not ICodexAccountOperations candidateOperations)
                return new(CodexQuotaStatus.Unavailable, Detail: "codex-reconnect-unavailable");

            var login = await candidateOperations.LoginAsync(openBrowser, token).ConfigureAwait(false);
            if (login.Status != CodexQuotaStatus.Available) return login;
            if (login.Identity is not { Status: CodexQuotaStatus.Available, StableAccountFingerprint: not null })
                return new(CodexQuotaStatus.Unavailable, login.Identity, "codex-reconnect-identity-failed");

            await _processSlots.WaitAsync(token).ConfigureAwait(false);
            CodexRefreshResult refresh;
            try { refresh = await candidateService.RefreshAsync(token).ConfigureAwait(false); }
            finally { _processSlots.Release(); }
            var snapshot = refresh.Snapshot;
            var candidateFingerprint = candidateService.IdentityFingerprint ?? snapshot.IdentityFingerprint;
            if (snapshot.Status != CodexQuotaStatus.Available || !CodexRingPresentation.From(snapshot).IsAvailable
                || candidateFingerprint is null || candidateFingerprint != login.Identity.StableAccountFingerprint)
            {
                var status = snapshot.Status == CodexQuotaStatus.Available ? CodexQuotaStatus.Unavailable : snapshot.Status;
                return new(status, login.Identity, "codex-reconnect-quota-failed");
            }

            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_services.ContainsKey(profileId)) return new(CodexQuotaStatus.Unavailable, Detail: "codex-reconnect-unavailable");
                var current = _configuration;
                var currentProfiles = current.Profiles.ToArray();
                var currentIndex = Array.FindIndex(currentProfiles, profile => profile.Id == profileId);
                if (currentIndex < 0) return new(CodexQuotaStatus.Unavailable, Detail: "codex-reconnect-unavailable");
                var duplicate = currentProfiles
                    .Where(profile => profile.Provider == UsageProviderId.Codex && profile.Id != profileId)
                    .Any(profile => _services.TryGetValue(profile.Id, out var service)
                        && (IdentityOf(service) == candidateFingerprint
                            || IdentityOf(service) == login.Identity.Fingerprint));
                if (duplicate) return new(CodexQuotaStatus.Unavailable, login.Identity, "codex-identity-conflict");

                var currentProfile = currentProfiles[currentIndex];
                var replacement = candidate with { Label = currentProfile.Label };
                currentProfiles[currentIndex] = replacement;
                var ignoredHomes = current.IgnoredHomes.Append(currentProfile.HomePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var selected = string.Equals(current.SelectedId, profileId, StringComparison.Ordinal)
                    ? replacement.Id : current.SelectedId;
                token.ThrowIfCancellationRequested();
                Save(current with { Profiles = currentProfiles, SelectedId = selected, IgnoredHomes = ignoredHomes });
                if (_serviceHandlers.Remove(profileId, out var handler)) originalService.Changed -= handler;
                _services.Remove(profileId);
                _identityConflicts.Remove(profileId);
                AddService(replacement, candidateService);
            }
            Changed?.Invoke();
            return new(CodexQuotaStatus.Available, login.Identity);
        }
        catch (OperationCanceledException) { return new(CodexQuotaStatus.Cancelled); }
        catch (Exception) { return new(CodexQuotaStatus.Unavailable, Detail: "codex-reconnect-unavailable"); }
    }
    public Task<CreditRedemptionOutcome> ConsumeCreditAsync(string profileId, string creditId, CancellationToken token)
    {
        lock (_gate)
        {
            var projected = Accounts.FirstOrDefault(account => account.Profile.Id == profileId);
            if (projected is null || projected.Profile.Provider != UsageProviderId.Codex
                || !projected.IsConnected || projected.Snapshot.Status != CodexQuotaStatus.Available
                || !CodexRingPresentation.From(projected.Snapshot).IsAvailable
                || profileId == _loginProfile || !_services.TryGetValue(profileId, out var service)
                || service is not ICodexAccountOperations operations)
                return Task.FromResult(CreditRedemptionOutcome.Unavailable);
            return operations.ConsumeCreditAsync(creditId, token);
        }
    }
    private IUsageAccountService AddService(CodexAccountProfile profile, IUsageAccountService? service = null)
    {
        service ??= _createService(profile);
        _services.Add(profile.Id, service);
        Action<CodexQuotaSnapshot> handler = _ => Changed?.Invoke();
        _serviceHandlers.Add(profile.Id, handler);
        service.Changed += handler;
        return service;
    }

    private static string? IdentityOf(IUsageAccountService service) =>
        service.IdentityFingerprint ?? service.Snapshot.IdentityFingerprint;

    private bool IsImportedCodex(string profileId)
    {
        lock (_gate) return _configuration.Profiles.Any(profile => profile.Id == profileId
            && profile.Provider == UsageProviderId.Codex && !profile.IsManaged);
    }



    private void Save(CodexAccountConfiguration configuration)
    {
        _store.Save(configuration);
        _configuration = configuration;
    }
}
