using CycleArc.Services;

namespace CycleArc.Monitoring;

/// <summary>Scale, labels and hit testing for the observed-trends chart.</summary>
public static class UsageHistoryChart
{
    /// <summary>
    /// Percentages always use the whole 0-100 range, so a move from 40% to 41% does not fill the
    /// chart. Amounts start from zero (or include it when a change goes negative), so the height
    /// of a line stays proportional to the value it shows.
    /// </summary>
    public static (double Min, double Max) Scale(UsageHistoryMetric metric, IReadOnlyCollection<decimal> values)
    {
        if (metric == UsageHistoryMetric.UsagePercent)
        {
            var top = values.Count == 0 ? 100d : Math.Max(100d, (double)values.Max());
            var bottom = values.Count == 0 ? 0d : Math.Min(0d, (double)values.Min());
            return (bottom, top);
        }
        if (values.Count == 0) return (0, 1);
        var min = Math.Min(0d, (double)values.Min());
        var max = Math.Max(0d, (double)values.Max());
        if (max - min < 1e-9) return (min, min + 1);
        // A little headroom keeps the highest point off the chart's top edge.
        var pad = (max - min) * 0.08;
        return (min < 0 ? min - pad : min, max > 0 ? max + pad : max);
    }

    /// <summary>The value of the middle gridline.</summary>
    public static double Middle((double Min, double Max) scale) => scale.Min + (scale.Max - scale.Min) / 2;

    public static string AxisValue(double value, string unit) =>
        value.ToString(Math.Abs(value) >= 1e8 ? "0.#E+0" : Math.Abs(value) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture)
        + (unit == "%" ? "%" : "");

    /// <summary>The point closest in time to the pointer, or -1 for an empty series.</summary>
    public static int NearestIndex(IReadOnlyList<UsageHistoryPoint> points, DateTimeOffset at)
    {
        var best = -1;
        var distance = TimeSpan.MaxValue;
        for (var index = 0; index < points.Count; index++)
        {
            var gap = (points[index].ObservedAt - at).Duration();
            if (gap >= distance) continue;
            distance = gap;
            best = index;
        }
        return best;
    }

    public static string SourceName(UsageHistorySource source) => source switch
    {
        UsageHistorySource.CodexAppServer => UiText.T("Codex server", "Codex 서버"),
        UsageHistorySource.ClaudeOAuth => UiText.T("Claude server", "Claude 서버"),
        UsageHistorySource.ClaudeDesktopHistory => UiText.T("Claude Desktop history", "Claude Desktop 기록"),
        UsageHistorySource.ClaudeStatusLine => UiText.T("Claude Code status line", "Claude Code 상태 표시줄"),
        _ => UiText.T("Cursor server", "Cursor 서버")
    };

    public static string DiscontinuityName(UsageHistoryDiscontinuity value)
    {
        var parts = new List<string>();
        void Add(UsageHistoryDiscontinuity flag, string english, string korean)
        {
            if (value.HasFlag(flag)) parts.Add(UiText.T(english, korean));
        }
        Add(UsageHistoryDiscontinuity.SourceChanged, "source changed", "출처 변경");
        Add(UsageHistoryDiscontinuity.PeriodChanged, "new period", "기간 변경");
        Add(UsageHistoryDiscontinuity.LimitChanged, "limit changed", "한도 변경");
        Add(UsageHistoryDiscontinuity.CumulativeDecreased, "total decreased", "누적액 감소");
        Add(UsageHistoryDiscontinuity.BalanceChanged, "balance changed", "잔액 변화");
        Add(UsageHistoryDiscontinuity.Gap, "observation gap", "관측 누락");
        return string.Join(", ", parts);
    }
}
