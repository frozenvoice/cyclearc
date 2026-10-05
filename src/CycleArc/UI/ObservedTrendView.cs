using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using CycleArc.Codex;
using CycleArc.Observations;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Control = System.Windows.Controls.Control;

namespace CycleArc.UI;

/// <summary>A cached view of real observations in the represented quota window.</summary>
public sealed class ObservedTrendView : Border
{
    private readonly bool _compact;
    private readonly ComboBox _metricPicker = new() { MinWidth = 88, MaxWidth = 190, FontSize = 11 };
    private readonly ListBox _values = new() { MaxHeight = 180, BorderThickness = new Thickness(0), FontSize = 11 };
    private readonly TextBlock _range = new() { FontSize = 10, TextWrapping = TextWrapping.Wrap };
    private QuotaObservationHistorySnapshot? _history;
    private CodexQuotaWindow? _window;
    private string? _profileId;
    private UiLanguage? _language;
    private bool _loading, _unavailable, _hidden, _binding;
    private QuotaObservationMetric _metric = QuotaObservationMetric.UsedPercent;
    private QuotaObservationWindow? _rendered;
    private List<MetricChoice>? _metricChoices;
    private int _metricChoicesMask;
    private UiLanguage? _metricChoicesLanguage;
    private string? _metricChoicesUnit;

    public QuotaSparkline Chart { get; } = new();
    public TextBlock TitleText { get; } = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    public TextBlock LastObservationText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public TextBlock EmptyText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public TextBlock StorageNotice { get; } = new() { FontSize = 10, TextWrapping = TextWrapping.Wrap };
    public Expander ValuesExpander { get; } = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    public int ValuesBuildCount { get; private set; }
    public int ValueRowCount => _values.Items.Count;
    public ComboBox MetricPicker => _metricPicker;

