using System.Windows.Automation.Peers;
using System.Windows.Media;
using CycleArc.Observations;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

namespace CycleArc.UI;

/// <summary>Draws only persisted observations. Gaps and window boundaries have no joining edge.</summary>
public sealed class QuotaSparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(QuotaSparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridStrokeProperty = DependencyProperty.Register(
        nameof(GridStroke), typeof(Brush), typeof(QuotaSparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private QuotaObservationWindow? _data;
    private QuotaObservationWindow? _preparedData;
    private Size _preparedSize = Size.Empty;
    private StreamGeometry? _connections;
    private StreamGeometry? _markers;

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridStroke { get => (Brush)GetValue(GridStrokeProperty); set => SetValue(GridStrokeProperty, value); }
    public QuotaObservationWindow? Data => _data;
    public int GeometryBuildCount { get; private set; }
    public int RenderedPointCount { get; private set; }
    public int RenderedConnectionCount { get; private set; }
    public decimal MinimumValue { get; private set; }
    public decimal MaximumValue { get; private set; }

    public QuotaSparkline()
    {
        Height = 52;
        MinWidth = 80;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SetResourceReference(StrokeProperty, "AccentBrush");
        SetResourceReference(GridStrokeProperty, "LineBrush");
    }

    public void SetData(QuotaObservationWindow? data)
    {
        if (ReferenceEquals(_data, data)) return;
        _data = data;
        if (data is null)
        {
            // Hidden/reconnected views must not retain the previous account's geometry or samples.
            _preparedData = null;
            _preparedSize = Size.Empty;
            _connections = _markers = null;
            RenderedPointCount = RenderedConnectionCount = 0;
        }
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SparklinePeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_preparedSize != RenderSize || !ReferenceEquals(_preparedData, _data)) PrepareGeometry();
        if (RenderedPointCount == 0) return;

        var bounds = new Rect(RenderSize);
        drawingContext.PushClip(new RectangleGeometry(bounds));
        var gridPen = new Pen(GridStroke, 0.6);
        drawingContext.DrawLine(gridPen, new Point(4, 4), new Point(Math.Max(4, ActualWidth - 4), 4));
        drawingContext.DrawLine(gridPen, new Point(4, Math.Max(4, ActualHeight - 4)),
            new Point(Math.Max(4, ActualWidth - 4), Math.Max(4, ActualHeight - 4)));
        if (_connections is not null) drawingContext.DrawGeometry(null, new Pen(Stroke, 1.4), _connections);
        if (_markers is not null) drawingContext.DrawGeometry(Stroke, null, _markers);
        drawingContext.Pop();
    }

    private void PrepareGeometry()
    {
        GeometryBuildCount++;
        _preparedData = _data;
        _preparedSize = RenderSize;
        _connections = null;
        _markers = null;
        RenderedPointCount = 0;
        RenderedConnectionCount = 0;
        MinimumValue = 0;
        MaximumValue = 100;
        if (_data is null || _data.Points.IsEmpty || ActualWidth <= 8 || ActualHeight <= 8) return;

        var points = _data.Points;
        if (_data.Metric != QuotaObservationMetric.UsedPercent)
        {
            MinimumValue = Math.Min(0, points.Min(point => point.Value));
            MaximumValue = Math.Max(0, points.Max(point => point.Value));
            if (MaximumValue == MinimumValue) MaximumValue = MinimumValue + 1;
        }
        var firstTime = points[0].ObservedAt.UtcTicks;
        var timeSpan = points[^1].ObservedAt.UtcTicks - firstTime;
        var width = ActualWidth - 8;
        var height = ActualHeight - 8;
        var valueSpan = (double)(MaximumValue - MinimumValue);
        Point Position(QuotaObservationPoint point) => new(
            4 + (timeSpan == 0 ? width / 2 : width * (point.ObservedAt.UtcTicks - firstTime) / timeSpan),
            4 + height * (1 - Math.Clamp((double)(point.Value - MinimumValue) / valueSpan, 0, 1)));

        var connections = new StreamGeometry();
        var markers = new StreamGeometry();
        using (var lines = connections.Open())
        using (var dots = markers.Open())
        {
            Point previous = default;
            QuotaObservationPoint? previousObservation = null;
            foreach (var observation in points)
            {
                var point = Position(observation);
                if (previousObservation is not null && observation.CanConnect && previousObservation.CanConnect
                    && observation.SegmentId == previousObservation.SegmentId
                    && observation.ObservedAt > previousObservation.ObservedAt)
                {
                    lines.BeginFigure(previous, isFilled: false, isClosed: false);
                    lines.LineTo(point, isStroked: true, isSmoothJoin: false);
                    RenderedConnectionCount++;
                }

                // A single sample is one actual dot, with no extrapolation or estimated span.
                const double radius = 2.15;
                dots.BeginFigure(new Point(point.X - radius, point.Y), isFilled: true, isClosed: true);
                dots.ArcTo(new Point(point.X + radius, point.Y), new Size(radius, radius), 0, false,
                    SweepDirection.Clockwise, true, false);
                dots.ArcTo(new Point(point.X - radius, point.Y), new Size(radius, radius), 0, false,
                    SweepDirection.Clockwise, true, false);
                previous = point;
                previousObservation = observation;
                RenderedPointCount++;
            }
        }
        connections.Freeze();
        markers.Freeze();
        _connections = connections;
        _markers = markers;
    }

    private sealed class SparklinePeer(QuotaSparkline owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(QuotaSparkline);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
