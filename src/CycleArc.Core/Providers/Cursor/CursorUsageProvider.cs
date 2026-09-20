using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>Creates isolated Cursor account services backed by the current Windows login.</summary>
public sealed class CursorUsageProvider : IUsageProvider
{
    private readonly CodexAccountStore _accounts;
    private readonly IClock _clock;
    private readonly Func<CodexAccountProfile, ICursorUsageClient>? _clientFactory;
    private readonly Func<CodexAccountProfile, ICursorAuthSource>? _authFactory;
    private readonly Func<CodexAccountProfile, ICursorUsageSource>? _sourceFactory;

    public CursorUsageProvider(CodexAccountStore accounts, IClock? clock = null,
        Func<CodexAccountProfile, ICursorUsageClient>? clientFactory = null,
        Func<CodexAccountProfile, ICursorAuthSource>? authFactory = null,
        Func<CodexAccountProfile, ICursorUsageSource>? sourceFactory = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _clock = clock ?? SystemClock.Instance;
        _clientFactory = clientFactory;
        _authFactory = authFactory;
        _sourceFactory = sourceFactory;
    }

    public UsageProviderId Id => UsageProviderId.Cursor;

    public IUsageAccountService Create(CodexAccountProfile profile)
    {
        if (profile.Provider != Id) throw new ArgumentException("Wrong usage provider.", nameof(profile));
        var auth = _authFactory?.Invoke(profile) ?? new CursorAuthStateDatabaseReader();
        var client = _clientFactory?.Invoke(profile) ?? new CursorUsageClient(auth, clock: _clock);
        var source = _sourceFactory?.Invoke(profile)
            ?? new CursorUsageCollector(_accounts, profile.Id, client, _clock);
        return new CursorQuotaService(_accounts, profile, client, source, _clock);
    }
}

public sealed class CursorQuotaService : IUsageAccountService, ILiveUsageAccountService, ICursorAccountOperations
{
    private readonly CodexAccountStore _accounts;
    private readonly CodexAccountProfile _profile;
    private readonly ICursorUsageClient _client;
    private readonly ICursorUsageSource _source;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private CursorConnectionBinding? _binding;
    private string? _email;
    private CodexQuotaSnapshot _snapshot;
    private CodexQuotaSnapshot? _lastPublished;
    private string? _lastPublishedEmail;

    public CursorQuotaService(CodexAccountStore accounts, CodexAccountProfile profile,
        ICursorUsageClient client, ICursorUsageSource source, IClock? clock = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (profile.Provider != UsageProviderId.Cursor)
            throw new ArgumentException("Wrong usage provider.", nameof(profile));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? SystemClock.Instance;
        _binding = ReadBinding();
        var binding = ActiveBinding();
        _snapshot = Project(_source.ReadCached(binding), binding);
    }

    // Snapshot is intentionally a cheap, event-free read. Account-manager projection can
    // access it while holding its own lock; binding and cache reconciliation happens only in
    // an explicit refresh or connection operation.
    public CodexQuotaSnapshot Snapshot => _snapshot;

    public string? Email => IsConnected ? _email : null;
    public string? IdentityFingerprint => BoundIdentityFingerprint;
    public string? BoundIdentityFingerprint => _binding?.IdentityFingerprint;
    public bool IsConnected => _binding is { Disconnected: false };
    public bool IsRefreshing { get; private set; }
    public bool ReceivesPassiveUpdates => false;
    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => IsConnected
        && (Snapshot.LastAttemptedRefresh is null || now - Snapshot.LastAttemptedRefresh.Value >= interval);
    public event Action<CodexQuotaSnapshot>? Changed;

