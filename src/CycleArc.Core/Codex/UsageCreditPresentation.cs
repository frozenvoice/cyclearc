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
    private static string Stamp(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
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
