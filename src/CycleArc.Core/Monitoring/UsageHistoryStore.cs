using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CycleArc.Providers.Cursor;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.Monitoring;

/// <summary>Bounded, measurement-only local history. Call Record on a serialized background queue.
/// No credentials, labels, emails, raw account IDs, request payloads or diagnostic text are persisted.</summary>
public sealed class UsageHistoryStore(string root)
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumPoints = 8192;
    public const int MaximumStatuses = 256;
    public const int MaximumAccounts = 64;
    public const int MaximumLoadedAccounts = 16;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);
    public static readonly TimeSpan ChangedInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan UnchangedInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    // Clean full histories are evictable; unsaved measured tails and throttle clocks are not.
    private readonly Dictionary<string, Overlay> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _writeTimes = new(StringComparer.Ordinal);
    private readonly string _directory = Path.Combine(root, "usage-history");
    private DateTimeOffset? _lastCleanup;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };

    public void Record(IEnumerable<CodexAccountView> accounts, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var account in accounts.Take(MaximumAccounts))
            {
                var key = Key(account);
                if (key is null) continue;
                var state = Get(key, now);
                if (state.Rejected) continue;
                var snapshot = account.Snapshot;
                var quality = Quality(snapshot, now);
                var statusAt = quality == UsageHistoryQuality.Failure
                    ? snapshot.LastAttemptedRefresh ?? now : now;
                if (ValidTime(statusAt, now) && (state.Statuses.LastOrDefault(s => s.SeriesId is null)?.Quality != quality))
                {
                    state.Statuses.Add(new(statusAt, quality, snapshot.LastSuccessfulRefresh));
                    state.Dirty = true;
                }
                if (quality == UsageHistoryQuality.Observed)
                {
                    foreach (var point in Project(snapshot, now)) Add(state, point);
                    if (snapshot.CursorPlanAmountsFailure is not null) Gap(state, "cursor-plan-amounts", now, snapshot.LastAttemptedRefresh);
                    // Independently failed money fields retain their own gap, never fresh amounts.
                    if (snapshot.UsageCreditsFailure is not null) Gap(state, CreditsSeriesId(snapshot, state), now, snapshot.LastAttemptedRefresh);
                    if (snapshot.ExtraUsageFailure is not null) Gap(state, "extra", now, snapshot.LastAttemptedRefresh);
                    foreach (var window in snapshot.Windows.Where(w => w.AmountFailure is not null))
                        Gap(state, WindowId(window), now, snapshot.LastAttemptedRefresh);
                    if (snapshot.UsageCredits?.ObservedAt is { } creditsAt && now - creditsAt > StaleAfter)
                        Gap(state, CreditsSeriesId(snapshot, state), now, now, UsageHistoryQuality.Stale, creditsAt);
                    if (snapshot.ExtraUsage?.ObservedAt is { } extraAt && now - extraAt > StaleAfter)
                        Gap(state, "extra", now, now, UsageHistoryQuality.Stale, extraAt);
                    if (snapshot.CursorPlanAmounts?.ObservedAt is { } planAt && now - planAt > StaleAfter)
                        Gap(state, "cursor-plan-amounts", now, now, UsageHistoryQuality.Stale, planAt);
                    foreach (var window in snapshot.Windows.Where(w => w.AmountObservedAt is { } at && now - at > StaleAfter))
                        Gap(state, WindowId(window), now, now, UsageHistoryQuality.Stale, window.AmountObservedAt);
                }
                Trim(state, now);
                if (state.Dirty && (state.LastWrite is null || now - state.LastWrite >= ChangedInterval)) Save(key, state, now);
            }
        }
    }

    /// <summary>Returns only points inside the requested range; the earlier unobserved interval remains empty.</summary>
    public UsageHistoryView Read(CodexAccountView account, TimeSpan span, DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = Key(account);
            if (key is null) return new([], [new(now, UsageHistoryQuality.IdentityUnavailable, null)], null);
            var state = Get(key, now);
            var start = now - (span <= TimeSpan.Zero ? TimeSpan.FromHours(24) : span > Retention ? Retention : span);
            var statuses = state.Statuses.Where(s => s.At >= start && s.At <= now).ToList();
            var quality = Quality(account.Snapshot, now);
            if (statuses.LastOrDefault(s => s.SeriesId is null)?.Quality != quality)
                statuses.Add(new(now, quality, account.Snapshot.LastSuccessfulRefresh));
            return new(state.Points.Where(p => p.ObservedAt >= start && p.ObservedAt <= now).ToArray(),
                statuses, state.Points.Count > 0 ? state.Points.Min(p => p.ObservedAt) : null, state.Warning);
        }
    }

    private static string? Key(CodexAccountView account)
    {
        var identity = account.Snapshot.IdentityFingerprint;
        if (!account.IsConnected || UsageCreditPresentation.Hidden(account.Snapshot) || account.Profile.Provider != account.Snapshot.Provider
            || identity is not { Length: 64 } || !identity.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(account.Profile.Id) || account.Profile.Id.Length > 200) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            account.Profile.Provider + "\n" + account.Profile.Id + "\n" + identity.ToUpperInvariant())));
    }

    private static UsageHistoryQuality Quality(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status == CodexQuotaStatus.Refreshing) return UsageHistoryQuality.Stale;
        if (snapshot.Status == CodexQuotaStatus.Stale) return snapshot.LastAttemptedRefresh > snapshot.LastSuccessfulRefresh
            ? UsageHistoryQuality.Failure : UsageHistoryQuality.Stale;
        if (snapshot.Status != CodexQuotaStatus.Available) return snapshot.LastAttemptedRefresh is not null
            ? UsageHistoryQuality.Failure : UsageHistoryQuality.Missing;
        if (snapshot.LastSuccessfulRefresh is not { } at || !ValidTime(at, now)) return UsageHistoryQuality.Missing;
        return now - at > StaleAfter ? UsageHistoryQuality.Stale : UsageHistoryQuality.Observed;
    }

    private static IEnumerable<UsageHistoryPoint> Project(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var source = snapshot.Provider switch
        {
            UsageProviderId.Claude when snapshot.LastSuccessfulObservationWasServer == true => UsageHistorySource.ClaudeOAuth,
            UsageProviderId.Claude when snapshot.TechnicalDetail == "claude-desktop-history" => UsageHistorySource.ClaudeDesktopHistory,
            UsageProviderId.Claude => UsageHistorySource.ClaudeStatusLine,
            UsageProviderId.Cursor => UsageHistorySource.CursorUsage,
            _ => UsageHistorySource.CodexAppServer
        };
        foreach (var window in snapshot.Windows.Take(16))
        {
            var id = WindowId(window);
            var label = WindowLabel(window);
            if (window.UsedPercent is { } percent && double.IsFinite(percent) && percent >= 0 && decimal.TryParse(percent.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var percentValue)
                && snapshot.LastSuccessfulRefresh is { } percentAt && ValidTime(percentAt, now))
                yield return Point(percentAt, id, label, UsageHistoryMetric.UsagePercent, percentValue, "%", source, window.ResetsAt);
            var amountAt = window.AmountObservedAt ?? snapshot.LastSuccessfulRefresh;
            if (window.AmountFailure is not null || amountAt is not { } at || !ValidTime(at, now)
                || now - at > StaleAfter || !SafeUnit(window.Unit)) continue;
            // Included plan dollar amounts are reported usage, not an additional bill.
            var spend = window.LimitId is "cursor-on-demand" or "cursor-team-on-demand";
            if (window.UsedAmount is { } used && used >= 0)
                yield return Point(at, id, label, spend ? UsageHistoryMetric.ActualSpend : UsageHistoryMetric.ReportedUsageAmount,
                    used, window.Unit!, source, window.ResetsAt);
            if (window.LimitAmount is { } limit && limit >= 0 && !window.IsUnlimited)
                yield return Point(at, id, label, UsageHistoryMetric.SpendingLimit, limit, window.Unit!, source, window.ResetsAt);
            if (window.RemainingAmount is { } remaining && !window.IsUnlimited)
                yield return Point(at, id, label, UsageHistoryMetric.RemainingAmount, remaining, window.Unit!, source, window.ResetsAt);
        }
        if (snapshot.Provider == UsageProviderId.Cursor && snapshot.CursorPlanAmountsFailure is null
            && snapshot.CursorPlanAmounts is { } plan && CursorPlanAmounts.Valid(plan, now)
            && plan.IsEnabled != false && now - plan.ObservedAt <= StaleAfter)
        {
            var end = snapshot.Windows.FirstOrDefault(w => w.LimitId is "cursor-auto" or "cursor-api" or "cursor-plan")?.ResetsAt;
            if (plan.UsedAmount is { } used)
                yield return Point(plan.ObservedAt, "cursor-plan-amounts", "Plan included", UsageHistoryMetric.ReportedUsageAmount,
                    used, plan.Currency, UsageHistorySource.CursorUsage, end);
            if (!plan.IsUnlimited && plan.LimitAmount is { } limit)
                yield return Point(plan.ObservedAt, "cursor-plan-amounts", "Plan included", UsageHistoryMetric.SpendingLimit,
                    limit, plan.Currency, UsageHistorySource.CursorUsage, end);
            if (!plan.IsUnlimited && plan.RemainingAmount is { } remaining)
                yield return Point(plan.ObservedAt, "cursor-plan-amounts", "Plan included", UsageHistoryMetric.RemainingAmount,
                    remaining, plan.Currency, UsageHistorySource.CursorUsage, end);
        }
        if (snapshot.Provider == UsageProviderId.Codex && snapshot.UsageCreditsFailure is null
            && snapshot.UsageCredits is { Balance: { } balance, ObservedAt: { } creditAt } credits
            && ValidTime(creditAt, now) && now - creditAt <= StaleAfter)
            yield return Point(creditAt, CreditsSeriesId(credits), "Credits", UsageHistoryMetric.CreditBalance,
                balance, "credits", UsageHistorySource.CodexAppServer, null);
        if (snapshot.Provider == UsageProviderId.Claude && snapshot.ExtraUsageFailure is null
            && snapshot.ExtraUsage is { } extra && ClaudeExtraUsage.Valid(extra, now) && now - extra.ObservedAt <= StaleAfter
            && extra.IsEnabled && extra.Currency is { } currency)
        {
            if (extra.UsedAmount is { } used)
                yield return Point(extra.ObservedAt, "extra", "Extra usage", UsageHistoryMetric.ActualSpend, used, currency, UsageHistorySource.ClaudeOAuth, null);
            if (!extra.IsUnlimited && extra.MonthlyLimitAmount is { } limit)
                yield return Point(extra.ObservedAt, "extra", "Extra usage", UsageHistoryMetric.SpendingLimit, limit, currency, UsageHistorySource.ClaudeOAuth, null);
            if (extra.RemainingAmount is { } remaining)
                yield return Point(extra.ObservedAt, "extra", "Extra usage", UsageHistoryMetric.RemainingAmount, remaining, currency, UsageHistorySource.ClaudeOAuth, null);
        }
    }

    private static UsageHistoryPoint Point(DateTimeOffset at, string id, string label, UsageHistoryMetric metric,
        decimal value, string unit, UsageHistorySource source, DateTimeOffset? end) =>
        new(at, id, label, metric, value, unit, source, end, UsageHistoryDiscontinuity.None);

    private static void Add(State state, UsageHistoryPoint candidate)
    {
        var recoveredGap = false;
        if (state.Statuses.LastOrDefault(s => s.SeriesId == candidate.SeriesId) is { Quality: not UsageHistoryQuality.Observed } gap
            && candidate.ObservedAt > gap.At)
        {
            state.Statuses.Add(new(candidate.ObservedAt, UsageHistoryQuality.Observed, candidate.ObservedAt, candidate.SeriesId));
            state.Dirty = true;
            recoveredGap = true;
        }
        var index = state.Points.FindLastIndex(p => p.SeriesId == candidate.SeriesId && p.Metric == candidate.Metric);
        var previous = index < 0 ? null : state.Points[index];
        if (previous is not null && candidate.ObservedAt <= previous.ObservedAt) return;
        var changed = recoveredGap || previous is null || previous.Value != candidate.Value || previous.Unit != candidate.Unit
            || previous.Source != candidate.Source || previous.PeriodEndsAt != candidate.PeriodEndsAt;
        if (!changed && candidate.ObservedAt - previous!.ObservedAt < UnchangedInterval) return;
        var discontinuity = recoveredGap ? UsageHistoryDiscontinuity.Gap : UsageHistoryDiscontinuity.None;
        if (previous is not null)
        {
            if (previous.Source != candidate.Source || previous.Unit != candidate.Unit) discontinuity |= UsageHistoryDiscontinuity.SourceChanged;
            if (previous.PeriodEndsAt != candidate.PeriodEndsAt) discontinuity |= UsageHistoryDiscontinuity.PeriodChanged;
            if (candidate.Metric == UsageHistoryMetric.SpendingLimit && previous.Value != candidate.Value) discontinuity |= UsageHistoryDiscontinuity.LimitChanged;
            if (candidate.Metric is UsageHistoryMetric.ActualSpend or UsageHistoryMetric.ReportedUsageAmount or UsageHistoryMetric.UsagePercent
                && candidate.Value < previous.Value) discontinuity |= UsageHistoryDiscontinuity.CumulativeDecreased;
            if (candidate.ObservedAt - previous.ObservedAt > StaleAfter) discontinuity |= UsageHistoryDiscontinuity.Gap;
            if (candidate.Metric == UsageHistoryMetric.CreditBalance && previous.Value != candidate.Value)
                discontinuity |= UsageHistoryDiscontinuity.BalanceChanged;
        }
        candidate = candidate with { Discontinuity = discontinuity };
        // Coalesce rapid source changes in memory; persistence is independently throttled.
        var coalesced = previous is not null && candidate.ObservedAt - previous.ObservedAt < ChangedInterval;
        var previousDelta = previous is null ? null : state.Points.LastOrDefault(p => p.SeriesId == candidate.SeriesId
            && p.Metric == UsageHistoryMetric.BalanceChange && p.ObservedAt == previous.ObservedAt);
        Touch(state, candidate.SeriesId, candidate.Metric, coalesced ? previous!.ObservedAt : candidate.ObservedAt);
        if (coalesced)
        {
            state.Points[index] = candidate with { Discontinuity = candidate.Discontinuity | previous!.Discontinuity };
            state.Points.RemoveAll(p => p.SeriesId == candidate.SeriesId && p.Metric == UsageHistoryMetric.BalanceChange && p.ObservedAt == previous!.ObservedAt);
            if (candidate.Metric == UsageHistoryMetric.CreditBalance)
                Touch(state, candidate.SeriesId, UsageHistoryMetric.BalanceChange, previous!.ObservedAt);
        }
        else state.Points.Add(candidate);
        if (candidate.Metric == UsageHistoryMetric.CreditBalance && previous is not null
            && previous.Unit == candidate.Unit && previous.Source == candidate.Source && previous.Value != candidate.Value)
        {
            // Difference only: upward movement may be a top-up, bonus or correction; downward movement may also be a correction.
            try
            {
                var delta = candidate.Value - previous.Value + (coalesced ? previousDelta?.Value ?? 0 : 0);
                if (delta != 0)
                {
                    Touch(state, candidate.SeriesId, UsageHistoryMetric.BalanceChange, candidate.ObservedAt);
                    state.Points.Add(candidate with { Metric = UsageHistoryMetric.BalanceChange, Value = delta });
                }
            }
            catch (OverflowException)
            {
                // Both provider balances remain exact. Their difference can exceed decimal's
                // representable range; mark that missing delta rather than throwing or inventing zero.
                var balanceIndex = state.Points.FindLastIndex(point => point.SeriesId == candidate.SeriesId
                    && point.Metric == UsageHistoryMetric.CreditBalance && point.ObservedAt == candidate.ObservedAt);
                if (balanceIndex >= 0) state.Points[balanceIndex] = state.Points[balanceIndex] with
                    { Discontinuity = state.Points[balanceIndex].Discontinuity | UsageHistoryDiscontinuity.Gap };
                Gap(state, candidate.SeriesId, candidate.ObservedAt, candidate.ObservedAt,
                    UsageHistoryQuality.Missing, previous.ObservedAt);
            }
        }
        state.Dirty = true;
    }

    private static void Gap(State state, string series, DateTimeOffset now, DateTimeOffset? attempted,
        UsageHistoryQuality quality = UsageHistoryQuality.Failure, DateTimeOffset? observed = null)
    {
        var at = attempted is { } candidate && ValidTime(candidate, now) ? candidate : now;
        var previous = state.Statuses.LastOrDefault(s => s.SeriesId == series);
        if (previous?.At == at || previous is not null && previous.Quality == quality && at - previous.At < UnchangedInterval) return;
        state.Statuses.Add(new(at, quality, observed, series));
        state.Dirty = true;
    }

    private State Get(string key, DateTimeOffset now)
    {
        if (_states.TryGetValue(key, out var state)) return state;
        if (!_pending.ContainsKey(key) && !_writeTimes.ContainsKey(key)
            && _states.Keys.Concat(_pending.Keys).Concat(_writeTimes.Keys).Distinct(StringComparer.Ordinal).Count() >= MaximumAccounts)
        {
            var retired = _writeTimes.Where(pair => !_pending.ContainsKey(pair.Key)
                    && (!_states.TryGetValue(pair.Key, out var known) || !known.Dirty))
                .OrderBy(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
            if (retired is null) return new() { Warning = "history-capacity-unavailable", Rejected = true };
            _states.Remove(retired);
            _writeTimes.Remove(retired);
        }
        if (_states.Count >= MaximumLoadedAccounts)
        {
            var oldestKey = _states.Keys.First();
            var evicted = _states[oldestKey];
            if (evicted.Dirty)
                _pending[oldestKey] = new(evicted.Points.Where(point => evicted.PendingFrom.TryGetValue((point.SeriesId, point.Metric), out var from)
                        && point.ObservedAt >= from).ToList(), evicted.Statuses.ToList(), new(evicted.PendingFrom), evicted.Warning);
            _states.Remove(oldestKey);
        }
        var primary = Path.Combine(_directory, key + ".json");
        state = Load(primary, key, now);
        if (state is null)
        {
            state = Load(primary + ".bak", key, now);
            if (state is not null) { state.PrimaryValid = false; state.Warning = "history-backup-recovered"; }
            else state = new() { Warning = File.Exists(primary) || File.Exists(primary + ".bak") ? "history-unavailable" : null };
        }
        if (_writeTimes.TryGetValue(key, out var writeAt)) state.LastWrite = writeAt;
        if (_pending.Remove(key, out var overlay))
        {
            state.Points.RemoveAll(point => overlay.From.TryGetValue((point.SeriesId, point.Metric), out var from)
                && point.ObservedAt >= from);
            state.Points.AddRange(overlay.Points);
            state.Statuses = overlay.Statuses;
            state.PendingFrom = overlay.From;
            state.Warning = overlay.Warning ?? state.Warning;
            state.Dirty = true;
            Trim(state, now);
        }
        _states[key] = state;
        return state;
    }

    private static State? Load(string path, string key, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaximumFileBytes) return null;
            var boundedBytes = new byte[(int)stream.Length];
            stream.ReadExactly(boundedBytes);
            if (stream.ReadByte() != -1) return null;
            var document = JsonSerializer.Deserialize<Document>(boundedBytes, Json);
            if (document is null || document.Version != 1 || document.Key != key
                || document.Points is null || document.Statuses is null || document.Points.Count > MaximumPoints
                || document.Statuses.Count > MaximumStatuses || document.Points.Any(p => !ValidPoint(p, now))
                || document.Statuses.Any(s => s is null || !ValidTime(s.At, now) || !Enum.IsDefined(s.Quality)
                    || s.LastObservedAt is { } observed && !ValidTime(observed, now)
                    || s.SeriesId is { } id && !SafeId(id))) return null;
            var state = new State { Points = document.Points, Statuses = document.Statuses, PrimaryValid = true,
                LastWrite = document.WrittenAt is { } written && ValidTime(written, now) ? written : null };
            Trim(state, now);
            state.Dirty = false;
            state.PendingFrom.Clear();
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private void Save(string key, State state, DateTimeOffset now)
    {
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, key, state.Points, state.Statuses, now), Json);
            while (bytes.Length > MaximumFileBytes && state.Points.Count > 0)
            {
                state.Points = Spread(state.Points, Math.Max(0, state.Points.Count - 128));
                bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, key, state.Points, state.Statuses, now), Json);
            }
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, key + ".json");
            temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            var firstWrite = !state.PrimaryValid;
            if (state.PrimaryValid && File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path, true);
            state.PrimaryValid = true;
            state.LastWrite = now;
            state.Dirty = false;
            state.PendingFrom.Clear();
            state.Warning = null;
            Cleanup(key, now, firstWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { state.Warning = "history-write-unavailable"; state.LastWrite = now; }
        finally
        {
            _writeTimes[key] = now;
            // Record accepts at most64 profiles. Keep clocks bounded if ad-hoc reads introduce more.
            if (_writeTimes.Count > MaximumAccounts)
            {
                var retired = _writeTimes.Where(pair => pair.Key != key && !_states.ContainsKey(pair.Key) && !_pending.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
                if (retired is not null) _writeTimes.Remove(retired);
            }
            if (temporary is not null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Cleanup(string currentKey, DateTimeOffset now, bool firstWrite)
    {
        if (!firstWrite && _lastCleanup is { } cleaned && now - cleaned < TimeSpan.FromHours(24)) return;
        _lastCleanup = now;
        // Own exact hashed files only. No traversal outside this directory and no recursive deletes.
        var files = Directory.EnumerateFiles(_directory, "*.json").Take(1024)
            .Select(path => new FileInfo(path)).Where(file =>
                Path.GetFileNameWithoutExtension(file.Name) is { Length: 64 } id && id.All(Uri.IsHexDigit))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        foreach (var file in files.Skip(MaximumAccounts))
        {
            if (Path.GetFileNameWithoutExtension(file.Name) == currentKey) continue;
            File.Delete(file.FullName);
            if (File.Exists(file.FullName + ".bak")) File.Delete(file.FullName + ".bak");
        }
        // Retired profiles are finite as well. Active histories update their file timestamp.
        var wallTime = DateTimeOffset.UtcNow;
        foreach (var file in files.Where(file => wallTime - file.LastWriteTimeUtc > Retention))
        {
            if (Path.GetFileNameWithoutExtension(file.Name) == currentKey) continue;
            File.Delete(file.FullName);
            if (File.Exists(file.FullName + ".bak")) File.Delete(file.FullName + ".bak");
        }
    }

    private static void Trim(State state, DateTimeOffset now)
    {
        var removed = state.Points.RemoveAll(p => p.ObservedAt < now - Retention);
        state.Points.Sort((a, b) => a.ObservedAt.CompareTo(b.ObservedAt));
        // Preserve recent detail and sparse older measurements rather than discarding the entire
        // beginning of a 30-day view. Every retained value remains an actual observation.
        var keep = new HashSet<int>();
        var buckets = new Dictionary<(string, UsageHistoryMetric, long), int>();
        var firstSeries = new HashSet<(string, UsageHistoryMetric)>();
        for (var i = 0; i < state.Points.Count; i++)
        {
            var point = state.Points[i];
            var age = now - point.ObservedAt;
            var width = age <= TimeSpan.FromDays(1) ? ChangedInterval
                : age <= TimeSpan.FromDays(7) ? UnchangedInterval : TimeSpan.FromHours(1);
            var bucket = point.ObservedAt.UtcTicks / width.Ticks;
            buckets[(point.SeriesId, point.Metric, bucket)] = i;
            if (firstSeries.Add((point.SeriesId, point.Metric))) keep.Add(i);
            if ((point.Discontinuity & ~UsageHistoryDiscontinuity.BalanceChanged) != UsageHistoryDiscontinuity.None) keep.Add(i);
        }
        foreach (var index in buckets.Values) keep.Add(index);
        var sparse = state.Points.Where((_, i) => keep.Contains(i)).ToList();
        removed += state.Points.Count - sparse.Count;
        state.Points = sparse;
        if (state.Points.Count > MaximumPoints)
        {
            removed += state.Points.Count - MaximumPoints;
            state.Points = Spread(state.Points, MaximumPoints);
        }
        removed += state.Statuses.RemoveAll(s => s.At < now - Retention);
        state.Statuses.Sort((a, b) => a.At.CompareTo(b.At));
        if (state.Statuses.Count > MaximumStatuses) { removed += state.Statuses.Count - MaximumStatuses; state.Statuses.RemoveRange(0, state.Statuses.Count - MaximumStatuses); }
        var retainedSeries = state.Points.Select(point => (point.SeriesId, point.Metric)).ToHashSet();
        foreach (var touched in state.PendingFrom.Keys.Where(key => !retainedSeries.Contains(key)
            && !(key.Item2 == UsageHistoryMetric.BalanceChange && retainedSeries.Contains((key.Item1, UsageHistoryMetric.CreditBalance)))).ToArray())
            state.PendingFrom.Remove(touched);
        state.Dirty |= removed > 0;
    }

    // Under the final hard size/count bound, sample across the retained interval. This can
    // evict even a boundary when activity is extreme; never replace a missing value with zero.
    private static List<UsageHistoryPoint> Spread(List<UsageHistoryPoint> points, int count)
    {
        if (count <= 0) return [];
        if (count >= points.Count) return points;
        if (count == 1) return [points[^1]];
        return Enumerable.Range(0, count).Select(i => points[(int)((long)i * (points.Count - 1) / (count - 1))]).ToList();
    }

    private static bool ValidPoint(UsageHistoryPoint? point, DateTimeOffset now) => point is not null
        && ValidTime(point.ObservedAt, now) && SafeId(point.SeriesId) && point.Label is { Length: <= 80 }
        && point.Label.All(c => !char.IsControl(c)) && SafeUnit(point.Unit) && Enum.IsDefined(point.Metric)
        && Enum.IsDefined(point.Source) && ((int)point.Discontinuity & ~63) == 0
        && (point.PeriodEndsAt is null || point.PeriodEndsAt > DateTimeOffset.UnixEpoch)
        && (point.Metric is UsageHistoryMetric.CreditBalance or UsageHistoryMetric.BalanceChange or UsageHistoryMetric.RemainingAmount || point.Value >= 0);
    private static bool ValidTime(DateTimeOffset at, DateTimeOffset now) => at > DateTimeOffset.UnixEpoch && at <= now;
    private static bool SafeUnit(string? unit) => unit is { Length: > 0 and <= 12 } && unit.All(c => char.IsAsciiLetter(c) || c == '%');
    private static bool SafeId(string? id) => id is { Length: > 0 and <= 100 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private static string CreditsSeriesId(CodexUsageCredits credits) => "credits-" + Hash(credits.LimitId ?? "default");
    private static string CreditsSeriesId(CodexQuotaSnapshot snapshot, State state) => snapshot.UsageCredits is { Balance: not null } credits
        ? CreditsSeriesId(credits) : state.Points.LastOrDefault(point => point.Metric == UsageHistoryMetric.CreditBalance)?.SeriesId
            ?? CreditsSeriesId(snapshot.UsageCredits ?? new(false, false, null, null, null));
    private static void Touch(State state, string series, UsageHistoryMetric metric, DateTimeOffset at)
    {
        var key = (series, metric);
        if (!state.PendingFrom.TryGetValue(key, out var existing) || at < existing) state.PendingFrom[key] = at;
    }
    private static string WindowId(CodexQuotaWindow window) => window.LimitId switch
    {
        "five_hour" or "seven_day" or "cursor-auto" or "cursor-api" or "cursor-plan" or "cursor-on-demand"
            or "cursor-team-on-demand" or "cursor-team-pool" or "cursor-overall" or "cursor-grok" => window.LimitId,
        _ => "window-" + window.Kind + "-" + Hash(window.LimitId ?? window.WindowDurationMinutes?.ToString() ?? "unknown")
    };
    private static string WindowLabel(CodexQuotaWindow window) => window.LimitId switch
    {
        "cursor-auto" => "Auto", "cursor-api" => "API", "cursor-plan" => "Plan included", "cursor-on-demand" => "On-demand",
        "cursor-team-on-demand" => "Team on-demand", "cursor-team-pool" => "Team pool", "cursor-overall" => "Overall",
        "cursor-grok" => "Grok", _ => window.Kind switch { CodexWindowKind.FiveHour => "5-hour", CodexWindowKind.Weekly => "Weekly", _ => "Other limit" }
    };
    private sealed record Document(int Version, string Key, List<UsageHistoryPoint> Points, List<UsageHistoryStatus> Statuses,
        DateTimeOffset? WrittenAt = null);
    private sealed record Overlay(List<UsageHistoryPoint> Points, List<UsageHistoryStatus> Statuses,
        Dictionary<(string, UsageHistoryMetric), DateTimeOffset> From, string? Warning);
    private sealed class State
    {
        public List<UsageHistoryPoint> Points { get; set; } = [];
        public List<UsageHistoryStatus> Statuses { get; set; } = [];
        public Dictionary<(string, UsageHistoryMetric), DateTimeOffset> PendingFrom { get; set; } = [];
        public DateTimeOffset? LastWrite { get; set; }
        public bool PrimaryValid { get; set; }
        public bool Dirty { get; set; }
        public bool Rejected { get; init; }
        public string? Warning { get; set; }
    }
}

