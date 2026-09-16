using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using System.Globalization;

namespace CycleArc.Providers.Claude;

public static class ClaudeUsagePresentation
{
    public const string UsagePageUrl = "https://claude.ai/settings/usage";
    public static string Title => UiText.T("Claude subscription usage", "Claude 구독 사용량");
    public static string SharedScope => UiText.T("Shared across Web, Desktop and Code", "Web·Desktop·Code 공유 한도");
    public static string LastReceivedLabel => UiText.T("Last received", "마지막 수신");
    public static bool IsStale(CodexQuotaSnapshot snapshot) => snapshot.Provider == UsageProviderId.Claude
        && snapshot.Status == CodexQuotaStatus.Stale;
    public static string ReceiptStamp(CodexQuotaSnapshot snapshot) => snapshot.LastSuccessfulRefresh is { } received
        ? received.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : UiText.Never;
    public static string LastReceivedText(CodexQuotaSnapshot snapshot) => LastReceivedLabel + " " + ReceiptStamp(snapshot);
    public static string UsagePageLabel => UiText.T("Open usage page", "사용량 페이지 열기");
    public static string UsagePageHint => UiText.T("Check current limits in your browser under the intended Claude account. Opening the page does not update CycleArc; Desktop subscription usage history is read separately when available.",
        "브라우저에서 확인할 Claude 계정으로 로그인한 뒤 현재 한도를 보세요. 페이지를 열어도 CycleArc 수치는 갱신되지 않으며, 데스크톱 구독 사용량 기록은 별도로 확인할 수 있을 때만 읽습니다.");

    public static string? FailureLabel(string? detail) => detail switch
    {
        "claude-auth-required" => UiText.T("Login required", "로그인 필요"),
        "claude-request-failed" => UiText.T("Request failed", "요청 실패"),
        "claude-identity-mismatch" => UiText.T("Account changed", "계정 변경됨"),
        "claude-bridge-unavailable" => UiText.T("Connection error", "연결 오류"),
        "claude-desktop-unavailable" => UiText.T("Desktop usage unavailable", "데스크톱 사용량을 확인할 수 없음"),
        "claude-desktop-identity-unverified" => UiText.T("Desktop account not verified", "데스크톱 계정을 확인할 수 없음"),
        _ => null
    };

    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (FailureLabel(snapshot.TechnicalDetail) is { } failure)
        {
            var action = snapshot.TechnicalDetail switch
            {
                "claude-auth-required" or "claude-identity-mismatch" => UiText.T("Reauthenticate this Claude profile, then run Claude Code again or use Desktop Code to receive a new sample.",
                    "이 Claude 프로필을 다시 인증한 뒤 Claude Code를 다시 실행하거나 데스크톱 Code를 사용해 새 값을 받으세요."),
                "claude-request-failed" => UiText.T("Retry Claude Code to receive a new sample. Check the Claude Code terminal if the failure continues.",
                    "Claude Code를 다시 실행해 새 값을 받으세요. 계속 실패하면 Claude Code 터미널을 확인하세요."),
                "claude-desktop-unavailable" => UiText.T("Open Claude Desktop and use its Code tab, then refresh CycleArc after the app records a new sample.",
                    "Claude Desktop의 Code 탭을 사용해 앱이 새 값을 기록한 뒤 CycleArc를 새로고침하세요."),
                "claude-desktop-identity-unverified" => UiText.T("Sign in with the Claude Pro or Max account bound to this profile, then use Claude Desktop Code again.",
                    "이 프로필에 연결된 Claude Pro 또는 Max 계정으로 로그인한 뒤 Claude Desktop Code를 다시 사용하세요."),
                _ => UiText.T("Check the Claude connection, then run Claude Code again or use Desktop Code to receive a new sample.",
                    "Claude 연결을 확인한 뒤 Claude Code 또는 Claude Desktop Code를 다시 사용해 새 값을 받으세요.")
            };
            return snapshot.HasUsablePercentages
                ? failure + ". " + UiText.T("Showing the last valid values. ", "마지막 정상값을 표시합니다. ") + action
                : failure + ". " + action;
        }
        if (snapshot.Status == CodexQuotaStatus.Available)
            return snapshot.TechnicalDetail == "claude-desktop-history"
                ? UiText.T("Last values read from Claude Desktop subscription usage history. Account usage may have changed since then.",
                    "Claude Desktop 구독 사용량 기록에서 마지막으로 읽은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.")
                : UiText.T("Last values received via Claude Code. Account usage may have changed since then.",
                    "Claude Code에서 마지막으로 받은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-connected-waiting")
            return UiText.T("Connected; waiting for usage from Claude Code or Claude Desktop subscription history. Open the terminal in connection settings, use Desktop Code, or check the usage page for current limits.",
                "연결됨 · Claude Code 또는 Claude Desktop 구독 사용량 기록에서 사용량을 확인하는 중입니다. 연결 설정에서 터미널을 열거나 Desktop Code를 사용하고, 사용량 페이지에서 현재 한도를 확인하세요.");
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
            return UiText.T("Stale data · Last values received from Claude Code or read from Claude Desktop subscription usage history. Current usage may differ; check the usage page.",
                "오래된 데이터 · Claude Code에서 받거나 Claude Desktop 구독 사용량 기록에서 읽은 마지막 값입니다. 현재 사용량은 다를 수 있으니 사용량 페이지에서 확인하세요.");
        return UiText.T("No Claude subscription usage received. Use Claude Code or Claude Desktop Code with the connected Pro or Max account, then refresh. Limits may be absent before a first response or history sample.",
            "Claude 구독 사용량을 아직 받지 못했습니다. 연결된 Pro 또는 Max 계정으로 Claude Code나 Claude Desktop Code를 사용한 뒤 새로고침하세요. 첫 응답이나 기록이 생기기 전에는 한도 정보가 없을 수 있습니다.");
    }
}
