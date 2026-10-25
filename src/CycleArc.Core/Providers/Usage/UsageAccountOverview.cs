using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Providers.Usage;

/// <summary>The same usable-account selection drives the popup, tray and widget.</summary>
public sealed record UsageAccountOverview(IReadOnlyList<CodexAccountView> Accounts, CodexAccountView? Selected)
{
    public UsagePeriodPreference Preference { get; init; } = UsagePeriodPreference.Auto;
    public string SelectedId => Selected?.Profile.Id ?? "";
    public CodexQuotaSnapshot Snapshot => Selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut);
    public string Tooltip => Selected is null
        ? UiText.ProductName + Environment.NewLine + UiText.T("No usage yet. Connect an account in Manage accounts.", "사용량 대기 중 · 계정 관리에서 연결 상태를 확인하세요.")
        : WithOtherAccounts(CycleArcPresentation.TrayTooltip(Selected.Snapshot, Accounts.Count > 1 ? Selected.DisplayName : null, Preference));

    /// <summary>
    /// The selected account's text keeps its full priority within the native 127 characters;
    /// the other accounts' remaining share is added after it only as far as it fits.
    /// </summary>
    private string WithOtherAccounts(string selected)
    {
        if (Accounts.Count < 2 || Selected is null) return selected;
        var line = "";
        foreach (var account in Accounts)
        {
            if (account.Profile.Id == Selected.Profile.Id) continue;
            var ring = CodexRingPresentation.For(account, Preference, DateTimeOffset.Now);
            var name = account.DisplayName.Length > 14 ? account.DisplayName[..13] + "…" : account.DisplayName;
            var entry = name + " " + (ring.RemainingPercent is null ? "?" : ring.RemainingValueText);
            var next = line.Length == 0 ? UiText.T("Others: ", "다른 계정: ") + entry : line + " · " + entry;
            if (selected.Length + 1 + next.Length > NotifyIconText.MaximumLength) break;
            line = next;
        }
        return line.Length == 0 ? selected : selected + "\n" + line;
    }
    /// <summary>The selected account's Cursor recent-request pool for the tray ring, or null for the usual choice.</summary>
    public CodexQuotaWindow? RepresentativeWindow(DateTimeOffset now) => Selected is null ? null
        : Cursor.CursorRingPolicy.Resolve(Selected.Snapshot, Selected.CursorActivity, now).Window;

    public static bool CanDisplay(CodexAccountView account) => CodexIdentityPresentation.NeedsReconnection(account.Snapshot)
        || (account.IsConnected || account.Snapshot.HasUsablePercentages)
        && account.Snapshot.Status is not (CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound);

    public static UsageAccountOverview Create(IReadOnlyList<CodexAccountView> accounts, string selectedId,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var visible = accounts.Where(CanDisplay).ToArray();
        return new(visible, visible.FirstOrDefault(account => account.Profile.Id == selectedId) ?? visible.FirstOrDefault())
        {
            Preference = preference
        };
    }
}
