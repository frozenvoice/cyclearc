using System.Globalization;
using CycleArc.Codex;
using CycleArc.Services;
using CycleArc.Providers.Usage;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Presentation policy for Cursor's named allowances. Cursor reports several
/// independent monetary/request buckets; they stay separate all the way to the
/// popup, tray tooltip and widget.
/// </summary>
public static class CursorUsagePresentation
{
    public const string LiveDetail = "cursor-live";

    public static bool IsCursor(UsageProviderId provider) => provider == UsageProviderId.Cursor;

    public static bool IsCursor(CodexQuotaSnapshot snapshot) => IsCursor(snapshot.Provider);

    public static string Title => UiText.T("Cursor usage", "Cursor 사용량");

    public static string ConnectionHint => UiText.T(
        "CycleArc reads the signed-in Cursor account already available on this Windows PC. It keeps the provider credential in Cursor and stores only the selected account reference and quota snapshot.",
        "CycleArc는 이 Windows PC의 Cursor에 이미 로그인된 계정을 읽습니다. 인증정보는 Cursor가 보관하며 CycleArc에는 선택한 계정 참조와 한도 스냅샷만 저장합니다.");

    public static string UpdatedText(CodexQuotaSnapshot snapshot) => snapshot.LastSuccessfulRefresh is { } at
        ? UiText.T($"Updated {at.ToLocalTime():yyyy-MM-dd HH:mm}", $"업데이트 {at.ToLocalTime():yyyy-MM-dd HH:mm}")
        : UiText.T("Updated: unknown", "업데이트 시각: 확인 불가");

    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (!IsCursor(snapshot)) return "";
        if (snapshot.TechnicalDetail == "cursor-connected-waiting")
            return UiText.T("Connected; waiting for Cursor usage", "연결됨 · Cursor 사용량 수신 대기");
        return snapshot.Status switch
        {
            CodexQuotaStatus.Available => UpdatedText(snapshot),
            CodexQuotaStatus.Stale => UiText.T("Stale data · showing the last valid Cursor values", "오래된 데이터 · 마지막으로 확인된 Cursor 값을 표시합니다"),
            CodexQuotaStatus.Refreshing => UiText.T("Refreshing Cursor usage", "Cursor 사용량 새로고침 중"),
            CodexQuotaStatus.SignedOut => UiText.T("Cursor sign-in required", "Cursor 로그인 필요"),
            CodexQuotaStatus.ProtocolMismatch => UiText.T("Cursor usage format changed", "Cursor 사용량 형식이 변경됨"),
            _ => UiText.T("Cursor usage unavailable", "Cursor 사용량 확인 불가")
        };
    }

    public static string FailureText(string? detail) => detail switch
    {
        "cursor-auth-required" or "cursor-signed-out" or "cursor-live-auth-required" => UiText.T("Cursor sign-in required", "Cursor 로그인 필요"),
        "cursor-identity-mismatch" or "cursor-live-identity-mismatch" => UiText.T("Cursor account changed", "Cursor 계정 변경됨"),
        "cursor-credential-unavailable" or "cursor-live-unavailable" => UiText.T("Cursor account could not be read", "Cursor 계정을 읽을 수 없음"),
        "cursor-request-failed" or "cursor-live-request-failed" => UiText.T("Cursor usage request failed", "Cursor 사용량 조회 실패"),
        "cursor-live-rate-limited" => UiText.T("Cursor usage request was rate limited", "Cursor 사용량 조회가 제한됨"),
        "cursor-disconnected" => UiText.T("Cursor is disconnected", "Cursor 연결 해제됨"),
        "cursor-connection-unavailable" => UiText.T("Cursor connection could not be read", "Cursor 연결을 읽을 수 없음"),
        "cursor-connected-waiting" => UiText.T("Connected; waiting for Cursor usage", "연결됨 · Cursor 사용량 수신 대기"),
        "cursor-sand-unavailable" => UiText.T("Grok usage unavailable", "Grok 사용량 확인 불가"),
        "cursor-schema-mismatch" or "cursor-live-schema-mismatch" => UiText.T("Cursor usage format changed", "Cursor 사용량 형식이 변경됨"),
        _ => ""
    };

    public static string QuotaLabel(string? limitId)
    {
        if (string.IsNullOrWhiteSpace(limitId)) return UiText.T("Cursor quota", "Cursor 한도");
        var normalized = limitId.Trim().ToLowerInvariant();
        return normalized switch
        {
            "cursor-auto" or "auto" => UiText.T("Auto", "자동"),
            "cursor-api" or "api" => UiText.T("API", "API"),
            "cursor-plan" or "plan" => UiText.T("Plan total", "플랜 전체"),
            "cursor-on-demand" or "on-demand" or "ondemand" => UiText.T("On-demand", "온디맨드"),
            "cursor-team-on-demand" or "team-on-demand" or "teamondemand" => UiText.T("Team on-demand", "팀 온디맨드"),
            "cursor-team-pool" or "team-pool" or "teampool" => UiText.T("Team pool", "팀 풀"),
            "cursor-overall" or "overall" => UiText.T("Overall", "전체"),
            "cursor-sand" or "sand" => UiText.T("Grok", "Grok"),
            "cursor-included" or "included" => UiText.T("Included", "포함분"),
            _ => SafeLabel(limitId)
        };
    }

    private static string SafeLabel(string value)
    {
        var label = value.StartsWith("cursor-", StringComparison.OrdinalIgnoreCase)
            ? value[7..] : value;
        label = new string(label.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (label.Length == 0) return UiText.T("Cursor quota", "Cursor 한도");
        return string.Join(" ", label.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    public static string AmountText(decimal? value, string? unit)
    {
        if (value is not { } amount) return "?";
        var normalized = unit?.Trim().ToUpperInvariant();
        var number = amount.ToString("0.##", CultureInfo.InvariantCulture);
        return normalized switch
        {
            "USD" or "$" => "$" + number,
            "REQUEST" or "REQUESTS" or "COUNT" => number + " " + UiText.T("requests", "회"),
            null or "" => number,
            _ => number + " " + unit!.Trim()
        };
    }

    public static string RemainingText(CodexQuotaWindow window)
    {
        if (window.IsEnabled == false) return UiText.T("Off", "꺼짐");
        if (window.IsUnlimited == true) return UiText.T("Unlimited", "무제한");
        if (window.RemainingAmount is { } remaining)
            return AmountText(remaining, window.Unit);
        if (window.RemainingPercent is { } percent && double.IsFinite(percent))
            return CodexDisplayFormatting.PercentText(percent, UsageProviderId.Cursor);
        return "?";
    }

    public static bool HasKnownAmount(CodexQuotaWindow window) =>
        window.IsEnabled == false || window.IsUnlimited == true || window.RemainingAmount is not null
        || window.UsedAmount is not null || window.LimitAmount is not null;

    public static string RemainingSummary(CodexQuotaWindow window) => window.IsEnabled == false
        ? RemainingText(window)
        : UiText.T($"Remaining {RemainingText(window)}", $"잔여 {RemainingText(window)}");
}
