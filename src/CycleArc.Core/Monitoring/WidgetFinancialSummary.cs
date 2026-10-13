using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Monitoring;

public static class WidgetFinancialSummary
{
    public static (string Text, string Tooltip) Create(CodexQuotaSnapshot snapshot, bool money, bool resets, DateTimeOffset now)
    {
        if ((!money && !resets) || UsageCreditPresentation.Hidden(snapshot)) return ("", "");
        var values = new List<string>();
        if (money)
        {
            if (snapshot.Provider == UsageProviderId.Codex && snapshot.UsageCredits is { Unlimited: false, Balance: { } balance })
                values.Add(UiText.T("Credits ", "크레딧 ") + balance.ToString("#,0.####", CultureInfo.InvariantCulture));
            if (snapshot.Provider == UsageProviderId.Claude && snapshot.ExtraUsage is { IsEnabled: true, UsedAmount: { } used, Currency: { } currency })
                values.Add(UiText.T("Extra ", "추가 ") + Money(used, currency));
            if (snapshot.Provider == UsageProviderId.Cursor && snapshot.Windows.FirstOrDefault(w => w.LimitId == "cursor-on-demand")
                is { IsEnabled: not false, UsedAmount: { } demand, Unit: { } unit })
                values.Add(UiText.T("On-demand ", "온디맨드 ") + Money(demand, unit));
        }
        if (resets)
        {
            int? count = snapshot.Provider == UsageProviderId.Codex ? snapshot.ResetCreditsAvailable
                : snapshot.Provider == UsageProviderId.Claude && snapshot.ClaudeResetCredits is { Eligible: true } inventory ? inventory.AvailableCount(now) : null;
            if (count is { } known) values.Add(UiText.T($"Resets {known}", $"리셋권 {known}"));
        }
        var text = string.Join(" · ", values);
        var previous = snapshot.Status != CodexQuotaStatus.Available || snapshot.UsageCreditsFailure is not null
            || snapshot.ExtraUsageFailure is not null || snapshot.ClaudeResetCreditsFailure is not null
            || snapshot.Windows.Any(w => w.LimitId == "cursor-on-demand" && w.AmountFailure is not null);
        if (text.Length > 0 && previous) text = UiText.T("Previous · ", "이전 · ") + text;
        var tooltip = UsageCreditPresentation.Create(snapshot).Tooltip;
        if (resets && snapshot.Provider == UsageProviderId.Claude)
            tooltip += Environment.NewLine + UiText.T("Claude resets: read-only. Use the official Usage page.", "Claude 리셋권: 조회 전용. 공식 Usage 페이지에서 사용하세요.");
        if (resets && snapshot.Provider == UsageProviderId.Codex)
            tooltip += Environment.NewLine + CodexCreditCard.From(snapshot, now).CountText + Environment.NewLine
                + string.Join(Environment.NewLine, CodexCreditCard.From(snapshot, now).Rows.Select(r => r.Text));
        return (text, tooltip);
    }

    private static string Money(decimal value, string currency) => currency == "USD" ? "$" + value.ToString("N2", CultureInfo.InvariantCulture)
        : value.ToString(currency is "KRW" or "JPY" or "VND" ? "N0" : "N2", CultureInfo.InvariantCulture) + " " + currency;
}
