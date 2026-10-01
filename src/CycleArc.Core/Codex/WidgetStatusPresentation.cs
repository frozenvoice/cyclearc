using System.Globalization;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Codex;

public enum WidgetStatusSeverity { Normal, Pending, Warning }

/// <summary>Freshness and connection state, independent of quota bands and collector status.</summary>
public sealed record WidgetStatusPresentation(
    WidgetStatusSeverity Severity, string Summary, string? AgeText,
    DateTimeOffset? ObservationTime, string DetailText, bool ShowRow)
{
    public bool IsWarning => Severity == WidgetStatusSeverity.Warning;
    public string BrushKey => IsWarning ? "StaleBrush" : "MutedBrush";

    public static bool HidesQuota(CodexQuotaSnapshot snapshot) =>
        CodexIdentityPresentation.NeedsReconnection(snapshot)
        || snapshot.TechnicalDetail is "claude-identity-mismatch" or "claude-live-identity-mismatch"
            or "claude-desktop-identity-unverified" or "cursor-identity-mismatch" or "cursor-live-identity-mismatch"
        || snapshot.Status == CodexQuotaStatus.SignedOut;

    public static bool HasHealthyServerSample(CodexQuotaSnapshot snapshot) =>
        snapshot.Status == CodexQuotaStatus.Available
        && snapshot.LastSuccessfulRefresh is not null && snapshot.Windows.Count > 0
        && !HidesQuota(snapshot)
        && (snapshot.Provider == UsageProviderId.Codex
            || ClaudeUsagePresentation.IsLive(snapshot)
            || (CursorUsagePresentation.IsCursor(snapshot)
                && snapshot.TechnicalDetail is null or CursorUsagePresentation.LiveDetail));

    public static WidgetStatusPresentation From(CodexQuotaSnapshot snapshot, DateTimeOffset now,
        bool signingIn = false)
    {
        var detail = snapshot.TechnicalDetail;
        var originalStatus = snapshot.Status == CodexQuotaStatus.Refreshing
            ? snapshot.RefreshOriginStatus ?? snapshot.Status : snapshot.Status;
        var identity = HidesQuota(snapshot) && snapshot.Status != CodexQuotaStatus.SignedOut;
        var auth = snapshot.Status == CodexQuotaStatus.SignedOut
            || originalStatus == CodexQuotaStatus.SignedOut
            || detail is "claude-auth-required" or "claude-live-auth-required" or "cursor-auth-required"
                or "cursor-signed-out" or "cursor-live-auth-required";
        var knownFailure = FailureSummary(detail);
        var stale = originalStatus == CodexQuotaStatus.Stale;
        var failed = originalStatus is CodexQuotaStatus.ProtocolMismatch or CodexQuotaStatus.TimedOut
            or CodexQuotaStatus.Cancelled or CodexQuotaStatus.CodexNotFound
            || (originalStatus == CodexQuotaStatus.Unavailable && snapshot.LastAttemptedRefresh is not null
                && detail is not ("claude-connected-waiting" or "cursor-connected-waiting" or "codex-identity-pending"));
        var warning = identity || auth || stale || knownFailure is not null || failed;
        var severity = warning ? WidgetStatusSeverity.Warning
            : HasHealthyServerSample(snapshot) && !signingIn ? WidgetStatusSeverity.Normal : WidgetStatusSeverity.Pending;
        var summary = identity ? UiText.T("Check account", "계정 확인 필요")
            : auth ? detail == "claude-live-auth-required"
                ? UiText.T("Claude Desktop login required", "Claude Desktop 로그인 필요")
                : UiText.T("Sign in required", "로그인 필요")
            : knownFailure ?? (stale ? UiText.T("Previous data", "이전 데이터")
                : failed ? originalStatus == CodexQuotaStatus.CodexNotFound
                    ? UiText.T("Codex not found", "Codex 찾을 수 없음") : UiText.T("Check failed", "조회 실패")
                : signingIn ? UiText.T("Signing in…", "로그인 중…")
                : snapshot.Status == CodexQuotaStatus.Refreshing ? UiText.T("Checking…", "확인 중…")
                : snapshot.Status == CodexQuotaStatus.Available
                    ? snapshot.Provider == UsageProviderId.Claude && !ClaudeUsagePresentation.IsLive(snapshot)
                        ? UiText.T("Received", "수신됨") : UiText.T("Updated", "업데이트됨")
                    : UiText.T("Awaiting usage", "수신 대기"));

        // A local receipt retains its own timestamp. Never use the attempt/render time.
        var label = snapshot.Provider == UsageProviderId.Claude
            ? ClaudeUsagePresentation.ReceiptLabel(snapshot)
            : UiText.T("Last checked", "마지막 확인");
        var time = snapshot.LastSuccessfulRefresh;
        var stamp = time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var elapsed = CodexDeadlineFormatting.Elapsed(time, now);
        var age = stamp is null ? null : label + " " + (elapsed ?? stamp);
        var explanation = snapshot.Provider == UsageProviderId.Claude
            ? ClaudeUsagePresentation.StatusText(snapshot)
            : snapshot.Provider == UsageProviderId.Cursor
                ? CursorUsagePresentation.FailureText(detail) + Environment.NewLine + CursorUsagePresentation.StatusText(snapshot)
                : CodexIdentityPresentation.NeedsReconnection(snapshot)
                    ? CodexIdentityPresentation.Explanation(snapshot) : CycleArcPresentation.StatusLabel(snapshot);
        var full = string.Join(Environment.NewLine, new[] { summary, stamp is null ? null : label + " " + stamp, explanation }
            .Where(line => !string.IsNullOrWhiteSpace(line)).Distinct());
        return new(severity, summary, age, time, full, severity != WidgetStatusSeverity.Normal);
    }

    private static string? FailureSummary(string? detail) => detail switch
    {
        "claude-live-rate-limited" or "cursor-live-rate-limited" => UiText.T("Request limited", "요청 제한"),
        "claude-request-failed" or "claude-live-request-failed" or "cursor-request-failed"
            or "cursor-live-request-failed" or "stdout-closed" or "timed-out" => UiText.T("Check failed", "조회 실패"),
        "claude-statusline-malformed" or "cursor-schema-mismatch" or "cursor-live-schema-mismatch"
            or "oversized-or-invalid-jsonl" => UiText.T("Data unreadable", "데이터 확인 불가"),
        "claude-bridge-unavailable" or "claude-desktop-unavailable" or "claude-live-unavailable"
            or "cursor-credential-unavailable" or "cursor-live-unavailable" or "cursor-connection-unavailable"
            => UiText.T("Check connection", "연결 확인 필요"),
        _ => null
    };
}
