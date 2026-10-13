using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Monitoring;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace CycleArc.UI;

public partial class FlyoutWindow
{
    public Func<CodexAccountView, TimeSpan, UsageHistoryView>? HistoryReader { get; set; }
    private UsageHistoryPanel? _historyPanel;
    private void BindHistoryAccount(CodexAccountView? account)
    {
        if (_historyPanel is null)
        {
            _historyPanel = new UsageHistoryPanel();
            HistoryHost.Children.Add(_historyPanel);
        }
        _historyPanel.Bind(account, HistoryReader);
    }
}

/// <summary>Shows only locally observed values; asynchronous reads never block popup layout.</summary>
public sealed class UsageHistoryPanel : Expander
{
    private readonly ComboBox _range = new() { Width = 92, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _metric = new() { MinWidth = 80, MaxWidth = 260 };
    private readonly TextBlock _summary = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _notice = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _axis = new() { FontSize = 10, Margin = new Thickness(0, 4, 0, 0) };
    private readonly HistoryPlot _plot = new() { Height = 96, Margin = new Thickness(0, 8, 0, 0) };
    private CodexAccountView? _account;
    private Func<CodexAccountView, TimeSpan, UsageHistoryView>? _read;
    private UsageHistoryView? _view;
    private string[] _series = [];
    private int _generation;
    private bool _loading;
    private DateTimeOffset _lastRead;
    private DateTimeOffset _end;
    private TimeSpan Span => _range.SelectedIndex == 1 ? TimeSpan.FromDays(7) : _range.SelectedIndex == 2 ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24);
    public UsageHistoryPanel()
    {
        Header = UiText.T("Observed trends", "관측된 추이");
        Margin = new Thickness(12, 12, 12, 0);
        var content = new StackPanel();
        var controls = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        _range.ItemsSource = new[] { UiText.T("24 hours", "24시간"), UiText.T("7 days", "7일"), UiText.T("30 days", "30일") };
        _range.SelectedIndex = 0;
        controls.Children.Add(_range); controls.Children.Add(_metric);
        content.Children.Add(controls); content.Children.Add(_summary); content.Children.Add(_plot); content.Children.Add(_axis); content.Children.Add(_notice);
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _axis.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Content = content;
        System.Windows.Automation.AutomationProperties.SetName(_range, UiText.T("History range", "이력 기간"));
        System.Windows.Automation.AutomationProperties.SetName(_metric, UiText.T("Measured metric", "측정 지표"));
        _range.SelectionChanged += (_, _) => { _lastRead = default; Load(); };
        _metric.SelectionChanged += (_, _) => { if (!_loading) Draw(); };
        Expanded += (_, _) => Load();
    }

    public void Bind(CodexAccountView? account, Func<CodexAccountView, TimeSpan, UsageHistoryView>? read)
    {
        var changed = _account?.Profile.Id != account?.Profile.Id || _account?.Snapshot.IdentityFingerprint != account?.Snapshot.IdentityFingerprint;
        _account = account; _read = read;
        Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        if (changed || account is null || UsageCreditPresentation.Hidden(account.Snapshot))
        {
            _generation++; _lastRead = default; _view = null; _series = [];
            _metric.ItemsSource = null; _plot.Set([], [], DateTimeOffset.UtcNow - Span, DateTimeOffset.UtcNow);
            _plot.ToolTip = null; _axis.Text = "";
            _summary.Text = UiText.T("No observations", "관측 데이터 없음"); _notice.Text = "";
        }
        if (IsExpanded) Load();
    }

    private async void Load()
    {
        if (!IsExpanded || _account is null || _read is null || UsageCreditPresentation.Hidden(_account.Snapshot)) return;
        if (DateTimeOffset.UtcNow - _lastRead < TimeSpan.FromSeconds(30)) return;
        var account = _account; var reader = _read; var span = Span; var generation = ++_generation;
        _lastRead = _end = DateTimeOffset.UtcNow;
        try
        {
            var view = await Task.Run(() => reader(account, span));
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                var previous = _metric.SelectedIndex >= 0 && _metric.SelectedIndex < _series.Length ? _series[_metric.SelectedIndex] : null;
                _view = view;
                _series = view.Points.Select(SeriesKey).Distinct().ToArray();
                _loading = true;
                _metric.ItemsSource = _series.Select(id => MetricName(view.Points.First(p => SeriesKey(p) == id))).ToArray();
                _metric.SelectedIndex = _series.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(_series, previous));
                _loading = false;
                Draw();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { await Dispatcher.InvokeAsync(() => { if (generation == _generation) _notice.Text = UiText.T("History could not be read", "이력을 읽지 못했습니다"); }); }
    }

