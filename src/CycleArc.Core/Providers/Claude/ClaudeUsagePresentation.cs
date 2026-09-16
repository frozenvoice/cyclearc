using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using System.Globalization;

namespace CycleArc.Providers.Claude;

public static class ClaudeUsagePresentation
{
    public const string UsagePageUrl = "https://claude.ai/settings/usage";
    public const string LiveDetail = "claude-live";
    public static string Title => UiText.T("Claude subscription usage", "Claude 구독 사용량");
    public static string SharedScope => UiText.T("Shared across Web, Desktop and Code", "Web·Desktop·Code 공유 한도");
    public static string LastReceivedLabel => UiText.T("Last received", "마지막 수신");
    public static string LastCheckedLabel => UiText.T("Last checked", "마지막 확인");
    public static bool IsStale(CodexQuotaSnapshot snapshot) => snapshot.Provider == UsageProviderId.Claude
        && snapshot.Status == CodexQuotaStatus.Stale;
    public static bool IsLive(CodexQuotaSnapshot snapshot) => snapshot.Provider == UsageProviderId.Claude
        && string.Equals(snapshot.TechnicalDetail, LiveDetail, StringComparison.Ordinal);
    public static bool IsLiveFailure(string? detail) => detail is
        "claude-live-auth-required" or "claude-live-request-failed" or "claude-live-rate-limited"
        or "claude-live-identity-mismatch" or "claude-live-unavailable";
    public static string ReceiptStamp(CodexQuotaSnapshot snapshot) => snapshot.LastSuccessfulRefresh is { } received
        ? received.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : UiText.Never;
    public static string ReceiptLabel(CodexQuotaSnapshot snapshot) => IsLive(snapshot) || IsLiveFailure(snapshot.TechnicalDetail)
        ? LastCheckedLabel : LastReceivedLabel;
    public static string LastReceivedText(CodexQuotaSnapshot snapshot) => ReceiptLabel(snapshot) + " " + ReceiptStamp(snapshot);
    public static string SourceText(CodexQuotaSnapshot snapshot) => snapshot.TechnicalDetail switch
    {
        LiveDetail => UiText.T("Fetched from Claude server", "Claude 서버에서 조회됨"),
        "claude-desktop-history" => UiText.T("Read from Claude Desktop subscription history", "Claude Desktop 구독 사용량 기록에서 읽음"),
        var detail when IsLiveFailure(detail) => UiText.T("Claude server check failed · showing previous values", "Claude 서버 조회 실패 · 이전 값 표시"),
        _ => UiText.T("Received via Claude Code statusLine", "Claude Code statusLine 수신")
    };
    public static string SourceShortText(CodexQuotaSnapshot snapshot) => snapshot.TechnicalDetail switch
    {
        LiveDetail => UiText.T("Via server", "서버 조회"),
        "claude-desktop-history" => UiText.T("Via Desktop history", "Desktop 기록"),
        var detail when IsLiveFailure(detail) => UiText.T("Previous value · server check failed", "이전 값 · 서버 조회 실패"),
        _ => UiText.T("Via Code", "Code에서 수신")
    };
    public static string UsagePageLabel => UiText.T("Open usage page", "사용량 페이지 열기");
    public static string UsagePageHint => UiText.T("Check current limits in your browser under the intended Claude account. Opening the page does not update CycleArc; manual and automatic refresh check the shared quota through the connected Desktop account.",
        "브라우저에서 확인할 Claude 계정으로 로그인한 뒤 현재 한도를 보세요. 페이지를 열어도 CycleArc 수치는 갱신되지 않으며, 수동·자동 새로고침이 연결된 Desktop 계정으로 공유 한도를 확인합니다.");

