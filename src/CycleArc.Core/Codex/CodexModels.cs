using CycleArc.Providers.Usage;
using CycleArc.Models;

namespace CycleArc.Codex;

public enum CodexQuotaStatus
{
    Available,
    Refreshing,
    Stale,
    CodexNotFound,
    SignedOut,
    Unavailable,
    ProtocolMismatch,
    TimedOut,
    Cancelled
}

public enum CodexWindowKind
{
    FiveHour,
    Weekly,
    Other
}

public sealed record CodexQuotaWindow(
    string? LimitId,
    double? UsedPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt,
    CodexWindowKind Kind)
{
    public double? RemainingPercent =>
        UsedPercent is { } used
            ? Math.Clamp(100 - used, 0, 100)
            : null;
}

public sealed record CodexQuotaSnapshot(
    CodexQuotaStatus Status,
    string? PlanType,
    DateTimeOffset? LastSuccessfulRefresh,
    DateTimeOffset? LastAttemptedRefresh,
    bool? OrdinaryUsageAllowed,
    string? RateLimitReachedType,
    int? ResetCreditsAvailable,
    IReadOnlyList<CodexQuotaWindow> Windows,
    string? TechnicalDetail,
    IReadOnlyList<DateTimeOffset?>? ResetCreditExpirations = null)
{
    // Live identity is required for an explicit redemption; never serialize credit IDs.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<CodexResetCredit> RedeemableCredits { get; init; } = [];
    public string? IdentityFingerprint { get; init; }
    public UsageProviderId Provider { get; init; } = UsageProviderId.Codex;

    public static CodexQuotaSnapshot Empty(CodexQuotaStatus status, string? detail = null) =>
        new(status, null, null, null, null, null, null, [], detail);

    public bool HasUsablePercentages => Windows.Any(window => window.UsedPercent is not null);

    public CodexQuotaWindow? CompactWindow => DisplayWindow();

    public CodexQuotaWindow? DisplayWindow(UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var order = preference switch
        {
            UsagePeriodPreference.Weekly => new[] { CodexWindowKind.Weekly, CodexWindowKind.FiveHour },
            UsagePeriodPreference.FiveHour => new[] { CodexWindowKind.FiveHour, CodexWindowKind.Weekly },
            _ => new[] { CodexWindowKind.FiveHour, CodexWindowKind.Weekly }
        };
        var unknownOrder = order;

        foreach (var kind in order)
        {
            var known = Windows.FirstOrDefault(window => window.Kind == kind && IsKnown(window));
            if (known is not null) return known;
        }

        // An unrecognized duration is a fallback only after the standard periods.
        var otherKnown = Windows
            .Where(window => window.Kind is not CodexWindowKind.FiveHour and not CodexWindowKind.Weekly && IsKnown(window))
            .OrderByDescending(window => window.WindowDurationMinutes is > 0 ? window.WindowDurationMinutes : 0)
            .FirstOrDefault();
        if (otherKnown is not null) return otherKnown;

        // Preserve the explicitly requested period when it is present but unknown.
        foreach (var kind in unknownOrder)
        {
            var requested = Windows.FirstOrDefault(window => window.Kind == kind);
            if (requested is not null) return requested;
        }

        var otherPresent = Windows
            .Where(window => window.Kind is not CodexWindowKind.FiveHour and not CodexWindowKind.Weekly)
            .Where(window => window.WindowDurationMinutes is > 0)
            .OrderByDescending(window => window.WindowDurationMinutes)
            .FirstOrDefault();
        return otherPresent ?? Windows.FirstOrDefault();
    }

    private static bool IsKnown(CodexQuotaWindow window) =>
        window.UsedPercent is double used && double.IsFinite(used);

    public CodexQuotaSnapshot AsStale(DateTimeOffset attempted, string? detail)
    {
        if (!HasUsablePercentages)
        {
            return this with
            {
                Status = Status is CodexQuotaStatus.CodexNotFound or CodexQuotaStatus.SignedOut
                    ? Status
                    : CodexQuotaStatus.Unavailable,
                LastAttemptedRefresh = attempted,
                TechnicalDetail = detail
            };
        }

        return this with
        {
            Status = CodexQuotaStatus.Stale,
            LastAttemptedRefresh = attempted,
            TechnicalDetail = detail
        };
    }

    public CodexQuotaSnapshot AsRefreshing() => this with
    {
        Status = CodexQuotaStatus.Refreshing,
        // A retry must not make a disconnected account's cached values visible again.
        // The persisted cache remains available for a successfully reconnected account.
        Windows = (Status is CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound) ? [] : Windows
    };
}

public sealed record CodexRefreshResult(
    CodexQuotaSnapshot Snapshot,
    bool UsedCache,
    string? FailureCategory);

public static class CodexWindowClassifier
{
    public const int FiveHourMinutes = 300;
    public const int WeeklyMinutes = 10_080;

    public static CodexWindowKind FromDurationMinutes(int? minutes) => minutes switch
    {
        FiveHourMinutes => CodexWindowKind.FiveHour,
        WeeklyMinutes => CodexWindowKind.Weekly,
        > 0 => CodexWindowKind.Other,
        _ => CodexWindowKind.Other
    };
}

public sealed record CodexResetCredit(string Id, DateTimeOffset? ExpiresAt);
