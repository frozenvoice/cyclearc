using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Codex;

/// <summary>One provided limit window, as one compact line inside a widget module.</summary>
public sealed record WidgetPeriodLine(
    CodexWindowKind Kind,
    int? WindowDurationMinutes,
    string PeriodLabel,
    string RemainingText,
    string ResetText,
    string? ResetTooltip,
    bool IsRepresentative)
{
    public string? CadenceLabel { get; init; }
    public string? Tooltip { get; init; }
}

/// <summary>
/// One account as the floating widget shows it: the identity, the representative ring and
/// a compact summary of the periods the provider reported. Computed from the same
/// <see cref="CodexAccountView"/> the popup and tray use, so the widget never collects usage
/// of its own and cannot disagree with them.
/// </summary>
public sealed record WidgetAccountModel(
    string ProfileId,
    string DisplayName,
    UsageProviderId Provider,
    CodexRingPresentation Ring,
    IReadOnlyList<WidgetPeriodLine> Periods,
    string StatusText,
    bool IsStale,
    bool IsSelected,
    string Tooltip)
{
    public string? RingTargetLabel { get; init; }

    public static WidgetAccountModel From(CodexAccountView account, bool selected,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var snapshot = account.Snapshot;
        var cursorProtected = CursorUsagePresentation.IsCursor(snapshot)
            && CursorIdentityRequiresReconnection(snapshot);
        var ringSnapshot = snapshot;
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            var visibleWindows = cursorProtected ? [] : CursorPeriodWindows(snapshot);
            // The ring must not introduce a fourth allowance omitted from the summary.
            // Retain the shared selection/calculation among eligible source windows,
            // without mutating the full snapshot used by the popup, tray and tooltip.
            ringSnapshot = snapshot with
            {
                Windows = snapshot.Windows.Where(window => visibleWindows.Any(visible => ReferenceEquals(visible, window))).ToArray()
            };
        }
        var ring = CodexRingPresentation.From(ringSnapshot, preference);

        // A mismatched or unverified identity must never surface the previous binding's numbers,
        // so its module shows the reconnection status alone.
        var periods = CodexDisplayFormatting.ShowsQuotaWindows(snapshot)
            && !CodexIdentityPresentation.NeedsReconnection(snapshot)
            && !cursorProtected
            ? Lines(snapshot, ring, at)
            : [];

        // Claude always names its receipt state. Codex names any state that is not plain
        // success, including identity mismatch while Status stays Available, so a
        // quota-hidden account still says why instead of relying on color or tooltip.
        var showStatus = account.Profile.Provider == UsageProviderId.Claude
            || CursorUsagePresentation.IsCursor(account.Profile.Provider)
            || snapshot.Status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing)
            || CodexIdentityPresentation.NeedsReconnection(snapshot);
        var status = account.IsSigningIn ? UiText.T("Signing in…", "로그인 중…")
            : showStatus ? CycleArcPresentation.StatusLabel(snapshot) : "";
        if (CursorUsagePresentation.IsCursor(snapshot) && !account.IsSigningIn)
        {
            status = CursorStatusText(snapshot, at);
        }

        var model = new WidgetAccountModel(
            account.Profile.Id,
            account.DisplayName,
            account.Profile.Provider,
            ring,
            periods,
            status,
            ClaudeUsagePresentation.IsStale(snapshot)
                || (CursorUsagePresentation.IsCursor(snapshot) && snapshot.Status == CodexQuotaStatus.Stale),
            selected,
            CursorUsagePresentation.IsCursor(snapshot)
                ? account.DisplayName + Environment.NewLine + CursorTooltip(snapshot, ring, at, cursorProtected)
                : account.DisplayName + Environment.NewLine + CycleArcPresentation.Tooltip(snapshot, preference));

        return model with
        {
            RingTargetLabel = CursorUsagePresentation.IsCursor(snapshot) && !cursorProtected && ring.Window is not null
                ? CursorUsagePresentation.QuotaLabel(ring.Window.LimitId)
                : null
        };
    }

    public static IReadOnlyList<WidgetAccountModel> All(IReadOnlyList<CodexAccountView> accounts, string selectedId,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto, DateTimeOffset? now = null) =>
        // Account management owns the order; the widget never re-sorts by urgency or usage.
        accounts.Select(account => From(account, account.Profile.Id == selectedId, preference, now)).ToArray();

    private static IReadOnlyList<WidgetPeriodLine> Lines(CodexQuotaSnapshot snapshot, CodexRingPresentation ring,
        DateTimeOffset at)
    {
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            var cursorWindows = CursorPeriodWindows(snapshot);
            return cursorWindows.Select(window => CursorLine(window, ring)).ToArray();
        }

        // The represented period leads so the ring and its line read together; every other
        // period the response carried keeps the provider's own order below it. Periods the
        // response did not carry are never invented.
        var ordered = snapshot.Windows.Where(window => ReferenceEquals(window, ring.Window))
            .Concat(snapshot.Windows.Where(window => !ReferenceEquals(window, ring.Window)));
        return ordered.Select(window => new WidgetPeriodLine(
            window.Kind,
            window.WindowDurationMinutes,
            CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes),
            UiText.WidgetLeft(CodexDisplayFormatting.RemainingText(window, snapshot.Provider)),
            CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, at),
            CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt),
            ReferenceEquals(window, ring.Window))).ToArray();
    }

    private static WidgetPeriodLine CursorLine(CodexQuotaWindow window, CodexRingPresentation ring)
    {
        var resetTooltip = CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt);
        return new WidgetPeriodLine(
            window.Kind,
            window.WindowDurationMinutes,
            CursorUsagePresentation.QuotaLabel(window.LimitId),
            CursorUsagePresentation.RemainingText(window),
            "",
            resetTooltip,
            ReferenceEquals(window, ring.Window))
        {
            CadenceLabel = CursorUsagePresentation.QuotaPeriodLabel(window.LimitId),
            Tooltip = CursorWindowTooltip(window)
        };
    }

    private static IReadOnlyList<CodexQuotaWindow> CursorPeriodWindows(CodexQuotaSnapshot snapshot)
    {
        var result = new List<CodexQuotaWindow>(3);
        foreach (var key in new[] { "auto", "api", "sand" })
        {
            var window = snapshot.Windows.FirstOrDefault(candidate =>
                candidate.IsEnabled != false && CursorPeriodKey(candidate.LimitId) == key);
            if (window is not null) result.Add(window);
        }

        return result;
    }

    private static string? CursorPeriodKey(string? limitId) => limitId?.Trim().ToLowerInvariant() switch
    {
        "cursor-auto" or "auto" => "auto",
        "cursor-api" or "api" => "api",
        "cursor-sand" or "sand" => "sand",
        _ => null
    };

    private static string CursorStatusText(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status == CodexQuotaStatus.Stale)
        {
            var stale = UiText.T("Stale data", "오래된 데이터");
            if (CursorUsagePresentation.FailureText(snapshot.TechnicalDetail) is { Length: > 0 } staleFailure)
                stale += " · " + staleFailure;
            if (CodexDeadlineFormatting.Elapsed(snapshot.LastSuccessfulRefresh, now) is { } staleElapsed)
                stale += " · " + staleElapsed;
            return stale;
        }

        var status = CycleArcPresentation.StatusLabel(snapshot);
        if (snapshot.Status == CodexQuotaStatus.Available
            && status == UiText.T("Updated", "업데이트됨")
            && CodexDeadlineFormatting.Elapsed(snapshot.LastSuccessfulRefresh, now) is { } elapsed)
        {
            return status + " · " + elapsed;
        }

        return status;
    }

    private static string CursorTooltip(CodexQuotaSnapshot snapshot, CodexRingPresentation ring,
        DateTimeOffset at, bool protectedSnapshot)
    {
        var lines = new List<string>();
        var status = CursorStatusText(snapshot, at);
        if (!string.IsNullOrWhiteSpace(status)) lines.Add(UiText.T($"Status: {status}", $"상태: {status}"));
        if (CursorUsagePresentation.FailureText(snapshot.TechnicalDetail) is { Length: > 0 } failure
            && !status.Contains(failure, StringComparison.Ordinal))
            lines.Add(failure);
        lines.Add(CursorUsagePresentation.UpdatedText(snapshot));

        var ringTarget = !protectedSnapshot && ring.Window is not null
            ? CursorUsagePresentation.QuotaLabel(ring.Window.LimitId)
            : UiText.T("unknown", "확인 불가");
        lines.Add(UiText.T($"Ring target: {ringTarget}", $"링 대상: {ringTarget}"));

        if (!protectedSnapshot)
            lines.AddRange(snapshot.Windows.Select(CursorWindowTooltip));

        return string.Join(Environment.NewLine, lines);
    }

    private static string CursorWindowTooltip(CodexQuotaWindow window)
    {
        var reset = CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt) ?? UiText.ResetNotProvided;
        return UiText.T(
            $"{CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)} · Remaining {CursorUsagePresentation.RemainingText(window)} · Reset {reset}",
            $"{CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)} · 잔여 {CursorUsagePresentation.RemainingText(window)} · 리셋 {reset}");
    }

    private static bool CursorIdentityRequiresReconnection(CodexQuotaSnapshot snapshot) =>
        snapshot.Status is CodexQuotaStatus.SignedOut
        || snapshot.TechnicalDetail is "cursor-identity-mismatch" or "cursor-live-identity-mismatch";
}
