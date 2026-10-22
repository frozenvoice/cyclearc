namespace CycleArc.Services;

public readonly record struct ResetDisplayInfo(
    string TimeLabel,
    string TimeValue,
    string? EstimateLabel,
    string? EstimateValue);

public static class DisplayFormatting
{
    /// <summary>
    /// English follows the Windows 12/24-hour preference (set once at startup); Korean keeps
    /// 24-hour times. Tests keep the 24-hour default.
    /// </summary>
    public static bool Use12HourClock { get; set; }

    public static bool CultureUses12HourClock(CultureInfo culture) =>
        culture.DateTimeFormat.ShortTimePattern.Contains('h', StringComparison.Ordinal);

    /// <summary>Time of day in every user-visible label.</summary>
    public static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return !UiText.IsKorean && Use12HourClock
            ? local.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The one date-and-time form for user-visible labels: month, day and time, with the year
    /// only when it is not the current one.
    /// </summary>
    public static string FormatStamp(DateTimeOffset value) => FormatStamp(value, DateTimeOffset.Now);

    public static string FormatStamp(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.ToLocalTime();
        var otherYear = local.Year != now.ToLocalTime().Year;
        return UiText.IsKorean
            ? local.ToString(otherYear ? "yyyy년 M월 d일 " : "M월 d일 ", CultureInfo.GetCultureInfo("ko-KR")) + FormatTime(value)
            : local.ToString(otherYear ? "MMM d, yyyy " : "MMM d ", CultureInfo.InvariantCulture) + FormatTime(value);
    }

