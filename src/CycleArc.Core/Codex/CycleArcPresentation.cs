using CycleArc.Services;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;

namespace CycleArc.Codex;

public static class CycleArcPresentation
{
    public static string StatusLabel(CodexQuotaSnapshot snapshot)
    {
        if (CodexIdentityPresentation.NeedsReconnection(snapshot)) return CodexIdentityPresentation.Label(snapshot);
        if (snapshot.Provider == UsageProviderId.Claude && ClaudeUsagePresentation.FailureLabel(snapshot.TechnicalDetail) is { } failure)
            return failure;
        return snapshot.Status switch
        {
            CodexQuotaStatus.Unavailable when snapshot.TechnicalDetail == "claude-connected-waiting" => UiText.T("Awaiting usage", "수신 대기"),
            CodexQuotaStatus.Unavailable when snapshot.Provider == UsageProviderId.Claude => UiText.T("Waiting for data", "데이터 대기 중"),
            CodexQuotaStatus.ProtocolMismatch when snapshot.Provider == UsageProviderId.Claude => UiText.ProviderSchemaMismatch,
            CodexQuotaStatus.SignedOut when snapshot.Provider == UsageProviderId.Claude => UiText.T("Disconnected", "미연결"),
            CodexQuotaStatus.Available when ClaudeUsagePresentation.IsLive(snapshot) => UiText.T("Updated", "업데이트됨"),
            CodexQuotaStatus.Available when snapshot.Provider == UsageProviderId.Claude => UiText.T("Received", "수신됨"),
            CodexQuotaStatus.Available => UiText.T("Updated", "업데이트됨"),
            CodexQuotaStatus.Refreshing => UiText.T("Refreshing", "새로고침 중"),
            CodexQuotaStatus.Stale when snapshot.Provider == UsageProviderId.Claude => UiText.T("Stale data", "오래된 데이터"),
            CodexQuotaStatus.Stale => UiText.T("Saved data", "이전 데이터"),
            CodexQuotaStatus.SignedOut => UiText.T("Sign in required", "로그인 필요"),
            CodexQuotaStatus.CodexNotFound => UiText.T("Codex not found", "Codex 찾을 수 없음"),
            _ => UiText.T("Refresh failed", "조회 실패")
        };
    }
    public static string CompactText(CodexQuotaSnapshot snapshot, TaskbarStripMode mode = TaskbarStripMode.Full,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var ring = CodexRingPresentation.From(snapshot, preference);
        var prefix = mode == TaskbarStripMode.Full ? snapshot.Provider.Name() + " "
            : snapshot.Provider == UsageProviderId.Claude ? "Cl " : "C ";
        if (!ring.IsAvailable) return prefix + "?";
        var suffix = snapshot.Status == CodexQuotaStatus.Stale ? " ~" : snapshot.Status == CodexQuotaStatus.Refreshing ? " …" : "";
        return prefix + CodexDisplayFormatting.PercentText(ring.UsedPercent, snapshot.Provider) + suffix;
    }

    public static string Tooltip(CodexQuotaSnapshot snapshot, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var ring = CodexRingPresentation.From(snapshot, preference);
        var usage = ring.IsAvailable
            ? CodexDisplayFormatting.CompactWindowKindLabel(ring.Window, snapshot.Provider) + " " + ring.CenterValueText
            : CodexDisplayFormatting.StatusText(snapshot);
        var title = snapshot.Provider == UsageProviderId.Claude ? ClaudeUsagePresentation.Title : snapshot.Provider.Name();
        var context = snapshot.Provider == UsageProviderId.Claude
            ? Environment.NewLine + ClaudeUsagePresentation.SharedScope
                + Environment.NewLine + ClaudeUsagePresentation.SourceText(snapshot)
                + Environment.NewLine + ClaudeUsagePresentation.LastReceivedText(snapshot)
                : "";
        return UiText.ProductName + " · " + title + Environment.NewLine
            + usage + Environment.NewLine + StatusLabel(snapshot) + context;
    }

    public static string TrayTooltip(CodexQuotaSnapshot snapshot, string? accountName = null,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        if (CodexIdentityPresentation.NeedsReconnection(snapshot))
            return NotifyIconText.Safe((accountName is null ? "" : accountName + Environment.NewLine)
                + "Codex · " + CodexIdentityPresentation.Label(snapshot) + Environment.NewLine
                + CodexIdentityPresentation.Explanation(snapshot));
        if (snapshot.Provider != UsageProviderId.Claude)
            return NotifyIconText.Safe((accountName is null ? "" : accountName + Environment.NewLine) + Tooltip(snapshot, preference));

        // The native limit is 127 characters: preserve status, receipt and scope before an optional nickname.
        var ring = CodexRingPresentation.From(snapshot, preference);
        return NotifyIconText.Safe(string.Join("\n",
            "Claude · " + StatusLabel(snapshot),
            ring.CenterSubLabel + " " + ring.CenterValueText,
            ClaudeUsagePresentation.LastReceivedText(snapshot),
            UiText.T("Web·Desktop·Code shared quota", "Web·Desktop·Code 공유 한도"),
            ClaudeUsagePresentation.SourceShortText(snapshot))
            + (accountName is null ? "" : "\n" + accountName));
    }
}
