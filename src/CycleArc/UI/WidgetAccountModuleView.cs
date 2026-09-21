using System.Windows.Controls;
using System.Windows.Shapes;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;

namespace CycleArc.UI;

/// <summary>
/// One account inside the floating widget: identity, a small usage ring for the represented
/// period, and the provider's compact period summary. Built once and rebound in
/// place, so a new sample repaints text instead of recreating the window.
/// </summary>
public sealed class WidgetAccountModuleView : Border
{
    // Two 30-DIP period lines and their 4-DIP gap share the ring's vertical extent.
    internal const double RingDiameter = 64;
    private const double RingStroke = 6;
    private const double RingRadius = (RingDiameter - RingStroke) / 2;
    private const double RingCenter = RingDiameter / 2;

    private readonly Border _avatarHost = new() { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly UsageProviderBadge _badge = new();
    private readonly Ellipse _ringTrack = new() { Width = RingDiameter, Height = RingDiameter, StrokeThickness = RingStroke };
    private readonly Ellipse _ringFull = new() { Width = RingDiameter, Height = RingDiameter, StrokeThickness = RingStroke, Visibility = Visibility.Collapsed };
    private readonly Path _ringArc = new() { StrokeThickness = RingStroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
    private readonly PathFigure _ringFigure = new() { IsClosed = false };
    private readonly ArcSegment _ringSegment = new() { SweepDirection = SweepDirection.Clockwise };
    private readonly StackPanel _periodPanel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _ringHost = new() { Width = RingDiameter, Height = RingDiameter, VerticalAlignment = VerticalAlignment.Top };

    public TextBlock NameText { get; } = new()
    {
        FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center
    };

    public TextBlock RingValueText { get; } = new()
    {
        FontSize = 15, FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    public TextBlock RingUsedLabel { get; } = new()
    {
        FontSize = 8.5, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
    };

    public TextBlock RingTargetText { get; } = new()
    {
        FontSize = 10.5, LineHeight = 13, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed
    };

    public TextBlock StatusText { get; } = new()
    {
        FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed
    };

    public UsageProviderBadge Badge => _badge;
    public string ProfileId { get; private set; } = "";
    public WidgetAccountModel? Model { get; private set; }

    /// The period lines currently shown. Cursor keeps its named allowance priority order.
    public IReadOnlyList<WidgetPeriodLineView> Periods { get; private set; } = [];

    public WidgetAccountModuleView()
    {
        Tag = "WidgetAccountModule";
        Width = WidgetGridLayout.ModuleWidth;
        Padding = new Thickness(9, 8, 10, 8);
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(2, 0, 0, 0);
        BorderBrush = System.Windows.Media.Brushes.Transparent;
        Focusable = false;

        NameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        RingValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        RingUsedLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        RingTargetText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _ringTrack.SetResourceReference(Shape.StrokeProperty, "LineBrush");

        _ringArc.Data = new PathGeometry { Figures = { _ringFigure } };
        _ringFigure.Segments.Add(_ringSegment);
        var ringCentre = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ringCentre.Children.Add(RingTargetText);
        ringCentre.Children.Add(RingValueText);
        ringCentre.Children.Add(RingUsedLabel);
        _ringHost.Children.Add(_ringTrack);
        _ringHost.Children.Add(_ringArc);
        _ringHost.Children.Add(_ringFull);
        _ringHost.Children.Add(ringCentre);

        var identity = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_avatarHost, Dock.Left);
        DockPanel.SetDock(_badge, Dock.Right);
        _badge.Margin = new Thickness(6, 0, 0, 0);
        identity.Children.Add(_avatarHost);
        identity.Children.Add(_badge);
        identity.Children.Add(NameText);

        var body = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_ringHost);
        _periodPanel.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(_periodPanel, 1);
        body.Children.Add(_periodPanel);

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(identity);
        content.Children.Add(body);
        content.Children.Add(StatusText);
        Child = content;
    }

    public void Bind(CodexAccountView account, WidgetAccountModel model)
    {
        Model = model;
        ProfileId = model.ProfileId;
        _avatarHost.Child = AccountSummary.Avatar(account, 22);
        _badge.Provider = model.Provider;
        NameText.Text = model.DisplayName;
        // The module width is fixed, so a long nickname is trimmed here and stays whole in the tooltip.
        NameText.ToolTip = model.DisplayName;

        ApplyRing(model);
        ApplyPeriods(model);

        StatusText.Text = model.StatusText;
        StatusText.Visibility = string.IsNullOrEmpty(model.StatusText) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.ToolTip = CursorUsagePresentation.IsCursor(model.Provider) ? model.Tooltip
            : string.IsNullOrEmpty(model.StatusText) ? null : model.StatusText;
        StatusText.TextWrapping = CursorUsagePresentation.IsCursor(model.Provider)
            ? TextWrapping.Wrap : TextWrapping.NoWrap;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, model.IsStale ? "StaleBrush" : "MutedBrush");
        StatusText.FontWeight = model.IsStale ? FontWeights.SemiBold : FontWeights.Normal;

        if (model.IsSelected) SetResourceReference(BorderBrushProperty, "AccentBrush");
        else BorderBrush = System.Windows.Media.Brushes.Transparent;

        ToolTip = model.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(this, AutomationText(model));

        // BindAccounts measures immediately, before WPF propagates a status-line size change.
        ((FrameworkElement)Child).InvalidateMeasure();
        InvalidateMeasure();
    }

