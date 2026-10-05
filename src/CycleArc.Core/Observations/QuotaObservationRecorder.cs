using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Observations;

public sealed record QuotaObservationRecorderStatistics(long AcceptedSnapshots, long IgnoredSnapshots,
    long LoadAttempts, long SaveAttempts, long SavedFiles, long EpochWrites, long Failures, long PublishedRevisions);

/// <summary>
/// Optional local recording of provider-accepted observations. Ingestion keeps every
/// accepted point in memory; one background worker coalesces file writes, without a timer.
/// Provider, settings and account-registry operations never depend on this recorder.
/// </summary>
public sealed class QuotaObservationRecorder
{
    public const string FileName = "quota-observations.json";
    public const string EpochFileName = "quota-observation-epoch.json";
    private readonly object _gate = new();
    private readonly string _root;
    private readonly IClock _clock;
    private readonly Action<string>? _warning;
    private readonly Action<string>? _beforeIo;
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownProfiles;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.Ordinal);
    // Accessed only by the serial disk worker, including startup reconciliation.
    private readonly HashSet<string> _temporaryCleanupCompleted = new(StringComparer.Ordinal);
    private Task _worker = Task.CompletedTask;
    private bool _reconcile = true;
    private bool _stopped;
    private long _accepted, _ignored, _loads, _saveAttempts, _saved, _epochWrites, _failures, _published;

    public QuotaObservationRecorder(string root, IEnumerable<string> profileIds, IClock? clock = null,
        Action<string>? warning = null)
        : this(root, profileIds, clock, warning, null) { }

    internal QuotaObservationRecorder(string root, IEnumerable<string> profileIds, IClock? clock,
        Action<string>? warning, Action<string>? beforeIo)
    {
        _root = Path.GetFullPath(root);
        _clock = clock ?? SystemClock.Instance;
        _warning = warning;
        _beforeIo = beforeIo;
        _knownProfiles = profileIds.Where(ValidProfileId).ToHashSet(StringComparer.Ordinal);
        lock (_gate) StartWorkerLocked();
    }

    public event Action? Changed;

    public QuotaObservationRecorderStatistics Statistics
    {
        get { lock (_gate) return new(_accepted, _ignored, _loads, _saveAttempts, _saved, _epochWrites, _failures, _published); }
    }

    public QuotaObservationHistorySnapshot? GetSnapshot(string profileId)
    {
        lock (_gate) return _registrations.TryGetValue(profileId, out var registration)
            && registration.Scope is { Resolved: true, Disabled: false } scope ? scope.Published : null;
    }

    public bool GetStorageUnavailable(string profileId)
    {
        lock (_gate) return _registrations.TryGetValue(profileId, out var registration)
            && registration.Scope is { } scope && (scope.Disabled || scope.StorageUnavailable);
    }

    public bool GetLoading(string profileId)
    {
        lock (_gate) return _registrations.TryGetValue(profileId, out var registration)
            && registration.Scope is { Disabled: false } scope && (!scope.Resolved || scope.NeedsLoad);
    }

    public void Register(CodexAccountProfile profile, string? bindingKey, CodexQuotaSnapshot initial)
    {
        Safe(() =>
        {
            lock (_gate)
            {
                if (_stopped || !ValidProfileId(profile.Id)) return;
                _knownProfiles.Add(profile.Id);
                var registration = new Registration(profile, initial);
                _registrations[profile.Id] = registration;
                if (SafeBinding(bindingKey) is { } key)
                    registration.Scope = NewScope(registration, key, initial);
                QueueLocked(profile.Id);
            }
        });
    }

    public void Observe(string profileId, string? bindingKey, CodexQuotaSnapshot snapshot)
    {
        Safe(() =>
        {
            lock (_gate)
            {
                if (_stopped || !_registrations.TryGetValue(profileId, out var registration)) return;
                var key = SafeBinding(bindingKey);
                if (key is null)
                {
                    _ignored++;
                    KeepWatermark(registration, snapshot);
                    if (registration.Scope is { Disabled: false } uncertain
                        && uncertain.History.Observe(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable)
                            with { Provider = registration.Profile.Provider }, _clock.UtcNow))
                    {
                        uncertain.NeedsSave = true;
                        PublishLocked(uncertain);
                        QueueLocked(profileId);
                    }
                    return;
                }
                if (registration.Scope is not { } scope || scope.RawBinding != key)
                {
                    registration.Scope = scope = NewScope(registration, key, registration.WatermarkSnapshot);
                    QueueLocked(profileId);
                }
                KeepWatermark(registration, snapshot);
                if (scope.Disabled) { _ignored++; return; }
                if (!scope.History.Observe(snapshot, _clock.UtcNow)) { _ignored++; return; }
                _accepted++;
                scope.NeedsSave = true;
                PublishLocked(scope);
                QueueLocked(profileId);
            }
        });
    }

    /// <summary>Only after successful explicit reconnect, before its follow-up quota read.</summary>
    public Task<bool> ResetAsync(CodexAccountProfile profile, string? bindingKey, CodexQuotaSnapshot initial,
        TimeSpan budget, CancellationToken token)
    {
        Scope? reset = null;
        Safe(() =>
        {
            lock (_gate)
            {
                if (_stopped || !_registrations.TryGetValue(profile.Id, out var registration)) return;
                var key = SafeBinding(bindingKey);
                registration.Scope?.ResetCompletion?.TrySetResult(false);
                if (key is null) { registration.Scope = null; return; }
                var epoch = HasDurableEpoch(profile.Provider) ? Guid.NewGuid().ToString("N") : null;
                registration.Scope = reset = NewScope(registration, key, initial, epoch);
                reset.ResetCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                reset.NeedsSave = true;
                if (epoch is null) reset.ResetCompletion.TrySetResult(true);
                QueueLocked(profile.Id);
            }
        });
        return reset is null ? Task.FromResult(false) : WaitForResetAsync(reset, budget, token);
    }

    private async Task<bool> WaitForResetAsync(Scope scope, TimeSpan budget, CancellationToken token)
    {
        try { return await scope.ResetCompletion!.Task.WaitAsync(budget, token).ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate)
            {
                // The queued epoch may still finish, but uncertain history stays hidden
                // for this session. Authentication and current quota remain usable.
                scope.Disabled = true;
                scope.StorageUnavailable = true;
                scope.Published = null;
            }
            ReportFailure("Observed quota history reconnect reset did not finish within its budget; current quota is unaffected.", background: true);
            NotifyChanged();
            return false;
        }
    }

    public void Remove(string profileId)
    {
        Safe(() =>
        {
            lock (_gate)
            {
                if (_stopped || !ValidProfileId(profileId)) return;
                if (_registrations.Remove(profileId, out var registration))
                    registration.Scope?.ResetCompletion?.TrySetResult(false);
                _knownProfiles.Remove(profileId);
                _pending.Remove(profileId);
                _pendingDeletes.Add(profileId);
                StartWorkerLocked();
            }
        });
    }

    // Joins finite scheduled work; it never causes an observation or a write on its own.
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task worker;
            lock (_gate) worker = _worker;
            await worker.ConfigureAwait(false);
            lock (_gate)
                if (_worker.IsCompleted && _pending.Count == 0 && _pendingDeletes.Count == 0) return;
        }
    }

    public async Task StopAsync(TimeSpan budget)
    {
        lock (_gate) _stopped = true;
        try { await WaitForIdleAsync().WaitAsync(budget).ConfigureAwait(false); }
        catch (Exception) { ReportFailure("Observed quota history shutdown did not finish within its budget.", background: true); }
    }

    private Scope NewScope(Registration registration, string key, CodexQuotaSnapshot initial, string? forcedEpoch = null)
    {
        var context = Context(registration.Profile, key, forcedEpoch);
        var history = new QuotaObservationHistory(context, _clock);
        history.SeedInitialSnapshot(initial);
        if (registration.Scope is { } previous)
        {
            var old = previous.History.Snapshot;
            // A reconnect cannot promote an unchanged cached source receipt into a new
            // generation. Carry only its original watermarks, never its numeric points.
            history.MergeLoaded(old with { Context = context, Series = old.Series
                .Select(series => series with { Points = [], PendingGap = true }).ToImmutableArray() });
        }
        var scope = new Scope(registration.Profile, key, initial, history)
        {
            Resolved = !HasDurableEpoch(registration.Profile.Provider),
            ForcedEpoch = forcedEpoch
        };
        if (scope.Resolved) PublishLocked(scope);
        return scope;
    }

    private void QueueLocked(string profileId)
    {
        _pending.Add(profileId);
        StartWorkerLocked();
    }

    private void StartWorkerLocked()
    {
        if (_worker.IsCompleted) _worker = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            string? deleted = null;
            Scope? scope = null;
            bool reconcile;
            lock (_gate)
            {
                reconcile = _reconcile;
                _reconcile = false;
                if (_pendingDeletes.Count > 0)
                {
                    deleted = _pendingDeletes.First();
                    _pendingDeletes.Remove(deleted);
                }
                else if (_pending.Count > 0)
                {
                    var id = _pending.First();
                    _pending.Remove(id);
                    if (_registrations.TryGetValue(id, out var registration)) scope = registration.Scope;
                }
                else if (!reconcile)
                {
                    _worker = Task.CompletedTask;
                    return;
                }
            }
            if (reconcile) Safe(Reconcile);
            if (deleted is not null) Safe(() => DeleteOwnedFiles(deleted));
            if (scope is not null) Process(scope);
        }
    }

    private void Process(Scope scope)
    {
        try
        {
            lock (_gate) if (!RegisteredLocked(scope) || scope.Disabled && scope.ForcedEpoch is null) return;
            if (!_temporaryCleanupCompleted.Contains(scope.Profile.Id))
            {
                DeleteOwnedTemporaryFiles(scope.Profile.Id);
                _temporaryCleanupCompleted.Add(scope.Profile.Id);
            }
            if (!scope.Resolved)
            {
                var epoch = scope.ForcedEpoch is { } forced
                    ? ReplaceEpoch(scope, forced) : LoadOrCreateEpoch(scope);
                lock (_gate)
                {
                    if (!RegisteredLocked(scope)) { scope.ResetCompletion?.TrySetResult(false); return; }
                    var context = Context(scope.Profile, scope.RawBinding, epoch);
                    scope.History = scope.History.Clone(context);
                    scope.Resolved = true;
                    // A completed reconnect must survive restart even if the optional
                    // observation save is still queued behind other disk work.
                    scope.ResetCompletion?.TrySetResult(true);
                    PublishLocked(scope);
                }
            }
            lock (_gate) if (!CurrentLocked(scope)) return;
            var store = new QuotaObservationStore(PathFor(scope.Profile.Id, FileName), scope.History.Snapshot.Context, _clock);
            if (scope.NeedsLoad)
            {
                lock (_gate) { if (!CurrentLocked(scope)) return; _loads++; }
                var loaded = store.Load();
                if (loaded is not null)
                {
                    while (true)
                    {
                        QuotaObservationHistory history;
                        QuotaObservationHistorySnapshot live;
                        lock (_gate)
                        {
                            if (!CurrentLocked(scope)) return;
                            live = scope.History.Snapshot;
                            history = scope.History.Clone();
                        }
                        history.MergeLoaded(loaded);
                        lock (_gate)
                        {
                            if (!CurrentLocked(scope)) return;
                            if (!ReferenceEquals(scope.History.Snapshot, live)) continue;
                            scope.History = history;
                            PublishLocked(scope);
                            break;
                        }
                    }
                }
                lock (_gate)
                {
                    if (!CurrentLocked(scope)) return;
                    scope.NeedsLoad = false;
                    scope.StorageUnavailable = store.Unavailable;
                }
                if (store.Unavailable)
                    ReportFailure("Observed quota history needed recovery; current quota is unaffected.");
                NotifyChanged();
            }
            QuotaObservationHistorySnapshot snapshot;
            lock (_gate)
            {
                if (!CurrentLocked(scope) || !scope.NeedsSave) return;
                snapshot = scope.History.Snapshot;
                scope.NeedsSave = false;
                _saveAttempts++;
            }
            _beforeIo?.Invoke("save");
            store.Save(snapshot);
            bool recovered;
            lock (_gate) { _saved++; recovered = scope.StorageUnavailable; scope.StorageUnavailable = false; }
            if (recovered) NotifyChanged();
        }
        catch (Exception)
        {
            lock (_gate)
            {
                scope.NeedsLoad = false;
                scope.StorageUnavailable = true;
                scope.ResetCompletion?.TrySetResult(false);
                if (!scope.Resolved)
                {
                    // Uncertain generation state cannot restore or append prior-session records.
                    scope.Disabled = true;
                    scope.Published = null;
                }
            }
            ReportFailure("Observed quota history could not be read or saved; current quota is unaffected.");
            NotifyChanged();
        }
    }

    private bool CurrentLocked(Scope scope) => !scope.Disabled && RegisteredLocked(scope);

    private bool RegisteredLocked(Scope scope) => _registrations.TryGetValue(scope.Profile.Id, out var registration)
        && ReferenceEquals(registration.Scope, scope);

    private void PublishLocked(Scope scope)
    {
        if (!scope.Resolved || scope.Disabled) return;
        var snapshot = scope.History.Snapshot;
        if (ReferenceEquals(scope.Published, snapshot)) return;
        scope.Published = snapshot;
        _published++;
    }

    private void Reconcile()
    {
        var directory = Path.Combine(_root, "accounts");
        if (!Directory.Exists(directory) || IsReparse(directory)) return;
        // Only bounded top-level profile directories and our exact own filenames.
        foreach (var path in Directory.EnumerateDirectories(directory).Take(1024))
        {
            var id = Path.GetFileName(path);
            if (!ValidProfileId(id) || IsReparse(path)) continue;
            bool known;
            lock (_gate) known = _knownProfiles.Contains(id);
            Safe(() =>
            {
                if (!known) DeleteOwnedFiles(id);
                else
                {
                    DeleteOwnedTemporaryFiles(id);
                    _temporaryCleanupCompleted.Add(id);
                }
            });
        }
    }

    private void DeleteOwnedFiles(string profileId)
    {
        foreach (var name in new[] { FileName, FileName + ".bak", EpochFileName, EpochFileName + ".bak" })
        {
            var path = PathFor(profileId, name);
            if (File.Exists(path)) File.Delete(path);
        }
        DeleteOwnedTemporaryFiles(profileId);
        _temporaryCleanupCompleted.Remove(profileId);
    }

    private void DeleteOwnedTemporaryFiles(string profileId)
    {
        var directory = Path.GetDirectoryName(PathFor(profileId, FileName))!;
        if (!Directory.Exists(directory)) return;
        // Only our actual atomic-writer names, only top-level files, and finite
        // enumeration. This worker owns all writes, so none can be active here.
        foreach (var path in Directory.EnumerateFiles(directory, "quota-*.tmp", SearchOption.TopDirectoryOnly).Take(256))
        {
            var name = Path.GetFileName(path);
            if (!OwnedTemporaryName(name)) continue;
            var safePath = PathFor(profileId, name); // Includes file/directory reparse checks.
            if (File.Exists(safePath)) File.Delete(safePath);
        }
    }

    private static bool OwnedTemporaryName(string name)
    {
        if (!name.EndsWith(".tmp", StringComparison.Ordinal)) return false;
        foreach (var prefix in new[] { FileName + ".", EpochFileName + ".", EpochFileName + ".bak." })
        {
            if (name.Length != prefix.Length + 36 || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _)) return true;
        }
        return false;
    }

    private string PathFor(string profileId, string fileName)
    {
        if (!ValidProfileId(profileId)) throw new InvalidDataException("Invalid observation profile.");
        var accounts = Path.Combine(_root, "accounts");
        var profile = Path.Combine(accounts, profileId);
        if (Directory.Exists(_root) && IsReparse(_root) || Directory.Exists(accounts) && IsReparse(accounts)
            || Directory.Exists(profile) && IsReparse(profile))
            throw new InvalidDataException("Observation directories cannot be links.");
        var path = Path.Combine(profile, fileName);
        if (File.Exists(path) && IsReparse(path)) throw new InvalidDataException("Observation files cannot be links.");
        return path;
    }

    private string LoadOrCreateEpoch(Scope scope)
    {
        var path = PathFor(scope.Profile.Id, EpochFileName);
        var primaryExists = File.Exists(path);
        var backupExists = File.Exists(path + ".bak");
        if (!primaryExists && !backupExists) return ReplaceEpoch(scope, Guid.NewGuid().ToString("N"));
        // Fail closed through the two-file commit window; never resurrect an earlier epoch.
        var primary = ReadEpoch(path);
        var backup = ReadEpoch(path + ".bak");
        if (primary is null || backup is null || primary != backup)
            throw new InvalidDataException("Observation epoch is unavailable.");
        return primary.BindingKey == BaseBinding(scope.RawBinding)
            ? primary.Generation : ReplaceEpoch(scope, Guid.NewGuid().ToString("N"));
    }

    private string ReplaceEpoch(Scope scope, string epoch)
    {
        var path = PathFor(scope.Profile.Id, EpochFileName);
        var state = new EpochState(1, BaseBinding(scope.RawBinding), epoch);
        _beforeIo?.Invoke("epoch");
        ReplaceEpochFile(path, state);
        lock (_gate) _epochWrites++;
        ReplaceEpochFile(path + ".bak", state);
        lock (_gate) _epochWrites++;
        return epoch;
    }

    private static EpochState? ReadEpoch(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > 2048) return null;
        var value = JsonSerializer.Deserialize<EpochState>(stream, EpochOptions);
        return value is { Version: 1, BindingKey.Length: 64 } && value.BindingKey.All(Uri.IsHexDigit)
            && Guid.TryParseExact(value.Generation, "N", out _) ? value : null;
    }

    private static void ReplaceEpochFile(string path, EpochState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, EpochOptions);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static readonly JsonSerializerOptions EpochOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };
    private sealed record EpochState(int Version, string BindingKey, string Generation);

    private static QuotaObservationContext Context(CodexAccountProfile profile, string key, string? epoch) =>
        new(profile.Id, profile.Provider, Hash(profile.Provider + ":" + key + ":" + epoch));
    private static string BaseBinding(string key) => Hash(key);
    private static bool HasDurableEpoch(UsageProviderId provider) => provider is UsageProviderId.Codex or UsageProviderId.Cursor;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? SafeBinding(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) ? value : null;
    private static bool ValidProfileId(string id) => id == CodexAccountStore.LegacyProfileId || Guid.TryParseExact(id, "N", out _);
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void KeepWatermark(Registration registration, CodexQuotaSnapshot snapshot)
    {
        if (snapshot.LastSuccessfulRefresh is { } time
            && (registration.WatermarkSnapshot.LastSuccessfulRefresh is not { } previous || time >= previous))
            registration.WatermarkSnapshot = snapshot;
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception) { ReportFailure("Observed quota history is unavailable; current quota is unaffected."); }
    }

    private void ReportFailure(string message, bool background = false)
    {
        lock (_gate) _failures++;
        if (background)
        {
            // Optional file logging must not extend an exhausted lifecycle budget.
            _ = Task.Run(() => WriteWarning(message));
            return;
        }
        WriteWarning(message);
    }

    private void WriteWarning(string message)
    {
        try { _warning?.Invoke(message); }
        catch (Exception) { /* Optional diagnostics cannot affect quota or shutdown. */ }
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* Optional presentation subscribers cannot stop recording. */ }
    }

    private sealed class Registration(CodexAccountProfile profile, CodexQuotaSnapshot initial)
    {
        public CodexAccountProfile Profile { get; } = profile;
        public CodexQuotaSnapshot WatermarkSnapshot { get; set; } = initial;
        public Scope? Scope { get; set; }
    }

    private sealed class Scope(CodexAccountProfile profile, string binding, CodexQuotaSnapshot initial, QuotaObservationHistory history)
    {
        public CodexAccountProfile Profile { get; } = profile;
        public string RawBinding { get; } = binding;
        public CodexQuotaSnapshot InitialSnapshot { get; } = initial;
        public QuotaObservationHistory History { get; set; } = history;
        public QuotaObservationHistorySnapshot? Published { get; set; }
        public bool Resolved { get; set; }
        public bool Disabled { get; set; }
        public bool StorageUnavailable { get; set; }
        public string? ForcedEpoch { get; set; }
        public TaskCompletionSource<bool>? ResetCompletion { get; set; }
        public bool NeedsLoad { get; set; } = true;
        public bool NeedsSave { get; set; }
    }
}
