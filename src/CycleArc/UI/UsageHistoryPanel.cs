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
    // The time axis sits under the plotted area, its first and last labels at each end.
    private readonly Grid _axis = new() { Margin = new Thickness(HistoryPlot.PlotLeft, 4, HistoryPlot.PlotRight, 0) };
    private readonly TextBlock _axisStart = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _axisEnd = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly HistoryPlot _plot = new() { Height = 96, Margin = new Thickness(0, 8, 0, 0) };
    private readonly System.Windows.Controls.Button _export = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed
    };
    // Offline UI tests choose the file; production always asks with the save dialog.
    internal Func<string?>? ChooseExportPathForTest { get; set; }
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
        _export.SetResourceReference(StyleProperty, "FlyoutLinkButton");
        _export.Content = UiText.T("Export CSV…", "CSV로 내보내기…");
        _export.ToolTip = UiText.T("Save this range's measurements for a spreadsheet. No account identity is included.",
            "이 기간의 측정값을 스프레드시트용으로 저장합니다. 계정 정보는 포함하지 않습니다.");
        _export.Click += (_, _) => Export();
        content.Children.Add(_export);
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _axis.Children.Add(_axisStart); _axis.Children.Add(_axisEnd);
        _axisStart.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _axisEnd.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _plot.Describe = point => FormatStamp(point.ObservedAt) + " · " + Amount(point) + " · " + Source(point.Source)
            + (point.Discontinuity == UsageHistoryDiscontinuity.None ? "" : " · " + BreakName(point.Discontinuity));
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
            _axisStart.Text = _axisEnd.Text = "";
            _summary.Text = UiText.T("No observations", "관측 데이터 없음"); _notice.Text = ""; _export.Visibility = Visibility.Collapsed;
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
        _axisStart.Text = FormatStamp(_end - Span);
        _axisEnd.Text = FormatStamp(_end);
        _export.Visibility = _view.Points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (points.Length == 0)
        {
            _summary.Text = UiText.T("No measurements in this range", "이 기간에 측정된 데이터가 없습니다");
            System.Windows.Automation.AutomationProperties.SetName(_plot, _summary.Text);
        }
        else
        {
            var last = points[^1];
            _summary.Text = MetricName(last) + ": " + Amount(last) + " · " + FormatStamp(last.ObservedAt);
            // The chart is a picture; its name tells a screen reader what it shows.
            var lowest = Amount(points.MinBy(p => p.Value)!);
            var highest = Amount(points.MaxBy(p => p.Value)!);
            System.Windows.Automation.AutomationProperties.SetName(_plot, UiText.T(
                $"{MetricName(last)} chart, {points.Length} measurements from {lowest} to {highest}. Latest {Amount(last)} at {FormatStamp(last.ObservedAt)}.",
                $"{MetricName(last)} 차트, 측정 {points.Length}개, 최저 {lowest} · 최고 {highest}. 최근 {FormatStamp(last.ObservedAt)} {Amount(last)}."));
        }
        var started = _view.ObservationStartedAt is { } start ? UiText.T("Observed since ", "관측 시작 ") + FormatStamp(start) + ". " : "";
        var gaps = statuses.Count(s => s.Quality != UsageHistoryQuality.Observed);
        var boundaries = points.Count(p => p.Discontinuity != UsageHistoryDiscontinuity.None);
        var source = points.Length > 0 ? UiText.T("Source: ", "출처: ") + string.Join(" / ", points.Select(p => Source(p.Source)).Distinct()) + ". " : "";
        var quality = statuses.LastOrDefault()?.Quality;
        _notice.Text = started + source + UiText.T("Unmeasured time is blank. ", "미측정 구간은 비어 있습니다. ")
            + (quality is null ? "" : UiText.T("Latest state: ", "최근 상태: ") + Quality(quality.Value) + ". ")
            + (gaps > 0 ? UiText.T($"{gaps} missing/stale/failed observations. ", $"누락·오래됨·실패 기록 {gaps}개. ") : "")
            + (boundaries > 0 ? UiText.T($"{boundaries} changes in period, cap, source or balance (point at the chart). ", $"기간·한도·출처·잔액 변화 {boundaries}개 (차트에 마우스를 올려 확인). ") : "")
            + (points.Any(p => p.Metric is UsageHistoryMetric.CreditBalance or UsageHistoryMetric.BalanceChange)
                ? UiText.T("Balance changes include unknown adjustments; they are not actual consumption. ", "잔액 변화에는 원인 불명의 보정이 포함될 수 있으며 실제 소비량이 아닙니다. ") : "")
            + (_view.StorageWarning is null ? "" : UiText.T("Storage warning: ", "저장 경고: ") + _view.StorageWarning);
    }

    private void Export()
    {
        if (_view is not { Points.Count: > 0 } view) return;
        var path = ChooseExportPathForTest is { } choose ? choose() : AskExportPath();
        if (path is null) return;
        try
        {
            // A byte order mark lets spreadsheet programs read Korean labels correctly.
            File.WriteAllText(path, UsageHistoryCsv.Write(view), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _notice.Text = UiText.T("Saved ", "저장함: ") + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice.Text = UiText.T("The file could not be saved: ", "파일을 저장하지 못했습니다: ") + ex.Message;
        }
    }

    private string? AskExportPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"cyclearc-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
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
    private static string Amount(UsageHistoryPoint p) => p.Value.ToString("0.################", CultureInfo.InvariantCulture) + (p.Unit == "%" ? "%" : " " + p.Unit);
    private static string FormatStamp(DateTimeOffset t) => DisplayFormatting.FormatStamp(t);
    private static string Source(UsageHistorySource source) => UsageHistoryChart.SourceName(source);
    private static string Quality(UsageHistoryQuality q) => q switch
    { UsageHistoryQuality.Observed => UiText.T("Observed", "관측됨"), UsageHistoryQuality.Missing => UiText.T("Not provided", "미제공"),
        UsageHistoryQuality.Failure => UiText.T("Check failed", "조회 실패"), UsageHistoryQuality.Stale => UiText.T("Old snapshot", "오래된 스냅샷"), _ => UiText.T("Identity unavailable", "계정 확인 불가") };
    private static string BreakName(UsageHistoryDiscontinuity d) => UsageHistoryChart.DiscontinuityName(d);
}

