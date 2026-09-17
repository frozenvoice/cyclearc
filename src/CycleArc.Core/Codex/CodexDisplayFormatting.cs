using System.Globalization;
using CycleArc.Services;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;

namespace CycleArc.Codex;

public sealed record CodexDisplayRow(string Label, string Value, bool EmphasizeDanger, string? Detail = null, string? Tooltip = null);

public static class CodexDisplayFormatting
{
    private static string FormatStale(CodexQuotaSnapshot snapshot)
    {
        var recent = RecentFailureText(snapshot.TechnicalDetail);
        return string.IsNullOrWhiteSpace(recent)
            ? UiText.CodexDataStale
            : $"{UiText.CodexDataStale}{Environment.NewLine}{UiText.CodexRecentRefreshError}: {recent}";
    }

    public static string? RecentFailureText(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        if (detail.Contains("protocol", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("response-error", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("root-keys", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexProtocolChanged;
        }

        if (detail.Contains("timed-out", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexTimedOut;
        }

        if (detail.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexCancelled;
        }

        return null;
    }

    public static string SectionTitle => "CODEX";

    public static string StatusText(CodexQuotaSnapshot snapshot) => CodexIdentityPresentation.NeedsReconnection(snapshot)
        ? CodexIdentityPresentation.Explanation(snapshot) : snapshot.Provider == UsageProviderId.Claude
        ? ClaudeUsagePresentation.StatusText(snapshot) : snapshot.Status switch
    {
        CodexQuotaStatus.Refreshing when !snapshot.HasUsablePercentages => UiText.CodexRefreshing,
        CodexQuotaStatus.Refreshing => UiText.CodexRefreshing,
        CodexQuotaStatus.Stale => FormatStale(snapshot),
        CodexQuotaStatus.CodexNotFound => UiText.CodexNotFound,
        CodexQuotaStatus.SignedOut => UiText.CodexSignIn,
        CodexQuotaStatus.ProtocolMismatch => UiText.CodexProtocolChanged,
        CodexQuotaStatus.TimedOut => UiText.CodexTimedOut,
        CodexQuotaStatus.Cancelled => UiText.CodexCancelled,
        CodexQuotaStatus.Unavailable => UiText.CodexUnavailable,
        CodexQuotaStatus.Available => "",
        _ => UiText.CodexUnavailable
    };

    /// <summary>
    /// Whether this snapshot's quota windows may be shown at all. A signed-out or not-found
    /// account never shows windows, and a failed refresh shows them only while it still holds
    /// last-good percentages. Shared so the detail rows and the widget modules cannot drift.
    /// </summary>
    public static bool ShowsQuotaWindows(CodexQuotaSnapshot snapshot) =>
        snapshot.Status is not (CodexQuotaStatus.CodexNotFound or CodexQuotaStatus.SignedOut)
        && (snapshot.HasUsablePercentages
            || snapshot.Status is not (CodexQuotaStatus.Unavailable
                or CodexQuotaStatus.ProtocolMismatch
                or CodexQuotaStatus.TimedOut
                or CodexQuotaStatus.Cancelled
                or CodexQuotaStatus.Refreshing));

    public static IReadOnlyList<CodexDisplayRow> Rows(CodexQuotaSnapshot snapshot, DateTimeOffset? now = null, bool includeResetCredits = true)
    {
        if (!ShowsQuotaWindows(snapshot))
        {
            return [];
        }

        var at = now ?? DateTimeOffset.Now;
        var rows = new List<CodexDisplayRow>();
        foreach (var window in snapshot.Windows)
        {
            var usedLabel = UsedLabel(window);
            var hasRemaining = window.RemainingPercent is not null;
            var label = hasRemaining ? $"{usedLabel} / {UiText.T("left", "남음")}" : usedLabel;
            var value = hasRemaining
                ? $"{PercentText(window.UsedPercent, snapshot.Provider)} / {PercentText(window.RemainingPercent, snapshot.Provider)}"
                : PercentText(window.UsedPercent, snapshot.Provider);
            rows.Add(new CodexDisplayRow(label, value, window.UsedPercent >= 100));

            rows.Add(new CodexDisplayRow(UiText.Reset, ResetStamp(window.ResetsAt), false,
                CodexDeadlineFormatting.Remaining(window.ResetsAt, at)));
        }

        if (includeResetCredits && snapshot.ResetCreditsAvailable is int credits)
        {
            var expiry = CodexDeadlineFormatting.CreditExpiry(snapshot, at);
            rows.Add(new CodexDisplayRow(UiText.ResetCredits, credits.ToString(CultureInfo.InvariantCulture), false, expiry.Detail, expiry.Tooltip));
        }

        if (snapshot.LastSuccessfulRefresh is { } checkedAt)
        {
            var elapsed = CodexDeadlineFormatting.Elapsed(checkedAt, at);
            var value = elapsed is null ? TimeOfDay(checkedAt) : $"{TimeOfDay(checkedAt)} · {elapsed}";
            rows.Add(snapshot.Provider == UsageProviderId.Claude
                ? new CodexDisplayRow(ClaudeUsagePresentation.ReceiptLabel(snapshot),
                    checkedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false, value,
                    ClaudeUsagePresentation.LastReceivedText(snapshot))
                : new CodexDisplayRow(UiText.LastChecked, value, false));
        }

        return rows;
    }

    public static string OverviewText(CodexQuotaSnapshot snapshot)
    {
        var status = StatusText(snapshot);
        var rows = Rows(snapshot);
        if (rows.Count == 0)
        {
            return string.IsNullOrWhiteSpace(status) ? UiText.CodexUnavailable : status;
        }

        var body = string.Join("   ", rows.Select(row => $"{row.Label} {row.Value}"));
        return string.IsNullOrWhiteSpace(status) ? body : $"{status}   ·   {body}";
    }

    public static IReadOnlyList<string> DiagnosticLines(CodexQuotaSnapshot snapshot, bool executableFound)
    {
        return
        [
            $"{UiText.CodexExecutable}: {(executableFound ? UiText.Found : UiText.NotFound)}",
            $"{UiText.CodexSignInState}: {SignInLabel(snapshot)}",
            $"{UiText.CodexFreshness}: {FreshnessLabel(snapshot)}",
            $"{UiText.LastChecked}: {(snapshot.LastSuccessfulRefresh is { } success ? TimeOfDay(success) : UiText.Never)}",
            $"{UiText.CodexWindows}: {WindowSummary(snapshot)}",
            $"{UiText.CodexFailureCategory}: {snapshot.TechnicalDetail ?? snapshot.Status.ToString()}"
        ];
    }

    public static string DurationLabel(int? minutes)
    {
        if (minutes is null or <= 0)
        {
            return UiText.T("Unknown duration", "알 수 없는 기간");
        }

        if (minutes == CodexWindowClassifier.FiveHourMinutes)
        {
            return UiText.T("5-hour", "5시간");
        }

        if (minutes == CodexWindowClassifier.WeeklyMinutes)
        {
            return UiText.T("Weekly", "주간");
        }

        if (minutes % 1440 == 0)
        {
            var days = minutes.Value / 1440;
            return days == 1
                ? UiText.T("24-hour", "24시간")
                : UiText.T($"{days}-day", $"{days}일");
        }

        if (minutes % 60 == 0)
        {
            var hours = minutes.Value / 60;
            return UiText.T($"{hours}-hour", $"{hours}시간");
        }

        return UiText.T($"{minutes} min", $"{minutes}분");
    }

    public static string ResetStamp(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return UiText.NotAvailable;
        }

        var local = resetsAt.Value.ToLocalTime();
        var now = DateTimeOffset.Now.ToLocalTime();
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return DisplayFormatting.FormatStamp(local);
    }

    public static string TimeOfDay(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string PercentText(double? value, UsageProviderId provider = UsageProviderId.Codex) =>
        value is { } percent && double.IsFinite(percent)
            ? (provider == UsageProviderId.Claude
                ? Math.Clamp(percent, 0, 100).ToString("0.##", CultureInfo.InvariantCulture)
                : Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)) + "%"
            : "?";

    public static string CompactWindowKindLabel(CodexQuotaWindow? window, UsageProviderId provider = UsageProviderId.Codex)
    {
        var name = provider.Name();
        if (window is null)
        {
            return UiText.T($"{name} usage", $"{name} 사용량");
        }

        return window.Kind switch
        {
            CodexWindowKind.Weekly => UiText.T($"{name} weekly usage", $"{name} 주간 사용"),
            CodexWindowKind.FiveHour => UiText.T($"{name} 5-hour usage", $"{name} 5시간 사용"),
            _ => UiText.T($"{name} {DurationLabel(window.WindowDurationMinutes)} usage", $"{name} {DurationLabel(window.WindowDurationMinutes)} 사용")
        };
    }

    private static string UsedLabel(CodexQuotaWindow window) => window.Kind switch
    {
        CodexWindowKind.FiveHour => UiText.FiveHourUsed,
        CodexWindowKind.Weekly => UiText.WeeklyUsed,
        _ => UiText.T($"{DurationLabel(window.WindowDurationMinutes)} used", $"{DurationLabel(window.WindowDurationMinutes)} 사용량")
    };

    private static string SignInLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.SignedOut => UiText.SignedOut,
        CodexQuotaStatus.CodexNotFound => UiText.NotFound,
        CodexQuotaStatus.Available or CodexQuotaStatus.Stale or CodexQuotaStatus.Refreshing => UiText.T("Signed in", "로그인됨"),
        _ => UiText.Unavailable
    };

    private static string FreshnessLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Available => UiText.T("Fresh", "최신"),
        CodexQuotaStatus.Stale => UiText.T("Stale", "오래됨"),
        CodexQuotaStatus.Refreshing => UiText.CodexRefreshing,
        _ => UiText.Unavailable
    };

    private static string WindowSummary(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0)
        {
            return UiText.NotAvailable;
        }

        return string.Join(
            ", ",
            snapshot.Windows.Select(window =>
                $"{DurationLabel(window.WindowDurationMinutes)} {PercentText(window.UsedPercent)}"));
    }
}
