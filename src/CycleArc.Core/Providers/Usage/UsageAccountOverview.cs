using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Usage;

/// <summary>The same usable-account selection drives the popup, tray and widget.</summary>
public sealed record UsageAccountOverview(IReadOnlyList<CodexAccountView> Accounts, CodexAccountView? Selected)
{
    public string SelectedId => Selected?.Profile.Id ?? "";
    public CodexQuotaSnapshot Snapshot => Selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut);
    public string Tooltip => Selected is null
        ? UiText.ProductName + Environment.NewLine + UiText.T("No usage yet. Connect an account in Manage accounts.", "사용량 대기 중 · 계정 관리에서 연결 상태를 확인하세요.")
        : CycleArcPresentation.TrayTooltip(Selected.Snapshot, Accounts.Count > 1 ? Selected.DisplayName : null);
    public static bool CanDisplay(CodexAccountView account) => CodexIdentityPresentation.NeedsReconnection(account.Snapshot)
        || (account.IsConnected || account.Snapshot.HasUsablePercentages)
        && account.Snapshot.Status is not (CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound);

    public static UsageAccountOverview Create(IReadOnlyList<CodexAccountView> accounts, string selectedId)
    {
        var visible = accounts.Where(CanDisplay).ToArray();
        return new(visible, visible.FirstOrDefault(account => account.Profile.Id == selectedId) ?? visible.FirstOrDefault());
    }
}
