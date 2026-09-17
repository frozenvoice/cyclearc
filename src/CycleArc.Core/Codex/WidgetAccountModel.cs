using CycleArc.Providers.Claude;
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
    bool IsRepresentative);

/// <summary>
/// One account as the floating widget shows it: the identity, the representative ring and
/// every period the provider actually reported. Computed from the same
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
    public static WidgetAccountModel From(CodexAccountView account, bool selected,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var snapshot = account.Snapshot;
        var ring = CodexRingPresentation.From(snapshot, preference);

        // A mismatched or unverified identity must never surface the previous binding's numbers,
        // so its module shows the reconnection status alone.
        var periods = CodexDisplayFormatting.ShowsQuotaWindows(snapshot)
            && !CodexIdentityPresentation.NeedsReconnection(snapshot)
            ? Lines(snapshot, ring, at)
            : [];

        // Claude always names its receipt state. Codex names any state that is not plain
        // success, including identity mismatch while Status stays Available, so a
        // quota-hidden account still says why instead of relying on color or tooltip.
        var showStatus = account.Profile.Provider == UsageProviderId.Claude
            || snapshot.Status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing)
            || CodexIdentityPresentation.NeedsReconnection(snapshot);
        var status = account.IsSigningIn ? UiText.T("Signing in…", "로그인 중…")
            : showStatus ? CycleArcPresentation.StatusLabel(snapshot) : "";

        return new WidgetAccountModel(
            account.Profile.Id,
            account.DisplayName,
            account.Profile.Provider,
            ring,
            periods,
            status,
            ClaudeUsagePresentation.IsStale(snapshot),
            selected,
            account.DisplayName + Environment.NewLine + CycleArcPresentation.Tooltip(snapshot, preference));
    }

    public static IReadOnlyList<WidgetAccountModel> All(IReadOnlyList<CodexAccountView> accounts, string selectedId,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto, DateTimeOffset? now = null) =>
        // Account management owns the order; the widget never re-sorts by urgency or usage.
        accounts.Select(account => From(account, account.Profile.Id == selectedId, preference, now)).ToArray();

    private static IReadOnlyList<WidgetPeriodLine> Lines(CodexQuotaSnapshot snapshot, CodexRingPresentation ring,
        DateTimeOffset at)
    {
        // The represented period leads so the ring and its line read together; every other
        // period the response carried keeps the provider's own order below it. Periods the
        // response did not carry are never invented.
        var ordered = snapshot.Windows.Where(window => ReferenceEquals(window, ring.Window))
            .Concat(snapshot.Windows.Where(window => !ReferenceEquals(window, ring.Window)));
        return ordered.Select(window => new WidgetPeriodLine(
            window.Kind,
            window.WindowDurationMinutes,
            CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes),
            UiText.WidgetLeft(CodexDisplayFormatting.PercentText(window.RemainingPercent, snapshot.Provider)),
            CodexDeadlineFormatting.ResetCountdown(window.ResetsAt, at),
            CodexDeadlineFormatting.ResetStampTooltip(window.ResetsAt),
            ReferenceEquals(window, ring.Window))).ToArray();
    }
}
