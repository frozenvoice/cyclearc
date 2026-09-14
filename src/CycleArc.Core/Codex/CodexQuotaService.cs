namespace CycleArc.Codex;

public sealed class CodexQuotaService
{
    public static readonly TimeSpan FlyoutRefreshAge = TimeSpan.FromMinutes(2);


    private readonly CodexExecutableLocator _locator;
    private readonly CodexAppServerClient _client;
    private readonly CodexSnapshotStore _store;
    private readonly string _clientVersion;
    private readonly Action<string>? _log;
    private readonly Services.IClock _clock;
    private readonly CodexAccountProfile? _profile;
    private CodexAccountIdentity? _identity;
    private bool _discardCachedIdentity;
    private readonly CodexIdentityBindingStore? _identityBindings;
    private CodexQuotaSnapshot? _boundCache;
    private bool _identityMismatch;
    private bool _identityBindingUnavailable;
    private bool _identityVerified;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
    private string? _lastFailureSignature;

    public CodexQuotaService(
        CodexExecutableLocator locator,
        CodexAppServerClient client,
        CodexSnapshotStore store,
        string clientVersion,
        Action<string>? log = null,
        Services.IClock? clock = null,
        CodexAccountProfile? profile = null)
    {
        _locator = locator;
        _client = client;
        _store = store;
        _clientVersion = clientVersion;
        _log = log;
        _clock = clock ?? Services.SystemClock.Instance;
        _profile = profile;
        _snapshot = store.Load() ?? _snapshot;
        if (profile is not null)
        {
            _boundCache = _snapshot.HasUsablePercentages ? _snapshot : null;
            _identityBindings = new CodexIdentityBindingStore(store, profile.Id);
            CodexIdentityBindingRead binding;
            try { binding = _identityBindings.ReadOrSeedLegacy(_snapshot.IdentityFingerprint); }
            catch (IOException) { binding = new(null, true); }
            catch (UnauthorizedAccessException) { binding = new(null, true); }
            _identityBindingUnavailable = binding.Unavailable;
            if (binding.Unavailable || binding.State is not null)
            {
                var expected = binding.State?.AccountFingerprint ?? _snapshot.IdentityFingerprint;
                _snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable,
                    binding.Unavailable ? "codex-identity-binding-unavailable" : "codex-identity-pending")
                    with { IdentityFingerprint = expected };
            }
        }
        else if (_snapshot.Status is CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing)
            _snapshot = _snapshot with { Status = CodexQuotaStatus.Stale, RedeemableCredits = [] };
    }

    public CodexQuotaSnapshot Snapshot => _snapshot;
    public bool IsRefreshing { get; private set; }
    public bool IsSigningIn { get; private set; }
    public CodexAccountIdentity? Identity => _identity;
    // Manager uses only the last identity verified by an account/read response.
    // Binding files are guarded by a cross-process lock and must never be read on
    // the UI projection path. RefreshAsync/ProbeAccountAsync revalidate the binding
    // in the background before replacing this cached value.
    public string? ValidatedIdentityFingerprint =>
        _identityVerified ? _identity?.StableAccountFingerprint : null;
    public event Action<CodexQuotaSnapshot>? Changed;

    public static bool ShouldRefreshOnFlyoutOpen(CodexQuotaSnapshot snapshot, DateTimeOffset now, TimeSpan? refreshInterval = null)
    {
        if (snapshot.Status == CodexQuotaStatus.Refreshing)
        {
            return false;
        }

        var last = snapshot.LastSuccessfulRefresh;
        if (snapshot.LastAttemptedRefresh is { } attempt && (last is null || attempt > last)) last = attempt;
        var age = refreshInterval ?? FlyoutRefreshAge;
        // Short schedules must not turn failures into rapid automatic retries.
        if (snapshot.Status != CodexQuotaStatus.Available && age < FlyoutRefreshAge) age = FlyoutRefreshAge;
        return last is null || now - last.Value >= age;
    }

    public async Task<CodexRefreshResult> RefreshAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new CodexRefreshResult(_snapshot, UsedCache: true, "already-running");
        }

        IsRefreshing = true;
        Publish(_snapshot.AsRefreshing());
        var attempted = _clock.UtcNow;
        try
        {
            var command = Locate(configuredPath);
            if (command is null)
            {
                return PersistFailure(CodexQuotaStatus.CodexNotFound, attempted, "codex-not-found", "locate");
            }

            var session = await _client.ReadQuotaAsync(command, _clientVersion, cancellationToken).ConfigureAwait(false);
            if (_profile is not null)
            {
                // An initialize/startup failure has no account result by design. Keep a
                // previously verified cache eligible for a normal transient fallback;
                // only account/read responses can establish a missing or malformed identity.
                var accountReadWasSent = session.SentMethods.Any(method =>
                    string.Equals(method, "account/read", StringComparison.Ordinal));
                if (session.AccountResult is not null || accountReadWasSent)
                {
                    var identity = session.AccountResult is null
                        ? new CodexAccountIdentity(CodexQuotaStatus.ProtocolMismatch)
                        : CodexAccountIdentity.Parse(session.AccountResult);
                    if (identity.Status != CodexQuotaStatus.Available)
                    {
                        // A bound profile must not keep presenting quota when account identity is missing or malformed.
                        ForgetIdentity();
                        return PersistFailure(identity.Status, attempted, session.Detail ?? "account-unavailable", "account/read");
                    }
                    var identityMatch = ValidateProfileIdentity(identity);
                    if (identityMatch is not (CodexIdentityBindingMatch.Matched or CodexIdentityBindingMatch.FirstSeen))
                        return PersistIdentityFailure(identityMatch, attempted);
                    ApplyIdentity(identity);
                }
            }
            if (session.Status == CodexQuotaStatus.Available)
            {
                var parsed = CodexRateLimitParser.Parse(session.AccountResult, session.RateLimitsResult);
                if (parsed.Status == CodexQuotaStatus.Available)
                {
                    var success = new CodexQuotaSnapshot(
                        CodexQuotaStatus.Available,
                        parsed.PlanType,
                        _clock.UtcNow,
                        attempted,
                        parsed.OrdinaryUsageAllowed,
                        parsed.RateLimitReachedType,
                        parsed.ResetCreditsAvailable,
                        parsed.Windows,
                        SafeDetail(session, parsed.Detail),
                        parsed.ResetCreditExpirations) {
                            RedeemableCredits = CodexRateLimitParser.ReadRedeemableCredits(session.RateLimitsResult),
                            IdentityFingerprint = _identity?.StableAccountFingerprint };
                    _store.Save(success);
                    _boundCache = success;
                    _discardCachedIdentity = false;
                    Publish(success);
                    _lastFailureSignature = null;
                    return new CodexRefreshResult(success, UsedCache: false, null);
                }

                return PersistFailure(
                    parsed.Status,
                    attempted,
                    parsed.Detail ?? session.Detail,
                    InferStage(session, parsed));
            }

            return PersistFailure(session.Status, attempted, session.Detail, InferStage(session, null));
        }
        catch (OperationCanceledException)
        {
            return PersistFailure(
                cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                attempted,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out",
                "client");
        }
        catch (Exception ex)
        {
            return PersistFailure(CodexQuotaStatus.Unavailable, attempted, CodexProtocol.SanitizeDiagnostic(ex.GetType().Name, 80), "client");
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
        }
    }

    private readonly Dictionary<string, string> _redemptionKeys = new(StringComparer.Ordinal);

    // Called only after the user's explicit confirmation. Refresh and redemption share the gate.
    public async Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return CreditRedemptionOutcome.Busy;
        try
        {
            if (_snapshot.Status != CodexQuotaStatus.Available
                || !_snapshot.RedeemableCredits.Any(x => x.Id == creditId))
                return CreditRedemptionOutcome.Unavailable;
            var command = Locate(configuredPath);
            if (command is null) return CreditRedemptionOutcome.Unavailable;
            if (!_redemptionKeys.TryGetValue(creditId, out var key))
                _redemptionKeys[creditId] = key = Guid.NewGuid().ToString();
            var outcome = await _client.ConsumeCreditAsync(command, _clientVersion, creditId, key, cancellationToken,
                    _profile is null ? null : _identity?.Fingerprint, requireIdentity: _profile is not null)
                .ConfigureAwait(false);
            if (outcome is CreditRedemptionOutcome.NothingToReset or CreditRedemptionOutcome.NoCredit
                or CreditRedemptionOutcome.Unavailable) _redemptionKeys.Remove(creditId);
            // Do not log IDs, protocol errors, or server response bodies.
            _log?.Invoke($"codex credit redemption outcome={outcome}");
            Publish(_snapshot with { Status = CodexQuotaStatus.Stale, RedeemableCredits = [] });
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private CodexIdentityBindingMatch ValidateProfileIdentity(CodexAccountIdentity identity)
    {
        if (_identityBindings is null) return CodexIdentityBindingMatch.Matched;
        CodexIdentityBindingMatch result;
        try { result = _identityBindings.Match(identity); }
        catch (IOException) { result = CodexIdentityBindingMatch.Unavailable; }
        catch (UnauthorizedAccessException) { result = CodexIdentityBindingMatch.Unavailable; }
        _identityMismatch = result == CodexIdentityBindingMatch.Mismatch;
        _identityBindingUnavailable = result == CodexIdentityBindingMatch.Unavailable;
        if (result is CodexIdentityBindingMatch.Mismatch or CodexIdentityBindingMatch.Unavailable)
            _identity = null;
        if (result is CodexIdentityBindingMatch.Matched or CodexIdentityBindingMatch.FirstSeen)
        {
            _identityVerified = true;
            if (_boundCache is null && _snapshot.HasUsablePercentages) _boundCache = _snapshot;
        }
        return result;
    }

    private CodexRefreshResult PersistIdentityFailure(CodexIdentityBindingMatch match, DateTimeOffset attempted)
    {
        _identityVerified = false;
        _identityMismatch = match == CodexIdentityBindingMatch.Mismatch;
        _identityBindingUnavailable = match == CodexIdentityBindingMatch.Unavailable;
        var detail = match == CodexIdentityBindingMatch.Unavailable
            ? "codex-identity-binding-unavailable" : "codex-identity-mismatch";
        string? expected = null;
        try
        {
            var read = _identityBindings?.Read();
            if (read is { Unavailable: true }) _identityBindingUnavailable = true;
            expected = read?.State?.AccountFingerprint;
        }
        catch (IOException) { _identityBindingUnavailable = true; }
        catch (UnauthorizedAccessException) { _identityBindingUnavailable = true; }
        if (_identityBindingUnavailable) detail = "codex-identity-binding-unavailable";
        var next = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail) with
        {
            LastAttemptedRefresh = attempted, IdentityFingerprint = expected
        };
        Publish(next);
        LogFailure(CodexQuotaStatus.Unavailable, "account/read", detail);
        return new CodexRefreshResult(next, UsedCache: false, detail);
    }
    private CodexRefreshResult PersistFailure(
        CodexQuotaStatus status,
        DateTimeOffset attempted,
        string? detail,
        string stage)
    {
        if (_profile is not null && RequiresBoundIdentity())
            return PersistBoundFailure(status, attempted, stage, detail);

        var cached = _discardCachedIdentity ? _snapshot : _store.Load() ?? _snapshot;
        if (_profile is not null && _identityVerified && !CacheMatchesVerifiedIdentity(cached))
        {
            _identityMismatch = true;
            return PersistBoundFailure(status, attempted, stage, "codex-identity-mismatch");
        }
        var preserve = cached.HasUsablePercentages
            && status is not CodexQuotaStatus.SignedOut and not CodexQuotaStatus.CodexNotFound;
        var next = preserve
            ? cached.AsStale(attempted, detail)
            : CodexQuotaSnapshot.Empty(status, detail) with { LastAttemptedRefresh = attempted };
        next = next with
        {
            Status = preserve ? CodexQuotaStatus.Stale : status,
            // A failed refresh must never keep reset-credit actions actionable.
            RedeemableCredits = [],
            IdentityFingerprint = cached.IdentityFingerprint,
        };

        if (_discardCachedIdentity || next.HasUsablePercentages || next.LastSuccessfulRefresh is not null)
            _store.Save(next);
        LogFailure(status, stage, detail);
        Publish(next);
        return new CodexRefreshResult(next, UsedCache: preserve, detail);
    }

    private bool CacheMatchesVerifiedIdentity(CodexQuotaSnapshot cached)
    {
        var fingerprint = cached.IdentityFingerprint;
        if (fingerprint is null || _identity is null) return false;
        return string.Equals(fingerprint, _identity.StableAccountFingerprint, StringComparison.Ordinal)
            || string.Equals(fingerprint, _identity.Fingerprint, StringComparison.Ordinal);
    }
    private bool RequiresBoundIdentity()
    {
        if (_profile is null)
            return false;

        if (_identityMismatch || _identityBindingUnavailable)
            return true;

        if (_identityVerified)
            return false;

        try
        {
            var binding = _identityBindings?.Read();
            return binding is { Unavailable: true } || binding?.State is not null || _boundCache is not null;
        }
        catch (IOException)
        {
            _identityBindingUnavailable = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _identityBindingUnavailable = true;
            return true;
        }
    }

    private CodexRefreshResult PersistBoundFailure(
        CodexQuotaStatus status,
        DateTimeOffset attempted,
        string stage,
        string? detail)
    {
        CodexIdentityBindingRead? binding = null;
        try
        {
            binding = _identityBindings?.Read();
        }
        catch (IOException)
        {
            _identityBindingUnavailable = true;
        }
        catch (UnauthorizedAccessException)
        {
            _identityBindingUnavailable = true;
        }

        var effectiveStatus = status == CodexQuotaStatus.SignedOut
            ? CodexQuotaStatus.SignedOut
            : CodexQuotaStatus.Unavailable;
        var effectiveDetail = _identityBindingUnavailable || binding?.Unavailable == true
            ? "codex-identity-binding-unavailable"
            : _identityMismatch
                ? "codex-identity-mismatch"
                : detail ?? "account-unavailable";
        var next = CodexQuotaSnapshot.Empty(effectiveStatus, effectiveDetail) with
        {
            LastAttemptedRefresh = attempted,
            IdentityFingerprint = binding?.State?.AccountFingerprint,
            RedeemableCredits = [],
        };
        LogFailure(next.Status, stage, effectiveDetail);
        Publish(next);
        return new CodexRefreshResult(next, UsedCache: false, effectiveDetail);
    }
    private CodexLaunchCommand? Locate(string? configuredPath)
    {
        var command = _locator.Locate(configuredPath);
        return command is null || _profile is null ? command
            : command with { CodexHome = _profile.HomePath, ManagedHome = _profile.IsManaged };
    }

    private void ApplyIdentity(CodexAccountIdentity identity)
    {
        if (_profile is null)
        {
            if (_snapshot.IdentityFingerprint is not null
                && _snapshot.IdentityFingerprint != identity.Fingerprint)
            {
                ForgetIdentity();
            }

            _identity = identity;
            return;
        }

        _identity = identity;
        _identityVerified = true;
        _identityMismatch = false;
        _identityBindingUnavailable = false;
        _discardCachedIdentity = false;
    }

    private void ForgetIdentity()
    {
        _discardCachedIdentity = true;
        _identity = null;
        _identityVerified = false;
        _redemptionKeys.Clear();

        if (_profile is null)
        {
            Publish(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable));
            return;
        }

        CodexIdentityBindingRead? binding = null;
        try
        {
            binding = _identityBindings?.Read();
        }
        catch (IOException)
        {
            _identityBindingUnavailable = true;
        }
        catch (UnauthorizedAccessException)
        {
            _identityBindingUnavailable = true;
        }

        var detail = _identityBindingUnavailable || binding?.Unavailable == true
            ? "codex-identity-binding-unavailable"
            : _identityMismatch
                ? "codex-identity-mismatch"
                : binding?.State is not null ? "codex-identity-pending" : null;
        var next = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail) with
        {
            IdentityFingerprint = binding?.State?.AccountFingerprint,
        };
        Publish(next);
    }

    public async Task<CodexAccountIdentity> ProbeAccountAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = Locate(configuredPath);
            if (command is null) return new(CodexQuotaStatus.CodexNotFound);
            var session = await _client.ReadAccountAsync(command, _clientVersion, cancellationToken).ConfigureAwait(false);
            var identity = session.Status == CodexQuotaStatus.Available
                ? CodexAccountIdentity.Parse(session.AccountResult) : new CodexAccountIdentity(session.Status);
            if (identity.Status == CodexQuotaStatus.Available)
            {
                if (_profile is not null)
                {
                    var match = ValidateProfileIdentity(identity);
                    if (match is not (CodexIdentityBindingMatch.Matched or CodexIdentityBindingMatch.FirstSeen))
                    {
                        return new CodexAccountIdentity(CodexQuotaStatus.Unavailable);
                    }
                }

                ApplyIdentity(identity);
            }
            else if (_profile is not null)
            {
                // Missing or malformed account identity cannot validate a bound cache.
                ForgetIdentity();
            }
            return identity;
        }
        finally { _gate.Release(); }
    }

    public async Task<CodexLoginResult> LoginAsync(string? configuredPath,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken)
    {
        // Reauthentication is offered only for app-owned homes. Imported sessions stay owned by their Codex installation.
        if (_profile is not { IsManaged: true }) return new(CodexQuotaStatus.Unavailable);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsSigningIn = true;
        try
        {
            // Keep the expected binding and the last verified cache on disk until an explicit login succeeds.
            ForgetIdentity();
            Directory.CreateDirectory(_profile.HomePath);
            var command = Locate(configuredPath);
            if (command is null) return new(CodexQuotaStatus.CodexNotFound);
            var result = await _client.LoginAsync(command, _clientVersion, openBrowser, cancellationToken).ConfigureAwait(false);
            if (result.Identity is { Status: CodexQuotaStatus.Available } identity)
            {
                _identityBindings?.Reset(identity);
                _boundCache = null;
                _identityMismatch = false;
                _identityBindingUnavailable = false;
                ApplyIdentity(identity);
            }

            Publish(_snapshot with { Status = result.Status == CodexQuotaStatus.Available ? CodexQuotaStatus.Unavailable : result.Status });
            return result;
        }
        finally { IsSigningIn = false; _gate.Release(); Changed?.Invoke(_snapshot); }
    }
    private void LogFailure(CodexQuotaStatus status, string stage, string? detail)
    {
        var line = $"codex refresh status={status} stage={SanitizeStage(stage)} detail={CodexProtocol.SanitizeDiagnostic(detail, 120)}";
        if (string.Equals(line, _lastFailureSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastFailureSignature = line;
        _log?.Invoke(line);
    }

    private static string SanitizeStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return "unknown";
        }

        return stage is "initialize" or "initialized" or "account/read" or "rateLimits" or "locate" or "client"
            ? stage
            : "unknown";
    }

    private static string InferStage(CodexProtocolSession session, CodexParseResult? parsed)
    {
        if (session.Status is CodexQuotaStatus.ProtocolMismatch or CodexQuotaStatus.TimedOut or CodexQuotaStatus.Unavailable
            && !session.SentMethods.Contains("initialized"))
        {
            return "initialize";
        }

        if (session.SentMethods.Contains("account/read")
            && !session.SentMethods.Contains("account/rateLimits/read"))
        {
            return "account/read";
        }

        if (parsed?.Status == CodexQuotaStatus.ProtocolMismatch)
        {
            if (session.AccountResult is JsonObject account && account.ContainsKey("error"))
            {
                return "account/read";
            }

            return "rateLimits";
        }

        return session.SentMethods.Contains("account/rateLimits/read") ? "rateLimits" : "account/read";
    }

    private static string? SafeDetail(CodexProtocolSession session, string? parsed)
    {
        var parts = new[] { parsed, session.Detail, session.Status == CodexQuotaStatus.Available ? null : session.Status.ToString() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var text = string.Join(";", parts);
        return string.IsNullOrWhiteSpace(text) ? null : CodexProtocol.SanitizeDiagnostic(text, 160);
    }

    private void Publish(CodexQuotaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }
}
