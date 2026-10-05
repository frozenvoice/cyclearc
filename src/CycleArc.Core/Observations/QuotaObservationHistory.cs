using System.Collections.Immutable;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Observations;

/// <summary>Bounded observations of accepted quotas. No timer, transport or inferred samples.</summary>
public sealed class QuotaObservationHistory
{
    public const int MaxSeries = 32;
    public const int MaxPointsPerLimit = 2048;
    public const int MaxTotalPoints = 16_384;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly IClock _clock;
    private readonly Dictionary<SeriesKey, QuotaObservationSeries> _series = new();
    private DateTimeOffset? _watermark;
    private long _nextSegment = 1;
    private bool _seeded;
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    public QuotaObservationHistory(QuotaObservationContext context, IClock? clock = null)
    {
        if (!ValidContext(context)) throw new ArgumentException("Invalid observation context.", nameof(context));
        _clock = clock ?? SystemClock.Instance;
        Snapshot = new(context, 0, null, []);
    }

    public QuotaObservationHistorySnapshot Snapshot { get; private set; }
    public long Revision => Snapshot.Revision;

    /// <summary>Cheap immutable-point copy for doing bounded disk merges outside a UI-facing lock.</summary>
    public QuotaObservationHistory Clone(QuotaObservationContext? context = null)
    {
        var resolved = context ?? Snapshot.Context;
        if (resolved.ProfileId != Snapshot.Context.ProfileId || resolved.Provider != Snapshot.Context.Provider)
            throw new ArgumentException("Observation scope cannot change profile or provider.", nameof(context));
        var copy = new QuotaObservationHistory(resolved, _clock)
        { _watermark = _watermark, _nextSegment = _nextSegment, _seeded = _seeded,
            _nextPruneAt = _nextPruneAt, Snapshot = context is null ? Snapshot : Snapshot with { Context = resolved } };
        foreach (var entry in _series) copy._series.Add(entry.Key, entry.Value);
        return copy;
    }

    /// <summary>Constructor/cache samples establish replay watermarks, never historical points.</summary>
    public void SeedInitialSnapshot(CodexQuotaSnapshot snapshot)
    {
        if (_seeded) return;
        _seeded = true;
        if (snapshot.Provider != Snapshot.Context.Provider) return;
        var at = snapshot.LastSuccessfulRefresh;
        if (!ValidTime(at, _clock.UtcNow)) return;
        _watermark = at;
        foreach (var candidate in Candidates(snapshot, at!.Value))
        {
            if (!ValidKey(candidate.Key) || candidate.At > at || _series.Count >= MaxSeries) continue;
            _series[candidate.Key] = new(candidate.Key.LimitId, candidate.Key.Kind, candidate.Key.Duration, candidate.Key.Metric, candidate.Key.Unit,
                candidate.At, true, []);
        }
        Publish();
    }

