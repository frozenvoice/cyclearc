using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Services;

namespace CycleArc.Codex;

public enum UsagePaceTone
{
    /// <summary>At the observed rate the limit lasts until reset with room to spare. The popup
    /// keeps only the ring mark for this tone; the sentence is for tooltips and screen readers.</summary>
    Ahead,
    /// <summary>At the observed rate the limit lasts until reset, but only just.</summary>
    Tight,
    /// <summary>At the observed rate the limit runs out before it resets.</summary>
    RunsOut
}

/// <summary>
/// How the shown limit is being spent compared with an even pace through its window.
/// <c>EvenRemainingPercent</c> is what would be left now if the window were spent evenly; the
/// popup marks it on the ring. The projection extends the average rate since the window began,
/// measured at the sample's own time, so it never assumes usage the server did not report.
/// </summary>
public sealed record UsagePace(
    UsagePaceTone Tone,
    double EvenRemainingPercent,
    DateTimeOffset? RunsOutAt,
    double? RemainingAtResetPercent,
    string Text,
    string MarkerText);

public static class UsagePacePresentation
{
    // A projection from the first minutes of a window is mostly noise.
    private static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(30);
    private const double MinimumElapsedShare = 0.10;
    // Below this share left at reset the projection is called tight rather than comfortable.
    public const double TightRemainingPercent = 15;

    /// <summary>
    /// The pace of <paramref name="window"/>, or null when it cannot be stated honestly: no
    /// healthy server sample (a Desktop history or failed sample), Cursor's allowances, an
    /// unknown percentage, duration or reset, a reset that has passed or is unconfirmed, an
    /// unused or exhausted limit, or a window too young for its rate to mean anything
    /// (under 10% of its length, and never under 30 minutes).
    /// </summary>
    public static UsagePace? From(CodexQuotaSnapshot snapshot, CodexQuotaWindow? window, DateTimeOffset now)
    {
        // A check in progress keeps describing the sample it started from.
        var basis = snapshot.Status == CodexQuotaStatus.Refreshing && snapshot.RefreshOriginStatus is { } origin
            ? snapshot with { Status = origin }
            : snapshot;
        if (window is null || CursorUsagePresentation.IsCursor(basis.Provider)
            || !WidgetStatusPresentation.HasHealthyServerSample(basis)
            || basis.LastSuccessfulRefresh is not { } observedAt || observedAt > now
            || window.UsedPercent is not { } used || !double.IsFinite(used)
            || window.WindowDurationMinutes is not ({ } minutes and > 0)
            || window.ResetsAt is not { } resetsAt || resetsAt <= now
            || window.ResetObservation?.State is ClaudeResetObservationState.Elapsed or ClaudeResetObservationState.Stale)
        {
            return null;
        }

        var duration = TimeSpan.FromMinutes(minutes);
        var elapsed = duration - (resetsAt - observedAt);
        var minimum = TimeSpan.FromTicks(Math.Max(MinimumElapsed.Ticks, (long)(duration.Ticks * MinimumElapsedShare)));
        if (elapsed < minimum || elapsed > duration) return null;

        var usedShare = Math.Clamp(used, 0, 100);
        var remaining = 100 - usedShare;
        if (usedShare <= 0 || remaining <= 0) return null;

        var even = Math.Clamp((resetsAt - now).TotalMinutes / duration.TotalMinutes * 100, 0, 100);
        var marker = UiText.T($"Even-pace mark: {Math.Round(even):0}% left by now",
            $"적정 잔량 눈금: 지금 {Math.Round(even):0}%");

        var ratePerMinute = usedShare / elapsed.TotalMinutes;
        var runsOutAt = observedAt + TimeSpan.FromMinutes(remaining / ratePerMinute);
        if (runsOutAt < resetsAt)
        {
            var at = runsOutAt > now ? runsOutAt : now;
            var gap = Span(resetsAt - at);
            var text = runsOutAt > now
                ? UiText.T($"Runs out around {CodexDisplayFormatting.ResetStamp(runsOutAt, now)} · {gap} before reset",
                    $"{CodexDisplayFormatting.ResetStamp(runsOutAt, now)}쯤 소진 예상 · 리셋 {gap} 전")
                : UiText.T($"Runs out soon · {gap} before reset",
                    $"곧 소진 예상 · 리셋 {gap} 전");
            return new UsagePace(UsagePaceTone.RunsOut, even, runsOutAt, null, text, marker);
        }

        var atReset = Math.Clamp(remaining - ratePerMinute * (resetsAt - observedAt).TotalMinutes, 0, 100);
        var rounded = Math.Round(atReset);
        var tight = atReset < TightRemainingPercent;
        var summary = tight
            ? UiText.T($"Tight · about {rounded:0}% left at reset", $"빠듯함 · 리셋 때 약 {rounded:0}% 남음")
            : UiText.T($"On track · about {rounded:0}% left at reset", $"여유 · 리셋 때 약 {rounded:0}% 남음");
        return new UsagePace(tight ? UsagePaceTone.Tight : UsagePaceTone.Ahead, even, null, atReset, summary, marker);
    }

    // "38m", "2h 5m", "3d 4h": the gap alone, without "left" or "in".
    internal static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return UiText.T("under a minute", "1분 미만");
        if (span.TotalDays >= 1)
            return span.Hours == 0 ? UiText.T($"{span.Days}d", $"{span.Days}일")
                : UiText.T($"{span.Days}d {span.Hours}h", $"{span.Days}일 {span.Hours}시간");
        if (span.TotalHours >= 1)
            return span.Minutes == 0 ? UiText.T($"{span.Hours}h", $"{span.Hours}시간")
                : UiText.T($"{span.Hours}h {span.Minutes}m", $"{span.Hours}시간 {span.Minutes}분");
        return UiText.T($"{span.Minutes}m", $"{span.Minutes}분");
    }
}
