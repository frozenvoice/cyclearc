using System.Text;

namespace CycleArc.Monitoring;

/// <summary>
/// The observed measurements of one account as CSV, for a spreadsheet. It contains exactly what
/// the trends chart shows: typed values with their original times and sources. Gaps are rows
/// without a value, never zeros, and no account identity or credential is written.
/// </summary>
public static class UsageHistoryCsv
{
    public const string Header = "observed_at_utc,series,metric,value,unit,source,change,quality";

    public static string Write(UsageHistoryView view)
    {
        var builder = new StringBuilder(Header).Append("\r\n");
        var rows = view.Points.Select(point => (At: point.ObservedAt, Line: Row(point.ObservedAt, point.Label, point.Metric.ToString(),
                point.Value.ToString("0.################", CultureInfo.InvariantCulture), point.Unit, point.Source.ToString(),
                point.Discontinuity == UsageHistoryDiscontinuity.None ? "" : point.Discontinuity.ToString(), "Observed")))
            .Concat(view.Statuses.Where(status => status.Quality != UsageHistoryQuality.Observed)
                .Select(status => (At: status.At, Line: Row(status.At, status.SeriesId ?? "", "", "", "", "", "", status.Quality.ToString()))))
            .OrderBy(row => row.At);
        foreach (var row in rows) builder.Append(row.Line).Append("\r\n");
        return builder.ToString();
    }

    private static string Row(DateTimeOffset at, params string[] values) =>
        string.Join(",", new[] { at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }
            .Concat(values.Select(Escape)));

    // Quotes fields that contain separators, and neutralises leading formula characters so a
    // label can never run as a spreadsheet formula.
    internal static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
