using System.Globalization;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Codex;

public sealed record UsageCreditRow(string Label, string Value, string? Tooltip = null);
public sealed record UsageCreditCard(string Title, string Summary, string Notice,
    IReadOnlyList<UsageCreditRow> Rows, string Tooltip);

/// <summary>Selected-detail-only projection. No quota windows or alert state are created here.</summary>
public static class UsageCreditPresentation
{
    private static string Unknown => UiText.T("Not available", "확인 불가");
    private static string Missing => UiText.T("Not provided", "미제공");
    private static string Unlimited => UiText.T("Unlimited", "무제한");
    private static string Off => UiText.T("Off", "꺼짐");
    private static string On => UiText.T("On", "켜짐");

    public static UsageCreditCard Create(CodexQuotaSnapshot snapshot)
    {
        var title = snapshot.Provider switch
        {
            UsageProviderId.Claude => UiText.T("Extra usage", "추가 사용"),
            UsageProviderId.Cursor => UiText.T("On-demand", "온디맨드"),
            _ => UiText.T("Credit balance", "크레딧 잔액")
        };
        if (Hidden(snapshot))
            return Card(title, Unknown, UiText.T("Check account connection", "계정 연결 확인 필요"), []);
        return snapshot.Provider switch
        {
            UsageProviderId.Claude => Claude(snapshot, title),
            UsageProviderId.Cursor => Cursor(snapshot, title),
            _ => Codex(snapshot, title)
        };
    }

    public static bool Hidden(CodexQuotaSnapshot snapshot) =>
        snapshot.Status is CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound
        || CodexIdentityPresentation.NeedsReconnection(snapshot)
        || snapshot.TechnicalDetail is "claude-identity-mismatch" or "claude-live-identity-mismatch"
            or "claude-desktop-identity-unverified" or "cursor-live-identity-mismatch" or "cursor-disconnected";

    private static UsageCreditCard Codex(CodexQuotaSnapshot snapshot, string title)
    {
        var credits = snapshot.UsageCredits;
        var summary = credits is null ? snapshot.UsageCreditsFailure == "credits-malformed" ? Unknown : Missing
            : credits.Unlimited ? Unlimited : credits.Balance is { } balance
                ? CreditAmount(balance) : UiText.T("Balance unavailable", "잔액 확인 불가");
        var rows = new List<UsageCreditRow> { new(UiText.T("Balance", "잔액"), summary) };
        rows.Add(new(UiText.T("Credits consumed", "소비 크레딧"), Missing,
            UiText.T("Balance changes may include top-ups, grants and corrections.", "잔액 변동에는 충전, 지급 및 보정이 포함될 수 있습니다.")));
        rows.Add(new(UiText.T("Source", "출처"), "App Server", "account/rateLimits/read"));
        AddChecked(rows, credits?.ObservedAt);
        var notice = Failure(snapshot, snapshot.UsageCreditsFailure, credits is not null);
        return Card(title, credits is null && notice.Length > 0 ? Unknown : summary, notice, rows);
    }

