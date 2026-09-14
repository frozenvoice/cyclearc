using CycleArc.Codex;
using CycleArc.Services;
using CycleArc.Providers.Usage;
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
    public static string UsagePageHint => UiText.T("Check current limits in your browser under the intended Claude account. Opening the page does not update CycleArc.",
        "브라우저에서 확인할 Claude 계정으로 로그인한 뒤 현재 한도를 보세요. 페이지를 열어도 CycleArc 수치는 갱신되지 않습니다.");

    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Status == CodexQuotaStatus.Available)
            return UiText.T("Last values received via the Claude Code terminal. Account usage may have changed since then.",
                "Claude Code 터미널에서 마지막으로 받은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-connected-waiting")
            return UiText.T("Connected; waiting for usage from the Claude Code terminal. Desktop Code is not a supported source for CycleArc. Open the terminal in connection settings, or check the usage page for current limits.",
                "연결됨 · Claude Code 터미널에서 수신 대기 중입니다. CycleArc는 데스크톱 Code 탭에서 사용량을 받는 기능을 지원하지 않습니다. 연결 설정에서 터미널을 열거나 사용량 페이지에서 현재 한도를 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.SignedOut)
            return UiText.T("Claude is disconnected. Open Connect to reconnect this profile.", "Claude 연결이 해제되었습니다. 연결 버튼에서 다시 연결할 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-statusline-malformed" || snapshot.Status == CodexQuotaStatus.ProtocolMismatch)
            return snapshot.HasUsablePercentages
                ? UiText.T("Stale data · Showing the last valid values. The latest Claude sample could not be read.",
                    "오래된 데이터 · 마지막 정상 수신값입니다. 새 Claude 데이터를 읽지 못했습니다.")
                : UiText.T("Unsupported Claude statusLine data. Check your Claude Code version and connection command.",
                    "Claude statusLine 형식을 확인할 수 없습니다. Claude Code 버전과 연결 명령을 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.Stale && snapshot.TechnicalDetail == "claude-receipt-invalid")
            return UiText.T("Stale data · The receipt time could not be verified. Showing the last valid values.",
                "오래된 데이터 · 수신 시각을 확인할 수 없어 마지막 정상값을 표시합니다.");
        if (snapshot.Status == CodexQuotaStatus.Stale)
            return UiText.T("Stale data · Last values received via the Claude Code terminal. Current usage may differ; check the usage page.",
                "오래된 데이터 · Claude Code 터미널의 마지막 수신값입니다. 현재 사용량은 다를 수 있으니 사용량 페이지에서 확인하세요.");
        return UiText.T("No Claude subscription usage received. Connect a Claude Code terminal login to receive samples. Limits may be absent before the first response or on unsupported plans.",
            "Claude 구독 사용량을 아직 받지 못했습니다. Claude Code 터미널 로그인을 연결해 사용량을 받으세요. 첫 응답 전이거나 지원하지 않는 플랜이면 한도 정보가 없을 수 있습니다.");
    }
}
