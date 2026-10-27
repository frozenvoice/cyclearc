using System.Globalization;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public static class ClaudeResetPresentation
{
    public static string Stamp(CodexQuotaWindow window, DateTimeOffset now) => window.ResetObservation?.State switch
    {
        ClaudeResetObservationState.Elapsed => UiText.T("Next reset unconfirmed", "다음 리셋 시각 미확인"),
        ClaudeResetObservationState.Stale => UiText.T("Refresh needed", "새로고침 필요"),
        ClaudeResetObservationState.Preserved => CodexDisplayFormatting.ResetStamp(window.ResetsAt, now) + UiText.T(" · prior", " · 이전 확인"),
        ClaudeResetObservationState.NotProvided => UiText.ResetNotProvided,
        _ => CodexDisplayFormatting.ResetStamp(window.ResetsAt, now)
    };

    public static string Countdown(CodexQuotaWindow window, DateTimeOffset now) => window.ResetObservation?.State switch
    {
        ClaudeResetObservationState.Elapsed => UiText.T("Reset unconfirmed", "리셋 미확인"),
        ClaudeResetObservationState.Stale => UiText.T("Refresh needed", "새로고침 필요"),
        ClaudeResetObservationState.Preserved => UiText.T("Prior · ", "이전 · ") + CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, now),
        ClaudeResetObservationState.NotProvided => UiText.ResetNotProvided,
        _ => CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, now)
    };

    public static string SourceLabel(ClaudeResetSource source) => source switch
    {
        ClaudeResetSource.Server => "OAuth",
        ClaudeResetSource.StatusLine => "statusLine",
        ClaudeResetSource.Desktop => "Desktop",
        _ => source.ToString()
    };

    public static string? Tooltip(CodexQuotaWindow window, DateTimeOffset now, CodexQuotaSnapshot? usage = null)
    {
        if (window.ResetObservation is not { } evidence) return CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt);
        if (evidence.State == ClaudeResetObservationState.Elapsed)
            return ElapsedTooltip(evidence, usage);
        var state = evidence.State switch
        {
            ClaudeResetObservationState.Direct => UiText.T("Reset timestamp supplied in this observation", "이 관측에서 제공된 리셋 시각"),
            ClaudeResetObservationState.Preserved => UiText.T("Previously confirmed reset · retained while observed usage stays continuous; the current source did not supply it", "이전에 확인한 리셋 · 관측된 사용량의 연속성이 유지되어 보존됨. 현재 출처에서 제공된 시각이 아닙니다"),
            ClaudeResetObservationState.Stale => UiText.T("Observation continuity expired · refresh to verify the current period", "관측 연속성 만료 · 새로고침으로 현재 주기를 확인하세요"),
            _ => UiText.T("Reset time was not provided", "리셋 시각이 제공되지 않았습니다")
        };
        if (evidence.Source is { } source && evidence.ObservedAt is { } at)
            state += Environment.NewLine + SourceLabel(source) + " · " + Clock(at);
        if (evidence.LastKnownResetAt is { } reset) state += Environment.NewLine
            + UiText.T("Last supplied reset: ", "마지막 제공 리셋: ") + CodexDeadlineFormatting.ResetStampTooltip(reset);
        return state;
    }

    private static string ElapsedTooltip(ClaudeResetObservation evidence, CodexQuotaSnapshot? usage)
    {
        var lines = new List<string>
        {
            UiText.T("Next reset time has not been confirmed.", "새로운 리셋 시각은 아직 확인되지 않았습니다."),
            UiText.T("The last supplied reset has passed. It is not the next reset.", "마지막으로 제공된 리셋 시각은 지났으며, 다음 리셋 시각이 아닙니다."),
            UiText.T("Remaining usage does not show that a new period has started.", "남은 사용량만으로 새 주기가 시작된 것은 아닙니다.")
        };
        var usageSource = UsageSource(usage);
        var usageAt = usage?.LastSuccessfulRefresh;
        var resetSource = evidence.Source;
        var resetObserved = evidence.ObservedAt;
        var sameReading = resetSource is not null && usageSource == resetSource && resetObserved is not null && usageAt == resetObserved;
        if (sameReading)
            lines.Add(UiText.T("Usage and reset evidence · ", "사용량·리셋 근거 · ") + SourceLabel(resetSource!.Value) + " · " + Clock(resetObserved!.Value));
        else
        {
            if (usageSource is not null && usageAt is not null)
                lines.Add(UiText.T("Usage · ", "사용량 · ") + SourceLabel(usageSource.Value) + " · " + UiText.T("checked ", "확인 ") + Clock(usageAt.Value));
            if (resetSource is not null && resetObserved is not null)
                lines.Add(UiText.T("Reset evidence · ", "리셋 근거 · ") + SourceLabel(resetSource.Value) + " · " + UiText.T("observed ", "관측 ") + Clock(resetObserved.Value));
        }
        if (evidence.LastKnownResetAt is { } reset)
            lines.Add(UiText.T("Last supplied reset: ", "마지막 제공 리셋: ") + CodexDeadlineFormatting.ResetStampTooltip(reset)
                + UiText.T(" · elapsed", " · 경과"));
        return string.Join(Environment.NewLine, lines);
    }

    private static ClaudeResetSource? UsageSource(CodexQuotaSnapshot? snapshot)
    {
        if (snapshot?.LastSuccessfulRefresh is null) return null;
        if (snapshot.TechnicalDetail == "claude-desktop-history") return ClaudeResetSource.Desktop;
        if (snapshot.LastSuccessfulObservationWasServer == true
            || snapshot.TechnicalDetail == ClaudeUsagePresentation.LiveDetail)
            return ClaudeResetSource.Server;
        // A live failure keeps the last percentages but replaces the source label.
        // Do not call that receipt statusLine unless the snapshot still says so.
        if (ClaudeUsagePresentation.IsLiveFailure(snapshot.TechnicalDetail)) return null;
        return snapshot.LastSuccessfulObservationWasServer == false ? ClaudeResetSource.StatusLine : null;
    }

    private static string Clock(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
