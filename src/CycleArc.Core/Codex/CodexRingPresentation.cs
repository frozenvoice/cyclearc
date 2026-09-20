using CycleArc.Services;
using CycleArc.Models;
using CycleArc.Providers.Cursor;

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
                CursorUsagePresentation.IsCursor(snapshot.Provider) ? CursorUsagePresentation.QuotaLabel(window.LimitId) :
                window.Kind == CodexWindowKind.Weekly ? UiText.T("Weekly used", "주간 사용") :
                UiText.T($"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} used",
                    $"{CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes)} 사용"))
        {
            Window = window
        };
    }
}