    public bool Observe(CodexQuotaSnapshot snapshot, DateTimeOffset receivedAt)
    {
        if (snapshot.Provider != Snapshot.Context.Provider || !ValidTime(receivedAt, _clock.UtcNow)
            || snapshot.Status == CodexQuotaStatus.Refreshing) return false;
        var changed = false;
        if (snapshot.Status != CodexQuotaStatus.Available)
        {
            foreach (var entry in _series.ToArray())
                if (!entry.Value.PendingGap)
                { _series[entry.Key] = entry.Value with { PendingGap = true }; changed = true; }
        }
        else if (ValidTime(snapshot.LastSuccessfulRefresh, receivedAt)
            && (_watermark is null || snapshot.LastSuccessfulRefresh > _watermark))
        {
            var at = snapshot.LastSuccessfulRefresh!.Value;
            _watermark = at;
            changed = true;
            var seen = new HashSet<SeriesKey>();
            var candidates = Candidates(snapshot, at).ToArray();
            var duplicates = candidates.GroupBy(c => c.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var candidate in candidates)
            {
                if (duplicates.Contains(candidate.Key) || !ValidKey(candidate.Key) || !ValidTime(candidate.At, at)) continue;
                seen.Add(candidate.Key);
                if (!_series.TryGetValue(candidate.Key, out var series))
                {
                    if (_series.Count >= MaxSeries) continue;
                    series = new(candidate.Key.LimitId, candidate.Key.Kind, candidate.Key.Duration, candidate.Key.Metric, candidate.Key.Unit, null, true, []);
                }
                // A retained optional financial value has its own clock/failure state.
                // Repeated source timestamps and reverse delivery never become new points.
                if (candidate.Value is null || candidate.Failed)
                {
                    _series[candidate.Key] = series with { Watermark = Max(series.Watermark, candidate.At), PendingGap = true };
                    continue;
                }
                if (series.Watermark is { } mark && candidate.At <= mark) continue;
                var previous = series.Points.IsEmpty ? null : series.Points[^1];
                var split = series.PendingGap || previous is null || candidate.Window.ResetsAt is null
                    || candidate.Window.ResetsAt <= candidate.At
                    || previous.ResetAt != candidate.Window.ResetsAt
                    || previous.WindowDurationMinutes != candidate.Window.WindowDurationMinutes
                    || previous.Kind != candidate.Window.Kind || previous.BasisLimit != candidate.Window.LimitAmount
                    || previous.BasisUnit != candidate.Window.Unit
                    || HasDiscontinuity(candidate.Key.Metric, candidate.Value.Value, previous.Value);
                var segment = split ? _nextSegment++ : previous!.SegmentId;
                var point = new QuotaObservationPoint(candidate.At, receivedAt, candidate.Value.Value, segment,
                    candidate.Window.ResetsAt, candidate.Window.WindowDurationMinutes,
                    candidate.Window.Kind, candidate.Window.LimitAmount, candidate.Window.Unit);
                _series[candidate.Key] = series with
                { Watermark = candidate.At, PendingGap = false, Points = series.Points.Add(point) };
            }
            foreach (var entry in _series.ToArray())
                if (!seen.Contains(entry.Key))
                    _series[entry.Key] = entry.Value with { Watermark = at > entry.Value.Watermark ? at : entry.Value.Watermark,
                        PendingGap = true };
        }
        changed |= Prune(receivedAt, force: changed);
        if (changed) Publish();
        return changed;
    }

    /// <summary>Merge delayed disk reads without losing observations accepted while IO ran.</summary>
    public void MergeLoaded(QuotaObservationHistorySnapshot loaded)
    {
        if (!ValidSnapshot(loaded, Snapshot.Context, _clock.UtcNow)) return;
        var maxSavedSegment = loaded.Series.SelectMany(s => s.Points).Select(p => p.SegmentId).DefaultIfEmpty().Max();
        foreach (var saved in loaded.Series)
        {
            var key = Key(saved);
            if (!_series.TryGetValue(key, out var current))
            { if (_series.Count < MaxSeries) _series[key] = saved with { PendingGap = true }; continue; }
            var points = saved.Points.Concat(current.Points).GroupBy(p => p.ObservedAt)
                .Select(g => g.OrderBy(p => p.ReceivedAt).First()).OrderBy(p => p.ObservedAt).ToImmutableArray();
            // Segment numbers from independently accumulated live data may collide with disk
            // numbers. Separate the live portion rather than accidentally joining two epochs.
            var livePoints = current.Points.ToHashSet();
            long originalSegment = 0;
            long assignedSegment = 0;
            bool previousLive = false;
            QuotaObservationPoint? previous = null;
            _nextSegment = Math.Max(_nextSegment, maxSavedSegment + 1);
            points = points.Select(p =>
            {
                var live = livePoints.Contains(p);
                if (previous is null || previousLive != live || originalSegment != p.SegmentId
                    || !p.CanConnect || !previous.CanConnect || p.ResetAt != previous.ResetAt
                    || p.BasisLimit != previous.BasisLimit || p.BasisUnit != previous.BasisUnit
                    || HasDiscontinuity(saved.Metric, p.Value, previous.Value))
                    assignedSegment = _nextSegment++;
                originalSegment = p.SegmentId;
                previousLive = live;
                previous = p;
                return p with { SegmentId = assignedSegment };
            }).ToImmutableArray();
            _series[key] = current with { Points = points,
                Watermark = Max(current.Watermark, saved.Watermark),
                PendingGap = current.Points.IsEmpty || (current.Watermark >= saved.Watermark ? current.PendingGap : saved.PendingGap) };
        }
        _watermark = Max(_watermark, loaded.Watermark);
        _nextSegment = _series.Values.SelectMany(s => s.Points).Select(p => p.SegmentId).DefaultIfEmpty().Max() + 1;
        Prune(_clock.UtcNow, force: true);
        Publish(Math.Max(Revision, loaded.Revision));
    }

