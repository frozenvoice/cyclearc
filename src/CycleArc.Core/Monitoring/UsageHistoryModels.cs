using CycleArc.Codex;

namespace CycleArc.Monitoring;

public enum UsageHistoryMetric { UsagePercent, CreditBalance, BalanceChange, ActualSpend, SpendingLimit, RemainingAmount, ReportedUsageAmount }
public enum UsageHistorySource { CodexAppServer, ClaudeOAuth, ClaudeDesktopHistory, ClaudeStatusLine, CursorUsage }
public enum UsageHistoryQuality { Observed, Missing, Failure, Stale, IdentityUnavailable }
[Flags]
public enum UsageHistoryDiscontinuity { None = 0, SourceChanged = 1, PeriodChanged = 2, LimitChanged = 4, CumulativeDecreased = 8, BalanceChanged = 16, Gap = 32 }

/// <summary>Only measured values. BalanceChange is a signed difference, never an inferred spend.</summary>
public sealed record UsageHistoryPoint(DateTimeOffset ObservedAt, string SeriesId, string Label,
    UsageHistoryMetric Metric, decimal Value, string Unit, UsageHistorySource Source,
    DateTimeOffset? PeriodEndsAt, UsageHistoryDiscontinuity Discontinuity);

/// <summary>A gap/failure event; no monetary or percentage value is invented for this time.</summary>
public sealed record UsageHistoryStatus(DateTimeOffset At, UsageHistoryQuality Quality,
    DateTimeOffset? LastObservedAt, string? SeriesId = null);

public sealed record UsageHistoryView(IReadOnlyList<UsageHistoryPoint> Points,
    IReadOnlyList<UsageHistoryStatus> Statuses, DateTimeOffset? ObservationStartedAt,
    string? StorageWarning = null);
