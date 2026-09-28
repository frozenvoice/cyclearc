using CycleArc.Services;
using CycleArc.Models;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;

namespace CycleArc.Codex;

/// <summary>
/// Everything the Flyout Codex ring needs, computed once so the WPF code-behind only
/// assigns values instead of re-deriving Codex display policy.
/// </summary>
public sealed record CodexRingPresentation(
    double? UsedPercent,
    bool IsAvailable,
    bool IsDangerLevel,
    string CenterValueText,
    string CenterSubLabel)
{
    public CodexQuotaWindow? Window { get; init; }
    // Color band of this ring's own limit, from the unrounded value; unknown stays Normal.
    public UsageRingBand Band { get; init; }

    // The popup and widget rings fill with what is left of this limit. The color band above
    // still comes from the unrounded usage, so an almost empty ring reads as a warning.
    public double? RemainingPercent => UsedPercent is { } used ? 100 - used : null;
    // Remaining text uses the shared validation: invalid or absent usage stays unknown.
    public string RemainingValueText { get; init; } = "?";
    public string RemainingSubLabel { get; init; } = UiText.CodexLegendRemaining;

    public static CodexRingPresentation FromDetail(CodexQuotaSnapshot snapshot,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var ring = From(snapshot, preference);
        return ring with
        {
            // Format the original value, not the clamped geometry or widget glyph.
            CenterValueText = UsagePercentFormatting.Detail(ring.IsAvailable ? ring.Window?.UsedPercent : null)
        };
    }

    public static CodexRingPresentation From(CodexQuotaSnapshot snapshot, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var window = snapshot.DisplayWindow(preference);
        var used = snapshot.Status is CodexQuotaStatus.Available or CodexQuotaStatus.Stale or CodexQuotaStatus.Refreshing
            ? window?.UsedPercent : null;
        if (used is double raw && !double.IsFinite(raw)) used = null;
        var clamped = used is double value ? Math.Clamp(value, 0, 100) : (double?)null;
        return new CodexRingPresentation(
            UsedPercent: clamped,
            IsAvailable: clamped is not null,
            IsDangerLevel: clamped is >= 100,
            CenterValueText: CodexDisplayFormatting.PercentText(used, snapshot.Provider),
            CenterSubLabel: window is null ? UiText.CodexLegendUsed :
                CursorUsagePresentation.IsCursor(snapshot.Provider) ? CursorUsagePresentation.RingLabel(window.LimitId) :
                window.Kind == CodexWindowKind.Weekly ? UiText.T("Weekly used", "주간 사용") :
                UiText.T($"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} used",
                    $"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} 사용"))
        {
            Window = window,
            Band = clamped is null ? UsageRingBand.Normal : UsageRingBands.From(used),
            RemainingValueText = clamped is not null && window is not null
                ? UsagePercentFormatting.DetailRemaining(window) : "?",
            RemainingSubLabel = RemainingLabel(window, snapshot.Provider)
        };
    }

    private static string RemainingLabel(CodexQuotaWindow? window, UsageProviderId provider)
    {
        if (window is null) return UiText.CodexLegendRemaining;
        if (CursorUsagePresentation.IsCursor(provider))
        {
            var period = CursorUsagePresentation.QuotaPeriodLabel(window.LimitId);
            return CursorUsagePresentation.QuotaLabel(window.LimitId) + Environment.NewLine
                + (period is null ? UiText.CodexLegendRemaining : UiText.T($"{period} left", $"{period} 남음"));
        }
        return window.Kind == CodexWindowKind.Weekly
            ? UiText.T("Weekly left", "주간 남음")
            : UiText.T($"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} left",
                $"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} 남음");
    }
}
