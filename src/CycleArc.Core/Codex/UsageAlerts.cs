using System.Globalization;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Codex;

public enum UsageAlertLevel
{
    None,
    NearLimit,
    LimitReached
}

public sealed record UsageAlert(string ProfileId, string AccountName, string ProviderName, string LimitName,
    UsageAlertLevel Level, double UsedPercent)
{
    public string Title => Level == UsageAlertLevel.LimitReached
        ? UiText.T($"{AccountName}: {LimitName} limit reached", $"{AccountName}: {LimitName} 한도 도달")
        : UiText.T($"{AccountName}: {LimitName} limit nearly used", $"{AccountName}: {LimitName} 한도 임박");

    public string Body => Level == UsageAlertLevel.LimitReached
        ? UiText.T($"{ProviderName} · {LimitName} is used up. Click to open usage.",
            $"{ProviderName} · {LimitName} 한도를 모두 사용했습니다. 클릭하면 사용량을 엽니다.")
        : UiText.T($"{ProviderName} · {UsagePercentFormatting.Detail(UsedPercent)} of {LimitName} used. Click to open usage.",
            $"{ProviderName} · {LimitName} {UsagePercentFormatting.Detail(UsedPercent)} 사용. 클릭하면 사용량을 엽니다.");
}

/// <summary>
/// Decides when a limit deserves a notification: the first time in a period that it enters the
/// near-limit band (the rings' orange, 85% used) and again when it is used up (100%). The same
/// thresholds color the rings, so an alert never disagrees with what the popup shows.
///
/// Marks are keyed by account and limit and remember the period by its reset time, so repeated
/// refreshes and restarts do not repeat an alert while the next period starts afresh. Only current
/// values count: stale data, failures, unknown percentages and disabled or unlimited allowances
/// never alert.
/// </summary>
public static class UsageAlerts
{
    // A provider can report the same reset a few seconds or minutes apart between checks.
    private static readonly TimeSpan SamePeriodTolerance = TimeSpan.FromMinutes(10);

    public static UsageAlertLevel LevelFor(double used) => used >= UsageRingBands.ExhaustedPercent
        ? UsageAlertLevel.LimitReached
        : used >= UsageRingBands.NearLimitPercent ? UsageAlertLevel.NearLimit : UsageAlertLevel.None;

    public static (IReadOnlyList<UsageAlert> Alerts, Dictionary<string, string> Marks) Evaluate(
        IEnumerable<CodexAccountView> accounts, IReadOnlyDictionary<string, string>? marks)
    {
        var previous = marks ?? new Dictionary<string, string>();
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var alerts = new List<UsageAlert>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            present.Add(account.Profile.Id);
            var snapshot = account.Snapshot;
            if (!UsageAccountOverview.CanDisplay(account) || snapshot.Status != CodexQuotaStatus.Available)
                continue;
            foreach (var window in snapshot.Windows)
            {
                if (window.UsedPercent is not { } used || !double.IsFinite(used)
                    || window.IsEnabled == false || window.IsUnlimited)
                    continue;
                var key = account.Profile.Id + "|" + WindowKey(window);
                var level = LevelFor(used);
                var (period, marked) = previous.TryGetValue(key, out var value) ? Parse(value) : (null, UsageAlertLevel.None);
                if (!SamePeriod(period, window.ResetsAt)) marked = UsageAlertLevel.None;
                if (level > marked)
                    alerts.Add(new UsageAlert(account.Profile.Id, account.DisplayName, account.ProviderName,
                        LimitName(window, snapshot.Provider), level, used));
                // Usage that falls (a reset without a reported time, or a redeemed credit) lowers
                // the mark so the next rise alerts again.
                next[key] = Format(window.ResetsAt, level);
            }
        }
        // A listed account whose values were skipped this time (stale, failed, a limit briefly
        // missing) keeps its marks, so recovering does not repeat an alert. Removed accounts drop.
        foreach (var (key, value) in previous)
            if (!next.ContainsKey(key) && present.Contains(key.Split('|')[0]))
                next[key] = value;
        return (alerts, next);
    }

    // Codex reports its five-hour and weekly windows under one limit ID, so the duration and kind
    // are part of the key; otherwise the two periods would overwrite each other's marks.
    private static string WindowKey(CodexQuotaWindow window) =>
        (window.LimitId ?? "") + ":"
        + (window.WindowDurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "") + ":" + window.Kind;

    private static string LimitName(CodexQuotaWindow window, UsageProviderId provider) =>
        CursorUsagePresentation.IsCursor(provider)
            ? CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)
            : CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes);

    private static bool SamePeriod(DateTimeOffset? marked, DateTimeOffset? current) =>
        marked is null || current is null || (marked.Value - current.Value).Duration() <= SamePeriodTolerance;

    private static string Format(DateTimeOffset? resetsAt, UsageAlertLevel level) =>
        (resetsAt is { } reset ? reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "-")
        + "|" + ((int)level).ToString(CultureInfo.InvariantCulture);

    private static (DateTimeOffset? Period, UsageAlertLevel Level) Parse(string value)
    {
        var parts = value.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var level)
            || level is < 0 or > (int)UsageAlertLevel.LimitReached)
            return (null, UsageAlertLevel.None);
        DateTimeOffset? period = long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return (period, (UsageAlertLevel)level);
    }
}