    public static string? FailureLabel(string? detail) => detail switch
    {
        "claude-auth-required" => UiText.T("Login required", "로그인 필요"),
        "claude-request-failed" => UiText.T("Request failed", "요청 실패"),
        "claude-identity-mismatch" => UiText.T("Account changed", "계정 변경됨"),
        "claude-bridge-unavailable" => UiText.T("Connection error", "연결 오류"),
        "claude-desktop-unavailable" => UiText.T("Desktop usage unavailable", "데스크톱 사용량을 확인할 수 없음"),
        "claude-desktop-identity-unverified" => UiText.T("Desktop account not verified", "데스크톱 계정을 확인할 수 없음"),
        "claude-live-auth-required" => UiText.T("Claude Desktop login required", "Claude Desktop 로그인 필요"),
        "claude-live-request-failed" => UiText.T("Claude server request failed", "Claude 서버 조회 실패"),
        "claude-live-rate-limited" => UiText.T("Claude server rate limited", "Claude 서버 요청 제한"),
        "claude-live-identity-mismatch" => UiText.T("Claude Desktop account changed", "Claude Desktop 계정 변경됨"),
        "claude-live-unavailable" => UiText.T("Live usage unavailable", "실시간 사용량 조회 불가"),
        _ => null
    };

    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (FailureLabel(snapshot.TechnicalDetail) is { } failure)
        {
            var action = snapshot.TechnicalDetail switch
            {
                "claude-live-auth-required" or "claude-live-identity-mismatch" => UiText.T("Sign in to the intended Claude account in Claude Desktop, then retry CycleArc refresh. CycleArc checks the quota directly; no Claude Code model request is needed.",
                    "Claude Desktop에서 사용할 계정으로 로그인한 뒤 CycleArc 새로고침을 다시 시도하세요. CycleArc가 한도를 직접 확인하므로 Claude Code 모델 요청은 필요하지 않습니다."),
                "claude-live-rate-limited" => UiText.T("Wait briefly, then retry CycleArc refresh. The quota check does not use a model request.",
                    "잠시 기다린 뒤 CycleArc 새로고침을 다시 시도하세요. 한도 조회에는 모델 요청을 사용하지 않습니다."),
                "claude-live-request-failed" => UiText.T("Retry CycleArc refresh. The direct Claude server quota check failed; no model request is needed.",
                    "CycleArc 새로고침을 다시 시도하세요. Claude 서버 직접 한도 조회에 실패했으며 모델 요청은 필요하지 않습니다."),
                "claude-live-unavailable" => UiText.T("Open Claude Desktop, sign in to the intended account, then retry CycleArc refresh. The quota check does not run a model request.",
                    "Claude Desktop을 열고 사용할 계정으로 로그인한 뒤 CycleArc 새로고침을 다시 시도하세요. 한도 조회에는 모델 요청을 실행하지 않습니다."),
                "claude-auth-required" or "claude-identity-mismatch" => UiText.T("Reauthenticate this Claude profile, sign in to the same account in Claude Desktop, then refresh CycleArc.",
                    "이 Claude 프로필을 다시 인증하고 Claude Desktop에 같은 계정으로 로그인한 뒤 CycleArc를 새로고침하세요."),
                "claude-request-failed" => UiText.T("Retry CycleArc refresh. If you use Claude Code, check its terminal if the failure continues.",
                    "CycleArc 새로고침을 다시 시도하세요. Claude Code를 사용 중이고 오류가 계속되면 해당 터미널을 확인하세요."),
                "claude-desktop-unavailable" => UiText.T("Desktop usage history is unavailable. Sign in to the same account in Claude Desktop, then refresh CycleArc for a server check.",
                    "Desktop 사용량 기록을 확인할 수 없습니다. Claude Desktop에 같은 계정으로 로그인한 뒤 CycleArc를 새로고침해 서버 한도를 조회하세요."),
                "claude-desktop-identity-unverified" => UiText.T("Sign in with the Claude Pro or Max account bound to this profile in Claude Desktop, then retry CycleArc refresh.",
                    "Claude Desktop에서 이 프로필에 연결된 Claude Pro 또는 Max 계정으로 로그인한 뒤 CycleArc 새로고침을 다시 시도하세요."),
                _ => UiText.T("Check the Claude connection, then retry CycleArc refresh.",
                    "Claude 연결을 확인한 뒤 CycleArc 새로고침을 다시 시도하세요.")
            };
            return snapshot.HasUsablePercentages
                ? failure + ". " + UiText.T("Showing the last valid values; they may not be current. ", "마지막 정상값을 표시하며 현재 값이 아닐 수 있습니다. ") + action
                : failure + ". " + action;
        }
        if (snapshot.Status == CodexQuotaStatus.Available)
        {
            if (IsLive(snapshot))
                return UiText.T($"Updated from the Claude server at {ReceiptStamp(snapshot)}. Manual and automatic refresh check the shared subscription quota.",
                    $"Claude 서버에서 {ReceiptStamp(snapshot)}에 업데이트했습니다. 수동·자동 새로고침으로 공유 구독 한도를 확인합니다.");
            return snapshot.TechnicalDetail == "claude-desktop-history"
                ? UiText.T("Last values read from Claude Desktop subscription usage history. Account usage may have changed since then.",
                    "Claude Desktop 구독 사용량 기록에서 마지막으로 읽은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.")
                : UiText.T("Last values received via Claude Code. Account usage may have changed since then.",
                    "Claude Code에서 마지막으로 받은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.");
        }
        if (snapshot.TechnicalDetail == "claude-connected-waiting")
            return UiText.T("Connected; no usage received yet. Sign in to the same account in Claude Desktop and refresh, or check the usage page for current limits.",
                "연결됨 · 아직 사용량을 받지 못했습니다. Claude Desktop에 같은 계정으로 로그인한 뒤 새로고침하거나 사용량 페이지에서 현재 한도를 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.SignedOut)
            return UiText.T("Claude is disconnected. Open Connect to reconnect this profile.", "Claude 연결이 해제되었습니다. 연결 버튼에서 다시 연결할 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-statusline-malformed" || snapshot.Status == CodexQuotaStatus.ProtocolMismatch)
            return snapshot.HasUsablePercentages
                ? UiText.T("Stale data · Showing the last valid values. The latest Claude sample could not be read.",
                    "오래된 데이터 · 마지막 정상 수신값입니다. 새 Claude 데이터를 읽지 못했습니다.")
                : UiText.T("Unsupported Claude usage data. Check your Claude Code connection or Claude Desktop installation.",
                    "Claude 사용량 형식을 확인할 수 없습니다. Claude Code 연결이나 Claude Desktop 설치를 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.Stale && snapshot.TechnicalDetail == "claude-receipt-invalid")
            return UiText.T("Stale data · The receipt time could not be verified. Showing the last valid values.",
                "오래된 데이터 · 수신 시각을 확인할 수 없어 마지막 정상값을 표시합니다.");
        if (snapshot.Status == CodexQuotaStatus.Stale)
            return UiText.T("Stale data · Showing the last valid usage. Refresh CycleArc or check the usage page for current limits.",
                "오래된 데이터 · 마지막 정상 사용량입니다. CycleArc를 새로고침하거나 사용량 페이지에서 현재 한도를 확인하세요.");
        return UiText.T("No Claude subscription usage received. Sign in to the intended Pro or Max account in Claude Desktop, then refresh. CycleArc checks the shared quota directly; a model request is not needed.",
            "Claude 구독 사용량을 아직 받지 못했습니다. Claude Desktop에서 사용할 Pro 또는 Max 계정으로 로그인한 뒤 새로고침하세요. CycleArc가 공유 한도를 직접 확인하므로 모델 요청은 필요하지 않습니다.");
    }
}