    /// <summary>Reads the binding-scoped cache only. Explicit live refresh uses RefreshLiveAsync.</summary>
    public Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var binding = ReadBinding();
        if (binding != _binding) _email = null;
        _binding = binding;
        var activeBinding = ActiveBinding();
        var response = _source.ReadCached(activeBinding);
        _snapshot = Project(response, activeBinding);
        PublishIfChanged(_snapshot);
        return Task.FromResult(new CodexRefreshResult(_snapshot,
            UsedCache: response.Sample is not null, response.Failure));
    }

    public async Task<CodexRefreshResult> RefreshLiveAsync(CancellationToken token)
    {
        if (!await _liveGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            var current = Snapshot;
            return new(current, current.HasUsablePercentages, "cursor-live-busy");
        }

        IsRefreshing = true;
        try
        {
            var binding = ReadBinding();
            if (binding != _binding) _email = null;
            _binding = binding;
            if (binding is not { Disconnected: false })
            {
                _snapshot = Empty("cursor-disconnected") with { Status = CodexQuotaStatus.SignedOut };
                PublishIfChanged(_snapshot);
                return new(_snapshot, false, "cursor-disconnected");
            }

            var response = await _source.RefreshAsync(binding, token).ConfigureAwait(false);
            var current = ReadBinding();
            if (current != binding)
            {
                _binding = current;
                _email = null;
                _snapshot = current is { Disconnected: true }
                    ? Empty("cursor-disconnected") with { Status = CodexQuotaStatus.SignedOut }
                    : Empty("cursor-live-identity-mismatch") with { LastAttemptedRefresh = response.AttemptedAt };
                PublishIfChanged(_snapshot);
                return new(_snapshot, false, _snapshot.TechnicalDetail);
            }

            if (response.Email is not null) _email = response.Email;
            _snapshot = Project(response, binding);
            PublishIfChanged(_snapshot);
            return new(_snapshot, response.Sample is null, response.Failure);
        }
        finally
        {
            IsRefreshing = false;
            _liveGate.Release();
        }
    }

    public async Task<CursorConnectionResult> ConnectCurrentAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = new CursorConnectionStore(_accounts, _profile.Id);
            var read = store.Read();
            if (read.Unavailable) return new(false, "cursor-connection-unavailable");
            var identity = await _client.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!identity.Success || identity.IdentityFingerprint is null)
                return identity with { Failure = identity.Failure ?? "cursor-live-auth-required" };

            var previous = read.Binding;
            if (previous is { }
                && !string.Equals(previous.IdentityFingerprint, identity.IdentityFingerprint,
                    StringComparison.Ordinal))
            {
                // An established binding can never be replaced by a different current login
                // implicitly. Reauthentication/disconnect is an explicit user action.
                // Do not surface the newly observed account's email or fingerprint when it
                // does not match the profile's existing binding.
                return new(false, "cursor-live-identity-mismatch", Binding: previous);
            }

            var same = previous is { Disconnected: false }
                && string.Equals(previous.IdentityFingerprint, identity.IdentityFingerprint, StringComparison.Ordinal);
            var binding = same
                ? previous!
                : new CursorConnectionBinding(1, _profile.Id, identity.IdentityFingerprint,
                    _clock.UtcNow, Guid.NewGuid().ToString("N"));
            store.Save(binding);
            _binding = binding;
            _email = identity.Email;
            var cached = _source.ReadCached(binding);
            _snapshot = Project(cached, binding);
            PublishIfChanged(_snapshot);
            return new(true, null, identity.IdentityFingerprint, identity.Email, binding);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(false, "cursor-connection-unavailable"); }
        finally { _connectionGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = new CursorConnectionStore(_accounts, _profile.Id);
            var binding = store.Read().Binding;
            if (binding is null or { Disconnected: true }) return;
            token.ThrowIfCancellationRequested();
            store.Save(binding with { Disconnected = true });
            _binding = binding with { Disconnected = true };
            _email = null;
            _snapshot = Empty("cursor-disconnected") with { Status = CodexQuotaStatus.SignedOut };
            PublishIfChanged(_snapshot);
        }
        finally { _connectionGate.Release(); }
    }

    private CursorConnectionBinding? ReadBinding() =>
        new CursorConnectionStore(_accounts, _profile.Id).Read().Binding;

    private CursorConnectionBinding? ActiveBinding() => _binding is { Disconnected: false } ? _binding : null;

    private CodexQuotaSnapshot Project(CursorUsageResponse response, CursorConnectionBinding? binding)
    {
        if (binding is null)
            return Empty(_binding?.Disconnected == true ? "cursor-disconnected" : "cursor-not-connected") with
            { Status = _binding?.Disconnected == true ? CodexQuotaStatus.SignedOut : CodexQuotaStatus.Unavailable };
        if (response.Failure == "cursor-live-identity-mismatch")
            return Empty(response.Failure) with { LastAttemptedRefresh = response.AttemptedAt };
        var sample = response.Sample;
        if (sample is null)
            return Empty(response.Failure ?? "cursor-connected-waiting") with
            { LastAttemptedRefresh = response.AttemptedAt };
        return new(response.Failure is null ? CodexQuotaStatus.Available : CodexQuotaStatus.Stale,
            sample.MembershipType, sample.ObservedAt, response.AttemptedAt ?? sample.ObservedAt, null, null,
            null, sample.Windows, response.Failure ?? response.SandFailure, null)
        { Provider = UsageProviderId.Cursor, IdentityFingerprint = binding.IdentityFingerprint };
    }

    private static CodexQuotaSnapshot Empty(string? detail) =>
        CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail) with { Provider = UsageProviderId.Cursor };

    private void PublishIfChanged(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == _lastPublished && Email == _lastPublishedEmail) return;
        _lastPublished = snapshot;
        _lastPublishedEmail = Email;
        Changed?.Invoke(snapshot);
    }
}
