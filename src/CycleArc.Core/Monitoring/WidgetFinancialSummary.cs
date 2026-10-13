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
        var previous = snapshot.Status != CodexQuotaStatus.Available;
        if (money)
        {
            previous |= snapshot.Provider switch
            {
                UsageProviderId.Codex => snapshot.UsageCredits is not null && (!Fresh(snapshot.UsageCredits.ObservedAt, now) || snapshot.UsageCreditsFailure is not null),
                UsageProviderId.Claude => snapshot.ExtraUsage is not null && (!Fresh(snapshot.ExtraUsage.ObservedAt, now) || snapshot.ExtraUsageFailure is not null),
                UsageProviderId.Cursor => snapshot.Windows.FirstOrDefault(w => w.LimitId == "cursor-on-demand") is { } demandWindow
                    && (!Fresh(demandWindow.AmountObservedAt, now) || demandWindow.AmountFailure is not null),
                _ => false
            };
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
            previous |= snapshot.Provider == UsageProviderId.Codex && snapshot.ResetCreditsAvailable is not null && !Fresh(snapshot.LastSuccessfulRefresh, now)
                || snapshot.Provider == UsageProviderId.Claude && snapshot.ClaudeResetCredits is not null
                    && (!Fresh(snapshot.ClaudeResetCredits.ObservedAt, now) || snapshot.ClaudeResetCreditsFailure is not null);
            int? count = snapshot.Provider == UsageProviderId.Codex ? snapshot.ResetCreditsAvailable
                : snapshot.Provider == UsageProviderId.Claude && snapshot.ClaudeResetCredits is { Eligible: true } inventory ? inventory.AvailableCount(now) : null;
            if (count is { } known) values.Add(UiText.T($"Resets {known}", $"리셋권 {known}"));
        }
        var text = string.Join(" · ", values);
        if (text.Length > 0 && previous) text = UiText.T("Previous · ", "이전 · ") + text;
        var tooltip = UsageCreditPresentation.Create(snapshot).Tooltip;
        if (resets && snapshot.Provider == UsageProviderId.Claude)
            tooltip += Environment.NewLine + UiText.T("Claude resets: read-only. Use the official Usage page.", "Claude 리셋권: 조회 전용. 공식 Usage 페이지에서 사용하세요.");
        if (resets && snapshot.Provider == UsageProviderId.Codex)
            tooltip += Environment.NewLine + CodexCreditCard.From(snapshot, now).CountText + Environment.NewLine
                + string.Join(Environment.NewLine, CodexCreditCard.From(snapshot, now).Rows.Select(r => r.Text));
        return (text, tooltip);
    }

    private static bool Fresh(DateTimeOffset? at, DateTimeOffset now) => at is { } time && time <= now
        && now - time <= TimeSpan.FromMinutes(15);

    private static string Money(decimal value, string currency) => currency == "USD" ? "$" + value.ToString("N2", CultureInfo.InvariantCulture)
        : value.ToString(currency is "KRW" or "JPY" or "VND" ? "N0" : "N2", CultureInfo.InvariantCulture) + " " + currency;
}
