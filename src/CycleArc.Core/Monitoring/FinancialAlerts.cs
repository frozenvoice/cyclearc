using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Monitoring;

/// <summary>Optional alerts. Credit thresholds use the provider's raw credit unit, never currency.</summary>
public sealed class FinancialAlertOptions
{
    public bool CodexLowBalanceEnabled { get; set; }
    public decimal CodexLowBalanceThreshold { get; set; } = 10;
    public bool ClaudeSpendEnabled { get; set; }
    public decimal ClaudeSpendThresholdPercent { get; set; } = 85;
    public bool CursorSpendEnabled { get; set; }
    public decimal CursorSpendThresholdPercent { get; set; } = 85;
    public bool CodexResetExpiryEnabled { get; set; }
    public bool ClaudeResetExpiryEnabled { get; set; }
    public int ResetExpiryLeadDays { get; set; } = 3;
}

public enum FinancialAlertKind { LowCreditBalance, SpendingLimit, ResetExpiry }

public sealed record FinancialAlert(string ProfileId, string AccountName, string ProviderName,
    FinancialAlertKind Kind, decimal? Value = null, decimal? Limit = null, string? Unit = null,
    DateTimeOffset? ExpiresAt = null, int Count = 0)
{
    public string Title => Kind switch
    {
        FinancialAlertKind.LowCreditBalance => UiText.T($"{AccountName}: low credit balance", $"{AccountName}: 크레딧 잔액 부족"),
        FinancialAlertKind.SpendingLimit => Value >= Limit
            ? UiText.T($"{AccountName}: spending cap reached", $"{AccountName}: 지출 상한 도달")
            : UiText.T($"{AccountName}: spending cap approaching", $"{AccountName}: 지출 상한 임박"),
        _ => UiText.T($"{AccountName}: reset credits expire soon", $"{AccountName}: 리셋권 만료 임박")
    };

    public string Body => Kind switch
    {
        FinancialAlertKind.LowCreditBalance => UiText.T($"{ProviderName} · {Number(Value)} credits remaining. Click to open details.",
            $"{ProviderName} · 크레딧 잔액 {Number(Value)}. 클릭하면 상세 정보를 엽니다."),
        FinancialAlertKind.SpendingLimit => UiText.T($"{ProviderName} · {Number(Value)} / {Number(Limit)} {Unit} spent. Click to open details.",
            $"{ProviderName} · {Number(Value)} / {Number(Limit)} {Unit} 지출. 클릭하면 상세 정보를 엽니다."),
        _ => ProviderName == UsageProviderId.Claude.Name()
            ? UiText.T($"{ProviderName} · {Count} reset credits expire {ExpiresAt?.ToLocalTime():g}. Use them on the official usage page.",
                $"{ProviderName} · 리셋권 {Count}개 만료: {ExpiresAt?.ToLocalTime():g}. 공식 사용량 페이지에서 사용하세요.")
            : UiText.T($"{ProviderName} · {Count} reset credits expire {ExpiresAt?.ToLocalTime():g}. Click to open details.",
                $"{ProviderName} · 리셋권 {Count}개 만료: {ExpiresAt?.ToLocalTime():g}. 클릭하면 상세 정보를 엽니다.")
    };

    private static string Number(decimal? value) => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—";
}