    public static string FormatDay(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return UiText.IsKorean
            ? local.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR"))
            : local.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public static ResetDisplayInfo ResetDisplay(QuotaSnapshot snapshot)
    {
        var presentation = ProStatusPresentation.From(snapshot);
        if (presentation.HasServerReset && snapshot.ProServerStatus?.ResetAt is DateTimeOffset serverReset)
        {
            if (snapshot.PeriodStart != default && snapshot.PeriodStart >= serverReset)
            {
                var end = snapshot.PeriodEnd != default ? snapshot.PeriodEnd : serverReset.AddDays(7);
                return CycleWindow(snapshot.PeriodStart, end);
            }

            if (snapshot.PeriodEnd != default && snapshot.PeriodEnd != serverReset)
            {
                return CycleWindow(snapshot.PeriodStart, snapshot.PeriodEnd);
            }

            return new ResetDisplayInfo(UiText.ServerReset, FormatStamp(serverReset), null, null);
        }

        if (presentation.ResetAmbiguous)
        {
            return new ResetDisplayInfo(UiText.ServerReset, UiText.MultipleProResets, null, null);
        }

        if (snapshot.ResetAnchorSource == ResetAnchorSource.RetainedServer)
        {
            var start = snapshot.PeriodStart != default
                ? snapshot.PeriodStart
                : snapshot.ProServerStatus?.LastConfirmedResetAt ?? snapshot.ResetAt;
            if (start is DateTimeOffset cycleStart)
            {
                var end = snapshot.PeriodEnd != default ? snapshot.PeriodEnd : cycleStart.AddDays(7);
                return CycleWindow(cycleStart, end);
            }
        }

        if (snapshot.ResetAt is null || snapshot.ResetAnchorSource == ResetAnchorSource.Default)
        {
            return new ResetDisplayInfo(UiText.ResetTime, UiText.NotConfirmed, null, null);
        }

        var stamp = FormatStamp(snapshot.ResetAt.Value);
        return snapshot.ResetAnchorSource switch
        {
            ResetAnchorSource.Server => new ResetDisplayInfo(UiText.ServerReset, UiText.ResetServer(stamp), null, null),
            ResetAnchorSource.UserConfigured => new ResetDisplayInfo(UiText.ResetTime, UiText.ResetUserConfigured(stamp), null, null),
            _ => new ResetDisplayInfo(UiText.ResetTime, UiText.NotConfirmed, null, null)
        };
    }

    private static ResetDisplayInfo CycleWindow(DateTimeOffset start, DateTimeOffset end) =>
        new(UiText.CycleStart, FormatStamp(start), UiText.NextReset, UiText.EstimatedStamp(FormatStamp(end)));

    public static string ResetLabel(QuotaSnapshot snapshot)
    {
        var display = ResetDisplay(snapshot);
        if (display.EstimateValue is null)
        {
            return display.TimeValue;
        }

        return string.IsNullOrEmpty(display.TimeValue)
            ? $"{display.EstimateLabel} {display.EstimateValue}"
            : $"{display.TimeValue} · {display.EstimateLabel} {display.EstimateValue}";
    }

    public static string RemainingDuration(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null || reset <= now)
        {
            return UiText.Soon;
        }

        var span = reset.Value - now;
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public static string LastSyncLabel(DateTimeOffset? lastSync)
    {
        if (lastSync is null)
        {
            return UiText.Never;
        }

        return FormatTime(lastSync.Value);
    }

    public static string StatusLabel(QuotaSnapshot snapshot) =>
        snapshot.IsSyncing ? UiText.SyncingEllipsis : StatusLabel(snapshot.Status);

    public static string StatusLabel(AppSyncStatus status) => status switch
    {
        AppSyncStatus.SignedOut => UiText.SignedOut,
        AppSyncStatus.AuthenticationRequired => UiText.AuthenticationRequired,
        AppSyncStatus.DetectingAccount => UiText.DetectingAccount,
        AppSyncStatus.LoadingCatalog => UiText.LoadingCatalog,
        AppSyncStatus.Syncing => UiText.Syncing,
        AppSyncStatus.UpToDate => UiText.UpToDate,
        AppSyncStatus.RateLimited => UiText.RateLimited,
        AppSyncStatus.ApiChanged => UiText.ApiChanged,
        AppSyncStatus.ProviderSchemaMismatch => UiText.ProviderSchemaMismatch,
        AppSyncStatus.PartialData => UiText.PartialData,
        AppSyncStatus.Offline => UiText.Offline,
        AppSyncStatus.Error => UiText.Error,
        AppSyncStatus.Forbidden => UiText.Forbidden403,
        AppSyncStatus.ChatGptTabRequired => UiText.NoChatGptTab,
        AppSyncStatus.PageBridgeUnavailable => UiText.PageBridgeUnavailable,
        AppSyncStatus.CompanionDisconnected => UiText.CompanionDisconnectedStatus,
        AppSyncStatus.BridgeTimeout => UiText.BridgeTimeoutStatus,
        AppSyncStatus.BridgeWriteFailed => UiText.BridgeWriteFailedStatus,
        _ => UiText.Idle
    };

    public static string WindowUsage(int used, int? limit, bool authoritative, bool lowerBound = false) =>
        authoritative && limit is int value
            ? $"{used} / {value}"
            : lowerBound
                ? $"{used}+"
                : UiText.ReconstructedCount(used);

    public static string UsageLabel(QuotaSnapshot snapshot)
    {
        var presentation = ProStatusPresentation.From(snapshot);
        return presentation.ExactRemainingAvailable
            ? $"{snapshot.Used} / {snapshot.Limit}"
            : presentation.ReconstructedText;
    }

    public static string TrayIconText(QuotaSnapshot snapshot) =>
        ProStatusPresentation.From(snapshot).TrayIconGlyph;

    public static string AuthoritativeRemainingGlyph(QuotaSnapshot snapshot)
    {
        if (snapshot.DisplayUsageUnavailable)
        {
            return "?";
        }

        return snapshot.Remaining >= 100
            ? "99+"
            : snapshot.Remaining.ToString(CultureInfo.InvariantCulture);
    }

    public static string CountSourceLabel(QuotaSnapshot snapshot) =>
        ProStatusPresentation.From(snapshot).CountSourceText;

    public static string OverallCollectionLabel(CoverageInfo coverage) => coverage.OverallState switch
    {
        CollectionState.Complete => UiText.Complete,
        CollectionState.Partial => UiText.Partial,
        CollectionState.Estimated => UiText.Estimated,
        _ => UiText.Unavailable
    };

    public static string CollectionStateLabel(CollectionState state) => state switch
    {
        CollectionState.Complete => UiText.Complete,
        CollectionState.Partial => UiText.Partial,
        CollectionState.Failed => UiText.Failed,
        CollectionState.Estimated => UiText.Estimated,
        _ => UiText.Unavailable
    };

    public static string CoverageFlyoutValue(QuotaSnapshot snapshot) =>
        UserFacingHealth.From(snapshot).DataStatusText;

    public static string FlyoutHeader(QuotaSnapshot snapshot) =>
        UserFacingHealth.From(snapshot).HeaderText;

    public static string CoverageCompactLabel(CoverageInfo coverage)
    {
        var summary = coverage.FailureSummary;
        if (!summary.HasConversationFailures)
        {
            return OverallCollectionLabel(coverage);
        }

        var conversationLabel = summary.FailedThisSyncCount > 0 && summary.DeferredCount == 0
            ? UiText.ConversationsNotRead(summary.FailedThisSyncCount)
            : UiText.ConversationsNotApplied(summary.UnresolvedConversationCount);
        return HasAdditionalPartialCauses(coverage)
            ? $"{UiText.PartialUnapplied} · {conversationLabel}"
            : conversationLabel;
    }

    public static IReadOnlyList<string> CoverageFailureDetailLines(CoverageInfo coverage)
    {
        var summary = coverage.FailureSummary;
        if (!summary.HasConversationFailures)
        {
            return [];
        }

        var lines = new List<string>();
        if (coverage.ConversationSchemaSystemicFailure)
        {
            lines.Add(UiText.RepeatedConversationSchemaMismatch);
        }

        lines.Add(UiText.DataPartiallyNotApplied);
        AddCategoryLine(lines, summary.BodyTimeoutCount, UiText.ReadTimeout);
        AddCategoryLine(lines, summary.SchemaMismatchCount, UiText.ResponseFormatMismatch);
        AppendSchemaMismatchReasons(lines, summary);
        AddCategoryLine(lines, summary.PayloadTooLargeCount, UiText.ResponseTooLarge);
        AddCategoryLine(lines, summary.CompanionDisconnectedCount, UiText.ConversationCompanionFailure);
        AddCategoryLine(lines, summary.AuthenticationCount, UiText.AuthenticationRequired);
        AddCategoryLine(lines, summary.OtherConversationFailureCount, UiText.FailureCategoryOther);
        lines.Add($"{UiText.FailedThisSync}    {summary.FailedThisSyncCount}");
        lines.Add($"{UiText.WaitingToRetry}    {summary.DeferredCount}");
        lines.Add(UiText.CoverageLowerBoundNote);
        lines.Add(UiText.CoverageAutoRetryNote);
        if (!coverage.ConversationSchemaSystemicFailure)
        {
            lines.Add(UiText.CoverageNoUserActionNote);
        }

        return lines;
    }

    public static string FailureCategoryLabel(string? category) =>
        ConversationFetchBackoff.NormalizeCategory(category) switch
        {
            ConversationFetchBackoff.BodyTimeout => UiText.ReadTimeout,
            ConversationFetchBackoff.SchemaMismatch => UiText.ResponseFormatMismatch,
            ConversationFetchBackoff.PayloadTooLarge => UiText.ResponseTooLarge,
            ConversationFetchBackoff.CompanionDisconnected => UiText.ConversationCompanionFailure,
            ConversationFetchBackoff.Authentication => UiText.AuthenticationRequired,
            _ => UiText.FailureCategoryOther
        };

    private static void AddCategoryLine(List<string> lines, int count, string label)
    {
        if (count > 0)
        {
            lines.Add($"{label}    {count}");
        }
    }

    private static void AppendSchemaMismatchReasons(List<string> lines, SyncFailureSummary summary)
    {
        foreach (var pair in summary.SchemaMismatchReasons
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            lines.Add($"  · {UiText.SchemaMismatchDetail(pair.Key)}    {pair.Value}");
        }
    }

    private static bool HasAdditionalPartialCauses(CoverageInfo coverage)
    {
        if (coverage.IndexIncomplete || coverage.HistoryLoadedWithoutUsage)
        {
            return true;
        }

        return coverage.NormalIndexState is CollectionState.Partial or CollectionState.Failed
            || coverage.ArchivedIndexState is CollectionState.Partial or CollectionState.Failed
            || coverage.ProjectsIndexState is CollectionState.Partial or CollectionState.Failed;
    }

    /// <summary>
    /// A bare count with the Korean counter suffix ("5회") when the UI language is Korean,
    /// otherwise the plain number. Used for compact circular statistic badges that are not
    /// quota meters (e.g. the Sol Reasoning "this week" badge).
    /// </summary>
    public static string CountWithUnit(int count) =>
        UiText.IsKorean ? $"{count.ToString(CultureInfo.InvariantCulture)}회" : count.ToString(CultureInfo.InvariantCulture);

    public static string ReasoningLimitValue(int? limit) =>
        limit is int value ? value.ToString(CultureInfo.InvariantCulture) : UiText.NotAvailable;

    public static string CountConfidenceLabel(CoverageConfidence confidence) => confidence switch
    {
        CoverageConfidence.Authoritative => UiText.CountConfidenceAuthoritative,
        CoverageConfidence.HighConfidence => UiText.CountConfidenceHigh,
        CoverageConfidence.Estimated => UiText.CountConfidenceEstimated,
        _ => UiText.CountConfidenceIncomplete
    };

    public static string Headline(QuotaSnapshot snapshot) =>
        ProStatusPresentation.From(snapshot).Headline;

    public static string TrayTooltip(QuotaSnapshot snapshot) =>
        ProStatusPresentation.From(snapshot).TrayTooltip(snapshot);

    public static string Tooltip(QuotaSnapshot snapshot) =>
        ProStatusPresentation.From(snapshot).DetailedTooltip(snapshot);
}