    private bool Prune(DateTimeOffset now, bool force = false)
    {
        if (!force && now < _nextPruneAt) return false;
        var changed = false;
        var cutoff = now - Retention;
        foreach (var entry in _series.ToArray())
        {
            var kept = entry.Value.Points.Where(p => p.ObservedAt >= cutoff).ToImmutableArray();
            if (kept.Length != entry.Value.Points.Length)
            { _series[entry.Key] = entry.Value with { Points = kept }; changed = true; }
        }
        // The per-limit cap is shared across unit/window/basis changes, not multiplied by them.
        foreach (var group in _series.GroupBy(e => (e.Key.LimitId, e.Key.Kind, e.Key.Duration, e.Key.Metric)))
        {
            var keep = group.SelectMany(e => e.Value.Points.Select(p => (e.Key, Point: p)))
                .OrderByDescending(p => p.Point.ObservedAt).Take(MaxPointsPerLimit).ToHashSet();
            foreach (var entry in group.ToArray())
            {
                var points = entry.Value.Points.Where(p => keep.Contains((entry.Key, p))).ToImmutableArray();
                if (points.Length != entry.Value.Points.Length)
                { _series[entry.Key] = entry.Value with { Points = points }; changed = true; }
            }
        }
        var totalKeep = _series.SelectMany(e => e.Value.Points.Select(p => (e.Key, Point: p)))
            .OrderByDescending(p => p.Point.ObservedAt).Take(MaxTotalPoints).ToHashSet();
        foreach (var entry in _series.ToArray())
        {
            var points = entry.Value.Points.Where(p => totalKeep.Contains((entry.Key, p))).ToImmutableArray();
            if (points.Length != entry.Value.Points.Length)
            { _series[entry.Key] = entry.Value with { Points = points }; changed = true; }
            if (points.IsEmpty && entry.Value.Watermark < cutoff)
            { _series.Remove(entry.Key); changed = true; }
        }
        var earliest = _series.Values.SelectMany(s => s.Points).Select(p => p.ObservedAt).DefaultIfEmpty(now).Min();
        _nextPruneAt = earliest + Retention + TimeSpan.FromTicks(1);
        return changed;
    }

    private void Publish(long? revision = null) => Snapshot = new(Snapshot.Context,
        (revision ?? Revision) + 1, _watermark, _series.OrderBy(e => e.Key.LimitId, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Metric).ThenBy(e => e.Key.Unit, StringComparer.Ordinal).Select(e => e.Value).ToImmutableArray());

    private static IEnumerable<Candidate> Candidates(CodexQuotaSnapshot snapshot, DateTimeOffset at)
    {
        foreach (var window in snapshot.Windows)
        {
            if (window is null || !Enum.IsDefined(window.Kind) || window.WindowDurationMinutes is <= 0
                || window.ResetsAt is { } reset && reset <= DateTimeOffset.UnixEpoch
                || window.LimitAmount is < 0 || window.Unit is { } unit && !SafeLabel(unit, 16)) continue;
            var id = window.LimitId;
            var percent = window.UsedPercent is { } value && double.IsFinite(value) && value is >= 0 and <= 100
                ? (decimal?)value : null;
            yield return new(new(id, window.Kind, window.WindowDurationMinutes, QuotaObservationMetric.UsedPercent, "%"), at, percent, false, window);
            if (string.IsNullOrWhiteSpace(window.Unit)) continue;
            var amountAt = window.AmountObservedAt ?? at;
            yield return new(new(id, window.Kind, window.WindowDurationMinutes, QuotaObservationMetric.UsedAmount, window.Unit), amountAt,
                window.UsedAmount is >= 0 ? window.UsedAmount : null, window.AmountFailure is not null, window);
            yield return new(new(id, window.Kind, window.WindowDurationMinutes, QuotaObservationMetric.RemainingAmount, window.Unit), amountAt,
                window.RemainingAmount is >= 0 ? window.RemainingAmount : null, window.AmountFailure is not null, window);
        }
    }

    internal static bool ValidContext(QuotaObservationContext context) => context is not null
        && (context.ProfileId == "default" || Guid.TryParseExact(context.ProfileId, "N", out _))
        && Enum.IsDefined(context.Provider) && context.BindingKey is { Length: 64 } && context.BindingKey.All(Uri.IsHexDigit);