    private void Draw()
    {
        if (_view is null) return;
        UsageHistoryPoint[] points = _metric.SelectedIndex < 0 ? [] : _view.Points.Where(p => SeriesKey(p) == _series[_metric.SelectedIndex]).OrderBy(p => p.ObservedAt).ToArray();
        var statuses = _view.Statuses.Where(s => s.SeriesId is null || points.Length > 0 && s.SeriesId == points[0].SeriesId)
            .OrderBy(s => s.At).ToArray();
        _plot.Set(points, statuses, _end - Span, _end);
        _axis.Text = (_end - Span).ToLocalTime().ToString("MM-dd HH:mm") + "                                        " + _end.ToLocalTime().ToString("MM-dd HH:mm");
        if (points.Length == 0)
        {
            _summary.Text = UiText.T("No measurements in this range", "이 기간에 측정된 데이터가 없습니다");
            _plot.ToolTip = null;
        }
        else
        {
            var last = points[^1];
            _summary.Text = MetricName(last) + ": " + Amount(last) + " · " + Stamp(last.ObservedAt);
            _plot.ToolTip = string.Join(Environment.NewLine, points.TakeLast(12).Select(p => Stamp(p.ObservedAt) + " · " + Amount(p) + " · " + Source(p.Source)
                + (p.Discontinuity == UsageHistoryDiscontinuity.None ? "" : " · " + BreakName(p.Discontinuity))));
        }
        var started = _view.ObservationStartedAt is { } start ? UiText.T("Observed since ", "관측 시작 ") + Stamp(start) + ". " : "";
        var gaps = statuses.Count(s => s.Quality != UsageHistoryQuality.Observed);
        var boundaries = points.Count(p => p.Discontinuity != UsageHistoryDiscontinuity.None);
        var source = points.Length > 0 ? UiText.T("Source: ", "출처: ") + string.Join(" / ", points.Select(p => Source(p.Source)).Distinct()) + ". " : "";
        var quality = statuses.LastOrDefault()?.Quality;
        _notice.Text = started + source + UiText.T("Unmeasured time is blank. ", "미측정 구간은 비어 있습니다. ")
            + (quality is null ? "" : UiText.T("Latest state: ", "최근 상태: ") + Quality(quality.Value) + ". ")
            + (gaps > 0 ? UiText.T($"{gaps} missing/stale/failed observations. ", $"누락·오래됨·실패 기록 {gaps}개. ") : "")
            + (boundaries > 0 ? UiText.T($"{boundaries} changes in period, cap, source or balance (see tooltip). ", $"기간·한도·출처·잔액 변화 {boundaries}개 (툴팁 참고). ") : "")
            + (points.Any(p => p.Metric is UsageHistoryMetric.CreditBalance or UsageHistoryMetric.BalanceChange)
                ? UiText.T("Balance changes include unknown adjustments; they are not actual consumption. ", "잔액 변화에는 원인 불명의 보정이 포함될 수 있으며 실제 소비량이 아닙니다. ") : "")
            + (_view.StorageWarning is null ? "" : UiText.T("Storage warning: ", "저장 경고: ") + _view.StorageWarning);
    }

