using System.Collections.Immutable;
using System.Text.Json.Serialization;
using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.Observations;

public enum QuotaObservationMetric { UsedPercent, UsedAmount, RemainingAmount }

public sealed record QuotaObservationContext([property: JsonRequired] string ProfileId,
    [property: JsonRequired] UsageProviderId Provider, [property: JsonRequired] string BindingKey);

public sealed record QuotaObservationPoint(
    [property: JsonRequired] DateTimeOffset ObservedAt, [property: JsonRequired] DateTimeOffset ReceivedAt,
    [property: JsonRequired] decimal Value, [property: JsonRequired] long SegmentId,
    [property: JsonRequired] DateTimeOffset? ResetAt, [property: JsonRequired] int? WindowDurationMinutes,
    [property: JsonRequired] CodexWindowKind Kind, [property: JsonRequired] decimal? BasisLimit,
    [property: JsonRequired] string? BasisUnit = null)
{
    // A missing reset does not establish that two samples describe the same allowance window.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanConnect => ResetAt > ObservedAt;
}

public sealed record QuotaObservationSeries(
    [property: JsonRequired] string? LimitId, [property: JsonRequired] CodexWindowKind Kind,
    [property: JsonRequired] int? WindowDurationMinutes, [property: JsonRequired] QuotaObservationMetric Metric,
    [property: JsonRequired] string Unit, [property: JsonRequired] DateTimeOffset? Watermark,
    [property: JsonRequired] bool PendingGap, [property: JsonRequired] ImmutableArray<QuotaObservationPoint> Points);

public sealed record QuotaObservationWindow(
    QuotaObservationMetric Metric, string Unit, ImmutableArray<QuotaObservationPoint> Points)
{
    public DateTimeOffset? LastObservedAt => Points.IsEmpty ? null : Points[^1].ObservedAt;
    public DateTimeOffset? LastReceivedAt => Points.IsEmpty ? null : Points[^1].ReceivedAt;
}

public sealed record QuotaObservationHistorySnapshot(
    [property: JsonRequired] QuotaObservationContext Context, [property: JsonRequired] long Revision,
    [property: JsonRequired] DateTimeOffset? Watermark,
    [property: JsonRequired] ImmutableArray<QuotaObservationSeries> Series)
{
    // The immutable snapshot owns this tiny cache. Callers can keep the returned reference
    // until snapshot/selection/range changes; a clock tick requires no filtering or rendering.
    private readonly Dictionary<WindowKey, QuotaObservationWindow> _views = new();
    private readonly object _viewGate = new();

    public QuotaObservationWindow GetCurrentWindow(CodexQuotaWindow window,
        QuotaObservationMetric metric = QuotaObservationMetric.UsedPercent)
    {
        var unit = metric == QuotaObservationMetric.UsedPercent ? "%" : window.Unit ?? "";
        var key = new WindowKey(window.LimitId, metric, unit, window.ResetsAt,
            window.WindowDurationMinutes, window.Kind, window.LimitAmount, window.Unit);
        lock (_viewGate)
        {
            if (_views.TryGetValue(key, out var cached)) return cached;
            var series = Series.FirstOrDefault(s => s.LimitId == key.LimitId && s.Kind == key.Kind
                && s.WindowDurationMinutes == key.Duration && s.Metric == metric && s.Unit == unit);
            var points = series?.Points.Where(p => p.ResetAt == key.ResetAt
                && p.WindowDurationMinutes == key.Duration && p.Kind == key.Kind
                && p.BasisLimit == key.Basis && p.BasisUnit == key.BasisUnit).ToImmutableArray() ?? [];
            var result = new QuotaObservationWindow(metric, unit, points);
            // A snapshot cannot acquire an unbounded collection through arbitrary UI lookups.
            if (_views.Count < QuotaObservationHistory.MaxSeries * 3) _views.Add(key, result);
            return result;
        }
    }

    private sealed record WindowKey(string? LimitId, QuotaObservationMetric Metric, string Unit,
        DateTimeOffset? ResetAt, int? Duration, CodexWindowKind Kind, decimal? Basis, string? BasisUnit);
}