    public ObservedTrendView(bool compact = false)
    {
        _compact = compact;
        Margin = new Thickness(0, compact ? 5 : 10, 0, 0);
        Padding = new Thickness(0, compact ? 4 : 8, 0, 0);
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BorderBrushProperty, "LineBrush");
        if (!compact) SetResourceReference(BackgroundProperty, "CardBrush");
        var panel = new StackPanel();
        var heading = new DockPanel { LastChildFill = true };
        _metricPicker.Visibility = Visibility.Collapsed;
        if (!compact)
        {
            DockPanel.SetDock(_metricPicker, Dock.Right);
            _metricPicker.Margin = new Thickness(8, 0, 0, 0);
            _metricPicker.DisplayMemberPath = nameof(MetricChoice.Label);
            heading.Children.Add(_metricPicker);
        }
        heading.Children.Add(TitleText);
        TitleText.FontSize = compact ? 10 : 12;
        LastObservationText.FontSize = compact ? 9 : 10;
        EmptyText.FontSize = compact ? 10 : 11;
        Chart.Height = compact ? 26 : 52;
        Chart.Margin = new Thickness(0, 4, 0, 2);
        panel.Children.Add(heading);
        panel.Children.Add(EmptyText);
        panel.Children.Add(Chart);
        panel.Children.Add(_range);
        panel.Children.Add(LastObservationText);
        panel.Children.Add(StorageNotice);
        if (!compact) panel.Children.Add(ValuesExpander);
        Child = panel;
        foreach (var text in new[] { TitleText, LastObservationText, EmptyText, _range })
            text.SetResourceReference(TextBlock.ForegroundProperty, text == TitleText ? "TextBrush" : "MutedBrush");
        StorageNotice.SetResourceReference(TextBlock.ForegroundProperty, "StaleBrush");
        ValuesExpander.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        _values.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        _values.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_values, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_values, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_values, true);
        VirtualizingPanel.SetIsVirtualizing(_values, true);
        VirtualizingPanel.SetVirtualizationMode(_values, VirtualizationMode.Recycling);
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        row.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        _values.ItemTemplate = new DataTemplate { VisualTree = row };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _values.ItemContainerStyle = itemStyle;
        ValuesExpander.Content = _values;
        ValuesExpander.Expanded += (_, _) => BuildValues();
        ValuesExpander.Collapsed += (_, _) => _values.ItemsSource = null;
        _metricPicker.SelectionChanged += (_, _) =>
        {
            if (_binding || _metricPicker.SelectedItem is not MetricChoice choice) return;
            _metric = choice.Metric;
            Present();
        };
    }

    public void Bind(CodexAccountView? account, CodexQuotaWindow? window, bool hidden = false)
    {
        var history = account?.Observations;
        var loading = account?.ObservationHistoryLoading == true;
        var unavailable = account?.ObservationStorageUnavailable == true;
        var profileId = account?.Profile.Id;
        if (_language == UiText.Language && ReferenceEquals(_history, history) && Equals(_window, window)
            && _profileId == profileId && _loading == loading && _unavailable == unavailable && _hidden == hidden) return;
        if (_profileId != profileId || _history?.Context != history?.Context)
        {
            _metric = QuotaObservationMetric.UsedPercent;
            ValuesExpander.IsExpanded = false;
        }
        _profileId = profileId;
        _history = history;
        _window = window;
        _language = UiText.Language;
        _loading = loading;
        _unavailable = unavailable;
        _hidden = hidden;
        Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (hidden)
        {
            Chart.SetData(null);
            _rendered = null;
            _values.ItemsSource = null;
            return;
        }
        if (_compact)
        {
            // The widget has no metric selector. Keep its represented metric without
            // constructing unused metric views or resetting a collapsed ItemsControl.
            if (!HasMetric(_metric))
                _metric = HasMetric(QuotaObservationMetric.UsedPercent) ? QuotaObservationMetric.UsedPercent
                    : HasMetric(QuotaObservationMetric.UsedAmount) ? QuotaObservationMetric.UsedAmount
                    : HasMetric(QuotaObservationMetric.RemainingAmount) ? QuotaObservationMetric.RemainingAmount
                    : QuotaObservationMetric.UsedPercent;
        }
        else
        {
            UpdateMetricPicker();
            ValuesExpander.Header = UiText.T("Actual observations", "실제 관측값");
            AutomationProperties.SetName(_values, UiText.T("Actual observation time, received time and value", "실제 관측 시각, 앱 수신 시각과 값"));
        }
        Present();
    }

    private void UpdateMetricPicker()
    {
        var mask = 0;
        foreach (var metric in Enum.GetValues<QuotaObservationMetric>())
            if (HasMetric(metric)) mask |= 1 << (int)metric;
        if (mask == 0) mask = 1 << (int)QuotaObservationMetric.UsedPercent;
        var labelUnit = mask == (1 << (int)QuotaObservationMetric.UsedPercent) || string.IsNullOrWhiteSpace(_window?.Unit)
            ? null : _window?.Unit;
        if (_metricChoices is null || _metricChoicesMask != mask
            || _metricChoicesLanguage != UiText.Language || _metricChoicesUnit != labelUnit)
        {
            _metricChoices = Enum.GetValues<QuotaObservationMetric>()
                .Where(metric => (mask & (1 << (int)metric)) != 0)
                .Select(metric => new MetricChoice(metric, MetricLabel(metric))).ToList();
            _metricChoicesMask = mask;
            _metricChoicesLanguage = UiText.Language;
            _metricChoicesUnit = labelUnit;
        }
        var selected = _metricChoices.FirstOrDefault(choice => choice.Metric == _metric) ?? _metricChoices[0];
        _metric = selected.Metric;
        _binding = true;
        try
        {
            if (!ReferenceEquals(_metricPicker.ItemsSource, _metricChoices)) _metricPicker.ItemsSource = _metricChoices;
            _metricPicker.SelectedItem = selected;
            _metricPicker.Visibility = _metricChoices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _binding = false; }
        AutomationProperties.SetName(_metricPicker, UiText.T("Observed metric", "관측 항목"));
    }

    private bool HasMetric(QuotaObservationMetric metric) => _window is not null &&
        ((metric switch
        {
            QuotaObservationMetric.UsedPercent => _window.UsedPercent is not null,
            QuotaObservationMetric.UsedAmount => _window.UsedAmount is not null,
            _ => _window.RemainingAmount is not null
        }) || _history?.GetCurrentWindow(_window, metric).Points.IsEmpty == false);

    private string MetricLabel(QuotaObservationMetric metric)
    {
        var unit = metric == QuotaObservationMetric.UsedPercent ? "%" : _window?.Unit;
        var label = metric == QuotaObservationMetric.RemainingAmount ? UiText.T("Remaining", "잔여") : UiText.T("Used", "사용");
        return string.IsNullOrWhiteSpace(unit) ? label : $"{label} ({unit})";
    }

    private void Present()
    {
        var data = _window is null ? null : _history?.GetCurrentWindow(_window, _metric);
        var changed = !ReferenceEquals(_rendered, data);
        _rendered = data;
        Chart.SetData(data);
        var count = data?.Points.Length ?? 0;
        TitleText.Text = UiText.T("Observed usage", "관측 추이") + " · " + MetricLabel(_metric);
        EmptyText.Text = _loading ? UiText.T("Loading observations…", "관측 기록을 불러오는 중…")
            : UiText.T("No observations yet", "관측 기록이 아직 없어요");
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Chart.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ValuesExpander.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageNotice.Text = UiText.T("Local history could not be saved or fully restored.", "로컬 기록을 저장하거나 완전히 복원하지 못했습니다.");
        StorageNotice.Visibility = _unavailable ? Visibility.Visible : Visibility.Collapsed;
        LastObservationText.Text = count == 0 ? "" : UiText.T("Last observed ", "마지막 관측 ") + DisplayTime(data!.LastObservedAt!.Value, _compact);
        LastObservationText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var explanation = UiText.T("Only actual observations in the current allowance window. Gaps are not connected.",
            "현재 한도 구간의 실제 관측값입니다. 알 수 없는 구간은 연결하지 않습니다.");
        if (count > 0)
            explanation += Environment.NewLine + UiText.T("Observed: ", "원본 관측: ") + DisplayTime(data!.LastObservedAt!.Value)
                + Environment.NewLine + UiText.T("App received: ", "앱 수신: ") + DisplayTime(data.LastReceivedAt!.Value);
        ToolTip = explanation;
        LastObservationText.ToolTip = explanation;
        AutomationProperties.SetName(Chart, TitleText.Text + " · " + LastObservationText.Text);
        AutomationProperties.SetHelpText(Chart, explanation);
        _range.Text = count == 0 ? "" : _metric == QuotaObservationMetric.UsedPercent ? "0–100%"
            : $"0–{Math.Max(1, data!.Points.Max(point => point.Value)).ToString("G29", CultureInfo.CurrentCulture)} {data.Unit}";
        if (count > 1 && !_compact)
            _range.Text += " · " + DisplayTime(data!.Points[0].ObservedAt, true) + " – " + DisplayTime(data.Points[^1].ObservedAt, true);
        _range.Visibility = !_compact && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ValuesExpander.IsExpanded) BuildValues();
        else if (changed) _values.ItemsSource = null;
    }

    private void BuildValues()
    {
        ValuesBuildCount++;
        _values.ItemsSource = _rendered?.Points.Reverse().Select(point =>
            point.Value.ToString("G29", CultureInfo.CurrentCulture) + " " + _rendered.Unit
            + Environment.NewLine + UiText.T("Observed: ", "원본 관측: ") + DisplayTime(point.ObservedAt)
            + Environment.NewLine + UiText.T("App received: ", "앱 수신: ") + DisplayTime(point.ReceivedAt)).ToArray();
    }

    private static string DisplayTime(DateTimeOffset value, bool compact = false) =>
        value.ToLocalTime().ToString(compact ? "MM-dd HH:mm" : "MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
    private sealed record MetricChoice(QuotaObservationMetric Metric, string Label)
    {
        // The shared ComboBox selection template presents SelectionBoxItem directly.
        public override string ToString() => Label;
    }
}
