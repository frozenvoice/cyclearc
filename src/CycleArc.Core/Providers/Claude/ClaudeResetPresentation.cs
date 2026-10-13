using System.Globalization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public static class ClaudeResetPresentation
{
    public static string Stamp(CodexQuotaWindow window, DateTimeOffset now) => window.ResetObservation?.State switch
    {
        ClaudeResetObservationState.Elapsed => UiText.T("Awaiting update", "갱신 대기"),
        ClaudeResetObservationState.Stale => UiText.T("Refresh needed", "새로고침 필요"),
        ClaudeResetObservationState.Preserved => CodexDisplayFormatting.ResetStamp(window.ResetsAt, now) + UiText.T(" · prior", " · 이전 확인"),
        _ => CodexDisplayFormatting.ResetStamp(window.ResetsAt, now)
    };

    public static string Countdown(CodexQuotaWindow window, DateTimeOffset now) => window.ResetObservation?.State switch
    {
        ClaudeResetObservationState.Elapsed => UiText.T("Awaiting update", "갱신 대기"),
        ClaudeResetObservationState.Stale => UiText.T("Refresh needed", "새로고침 필요"),
        ClaudeResetObservationState.Preserved => UiText.T("Prior · ", "이전 · ") + CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, now),
        _ => CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, now)
    };

    public static string? Tooltip(CodexQuotaWindow window, DateTimeOffset now)
    {
        if (window.ResetObservation is not { } evidence) return CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt);
        var state = evidence.State switch
        {
            ClaudeResetObservationState.Direct => UiText.T("Reset timestamp supplied in this observation", "이 관측에서 제공된 리셋 시각"),
            ClaudeResetObservationState.Preserved => UiText.T("Previously confirmed reset · retained while observed usage stays continuous; the current source did not supply it", "이전에 확인한 리셋 · 관측된 사용량의 연속성이 유지되어 보존됨. 현재 출처에서 제공된 시각이 아닙니다"),
            ClaudeResetObservationState.Elapsed => UiText.T("Previous reset has elapsed · waiting for a new supplied timestamp", "이전 리셋 시각 경과 · 새 시각 수신 대기"),
            ClaudeResetObservationState.Stale => UiText.T("Observation continuity expired · refresh to verify the current period", "관측 연속성 만료 · 새로고침으로 현재 주기를 확인하세요"),
            _ => UiText.T("Reset not provided or continuity could not be verified", "리셋 미제공 또는 주기 연속성을 확인할 수 없음")
        };
        if (evidence.Source is { } source && evidence.ObservedAt is { } at)
        {
            var label = source == ClaudeResetSource.Server ? UiText.T("Claude server", "Claude 서버") : "Claude Code statusLine";
            state += Environment.NewLine + label + " · " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        }
        if (evidence.LastKnownResetAt is { } reset) state += Environment.NewLine
            + UiText.T("Last supplied reset: ", "마지막 제공 리셋: ") + CodexDeadlineFormatting.ResetStampTooltip(reset);
        return state;
    }
}
