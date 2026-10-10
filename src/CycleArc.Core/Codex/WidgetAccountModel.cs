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
    public string DisplayRemainingText { get; init; } = RemainingText;
    // Validated remainder of this limit for its small bar; unknown usage draws no bar.
    public double? RemainingPercent { get; init; }
    // Usage band of the unrounded value, the same rule the rings use.
    public string BarBrushKey { get; init; } = UsageRingBands.ArcBrushKey(UsageRingBand.Normal);
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
    public WidgetStatusPresentation? StatusPresentation { get; init; }
    // Keep the full status for tooltips/accessibility even when the healthy footer is folded.
    public bool ShowStatusRow { get; init; } = !string.IsNullOrEmpty(StatusText);
    // Widget glyphs only: keep the shared ring's precise text, percentage and danger state.
    public string RingValueText => UsagePercentFormatting.Widget(Ring.IsAvailable ? Ring.Window?.UsedPercent : null);
    // The widget ring fills with what is left, so its glyph is the widget-precision remainder
    // of the same limit, sharing rounding with the used value above.
    public string RingRemainingValueText => Ring.IsAvailable && Ring.Window is { } window
        ? UsagePercentFormatting.WidgetRemaining(window) : "?";

    public static WidgetAccountModel From(CodexAccountView account, bool selected,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var snapshot = account.Snapshot;
        var statusPresentation = WidgetStatusPresentation.From(snapshot, at, account.IsSigningIn);
        var protectedIdentity = WidgetStatusPresentation.HidesQuota(snapshot);
        var cursorProtected = CursorUsagePresentation.IsCursor(snapshot)
            && CursorIdentityRequiresReconnection(snapshot);
        var ringSnapshot = snapshot;
        if (protectedIdentity) ringSnapshot = snapshot with { Windows = [] };
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            var visibleWindows = cursorProtected ? [] : CursorPeriodWindows(snapshot);
            // The ring must not introduce a fourth allowance omitted from the summary.
            // Retain the shared selection/calculation among eligible source windows,
            // without mutating the full snapshot used by the popup, tray and tooltip.
            ringSnapshot = snapshot with
            {
                // CursorPeriodWindows is deliberately canonical (Cursor Models, Other Models,
                // Grok Bot). Keep that order for the shared DisplayWindow known-value fallback
                // even when the provider returns its windows in a different order.
                Windows = visibleWindows
            };
        }
        var ring = CodexRingPresentation.FromDetail(ringSnapshot, preference);

        // A mismatched or unverified identity must never surface the previous binding's numbers,
        // so its module shows the reconnection status alone.
        var periods = CodexDisplayFormatting.ShowsQuotaWindows(snapshot)
            && !CodexIdentityPresentation.NeedsReconnection(snapshot)
            && !cursorProtected
            && !protectedIdentity
            ? Lines(snapshot, ring, at)
            : [];

        var status = statusPresentation.Summary;

        var model = new WidgetAccountModel(
            account.Profile.Id,
            account.DisplayName,
            account.Profile.Provider,
            ring,
            periods,
            status,
            snapshot.Status == CodexQuotaStatus.Stale,
            selected,
            CursorUsagePresentation.IsCursor(snapshot)
                ? account.DisplayName + Environment.NewLine + CursorTooltip(snapshot, ring, at, cursorProtected, account.CursorActivity)
                : account.DisplayName + Environment.NewLine + CycleArcPresentation.Tooltip(ringSnapshot, preference));

        return model with
        {
            StatusPresentation = statusPresentation,
            ShowStatusRow = statusPresentation.ShowRow,
            Tooltip = model.Tooltip + Environment.NewLine + statusPresentation.DetailText,
            RingTargetLabel = CursorUsagePresentation.IsCursor(snapshot) && !cursorProtected && ring.Window is not null
                ? CursorUsagePresentation.QuotaLabel(ring.Window.LimitId)
                : null
        };
    }

    // Shared with the popup detail, which folds the same healthy status text.
    public static bool HasHealthyServerSample(CodexQuotaSnapshot snapshot) =>
        WidgetStatusPresentation.HasHealthyServerSample(snapshot);

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
            UiText.WidgetLeft(UsagePercentFormatting.DetailRemaining(window)),
            CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, at),
            CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt),
            ReferenceEquals(window, ring.Window))
        {
            DisplayRemainingText = UiText.WidgetLeft(
                UsagePercentFormatting.WidgetRemaining(window)),
            RemainingPercent = window.UsedPercent is >= 0 and <= 100 && double.IsFinite(window.UsedPercent.Value)
                ? window.RemainingPercent : null,
            BarBrushKey = UsageRingBands.ArcBrushKey(UsageRingBands.From(window.UsedPercent))
        }).ToArray();
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
            Tooltip = CursorWindowTooltip(window),
            // Round directly from the percentage, never from an already formatted string.
            // Amounts, disabled/unlimited states and all tooltip text retain their own format.
            DisplayRemainingText = window.IsEnabled != false && !window.IsUnlimited && window.RemainingAmount is null
                ? UsagePercentFormatting.WidgetRemaining(window)
                : CursorUsagePresentation.RemainingText(window),
            // A percentage allowance gets the same small bar as other providers; money,
            // unlimited and disabled allowances keep their text only.
            RemainingPercent = window.IsEnabled != false && !window.IsUnlimited && window.RemainingAmount is null
                && window.UsedPercent is >= 0 and <= 100 ? window.RemainingPercent : null,
            BarBrushKey = UsageRingBands.ArcBrushKey(UsageRingBands.From(window.UsedPercent))
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
        DateTimeOffset at, bool protectedSnapshot, Providers.Cursor.CursorActivityView? activity)
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
        if (Providers.Cursor.CursorActivityPresentation.TooltipLine(activity, at) is { } recent) lines.Add(recent);

        if (!protectedSnapshot)
            lines.AddRange(snapshot.Windows.Select(CursorWindowTooltip));

        return string.Join(Environment.NewLine, lines);
    }

    private static string CursorWindowTooltip(CodexQuotaWindow window)
    {
        var reset = CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt) ?? UiText.ResetNotProvided;
        return UiText.T(
            $"{CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)} · Left {CursorUsagePresentation.RemainingText(window)} · Reset {reset}",
            $"{CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)} · 남음 {CursorUsagePresentation.RemainingText(window)} · 리셋 {reset}");
    }

    private static bool CursorIdentityRequiresReconnection(CodexQuotaSnapshot snapshot) =>
        snapshot.Status is CodexQuotaStatus.SignedOut
        || snapshot.TechnicalDetail is "cursor-identity-mismatch" or "cursor-live-identity-mismatch";
}
