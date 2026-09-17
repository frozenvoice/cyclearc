using System.Globalization;
using CycleArc.Services;

namespace CycleArc.Codex;

public static class CodexDeadlineFormatting
{
    public static string? Elapsed(DateTimeOffset? timestamp, DateTimeOffset now)
    {
        if (timestamp is null || timestamp > now) return null;
        var elapsed = now - timestamp.Value;
        if (elapsed.TotalDays >= 1)
            return UiText.T($"{elapsed.Days}d ago", $"{elapsed.Days}일 전");
        if (elapsed.TotalHours >= 1)
            return UiText.T($"{(int)elapsed.TotalHours}h ago", $"{(int)elapsed.TotalHours}시간 전");
        if (elapsed.TotalMinutes >= 1)
            return UiText.T($"{(int)elapsed.TotalMinutes}m ago", $"{(int)elapsed.TotalMinutes}분 전");
        return UiText.T("Just now", "방금 전");
    }

    public static string? Remaining(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is null) return null;
        var left = deadline.Value - now;
        if (left <= TimeSpan.Zero) return UiText.T("Awaiting refresh", "갱신 대기");
        if (left.TotalDays >= 1)
            return left.Hours == 0 ? UiText.T($"{left.Days}d left", $"{left.Days}일 남음")
                : UiText.T($"{left.Days}d {left.Hours}h left", $"{left.Days}일 {left.Hours}시간 남음");
        if (left.TotalHours >= 1)
            return left.Minutes == 0 ? UiText.T($"{left.Hours}h left", $"{left.Hours}시간 남음")
                : UiText.T($"{left.Hours}h {left.Minutes}m left", $"{left.Hours}시간 {left.Minutes}분 남음");
        return left.TotalMinutes >= 1 ? UiText.T($"{left.Minutes}m left", $"{left.Minutes}분 남음")
            : UiText.T("Less than a minute", "1분 미만 남음");
    }

    /// <summary>
    /// The widget's countdown to a reset: "in 35m" / "35분 후". Local clock arithmetic only,
    /// so a redraw never costs a request. A deadline that has passed stays "Awaiting refresh"
    /// instead of counting down through zero or claiming a new cycle only a server can confirm.
    /// </summary>
    public static string ResetCountdown(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is null) return UiText.ResetNotProvided;
        var left = deadline.Value - now;
        if (left <= TimeSpan.Zero) return UiText.T("Awaiting refresh", "갱신 대기");
        if (left.TotalDays >= 1)
            return left.Hours == 0 ? UiText.T($"in {left.Days}d", $"{left.Days}일 후")
                : UiText.T($"in {left.Days}d {left.Hours}h", $"{left.Days}일 {left.Hours}시간 후");
        if (left.TotalHours >= 1)
            return left.Minutes == 0 ? UiText.T($"in {left.Hours}h", $"{left.Hours}시간 후")
                : UiText.T($"in {left.Hours}h {left.Minutes}m", $"{left.Hours}시간 {left.Minutes}분 후");
        return left.TotalMinutes >= 1 ? UiText.T($"in {left.Minutes}m", $"{left.Minutes}분 후")
            : UiText.T("in under a minute", "1분 미만 후");
    }

    /// The exact local reset time behind the countdown, or null when the server gave none.
    public static string? ResetStampTooltip(DateTimeOffset? deadline) => deadline is null ? null
        : deadline.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string DateStamp(DateTimeOffset date, DateTimeOffset now)
    {
        var local = date.ToLocalTime();
        var sameYear = local.Year == now.ToLocalTime().Year;
        return UiText.IsKorean ? local.ToString(sameYear ? "M월 d일" : "yyyy년 M월 d일", CultureInfo.InvariantCulture)
            : local.ToString(sameYear ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static (string? Detail, string? Tooltip) CreditExpiry(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ResetCreditsAvailable is not > 0) return (null, null);
        var dates = snapshot.ResetCreditExpirations;
        var known = dates?.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList() ?? [];
        if (known.Count == 0) return (UiText.T("Expiry not provided", "만료일 미제공"), null);
        var complete = known.Count == snapshot.ResetCreditsAvailable;
        var groups = known.GroupBy(x => x.ToLocalTime().Date).ToList();
        var detail = string.Join(Environment.NewLine, groups.Take(3).Select(group =>
            UiText.T($"{DateStamp(group.First(), now)} · {group.Count()} " + (group.Count() == 1 ? "expires" : "expire"), $"{DateStamp(group.First(), now)} 만료 · {group.Count()}개")));
        if (groups.Count > 3) detail += Environment.NewLine + UiText.T($"+{groups.Count - 3} more dates", $"외 {groups.Count - 3}개 날짜");
        if (!complete) detail += Environment.NewLine + UiText.T("Some expiries unavailable", "일부 만료일 미제공");
        var lines = known.GroupBy(x => x).Select(group =>
            $"{DateStamp(group.Key, now)} {group.Key.ToLocalTime():HH:mm} · {group.Count()}" + UiText.T(" credits", "개")).ToList();
        if (!complete) lines.Add(UiText.T("Some credit expiry details are unavailable.", "일부 리셋권의 만료 정보는 제공되지 않았습니다."));
        if (known.Any(x => x <= now)) lines.Add(UiText.T("An expiry time has passed. Refresh to check availability.", "만료 시각이 지난 항목이 있습니다. 새로고침해 확인하세요."));
        return (detail, string.Join(Environment.NewLine, lines));
    }
}
