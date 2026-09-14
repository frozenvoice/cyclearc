using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Codex;

public static class CodexIdentityPresentation
{
    public static bool NeedsReconnection(CodexQuotaSnapshot snapshot) =>
        snapshot.Provider == UsageProviderId.Codex && snapshot.TechnicalDetail is
            "codex-identity-mismatch" or "codex-identity-conflict" or "codex-identity-binding-unavailable";

    public static string Label(CodexQuotaSnapshot snapshot) =>
        snapshot.TechnicalDetail == "codex-identity-mismatch"
            ? UiText.T("Login changed", "로그인 변경 감지")
            : UiText.T("Check connection", "연결 확인 필요");

    public static string Explanation(CodexQuotaSnapshot snapshot) => snapshot.TechnicalDetail switch
    {
        "codex-identity-mismatch" => UiText.T(
            "The linked Codex login changed. Open Accounts and reconnect this profile to the intended account.",
            "연결된 Codex의 로그인이 바뀌었습니다. 계정 관리에서 이 프로필을 원래 계정으로 다시 연결하세요."),
        "codex-identity-conflict" => UiText.T(
            "The linked Codex reports the same login email as another profile. Open Accounts and reconnect this profile to the intended account.",
            "연결된 Codex가 다른 프로필과 같은 로그인 이메일을 보고합니다. 계정 관리에서 이 프로필을 원래 계정으로 다시 연결하세요."),
        _ => UiText.T(
            "The saved account connection could not be verified. Open Accounts and sign in again.",
            "저장된 계정 연결을 확인할 수 없습니다. 계정 관리에서 다시 로그인하세요.")
    };
}