    private static string MetricName(UsageHistoryPoint p) => Label(p.Label) + " · " + (p.Metric switch
    {
        UsageHistoryMetric.UsagePercent => UiText.T("Usage", "사용률"),
        UsageHistoryMetric.CreditBalance => UiText.T("Credit balance", "크레딧 잔액"),
        UsageHistoryMetric.BalanceChange => UiText.T("Balance change", "잔액 변화"),
        UsageHistoryMetric.ActualSpend => UiText.T("Actual spending", "실제 지출"),
        UsageHistoryMetric.SpendingLimit => UiText.T("Spending cap", "지출 상한"),
        UsageHistoryMetric.RemainingAmount => UiText.T("Until cap", "상한까지 남음"),
        _ => UiText.T("Reported included usage", "서버 제공 포함 사용액")
    });
    private static string Label(string value) => value switch
    { "Extra usage" => UiText.T("Extra usage", "추가 사용"), "Credits" => UiText.T("Credits", "크레딧"),
        "Plan included" => UiText.T("Plan included", "플랜 포함"), _ => value };
    private static string SeriesKey(UsageHistoryPoint p) => p.SeriesId + "|" + p.Metric + "|" + p.Unit;
    private static string Amount(UsageHistoryPoint p) => p.Value.ToString("0.################", CultureInfo.InvariantCulture) + " " + p.Unit;
    private static string Stamp(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    private static string Source(UsageHistorySource source) => source switch
    {
        UsageHistorySource.CodexAppServer => "Codex App Server", UsageHistorySource.ClaudeOAuth => "Claude OAuth usage GET",
        UsageHistorySource.ClaudeDesktopHistory => "Claude Desktop", UsageHistorySource.ClaudeStatusLine => "Claude Code statusLine", _ => "Cursor usage-summary"
    };
    private static string Quality(UsageHistoryQuality q) => q switch
    { UsageHistoryQuality.Observed => UiText.T("Observed", "관측됨"), UsageHistoryQuality.Missing => UiText.T("Not provided", "미제공"),
        UsageHistoryQuality.Failure => UiText.T("Check failed", "조회 실패"), UsageHistoryQuality.Stale => UiText.T("Old snapshot", "오래된 스냅샷"), _ => UiText.T("Identity unavailable", "계정 확인 불가") };
    private static string BreakName(UsageHistoryDiscontinuity d) => UiText.T(d.ToString(),
        d.ToString().Replace("SourceChanged", "출처 변경").Replace("PeriodChanged", "기간 변경").Replace("LimitChanged", "한도 변경")
            .Replace("CumulativeDecreased", "누적액 감소").Replace("BalanceChanged", "잔액 변화").Replace("Gap", "관측 누락"));
}

internal sealed class HistoryPlot : FrameworkElement
{
    private IReadOnlyList<UsageHistoryPoint> _points = [];
    private IReadOnlyList<UsageHistoryStatus> _statuses = [];
    private DateTimeOffset _start, _end;
    public void Set(IReadOnlyList<UsageHistoryPoint> points, IReadOnlyList<UsageHistoryStatus> statuses, DateTimeOffset start, DateTimeOffset end)
    { _points = points; _statuses = statuses; _start = start; _end = end; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 1) return;
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        var line = TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
        dc.DrawLine(new Pen(line, 1), new Point(0, ActualHeight - 1), new Point(ActualWidth, ActualHeight - 1));
        if (_points.Count == 0) return;
        var min = _points.Min(p => (double)p.Value); var max = _points.Max(p => (double)p.Value);
        if (max == min) { var margin = Math.Max(1, Math.Abs(min) * 0.05); min -= margin; max += margin; }
        var left = 50d; var height = ActualHeight - 14; var width = Math.Max(1, ActualWidth - left - 5);
        foreach (var value in new[] { min, max })
            dc.DrawText(new FormattedText(value.ToString(Math.Abs(value) >= 1e8 ? "0.#E+0" : "0.##", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip),
                new Point(0, value == max ? 0 : height - 6));
        Point Position(UsageHistoryPoint p) => new(left + (p.ObservedAt - _start).TotalSeconds / Math.Max(1, (_end - _start).TotalSeconds) * width,
            5 + (max - (double)p.Value) / (max - min) * height);
        // At most 600 vertices; bucket boundaries are never bridged when downsampling.
        var stride = Math.Max(1, (int)Math.Ceiling(_points.Count / 600d));
        for (var i = 0; i < _points.Count; i += stride)
        {
            var current = _points[i]; var at = Position(current);
            if (i >= stride)
            {
                var previous = _points[i - stride];
                var broken = _points.Skip(i - stride + 1).Take(stride).Any(p => p.Discontinuity != UsageHistoryDiscontinuity.None)
                    || current.ObservedAt - previous.ObservedAt > TimeSpan.FromHours(2)
                    || _statuses.Any(s => s.Quality != UsageHistoryQuality.Observed && s.At > previous.ObservedAt && s.At <= current.ObservedAt);
                if (!broken) dc.DrawLine(new Pen(accent, 1.5), Position(previous), at);
            }
            dc.DrawEllipse(accent, null, at, 2, 2);
        }
        var last = Position(_points[^1]); dc.DrawEllipse(accent, null, last, 2.5, 2.5);
    }
}