    private static UsageCreditCard Claude(CodexQuotaSnapshot snapshot, string title)
    {
        var extra = snapshot.ExtraUsage;
        var rows = new List<UsageCreditRow>();
        var summary = Missing;
        if (extra is not null)
        {
            summary = extra.IsEnabled ? On : Off;
            rows.Add(new(UiText.T("Extra usage", "추가 사용"), summary));
            if (extra.IsEnabled)
            {
                var used = Money(extra.UsedAmount, extra.Currency);
                var limit = extra.IsUnlimited ? Unlimited : Money(extra.MonthlyLimitAmount, extra.Currency);
                summary = UiText.T($"{used} used / {limit} monthly cap", $"{used} 사용 / 월 상한 {limit}");
                rows.Add(new(UiText.T("Used this month", "이번 달 사용액"), used));
                rows.Add(new(UiText.T("Monthly spending cap", "월 지출 상한"), limit));
                rows.Add(new(UiText.T("Until cap", "상한까지 남음"), Money(extra.RemainingAmount, extra.Currency),
                    extra.IsRemainingCalculated ? UiText.T("Monthly cap minus usage for the same account and currency.",
                        "같은 계정과 통화의 월 상한에서 사용액을 뺀 계산값입니다.") : null));
            }
            AddChecked(rows, extra.ObservedAt);
        }
        var notice = Failure(snapshot, snapshot.ExtraUsageFailure, extra is not null);
        if (extra?.RemainingAmount < 0) notice = Join(notice, UiText.T("Spending cap exceeded", "상한 초과"));
        rows.Add(new(UiText.T("Prepaid balance", "선불 잔액"), Missing));
        var inventory = snapshot.ClaudeResetCredits;
        var now = DateTimeOffset.UtcNow;
        rows.Add(new(UiText.T("Limit resets", "무료 리셋권"), inventory is null ? Missing
            : !inventory.Eligible ? UiText.T("Not available for this source", "이 조회 경로에서 사용 불가")
            : UiText.T($"{inventory.AvailableCount(now)} available", $"{inventory.AvailableCount(now)}개 보유"),
            UiText.T("Read only. Use Settings > Usage on Claude to redeem.", "조회 전용입니다. 사용하려면 Claude 설정 > 사용량을 여세요.")));
        if (inventory is { Eligible: true })
        {
            foreach (var grant in inventory.Grants.Take(12))
            {
                var state = grant.Paused ? UiText.T("Paused", "일시정지")
                    : grant.StartsAt > now ? UiText.T("Not started", "시작 전")
                    : grant.EndsAt <= now ? UiText.T("Expired", "만료")
                    : grant.ResetsLeft == 0 ? UiText.T("Exhausted", "소진")
                    : grant.UsableNow == true ? UiText.T("Usable now", "지금 사용 가능")
                    : grant.UsableNow == false ? UiText.T("Use currently gated", "현재 사용 제한")
                    : UiText.T("Use availability not provided", "사용 가능 상태 미제공");
                var types = string.Join(" / ", grant.Clears.Select(clear => clear == "five_hour"
                    ? UiText.T("5-hour", "5시간") : UiText.T("Weekly", "주간")));
                rows.Add(new(UiText.T("Reset grant", "리셋권") + (types.Length > 0 ? " · " + types : ""),
                    $"{grant.ResetsLeft}/{grant.ResetsTotal} · {state}"));
                if (grant.StartsAt > now) rows.Add(new(UiText.T("Starts", "시작"), Stamp(grant.StartsAt.Value)));
                if (grant.EndsAt is { } expiry) rows.Add(new(UiText.T("Expires", "만료"), Stamp(expiry)));
            }
            rows.Add(new(UiText.T("Resets checked", "리셋권 확인 시각"), Stamp(inventory.ObservedAt)));
        }
        if (snapshot.ClaudeResetCreditsFailure is not null)
            notice = Join(notice, UiText.T("Reset inventory check failed or not provided; previous observation shown.",
                "리셋권 조회 실패 또는 미제공 · 이전 관측값 표시"));
        rows.Add(new(UiText.T("Source", "출처"), "OAuth usage", "GET https://api.anthropic.com/api/oauth/usage"));
        return Card(title, extra is null && notice.Length > 0 ? Unknown : summary, notice, rows);
    }