    private static string AutomationText(WidgetAccountModel model)
    {
        var periods = model.Periods.Select(period =>
            $"{period.PeriodLabel} {period.CadenceLabel} {period.RemainingText}"
            + (string.IsNullOrEmpty(period.ResetText) ? "" : $" · {UiText.WidgetReset} {period.ResetText}"));
        return string.Join(" · ", new[]
        {
            model.DisplayName,
            model.Provider.Name(),
            model.Ring.CenterSubLabel + " " + model.Ring.CenterValueText
        }.Concat(periods).Append(model.StatusText)
            .Where(part => !string.IsNullOrEmpty(part)))
            + (model.IsSelected ? UiText.T(" · Selected", " · 선택됨") : "");
    }

    private void ApplyRing(WidgetAccountModel model)
    {
        var ring = model.Ring;
        RingValueText.Text = ring.CenterValueText;
        // The ring fills with usage, so the value inside it is usage; remaining is on the lines.
        RingUsedLabel.Text = UiText.CodexLegendUsed;
        // Only the ring's repeated caption is shortened. The adjacent allowance rows,
        // cadence headings, tooltip and accessible name keep the complete quota name.
        RingTargetText.Text = model.RingTargetLabel switch
        {
            "Cursor Models" => "Cursor",
            "Other Models" => "Other",
            var target => target ?? ""
        };
        RingTargetText.Visibility = string.IsNullOrEmpty(model.RingTargetLabel) ? Visibility.Collapsed : Visibility.Visible;
        RingTargetText.ToolTip = ring.CenterSubLabel;
        _ringHost.ToolTip = ring.CenterSubLabel + " " + ring.CenterValueText;
        RingValueText.SetResourceReference(TextBlock.ForegroundProperty, model.IsStale ? "StaleBrush" : "TextBrush");
        var arcBrushKey = model.IsStale ? "StaleBrush" : ring.IsDangerLevel ? "DangerBrush" : "AccentBrush";
        _ringArc.SetResourceReference(Shape.StrokeProperty, arcBrushKey);
        _ringFull.SetResourceReference(Shape.StrokeProperty, arcBrushKey);
        _ringTrack.SetResourceReference(Shape.StrokeProperty, ring.IsAvailable ? "LineBrush" : "DisabledBrush");

        var arc = RingGeometry.ComputeUsedArc(ring.UsedPercent, RingCenter, RingCenter, RingRadius);
        _ringArc.Visibility = arc.Visible ? Visibility.Visible : Visibility.Collapsed;
        _ringFull.Visibility = arc.IsFullCircle ? Visibility.Visible : Visibility.Collapsed;
        if (!arc.Visible) return;
        // Qualified: the desktop project also imports WinForms, where Point and Size differ.
        _ringFigure.StartPoint = new System.Windows.Point(arc.Start.X, arc.Start.Y);
        _ringSegment.Point = new System.Windows.Point(arc.End.X, arc.End.Y);
        _ringSegment.Size = new System.Windows.Size(RingRadius, RingRadius);
        _ringSegment.IsLargeArc = arc.IsLargeArc;
    }

