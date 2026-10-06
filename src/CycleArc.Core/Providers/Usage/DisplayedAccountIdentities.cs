using CycleArc.Codex;

namespace CycleArc.Providers.Usage;

/// <summary>
/// The account identities a hidden view last displayed. A hidden popup is rebuilt just before
/// it is shown again, so ordinary quota changes need no hidden rebuild; it is rebuilt at once
/// only when it would otherwise keep a removed account, another login's email, or quota values
/// that identity protection now hides.
/// </summary>
public sealed class DisplayedAccountIdentities
{
    private readonly record struct Shown(string Id, string? Email, bool Protected);

    private Shown[] _shown = [];

    public static bool IsProtected(CodexQuotaSnapshot snapshot) =>
        UsageCreditPresentation.Hidden(snapshot) || WidgetStatusPresentation.HidesQuota(snapshot);

    /// <summary>Records what a completed bind displayed; only the latest generation is kept.</summary>
    public void Record(IReadOnlyList<CodexAccountView> accounts)
    {
        var shown = new Shown[accounts.Count];
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            shown[index] = new(account.Profile.Id, account.Email, IsProtected(account.Snapshot));
        }
        _shown = shown;
    }

    /// <summary>True when the last displayed generation holds data the current accounts withdraw.</summary>
    public bool Withdrawn(IReadOnlyList<CodexAccountView> current)
    {
        foreach (var shown in _shown)
        {
            CodexAccountView? match = null;
            foreach (var account in current)
                if (string.Equals(account.Profile.Id, shown.Id, StringComparison.Ordinal)) { match = account; break; }
            if (match is null || !string.Equals(match.Email, shown.Email, StringComparison.Ordinal)
                || (!shown.Protected && IsProtected(match.Snapshot)))
                return true;
        }
        return false;
    }
}