internal sealed class HistoryPlot : FrameworkElement
{
    // Room for the value labels on the left and the last point on the right.
    public const double PlotLeft = 50;
    public const double PlotRight = 5;
    private const double PlotTop = 5;
    private const double PlotBottom = 9;
    private IReadOnlyList<UsageHistoryPoint> _points = [];
    private IReadOnlyList<UsageHistoryStatus> _statuses = [];
    private DateTimeOffset _start, _end;
    private int _hover = -1;
    private readonly System.Windows.Controls.ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    /// <summary>The text shown for the point nearest the pointer.</summary>
    public Func<UsageHistoryPoint, string>? Describe { get; set; }

    public HistoryPlot()
    {
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
    }

    public void Set(IReadOnlyList<UsageHistoryPoint> points, IReadOnlyList<UsageHistoryStatus> statuses, DateTimeOffset start, DateTimeOffset end)
    {
        _points = points; _statuses = statuses; _start = start; _end = end; _hover = -1;
        _tip.Content = null;
        ToolTip = null;
        InvalidateVisual();
    }

    /// <summary>Shows the point nearest to a position across the plot; used by the pointer and tests.</summary>
    internal void HoverAt(double x)
    {
        if (_points.Count == 0 || ActualWidth <= PlotLeft + PlotRight) return;
        var offset = Math.Clamp(x - PlotLeft, 0, PlotWidth());
        var at = _start + TimeSpan.FromSeconds(offset / PlotWidth() * Math.Max(1, (_end - _start).TotalSeconds));
        var nearest = UsageHistoryChart.NearestIndex(_points, at);
        if (nearest == _hover) return;
        _hover = nearest;
        _tip.Content = nearest < 0 ? null : Describe?.Invoke(_points[nearest]);
        ToolTip = _tip.Content is null ? null : _tip;
        InvalidateVisual();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HoverAt(e.GetPosition(this).X);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    private double PlotWidth() => Math.Max(1, ActualWidth - PlotLeft - PlotRight);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 1) return;
        // A transparent ground lets the whole plot area, not only the drawn line, take the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        var line = TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
        var height = Math.Max(1, ActualHeight - PlotTop - PlotBottom);
        var width = PlotWidth();
        var metric = _points.Count == 0 ? UsageHistoryMetric.UsagePercent : _points[0].Metric;
        var unit = _points.Count == 0 ? "%" : _points[0].Unit;
        var (min, max) = UsageHistoryChart.Scale(metric, _points.Select(p => p.Value).ToArray());
        double Y(double value) => PlotTop + (max - value) / (max - min) * height;
        // Gridlines at the bottom, middle and top of the scale, each labelled with its value.
        var grid = new Pen(line, 1) { DashStyle = DashStyles.Dot };
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var value in new[] { min, UsageHistoryChart.Middle((min, max)), max })
        {
            var y = Y(value);
            dc.DrawLine(value == min ? new Pen(line, 1) : grid, new Point(PlotLeft, y), new Point(PlotLeft + width, y));
            var label = new FormattedText(UsageHistoryChart.AxisValue(value, unit), CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, dpi);
            dc.DrawText(label, new Point(Math.Max(0, PlotLeft - 6 - label.Width), Math.Clamp(y - label.Height / 2, 0, Math.Max(0, ActualHeight - label.Height))));
        }
        if (_points.Count == 0) return;
        Point Position(UsageHistoryPoint p) => new(PlotLeft + (p.ObservedAt - _start).TotalSeconds / Math.Max(1, (_end - _start).TotalSeconds) * width,
            Y((double)p.Value));
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
        if (_hover >= 0 && _hover < _points.Count)
        {
            var hovered = Position(_points[_hover]);
            dc.DrawLine(new Pen(muted, 1), new Point(hovered.X, PlotTop), new Point(hovered.X, PlotTop + height));
            dc.DrawEllipse(accent, new Pen(TryFindResource("CardBrush") as Brush ?? Brushes.White, 1.5), hovered, 4, 4);
        }
    }
}