    private void ApplyPeriods(WidgetAccountModel model)
    {
        // Rebuild only when the period shape changes; a countdown tick just rewrites text.
        if (Periods.Count != model.Periods.Count)
        {
            _periodPanel.Children.Clear();
            var rebuilt = new List<WidgetPeriodLineView>(model.Periods.Count);
            foreach (var _ in model.Periods)
            {
                var line = new WidgetPeriodLineView { Margin = new Thickness(0, rebuilt.Count == 0 ? 0 : 4, 0, 0) };
                _periodPanel.Children.Add(line);
                rebuilt.Add(line);
            }
            Periods = rebuilt;
        }
        var isCursor = CursorUsagePresentation.IsCursor(model.Provider);
        for (var i = 0; i < model.Periods.Count; i++)
        {
            var line = model.Periods[i];
            var startsGroup = isCursor && (i == 0 || line.CadenceLabel != model.Periods[i - 1].CadenceLabel);
            Periods[i].Margin = new Thickness(0, i == 0 ? 0 : isCursor ? startsGroup ? 4 : 0 : 4, 0, 0);
            Periods[i].Bind(line, model.IsStale, isCursor, startsGroup);
        }
    }
}

/// <summary>One period inside a module: its name, what is left, and when it resets.</summary>
public sealed class WidgetPeriodLineView : StackPanel
{
    public TextBlock CadenceText { get; } = new()
    {
        FontSize = 10.5, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed
    };
    public TextBlock PeriodText { get; } = new()
    {
        FontSize = 12, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    public TextBlock RemainingText { get; } = new()
    {
        FontSize = 12, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    public TextBlock ResetText { get; } = new()
    {
        FontSize = 10.5, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Ellipse _representative = new()
    {
        Width = 4, Height = 4, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Hidden
    };
    private readonly Grid _head = new();
    private readonly DockPanel _label = new() { LastChildFill = true };

    public WidgetPeriodLineView()
    {
        Tag = "WidgetPeriodLine";
        PeriodText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        CadenceText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        ResetText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _representative.SetResourceReference(Shape.FillProperty, "AccentBrush");

        _head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        DockPanel.SetDock(_representative, Dock.Left);
        _label.Children.Add(_representative);
        _label.Children.Add(PeriodText);
        _head.Children.Add(_label);
        Grid.SetColumn(RemainingText, 1);
        _head.Children.Add(RemainingText);
        Children.Add(CadenceText);
        Children.Add(_head);
        Children.Add(ResetText);
    }

    public void Bind(WidgetPeriodLine line, bool stale, bool isCursor = false, bool startsGroup = false)
    {
        PeriodText.Text = line.PeriodLabel;
        // Match the named allowance to the ring visually as well as in its tooltip.
        // Other providers retain their existing period typography.
        PeriodText.FontWeight = isCursor && line.IsRepresentative ? FontWeights.SemiBold : FontWeights.Normal;
        PeriodText.SetResourceReference(TextBlock.ForegroundProperty,
            isCursor && line.IsRepresentative ? stale ? "StaleBrush" : "AccentBrush" : "MutedBrush");
        PeriodText.TextWrapping = isCursor ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PeriodText.TextTrimming = isCursor ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        // Cursor's cadence is shared once above each group. Keep the actual allowance
        // names and values on one row, allowing a long monetary value to wrap the name.
        _head.ColumnDefinitions[0].Width = isCursor ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        _head.ColumnDefinitions[1].Width = isCursor ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        _label.Margin = new Thickness(0, 0, isCursor ? 4 : 0, 0);
        CadenceText.Text = startsGroup ? line.CadenceLabel + UiText.T(" · Left", " · 남음") : "";
        CadenceText.Visibility = startsGroup ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = line.Tooltip;
        RemainingText.Text = line.RemainingText;
        RemainingText.SetResourceReference(TextBlock.ForegroundProperty, stale ? "StaleBrush" : "TextBrush");
        ResetText.Text = line.ResetText;
        ResetText.Visibility = string.IsNullOrEmpty(line.ResetText) ? Visibility.Collapsed : Visibility.Visible;
        // The countdown stays readable; the exact local reset time is one hover away.
        ResetText.ToolTip = line.ResetTooltip is null ? null : UiText.WidgetReset + " " + line.ResetTooltip;
        // Reserve the marker gutter so both period names start in the same column.
        _representative.Visibility = line.IsRepresentative ? Visibility.Visible : Visibility.Hidden;
        _representative.ToolTip = line.IsRepresentative
            ? UiText.T("Shown in the ring", "링에 표시되는 기간") : null;
    }
}
