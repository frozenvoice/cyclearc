using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed class ClaudeUsageProvider(CodexAccountStore accounts, IClock? clock = null, ClaudeConnectionService? connections = null) : IUsageProvider
{
    public UsageProviderId Id => UsageProviderId.Claude;
    public IUsageAccountService Create(CodexAccountProfile profile)
    {
        if (profile.Provider != Id) throw new ArgumentException("Wrong usage provider.");
        return new ClaudeQuotaService(new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id), clock,
            () => new ClaudeConnectionStore(accounts, profile.Id).Read(), fingerprint => connections?.Email(profile.Id, fingerprint),
            new ClaudeFailureStore(accounts));
    }
}

public sealed class ClaudeQuotaService : IUsageAccountService
{
    private readonly ClaudeStatusLineStore _store;
    private readonly ClaudeFailureStore? _failureStore;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeStatusLineRead? _lastRead;
    private ClaudeFailureRead? _lastFailureRead;
    private CodexQuotaSnapshot _snapshot = Empty("claude-statusline-missing");
    private CodexQuotaSnapshot? _lastPublished;
    private readonly Func<ClaudeConnectionRead>? _readConnection;
    private readonly Func<string?, string?>? _email;
    private ClaudeConnectionRead? _connection;
    private string? _lastEmail;

    public ClaudeQuotaService(ClaudeStatusLineStore store, IClock? clock = null,
        Func<ClaudeConnectionRead>? readConnection = null, Func<string?, string?>? email = null,
        ClaudeFailureStore? failureStore = null)
    {
        _store = store;
        _failureStore = failureStore;
        _clock = clock ?? SystemClock.Instance;
        _readConnection = readConnection;
        _email = email;
        _connection = _readConnection?.Invoke();
        _lastRead = store.Read();
        _lastFailureRead = ReadFailure();
        Apply(_lastRead, _lastFailureRead);
    }

    private bool HasBoundConnection => _connection is { Unavailable: false, Binding.Disconnected: false };
    public CodexQuotaSnapshot Snapshot => HasBoundConnection && _snapshot.Status == CodexQuotaStatus.Unavailable
        && _snapshot.TechnicalDetail is "claude-statusline-missing" or "claude-statusline-waiting"
        ? _snapshot with { TechnicalDetail = "claude-connected-waiting" }
        : ApplyFreshness(_snapshot, _clock.UtcNow);
    public string? Email => _connection?.Binding?.Disconnected == true ? null : _email?.Invoke(_connection?.Binding?.IdentityFingerprint);
    public string? IdentityFingerprint => Email is null ? null : _connection?.Binding?.IdentityFingerprint;
    // A persisted non-disconnected binding is the connection fact. Auth liveness is shown separately.
    public bool IsConnected => HasBoundConnection;
    public bool IsRefreshing { get; private set; }
    public bool ReceivesPassiveUpdates => true;
    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
    public event Action<CodexQuotaSnapshot>? Changed;

    public async Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IsRefreshing = true;
        try
        {
            var (read, failure, connection) = await Task.Run(() => (_store.Read(), ReadFailure(), _readConnection?.Invoke()), token).ConfigureAwait(false);
            var changed = connection != _connection || failure != _lastFailureRead;
            _connection = connection;
            if (read != _lastRead || changed) Apply(read, failure);
            var snapshot = Snapshot;
            PublishIfChanged(snapshot);
            return new(snapshot, snapshot.Status != CodexQuotaStatus.Available, snapshot.TechnicalDetail);
        }
        finally { IsRefreshing = false; _gate.Release(); }
    }

    private ClaudeFailureRead? ReadFailure()
    {
        if (_failureStore is null || _connection?.Binding is null) return null;
        try { return _failureStore.Read(_connection.Binding.ProfileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(null, true); }
    }

    private void Apply(ClaudeStatusLineRead read, ClaudeFailureRead? failureRead)
    {
        _lastRead = read;
        _lastFailureRead = failureRead;
        if (_connection?.Binding?.Disconnected == true)
        {
            _snapshot = Empty("claude-disconnected") with { Status = CodexQuotaStatus.SignedOut };
            PublishIfChanged(Snapshot);
            return;
        }
        if (_connection?.Unavailable == true)
        {
            _snapshot = Empty("claude-connection-unavailable");
            PublishIfChanged(Snapshot);
            return;
        }

        var state = read.State;
        var good = state?.LastGood;
        if (good is not null && _connection?.Binding is { } sampleBinding && good.ReceivedAt < sampleBinding.ConnectedAt)
            good = null;
        var activeFailure = _connection?.Binding is { } binding && failureRead is { Unavailable: false, State: { } failure }
            && ClaudeFailureClassification.IsActive(failure, binding, good);
        var failureDetail = activeFailure ? ClaudeFailureClassification.TechnicalDetail(failureRead!.State!.Kind) : null;
        if (!activeFailure && failureRead?.Unavailable == true) failureDetail = "claude-bridge-unavailable";
        var statusDetail = read.Unavailable ? "claude-cache-unavailable" : state?.LastInputStatus switch
        {
            ClaudeInputStatus.Available => null,
            ClaudeInputStatus.Malformed => "claude-statusline-malformed",
            _ => "claude-statusline-missing"
        };
        var detail = failureDetail ?? statusDetail;
        if (detail is null && good is null && state?.LastGood is not null) detail = "claude-statusline-waiting";
        var attempted = state?.LastReceivedAt;
        if (activeFailure && failureRead!.State!.ObservedAt > (attempted ?? DateTimeOffset.MinValue))
            attempted = failureRead.State.ObservedAt;

        if (good is not null)
        {
            var windows = new List<CodexQuotaWindow>();
            if (good.FiveHour is { } five) windows.Add(Window(five, CodexWindowKind.FiveHour, 300, "five_hour"));
            if (good.SevenDay is { } week) windows.Add(Window(week, CodexWindowKind.Weekly, 10080, "seven_day"));
            _snapshot = new(detail is null ? CodexQuotaStatus.Available : CodexQuotaStatus.Stale,
                null, good.ReceivedAt, attempted, null, null, null, windows, detail)
                { Provider = UsageProviderId.Claude };
        }
        else if (_snapshot.HasUsablePercentages && (_connection?.Binding is not { } previousBinding
            || _snapshot.LastSuccessfulRefresh >= previousBinding.ConnectedAt))
            _snapshot = _snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = detail, LastAttemptedRefresh = attempted };
        else
            _snapshot = Empty(detail) with { Status = state?.LastInputStatus == ClaudeInputStatus.Malformed
                ? CodexQuotaStatus.ProtocolMismatch : CodexQuotaStatus.Unavailable,
                LastAttemptedRefresh = attempted };
        PublishIfChanged(Snapshot);
    }

    private void PublishIfChanged(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == _lastPublished && Email == _lastEmail) return;
        _lastPublished = snapshot;
        _lastEmail = Email;
        Changed?.Invoke(snapshot);
    }

    public static CodexQuotaSnapshot ApplyFreshness(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status != CodexQuotaStatus.Available) return snapshot;
        var received = snapshot.LastSuccessfulRefresh;
        if (received is null || received > now)
            return snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "claude-receipt-invalid" };
        return snapshot;
    }

    private static CodexQuotaSnapshot Empty(string? detail) => CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail)
        with { Provider = UsageProviderId.Claude };
    private static CodexQuotaWindow Window(ClaudeRateLimit value, CodexWindowKind kind, int minutes, string id) =>
        new(id, value.UsedPercentage, minutes, DateTimeOffset.FromUnixTimeSeconds(value.ResetsAt), kind);
}