    private static UsageCreditCard Cursor(CodexQuotaSnapshot snapshot, string title)
    {
        // Only the personal on-demand allowance; never combine it with team or plan windows.
        var window = snapshot.Windows.FirstOrDefault(item => item.LimitId == "cursor-on-demand");
        var rows = new List<UsageCreditRow>();
        var summary = Missing;
        if (window is not null)
        {
            var enabled = window.IsEnabled is true ? On : window.IsEnabled is false ? Off : Missing;
            rows.Add(new(UiText.T("On-demand", "온디맨드"), enabled));
            summary = enabled;
            if (window.IsEnabled is not false)
            {
                var used = Money(window.UsedAmount, window.Unit);
                var limit = window.IsUnlimited ? Unlimited : Money(window.LimitAmount, window.Unit);
                summary = UiText.T($"{used} used / {limit} cap", $"{used} 사용 / 상한 {limit}");
                rows.Add(new(UiText.T("Used this billing period", "이번 청구 기간 사용액"), used));
                rows.Add(new(UiText.T("Spending cap", "지출 상한"), limit));
                // Server remaining is authoritative. No fallback or percentage-to-money conversion.
                rows.Add(new(UiText.T("Until cap", "상한까지 남음"), Money(window.RemainingAmount, window.Unit)));
            }
            if (window.ResetsAt is { } end)
                rows.Add(new(UiText.T("Billing period ends", "청구 기간 종료"), Stamp(end)));
            AddChecked(rows, window.AmountObservedAt ?? (window.AmountFailure is null ? snapshot.LastSuccessfulRefresh : null));
        }
        var notice = Failure(snapshot, window?.AmountFailure, window is not null
            && (window.AmountObservedAt is not null || window.AmountFailure is null));
        if (window is { UsedAmount: { } usedAmount, LimitAmount: { } limitAmount } && usedAmount > limitAmount)
            notice = Join(notice, UiText.T("Spending cap exceeded", "상한 초과"));
        if (snapshot.CursorPlanAmounts is { } plan)
        {
            rows.Add(new(UiText.T("Plan included usage", "플랜 포함 사용액"), Money(plan.UsedAmount, plan.Currency)));
            rows.Add(new(UiText.T("Plan allowance", "플랜 금액 한도"), plan.IsUnlimited ? Unlimited : Money(plan.LimitAmount, plan.Currency)));
            rows.Add(new(UiText.T("Plan remaining", "플랜 남은 금액"), Money(plan.RemainingAmount, plan.Currency)));
            if (plan.IncludedAmount is not null) rows.Add(new(UiText.T("Included breakdown", "포함 사용 내역"), Money(plan.IncludedAmount, plan.Currency)));
            if (plan.BonusAmount is not null) rows.Add(new(UiText.T("Bonus breakdown", "보너스 사용 내역"), Money(plan.BonusAmount, plan.Currency)));
            if (plan.TotalAmount is not null) rows.Add(new(UiText.T("Total breakdown", "합계 사용 내역"), Money(plan.TotalAmount, plan.Currency)));
            rows.Add(new(UiText.T("Plan amounts checked", "플랜 금액 확인 시각"), Stamp(plan.ObservedAt)));
        }
        rows.Add(new(UiText.T("Auto / API amounts", "Auto / API 금액"), Missing,
            UiText.T("Separate percentage lanes do not establish separate currency amounts.", "서로 다른 사용률 지표에서 금액을 추정하지 않습니다.")));
        foreach (var team in snapshot.Windows.Where(item => item.LimitId is "cursor-team-on-demand" or "cursor-team-pool" or "cursor-overall"))
            rows.Add(new(team.LimitId == "cursor-overall" ? UiText.T("Member cap", "구성원 상한")
                : team.LimitId == "cursor-team-on-demand" ? UiText.T("Team on-demand", "팀 온디맨드")
                : UiText.T("Team pool", "팀 공용 한도"),
                Money(team.UsedAmount, team.Unit) + " / " + (team.IsUnlimited ? Unlimited : Money(team.LimitAmount, team.Unit))));
        if (snapshot.CursorPlanAmountsFailure is not null)
            notice = Join(notice, UiText.T("Plan amounts unavailable; check the original observation time.", "플랜 금액 조회 불가 · 원래 관측 시각 확인 필요"));
        rows.Add(new(UiText.T("Source", "출처"), "usage-summary", "GET https://cursor.com/api/usage-summary"));
        return Card(title, notice.Length > 0 && (window is null
            || window.AmountFailure is not null && window.AmountObservedAt is null) ? Unknown : summary, notice, rows);
    }

    private static string Failure(CodexQuotaSnapshot snapshot, string? optionalFailure, bool hasPrevious)
    {
        var failed = WidgetStatusPresentation.From(snapshot, snapshot.LastAttemptedRefresh ?? DateTimeOffset.UtcNow).IsWarning;
        if (optionalFailure is not null || failed)
            return hasPrevious ? UiText.T("Previous data · check the original time below", "이전 데이터 · 아래의 원래 확인 시각을 확인하세요")
                : !failed && optionalFailure is "credits-not-provided" ? "" : UiText.T("Check failed", "조회 실패");
        return "";
    }

    private static UsageCreditCard Card(string title, string summary, string notice, List<UsageCreditRow> rows) =>
        new(title, summary, notice, rows, string.Join(Environment.NewLine,
            new[] { title + ": " + summary, notice }.Where(value => value.Length > 0)
                .Concat(rows.Select(row => row.Label + ": " + row.Value))));

    private static void AddChecked(List<UsageCreditRow> rows, DateTimeOffset? at)
    {
        if (at is { } time) rows.Add(new(UiText.LastChecked, Stamp(time)));
    }
    private static string Stamp(DateTimeOffset time) => DisplayFormatting.FormatStamp(time);
    private static string Join(string left, string right) => left.Length == 0 ? right : left + " · " + right;
    private static string CreditAmount(decimal balance)
    {
        var value = balance.ToString("#,0.############################", CultureInfo.InvariantCulture);
        return UiText.T(value + " credits", value + " 크레딧");
    }
    private static string Money(decimal? amount, string? currency)
    {
        if (amount is null || string.IsNullOrWhiteSpace(currency)) return Missing;
        var value = amount.Value.ToString(currency is "JPY" or "KRW" or "VND" ? "N0" : "N2", CultureInfo.InvariantCulture);
        return currency == "USD" ? "$" + value : value + " " + currency;
    }
}
