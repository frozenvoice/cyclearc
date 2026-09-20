using CycleArc.Services;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;

namespace CycleArc.Codex;

public static class CycleArcPresentation
{
    public static string StatusLabel(CodexQuotaSnapshot snapshot)
    {
        if (CodexIdentityPresentation.NeedsReconnection(snapshot)) return CodexIdentityPresentation.Label(snapshot);
        if (snapshot.Provider == UsageProviderId.Claude && ClaudeUsagePresentation.FailureLabel(snapshot.TechnicalDetail) is { } failure)
            return failure;
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            // The optional Grok/Sand allowance is independent from Cursor's
            // monthly limits.  A failed Sand request must not make an otherwise
            // valid monthly sample look stale or unavailable on compact surfaces.
            if (snapshot.Status == CodexQuotaStatus.Available
                && string.Equals(snapshot.TechnicalDetail, "cursor-sand-unavailable", StringComparison.Ordinal))
                return UiText.T("Updated", "업데이트됨");
            if (CursorUsagePresentation.FailureText(snapshot.TechnicalDetail) is { Length: > 0 } cursorFailure)
                return cursorFailure;
            return snapshot.Status switch
            {
                CodexQuotaStatus.Available => UiText.T("Updated", "업데이트됨"),
                CodexQuotaStatus.Stale => UiText.T("Stale data", "오래된 데이터"),
                CodexQuotaStatus.Refreshing => UiText.T("Refreshing", "새로고침 중"),
                CodexQuotaStatus.SignedOut => UiText.T("Sign in required", "로그인 필요"),
                CodexQuotaStatus.ProtocolMismatch => UiText.ProviderSchemaMismatch,
                _ => UiText.T("Cursor usage unavailable", "Cursor 사용량 확인 불가")
            };
        }
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
            : snapshot.Provider == UsageProviderId.Claude ? "Cl "
            : CursorUsagePresentation.IsCursor(snapshot) ? "Cu " : "C ";
        if (!ring.IsAvailable) return prefix + "?";
        var suffix = snapshot.Status == CodexQuotaStatus.Stale ? " ~" : snapshot.Status == CodexQuotaStatus.Refreshing ? " …" : "";
        return prefix + CodexDisplayFormatting.PercentText(ring.UsedPercent, snapshot.Provider) + suffix;
    }

    public static string Tooltip(CodexQuotaSnapshot snapshot, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            var quotas = snapshot.Windows.Count == 0
                ? CursorUsagePresentation.StatusText(snapshot)
                : string.Join(Environment.NewLine, snapshot.Windows.Select(window =>
                    $"{CursorUsagePresentation.QuotaLabel(window.LimitId)}: {CursorUsagePresentation.RemainingText(window)}"));
            return UiText.ProductName + " · " + CursorUsagePresentation.Title + Environment.NewLine
                + quotas + Environment.NewLine + CursorUsagePresentation.UpdatedText(snapshot);
        }
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
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            // NotifyIcon has a native 127-character limit. Keep status and the
            // actual update time first, then fit as many independent allowances
            // as the native title can hold without dropping the timestamp.
            var text = "Cursor · " + StatusLabel(snapshot) + "\n" + CursorUsagePresentation.UpdatedText(snapshot);
            var omitted = 0;
            for (var index = 0; index < snapshot.Windows.Count; index++)
            {
                var window = snapshot.Windows[index];
                var quota = CursorUsagePresentation.QuotaLabel(window.LimitId) + " "
                    + CursorUsagePresentation.RemainingText(window);
                var candidate = text + "\n" + quota;
                if (candidate.Length > NotifyIconText.MaximumLength)
                {
                    omitted = snapshot.Windows.Count - index;
                    break;
                }
                text = candidate;
            }
            if (omitted > 0)
            {
                var more = UiText.T($"… +{omitted} more", $"… {omitted}개 더");
                if (text.Length + 1 + more.Length <= NotifyIconText.MaximumLength)
                    text += "\n" + more;
            }
            if (snapshot.Windows.Count == 0)
                text += "\n" + CursorUsagePresentation.StatusText(snapshot);
            if (accountName is not null && text.Length + accountName.Length + 1 <= NotifyIconText.MaximumLength)
                text += "\n" + accountName;
            return NotifyIconText.Safe(text);
        }
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
