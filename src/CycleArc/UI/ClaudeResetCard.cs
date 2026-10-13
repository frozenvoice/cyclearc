using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using Button = System.Windows.Controls.Button;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CycleArc.UI;

public partial class FlyoutWindow
{
    private void BindClaudeResetCard(CodexQuotaSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var inventory = UsageCreditPresentation.Hidden(snapshot) ? null : snapshot.ClaudeResetCredits;
        _creditRowsView = null;
        CreditExpiryRows.Items.Clear();
        _resetCreditsCompact = inventory is null && snapshot.ClaudeResetCreditsFailure is null && snapshot.Status == CodexQuotaStatus.Available;
        ResetCreditsCount.Text = inventory is null ? UiText.T("Not provided", "미제공")
            : !inventory.Eligible ? UiText.T("Unavailable", "사용 불가") : inventory.AvailableCount(now).ToString();
        var previous = snapshot.ClaudeResetCreditsFailure is not null || snapshot.Status != CodexQuotaStatus.Available
            || inventory is not null && (inventory.ObservedAt > now || now - inventory.ObservedAt > TimeSpan.FromMinutes(15));
        CreditSummaryText.Text = inventory?.NextExpiration(now) is { } expiry ? UiText.T("Expires ", "만료 ") + StampReset(expiry) : "";
        CreditExpiryNotice.Text = (previous ? inventory is null ? UiText.T("Check failed · ", "조회 실패 · ") : UiText.T("Previous data · ", "이전 데이터 · ") : "")
            + UiText.T("Read-only. Use a Limit Reset on Claude's Usage page.", "조회 전용입니다. Claude Usage 페이지에서 Limit Reset을 사용하세요.");
        if (inventory is null) CreditExpiryNotice.Text += " " + UiText.T("The server did not provide a verified inventory; this is not zero.", "서버가 검증된 보유 정보를 제공하지 않았습니다. 0개라는 뜻이 아닙니다.");
        CreditExpiryNotice.Visibility = Visibility.Visible;
        if (CreditHelpButton.ToolTip is ToolTip oldTip) oldTip.IsOpen = false;
        CreditHelpButton.ToolTip = MakeTooltip(ResetCreditsCount.Text + " · " + CreditExpiryNotice.Text);
        if (inventory is not null)
        {
            AddResetText(UiText.T("Last checked: ", "확인 시각: ") + StampReset(inventory.ObservedAt));
            AddResetText(UiText.T("Source: OAuth usage GET", "출처: OAuth usage GET"));
            var usable = inventory.UsableCount(now);
            AddResetText(UiText.T("Usable now: ", "지금 사용 가능: ") + (usable?.ToString() ?? UiText.T("Not provided", "미제공")));
            foreach (var grant in inventory.Grants)
            {
                var state = grant.Paused ? UiText.T("Paused", "일시정지")
                    : grant.StartsAt > now ? UiText.T("Not started", "시작 전")
                    : grant.EndsAt <= now ? UiText.T("Expired", "만료")
                    : grant.ResetsLeft == 0 ? UiText.T("None left", "남은 권한 없음")
                    : !inventory.Eligible ? UiText.T("Unavailable", "사용 불가") : UiText.T("Available", "보유 중");
                var type = grant.Clears.Count == 0 ? UiText.T("Type not provided", "유형 미제공")
                    : string.Join(" / ", grant.Clears.Select(t => t == "five_hour" ? UiText.T("5-hour", "5시간") : UiText.T("Weekly", "주간")));
                AddResetText($"{grant.ResetsLeft} / {grant.ResetsTotal} · {type} · {state}"
                    + (grant.StartsAt is { } starts ? Environment.NewLine + UiText.T("Starts: ", "시작: ") + StampReset(starts) : "")
                    + (grant.EndsAt is { } ends ? Environment.NewLine + UiText.T("Expires: ", "만료: ") + StampReset(ends) : "")
                    + (grant.UseRequiresLimit == true ? Environment.NewLine + UiText.T("Requires a reached usage limit", "사용 한도 도달 시 사용 가능") : ""));
            }
        }
        ClaudeResetUsageButton.Content = UiText.T("Open Claude Usage", "Claude Usage 열기");
        CreditListBorder.Visibility = inventory is not null ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(ResetCreditsCard, UiText.ResetCredits + ": " + ResetCreditsCount.Text);
        ApplyCreditExpansion();
    }
    private void AddResetText(string text)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); CreditExpiryRows.Items.Add(line);
    }
    private static string StampReset(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
