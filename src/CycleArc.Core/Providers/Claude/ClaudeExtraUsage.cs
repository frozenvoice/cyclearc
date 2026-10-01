using System.Text.Json.Serialization;

namespace CycleArc.Providers.Claude;

/// <summary>Month-to-date extra usage from the verified account/organization's OAuth usage response.
/// Amounts are normalized currency amounts, never a prepaid wallet balance.</summary>
public sealed record ClaudeExtraUsage(bool IsEnabled, decimal? UsedAmount, decimal? MonthlyLimitAmount,
    bool IsUnlimited, string? Currency, double? UsedPercentage, DateTimeOffset ObservedAt)
{
    // Both operands belong to this single response, currency and monthly aggregation.
    [JsonIgnore]
    public decimal? RemainingAmount => IsEnabled && !IsUnlimited && Currency is not null
        && MonthlyLimitAmount is { } limit && UsedAmount is { } used ? limit - used : null;
    [JsonIgnore]
    public bool IsRemainingCalculated => RemainingAmount is not null;

    public static bool Valid(ClaudeExtraUsage? value, DateTimeOffset now) => value is null
        || (value.ObservedAt > DateTimeOffset.UnixEpoch && value.ObservedAt <= now
            && value.UsedAmount is null or >= 0 && value.MonthlyLimitAmount is null or >= 0
            && (!value.IsUnlimited || (value.IsEnabled && value.MonthlyLimitAmount is null))
            && (value.Currency is null || (value.Currency.Length == 3
                && value.Currency.All(c => c is >= 'A' and <= 'Z')))
            && (value.Currency is not null || (value.UsedAmount is null && value.MonthlyLimitAmount is null))
            && (value.UsedPercentage is null || (double.IsFinite(value.UsedPercentage.Value)
                && value.UsedPercentage >= 0)));
}