/// <summary>
/// Pure read-only evaluation. Marks are independently persisted by the caller. Failed/missing/old
/// optional data keeps its marks, and never counts as recovery. Freshness is checked on the actual
/// field's observation, so a successful subscription refresh cannot rejuvenate cached money.
/// No reset IDs, account emails, credentials or requests are stored or emitted here.
/// </summary>
public static class FinancialAlerts
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);

    public static (IReadOnlyList<FinancialAlert> Alerts, Dictionary<string, string> Marks) Evaluate(
        IEnumerable<CodexAccountView> accounts, IReadOnlyDictionary<string, string>? marks,
        FinancialAlertOptions options, DateTimeOffset now)
    {
        var previous = marks ?? new Dictionary<string, string>();
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var alerts = new List<FinancialAlert>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var activeRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var refreshedExpiryRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var snapshot = account.Snapshot;
            var prefix = account.Profile.Id + "|" + account.Profile.Provider + "|";
            prefixes.Add(prefix);
            // Connection/identity checks remain owned by the existing account providers. An
            // unconnected cache, local CLI sample, or mismatched provider cannot alert.
            if (!account.IsConnected || snapshot.Provider != account.Profile.Provider
                || snapshot.Status != CodexQuotaStatus.Available
                || CodexIdentityPresentation.NeedsReconnection(snapshot)
                || snapshot.LastSuccessfulObservationWasServer == false
                || !VerifiedFingerprint(snapshot.IdentityFingerprint)) continue;
            var keyRoot = prefix + Hash(snapshot.IdentityFingerprint!) + "|";
            activeRoots[prefix] = keyRoot;
            if (snapshot.Provider == UsageProviderId.Codex && options.CodexLowBalanceEnabled
                && options.CodexLowBalanceThreshold >= 0 && snapshot.UsageCreditsFailure is null
                && snapshot.UsageCredits is { Unlimited: false, Balance: { } balance } credits
                && Fresh(credits.ObservedAt, now))
            {
                var threshold = options.CodexLowBalanceThreshold;
                var margin = Math.Max(1m, threshold / 10m);
                EvaluateThreshold(keyRoot + "credits:" + Hash(credits.LimitId ?? "selected"),
                    "", Number(threshold), balance <= threshold,
                    balance > threshold && balance - threshold >= margin,
                    new(account.Profile.Id, account.DisplayName, account.ProviderName,
                        FinancialAlertKind.LowCreditBalance, balance));
            }
            if (snapshot.Provider == UsageProviderId.Claude && options.ClaudeSpendEnabled
                && snapshot.ExtraUsageFailure is null && snapshot.ExtraUsage is
                    { IsEnabled: true, IsUnlimited: false, UsedAmount: { } used, MonthlyLimitAmount: > 0 } extra
                && Currency(extra.Currency) && Fresh(extra.ObservedAt, now))
            {
                // OAuth omits its billing boundary. Rearm only on an observed recovery or a
                // real cap change, without inventing calendar dates or borrowing quota resets.
                EvaluateSpend(keyRoot + "claude-extra", "",
                    used, extra.MonthlyLimitAmount!.Value, extra.Currency!, options.ClaudeSpendThresholdPercent, account);
            }
            if (snapshot.Provider == UsageProviderId.Cursor && options.CursorSpendEnabled)
                foreach (var window in snapshot.Windows.Where(w => w.LimitId == "cursor-on-demand"))
                    if (window.AmountFailure is null && window.IsEnabled != false && !window.IsUnlimited
                        && window.UsedAmount is { } spent && window.LimitAmount is > 0
                        && Currency(window.Unit) && Fresh(window.AmountObservedAt, now))
                        EvaluateSpend(keyRoot + "cursor-personal-on-demand", Period(window.ResetsAt), spent,
                            window.LimitAmount.Value, window.Unit!, options.CursorSpendThresholdPercent, account);
            if (options.ResetExpiryLeadDays is > 0 and <= 365)
            {
                if (snapshot.Provider == UsageProviderId.Codex && options.CodexResetExpiryEnabled
                    && snapshot.ResetCreditsAvailable is > 0 && Fresh(snapshot.LastSuccessfulRefresh, now)
                    && snapshot.ResetCreditExpirations is { } expirations
                    && expirations.Count <= snapshot.ResetCreditsAvailable)
                    EvaluateExpiry(account, keyRoot, expirations.Where(x => x > now).Select(x => (x!.Value, 1)));
                if (snapshot.Provider == UsageProviderId.Claude && options.ClaudeResetExpiryEnabled
                    && snapshot.ClaudeResetCreditsFailure is null
                    && snapshot.ClaudeResetCredits is { Eligible: true } inventory && Fresh(inventory.ObservedAt, now))
                    EvaluateExpiry(account, keyRoot, inventory.Grants.Where(grant => !grant.Paused && grant.ResetsLeft > 0
                        && (grant.StartsAt is null || grant.StartsAt <= now) && grant.EndsAt > now)
                        .Select(grant => (grant.EndsAt!.Value, grant.ResetsLeft)));
            }

            void EvaluateExpiry(CodexAccountView owner, string root, IEnumerable<(DateTimeOffset Expiry, int Count)> entries)
            {
                refreshedExpiryRoots.Add(root + "reset-expiry:");
                var grouped = entries.GroupBy(x => x.Expiry).OrderBy(x => x.Key)
                    .Select(group => (Expiry: group.Key, Count: group.Sum(x => (long)x.Count))).ToArray();
                var fingerprint = Hash(string.Join(";", grouped.Select(x => Period(x.Expiry) + ":" + x.Count)));
                foreach (var item in grouped)
                {
                    if (item.Expiry - now > TimeSpan.FromDays(options.ResetExpiryLeadDays)) continue;
                    // Fingerprint only dates/counts; no redeemable credit IDs ever reach disk.
                    var key = root + "reset-expiry:" + Period(item.Expiry);
                    EvaluateThreshold(key, fingerprint, "expiry", true, false,
                        new(owner.Profile.Id, owner.DisplayName, owner.ProviderName, FinancialAlertKind.ResetExpiry,
                            ExpiresAt: item.Expiry, Count: (int)Math.Min(int.MaxValue, item.Count)));
                }
            }
        }
        // Preserve marks for failures and temporary disconnections. Removed accounts forget.
        foreach (var (key, value) in previous)
            if (!next.ContainsKey(key) && prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)
                && (!activeRoots.TryGetValue(prefix, out var activeRoot) || key.StartsWith(activeRoot, StringComparison.Ordinal)))
                && !refreshedExpiryRoots.Any(root => key.StartsWith(root, StringComparison.Ordinal)))
                next[key] = value;
        return (alerts, next);

        void EvaluateSpend(string key, string period, decimal used, decimal cap, string currency,
            decimal threshold, CodexAccountView account)
        {
            if (used < 0 || threshold is <= 0 or > 100) return;
            var reached = used >= cap * (threshold / 100m);
            var recovered = used <= cap * (Math.Max(0, threshold - 5m) / 100m);
            EvaluateThreshold(key, period, currency + ":" + Number(cap) + ":" + Number(threshold), reached, recovered,
                new(account.Profile.Id, account.DisplayName, account.ProviderName, FinancialAlertKind.SpendingLimit,
                    used, cap, currency));
        }

        void EvaluateThreshold(string key, string period, string policy, bool reached, bool recovered, FinancialAlert alert)
        {
            var old = previous.TryGetValue(key, out var mark) ? mark.Split('|') : [];
            var announced = old.Length == 3 && old[1] == policy && SamePeriod(old[0], period) && old[2] == "1";
            if (reached && !announced) alerts.Add(alert);
            if (recovered) announced = false;
            if (reached) announced = true;
            var keptPeriod = period.Length == 0 && old.Length == 3 ? old[0] : period;
            next[key] = keptPeriod + "|" + policy + "|" + (announced ? "1" : "0");
        }
    }

    public static (string Title, string Body, string ProfileId) Summary(IReadOnlyList<FinancialAlert> alerts)
    {
        if (alerts.Count == 0) return ("", "", "");
        var first = alerts[0];
        if (alerts.Count == 1) return (Clip(first.Title, 63), Clip(first.Body, 255), first.ProfileId);
        var lines = new List<string>();
        for (var i = 0; i < alerts.Count; i++)
        {
            var line = Clip(alerts[i].Title, 70);
            var more = UiText.T($"+{alerts.Count - i} more in the popup", $"외 {alerts.Count - i}개는 팝업에서 확인");
            if (string.Join("\n", lines.Append(line).Append(more)).Length > 255)
            {
                lines.Add(more);
                break;
            }
            lines.Add(line);
        }
        return (UiText.T($"{alerts.Count} credit and spending alerts", $"크레딧·지출 알림 {alerts.Count}개"),
            Clip(string.Join("\n", lines), 255), first.ProfileId);
    }

    private static bool Fresh(DateTimeOffset? observed, DateTimeOffset now) =>
        observed is { } time && time > DateTimeOffset.UnixEpoch && time <= now && now - time <= MaximumAge;
    private static bool Currency(string? unit) => unit is { Length: 3 } && unit.All(c => c is >= 'A' and <= 'Z');
    private static bool VerifiedFingerprint(string? fingerprint) => fingerprint is { Length: 64 }
        && fingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    private static string Number(decimal number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Period(DateTimeOffset? time) => time?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "";
    private static bool SamePeriod(string previous, string current) => previous == current || previous.Length == 0 || current.Length == 0
        || (long.TryParse(previous, out var before) && long.TryParse(current, out var after) && Math.Abs((decimal)before - after) <= 600);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
