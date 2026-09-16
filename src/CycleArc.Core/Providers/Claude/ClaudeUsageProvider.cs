using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed class ClaudeUsageProvider(CodexAccountStore accounts, IClock? clock = null, ClaudeConnectionService? connections = null,
    Func<CodexAccountProfile, IClaudeDesktopUsageSource>? desktopFactory = null,
    Func<CodexAccountProfile, IClaudeLiveUsageSource>? liveFactory = null) : IUsageProvider
{
    public UsageProviderId Id => UsageProviderId.Claude;
    public IUsageAccountService Create(CodexAccountProfile profile)
    {
        if (profile.Provider != Id) throw new ArgumentException("Wrong usage provider.");
        return new ClaudeQuotaService(new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id), clock,
            () => new ClaudeConnectionStore(accounts, profile.Id).Read(), fingerprint => connections?.Email(profile.Id, fingerprint),
            new ClaudeFailureStore(accounts), desktopFactory?.Invoke(profile), liveFactory?.Invoke(profile));
    }
}

public sealed class ClaudeQuotaService : IUsageAccountService, ILiveUsageAccountService
{
    private readonly ClaudeStatusLineStore _store;
    private readonly ClaudeFailureStore? _failureStore;
    private readonly IClaudeDesktopUsageSource? _desktop;
    private readonly IClaudeLiveUsageSource? _live;
    private ClaudeDesktopUsageUpdate? _lastDesktop;
    private ClaudeLiveUsageUpdate? _lastLive;
    private DateTimeOffset? _lastLiveAttemptAt;
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
        ClaudeFailureStore? failureStore = null, IClaudeDesktopUsageSource? desktop = null,
        IClaudeLiveUsageSource? live = null)
    {
        _store = store;
        _failureStore = failureStore;
        _desktop = desktop;
        _live = live;
        _clock = clock ?? SystemClock.Instance;
        _readConnection = readConnection;
        _email = email;
        _connection = _readConnection?.Invoke();
        _lastRead = store.Read();
        _lastFailureRead = ReadFailure();
        _lastLive = ReadLiveCache(_connection);
        _lastLiveAttemptAt = _lastLive?.AttemptedAt;
        Apply(_lastRead, _lastFailureRead);
    }

    private bool HasBoundConnection => _connection is { Unavailable: false, Binding.Disconnected: false };
    public CodexQuotaSnapshot Snapshot => HasBoundConnection && _snapshot.Status == CodexQuotaStatus.Unavailable
        && _snapshot.TechnicalDetail is "claude-statusline-missing" or "claude-statusline-waiting"
        ? _snapshot with { TechnicalDetail = "claude-connected-waiting" }
        : ApplyFreshness(_snapshot, _clock.UtcNow);
    public string? Email => _connection?.Binding?.Disconnected == true ? null : _email?.Invoke(_connection?.Binding?.IdentityFingerprint);
    public string? IdentityFingerprint => Email is null ? null : _connection?.Binding?.IdentityFingerprint;
    public bool IsConnected => HasBoundConnection;
    public bool IsRefreshing { get; private set; }
    public bool ReceivesPassiveUpdates => true;
    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => _live is not null
        && HasBoundConnection
        && (_lastLiveAttemptAt is null || now - _lastLiveAttemptAt.Value >= interval);
    public event Action<CodexQuotaSnapshot>? Changed;

    /// <summary>Reads only projected statusLine/Desktop data. It never invokes the live source.</summary>
    public async Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IsRefreshing = true;
        try
        {
            var connection = await ReadConnectionAsync(token).ConfigureAwait(false);
            var desktop = _desktop is null ? null : await _desktop.RefreshAsync(
                connection is { Unavailable: false } ? connection.Binding : null, token).ConfigureAwait(false);
            var current = await ReadConnectionAsync(token).ConfigureAwait(false);
            if (current != connection) desktop = null;
            connection = current;
            var (read, failure) = await Task.Run(() => (_store.Read(), ReadFailure(connection)), token).ConfigureAwait(false);
            var bindingChanged = connection != _connection;
            var changed = bindingChanged || read != _lastRead || failure != _lastFailureRead || desktop != _lastDesktop;
            if (bindingChanged)
            {
                _lastLive = ReadLiveCache(connection);
                _lastLiveAttemptAt = _lastLive?.AttemptedAt;
                _snapshot = Empty("claude-live-identity-mismatch");
            }
            _connection = connection;
            _lastDesktop = desktop;
            if (changed) Apply(read, failure);
            var snapshot = Snapshot;
            PublishIfChanged(snapshot);
            return new(snapshot, snapshot.Status != CodexQuotaStatus.Available, snapshot.TechnicalDetail);
        }
        finally { IsRefreshing = false; _gate.Release(); }
    }

    /// <summary>Runs the explicitly requested live source, then projects local fallbacks.</summary>
    public async Task<CodexRefreshResult> RefreshLiveAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IsRefreshing = true;
        var attemptedAt = _clock.UtcNow;
        _lastLiveAttemptAt = attemptedAt;
        try
        {
            var connection = await ReadConnectionAsync(token).ConfigureAwait(false);
            // Do not allow a result from the previous account or binding generation to
            // become the fallback while this active read is in flight. A live cache is
            // safe to restore only after it has been checked against this binding.
            if (connection != _connection)
            {
                _lastLive = ReadLiveCache(connection);
                _lastLiveAttemptAt = _lastLive?.AttemptedAt;
                _lastDesktop = null;
                _snapshot = Empty("claude-live-identity-mismatch");
            }
            ClaudeLiveUsageUpdate? live = null;
            if (connection is { Unavailable: false, Binding: { Disconnected: false } binding } && _live is not null)
            {
                try { live = await _live.RefreshAsync(binding, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { live = new(null, "claude-live-unavailable", attemptedAt); }
            }

            // A result must not cross a reauthentication, disconnect or profile removal.
            var current = await ReadConnectionAsync(token).ConfigureAwait(false);
            if (current != connection)
            {
                _lastLiveAttemptAt = null;
                live = new(null, "claude-live-identity-mismatch", attemptedAt);
                _snapshot = Empty("claude-live-identity-mismatch");
            }
            else if (live?.AttemptedAt is { } sourceAttempt && sourceAttempt <= _clock.UtcNow)
                _lastLiveAttemptAt = sourceAttempt;

            if (live is not null)
            {
                if (live.Sample is null && live.Failure is null)
                    live = live with { Failure = "claude-live-unavailable" };
                if (live.Sample is not null && current?.Binding is { } currentBinding
                    && !Valid(live.Sample, currentBinding, _clock.UtcNow))
                    live = live with { Sample = null, Failure = "claude-live-unavailable" };
                if (live.Sample is null && _lastLive?.Sample is { } previous
                    && live.Failure is not "claude-live-identity-mismatch")
                    live = live with { Sample = previous };
                _lastLive = live;
            }

            _connection = current;
            var (read, failure) = await Task.Run(() => (_store.Read(), ReadFailure(current)), token).ConfigureAwait(false);
            var desktop = _desktop is null ? null : await _desktop.RefreshAsync(
                current is { Unavailable: false } ? current.Binding : null, token).ConfigureAwait(false);
            // Desktop history is also asynchronous. Revalidate the binding after it
            // completes so a reconnect or generation rotation cannot reintroduce an
            // old live sample through the fallback projection.
            var afterDesktop = await ReadConnectionAsync(token).ConfigureAwait(false);
            if (afterDesktop != current)
            {
                current = afterDesktop;
                _lastLiveAttemptAt = null;
                live = new(null, "claude-live-identity-mismatch", attemptedAt);
                _lastLive = live;
                desktop = null;
                _connection = current;
                _snapshot = Empty("claude-live-identity-mismatch");
                (read, failure) = await Task.Run(() => (_store.Read(), ReadFailure(current)), token)
                    .ConfigureAwait(false);
            }
            _lastDesktop = desktop;
            Apply(read, failure);
            var snapshot = Snapshot;
            PublishIfChanged(snapshot);
            return new(snapshot, snapshot.Status != CodexQuotaStatus.Available, snapshot.TechnicalDetail);
        }
        finally { IsRefreshing = false; _gate.Release(); }
    }

    private async Task<ClaudeConnectionRead?> ReadConnectionAsync(CancellationToken token) =>
        await Task.Run(() => _readConnection?.Invoke(), token).ConfigureAwait(false);

    private ClaudeFailureRead? ReadFailure() => ReadFailure(_connection);
    private ClaudeFailureRead? ReadFailure(ClaudeConnectionRead? connection)
    {
        if (_failureStore is null || connection?.Binding is null) return null;
        try { return _failureStore.Read(connection.Binding.ProfileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(null, true); }
    }

    private ClaudeLiveUsageUpdate? ReadLiveCache(ClaudeConnectionRead? connection)
    {
        if (_live is null || connection is not { Unavailable: false, Binding: { Disconnected: false } binding })
            return null;
        try
        {
            var cached = _live.ReadCached(binding);
            if (cached.Sample is { } sample && !Valid(sample, binding, _clock.UtcNow))
                return cached with { Sample = null, Failure = "claude-live-unavailable" };
            return cached;
        }
        catch (Exception) { return new(null, "claude-live-unavailable"); }
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
        var binding = _connection?.Binding is { Disconnected: false } currentBinding ? currentBinding : null;
        if (good is not null && binding is not null && good.ReceivedAt < binding.ConnectedAt) good = null;
        var desktop = _lastDesktop?.Sample;
        if (desktop is not null && (binding is null
            || desktop.ObservedAt < binding.ConnectedAt || desktop.ObservedAt > _clock.UtcNow)) desktop = null;
        var live = _lastLive?.Sample;
        if (live is not null && (binding is null || !Valid(live, binding, _clock.UtcNow))) live = null;

        // Never expose a quota after the live endpoint authenticated another account.
        if (string.Equals(_lastLive?.Failure, "claude-live-identity-mismatch", StringComparison.Ordinal))
        {
            _snapshot = Empty("claude-live-identity-mismatch") with
            {
                Status = CodexQuotaStatus.Unavailable,
                LastAttemptedRefresh = _lastLive?.AttemptedAt
            };
            PublishIfChanged(Snapshot);
            return;
        }

        var useLive = live is not null
            && (good is null || live.ObservedAt >= good.ReceivedAt)
            && (desktop is null || live.ObservedAt >= desktop.ObservedAt);
        var useDesktop = !useLive && desktop is not null
            && (good is null || desktop.ObservedAt > good.ReceivedAt);
        var received = useLive ? live!.ObservedAt : useDesktop ? desktop!.ObservedAt : good?.ReceivedAt;
        var liveSupersedesFailure = useLive && failureRead is { Unavailable: false, State: { } oldFailure }
            && oldFailure.ObservedAt < live!.ObservedAt;
        var activeFailure = binding is { } activeBinding && failureRead is { Unavailable: false, State: { } failure }
            && !liveSupersedesFailure
            && ClaudeFailureClassification.IsActive(failure, activeBinding, received);
        var failureDetail = activeFailure ? ClaudeFailureClassification.TechnicalDetail(failureRead!.State!.Kind) : null;
        if (!activeFailure && failureRead?.Unavailable == true) failureDetail = "claude-bridge-unavailable";
        var liveFailureDetail = string.IsNullOrWhiteSpace(_lastLive?.Failure) ? null : _lastLive!.Failure;
        var statusDetail = read.Unavailable ? "claude-cache-unavailable" : state?.LastInputStatus switch
        {
            ClaudeInputStatus.Available => null,
            ClaudeInputStatus.Malformed => "claude-statusline-malformed",
            _ => "claude-statusline-missing"
        };
        var detail = liveFailureDetail ?? failureDetail ?? (useLive ? null
            : useDesktop ? _lastDesktop?.Failure
            : good is null ? _lastDesktop?.Failure ?? statusDetail : statusDetail);
        if (!useLive && !useDesktop && detail is null && good is null && state?.LastGood is not null)
            detail = "claude-statusline-waiting";
        var attempted = _lastLive?.AttemptedAt
            ?? (useLive ? live!.ObservedAt : useDesktop ? desktop!.ObservedAt : state?.LastReceivedAt);
        if (activeFailure && failureRead!.State!.ObservedAt > (attempted ?? DateTimeOffset.MinValue))
            attempted = failureRead.State.ObservedAt;

        if (useLive)
        {
            var windows = new List<CodexQuotaWindow>();
            if (live!.FiveHour is { } five)
                windows.Add(new("five_hour", five.UsedPercentage, 300, five.ResetsAt, CodexWindowKind.FiveHour));
            if (live.SevenDay is { } week)
                windows.Add(new("seven_day", week.UsedPercentage, 10080, week.ResetsAt, CodexWindowKind.Weekly));
            _snapshot = new(detail is null ? CodexQuotaStatus.Available : CodexQuotaStatus.Stale,
                null, live.ObservedAt, attempted, null, null, null, windows, detail ?? "claude-live")
                { Provider = UsageProviderId.Claude };
        }
        else if (useDesktop)
        {
            var windows = new List<CodexQuotaWindow>();
            if (desktop!.FiveHour is { } five) windows.Add(new("five_hour", five, 300, null, CodexWindowKind.FiveHour));
            if (desktop.SevenDay is { } week) windows.Add(new("seven_day", week, 10080, null, CodexWindowKind.Weekly));
            _snapshot = new(detail is null ? CodexQuotaStatus.Available : CodexQuotaStatus.Stale,
                null, desktop.ObservedAt, attempted, null, null, null, windows, detail ?? "claude-desktop-history")
                { Provider = UsageProviderId.Claude };
        }
        else if (good is not null)
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

    private static bool Valid(ClaudeLiveUsageSample sample, ClaudeConnectionBinding binding, DateTimeOffset now) =>
        sample.ObservedAt > DateTimeOffset.UnixEpoch && sample.ObservedAt <= now
        && sample.ObservedAt >= binding.ConnectedAt
        && (sample.FiveHour is not null || sample.SevenDay is not null)
        && Valid(sample.FiveHour) && Valid(sample.SevenDay);

    private static bool Valid(ClaudeLiveUsageWindow? window) => window is null
        || (double.IsFinite(window.UsedPercentage) && window.UsedPercentage is >= 0 and <= 100
            && (window.ResetsAt is null || window.ResetsAt > DateTimeOffset.UnixEpoch));

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