    internal static bool ValidSnapshot(QuotaObservationHistorySnapshot snapshot, QuotaObservationContext context, DateTimeOffset now,
        bool enforceRetention = false)
    {
        if (snapshot is null || snapshot.Context != context || !ValidContext(context) || snapshot.Revision is < 0 or > long.MaxValue / 2
            || snapshot.Series.IsDefault || snapshot.Series.Length > MaxSeries
            || snapshot.Watermark is { } watermark && !ValidTime(watermark, now)) return false;
        var keys = new HashSet<SeriesKey>();
        var total = 0;
        foreach (var series in snapshot.Series)
        {
            if (series is null || !ValidKey(Key(series)) || !keys.Add(Key(series)) || series.Points.IsDefault
                || series.Watermark is { } mark && (!ValidTime(mark, now) || snapshot.Watermark is null || snapshot.Watermark < mark)) return false;
            total += series.Points.Length;
            DateTimeOffset? previous = null;
            QuotaObservationPoint? previousPoint = null;
            foreach (var point in series.Points)
            {
                if (point is null || !ValidTime(point.ObservedAt, now) || !ValidTime(point.ReceivedAt, now)
                    || point.ObservedAt > point.ReceivedAt || enforceRetention && point.ObservedAt < now - Retention
                    || series.Watermark is null || point.ObservedAt > series.Watermark
                    || previous is { } before && point.ObservedAt <= before || point.SegmentId is <= 0 or > long.MaxValue / 2
                    || !Enum.IsDefined(point.Kind) || point.WindowDurationMinutes is <= 0
                    || point.Kind != series.Kind || point.WindowDurationMinutes != series.WindowDurationMinutes
                    || point.ResetAt is { } reset && reset <= DateTimeOffset.UnixEpoch || point.BasisLimit is < 0
                    || point.BasisUnit is { } unit && !SafeLabel(unit, 16)
                    || point.Value < 0 || series.Metric == QuotaObservationMetric.UsedPercent && point.Value > 100) return false;
                if (previousPoint is { } beforePoint && (point.SegmentId < beforePoint.SegmentId
                    || point.SegmentId == beforePoint.SegmentId && (!point.CanConnect || !beforePoint.CanConnect
                        || point.ResetAt != beforePoint.ResetAt || point.BasisLimit != beforePoint.BasisLimit
                        || point.BasisUnit != beforePoint.BasisUnit
                        || HasDiscontinuity(series.Metric, point.Value, beforePoint.Value)))) return false;
                previous = point.ObservedAt;
                previousPoint = point;
            }
        }
        return total <= MaxTotalPoints && snapshot.Series.GroupBy(s => (s.LimitId, s.Kind, s.WindowDurationMinutes, s.Metric))
            .All(g => g.Sum(s => s.Points.Length) <= MaxPointsPerLimit);
    }

    private static bool ValidKey(SeriesKey key) => (key.LimitId is null || SafeLabel(key.LimitId, 128))
        && Enum.IsDefined(key.Kind) && key.Duration is not <= 0
        && Enum.IsDefined(key.Metric) && SafeLabel(key.Unit, 16)
        && (key.Metric != QuotaObservationMetric.UsedPercent || key.Unit == "%");
    private static bool SafeLabel(string value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '%' or '$')
        && !value.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    private static bool ValidTime(DateTimeOffset? at, DateTimeOffset now) => at is { } time
        && time > DateTimeOffset.UnixEpoch && time <= now;
    // Remaining amounts fall as usage increases. A rise may indicate a reset/refill;
    // used percentages/amounts have the opposite direction. Neither is extrapolated.
    private static bool HasDiscontinuity(QuotaObservationMetric metric, decimal current, decimal previous) =>
        metric == QuotaObservationMetric.RemainingAmount ? current > previous : current < previous;
    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : left > right ? left : right;
    private static SeriesKey Key(QuotaObservationSeries series) => new(series.LimitId, series.Kind, series.WindowDurationMinutes, series.Metric, series.Unit);
    private sealed record SeriesKey(string? LimitId, CodexWindowKind Kind, int? Duration, QuotaObservationMetric Metric, string Unit);
    private sealed record Candidate(SeriesKey Key, DateTimeOffset At, decimal? Value, bool Failed, CodexQuotaWindow Window);
}
