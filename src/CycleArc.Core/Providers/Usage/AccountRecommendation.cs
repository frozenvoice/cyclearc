using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Providers.Usage;

/// <summary>
/// Which account of the same service has the most room right now, for people who move work
/// between several logins. Only current values compare: stale, failed, refreshing-without-value
/// and unknown limits never win, and different services are never compared with each other
/// because their limits measure different things.
/// </summary>
public sealed record AccountRecommendation(CodexAccountView Account, double RemainingPercent)
{
    public string Text => UiText.T(
        $"Most room now: {Account.DisplayName} ({UsagePercentFormatting.Detail(RemainingPercent)} left)",
        $"지금 여유가 가장 많은 계정: {Account.DisplayName} ({UsagePercentFormatting.Detail(RemainingPercent)} 남음)");

    public static AccountRecommendation? For(UsageAccountOverview overview)
    {
        if (overview.Selected is not { } selected) return null;
        var candidates = overview.Accounts
            .Where(account => account.Profile.Provider == selected.Profile.Provider)
            .Select(account => (Account: account, Remaining: Remaining(account, overview.Preference),
                Reset: account.Snapshot.DisplayWindow(overview.Preference)?.ResetsAt))
            .Where(candidate => candidate.Remaining is not null)
            .ToArray();
        if (candidates.Length < 2) return null;
        var best = candidates
            .OrderByDescending(candidate => candidate.Remaining)
            .ThenBy(candidate => candidate.Reset ?? DateTimeOffset.MaxValue)
            .First();
        var current = candidates.FirstOrDefault(candidate => candidate.Account.Profile.Id == selected.Profile.Id);
        // Suggest a switch only for a clear difference, not for a point or two.
        if (best.Account.Profile.Id == selected.Profile.Id
            || current.Remaining is { } mine && best.Remaining - mine < 10)
            return null;
        return new(best.Account, best.Remaining!.Value);
    }

    private static double? Remaining(CodexAccountView account, UsagePeriodPreference preference)
    {
        var snapshot = account.Snapshot;
        if (account.IsSigningIn || snapshot.Status != CodexQuotaStatus.Available
            || !WidgetAccountModel.HasHealthyServerSample(snapshot)) return null;
        return CodexRingPresentation.From(snapshot, preference).RemainingPercent;
    }
}
