using System.Windows.Controls;
using System.Windows.Media.Animation;
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
    private readonly RotateTransform _statusRotation = new();
    private AnimationClock? _statusClock;
    private readonly StackPanel _content = new() { VerticalAlignment = VerticalAlignment.Top };

    // Selection is a short accent pill on a slightly lifted surface. The surface and its neutral
    // border mark the area; the accent stays this small so it never reads as a divider or as
    // one of the blue usage bars. It sits in the left padding, 1 DIP inside the border.
    internal const double SelectionPillWidth = 3;
    internal const double SelectionPillHeight = 22;
    private const double SurfaceBorder = 1;
    private static readonly Thickness SurfacePadding = new(10, 7, 9, 7);
    private readonly Border _selectionPill = new()
    {
        Width = SelectionPillWidth, Height = SelectionPillHeight, CornerRadius = new CornerRadius(1.5),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(-SurfacePadding.Left + SurfaceBorder, 0, 0, 0),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };

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
        LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Visibility = Visibility.Collapsed
    };

    public TextBlock StatusAgeText { get; } = new()
    {
        FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed
    };
    // Status belongs to the existing identity line. Its tooltip can grow independently
    // of the widget; there is no footer or reserved vertical space in a healthy module.
    public Grid StatusArea { get; } = new()
    {
        Width = 14, Height = 14, Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center, Background = System.Windows.Media.Brushes.Transparent,
        Visibility = Visibility.Collapsed
    };
    public System.Windows.Controls.ToolTip StatusTooltip { get; } = new() { MaxWidth = 320 };
    public TextBlock StatusDetailText { get; } = new()
    {
        FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 292,
        Margin = new Thickness(0, 4, 0, 0)
    };
    public Path StatusWarningIcon { get; } = new()
    {
        Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.35,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M6,1 L11,10 L1,10 Z M6,4 L6,6.5 M6,8 L6,8.3"),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
    };
    public Path StatusActivityIcon { get; } = new()
    {
        Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M6,1 A5,5 0 1 1 1,6"),
        RenderTransformOrigin = new System.Windows.Point(0.5, 0.5), Visibility = Visibility.Collapsed
    };
    public Path StatusInfoIcon { get; } = new()
    {
        Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.35,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M6,1 A5,5 0 1 1 6,11 A5,5 0 1 1 6,1 M6,5 L6,8 M6,3 L6,3.2"),
        Visibility = Visibility.Collapsed
    };

    public UsageProviderBadge Badge => _badge;
    public string ProfileId { get; private set; } = "";
    public WidgetAccountModel? Model { get; private set; }
    public ObservedTrendView? Trend { get; private set; }

    /// The period lines currently shown. Cursor keeps its named allowance priority order.
    public IReadOnlyList<WidgetPeriodLineView> Periods { get; private set; } = [];

    public WidgetAccountModuleView()
    {
        Tag = "WidgetAccountModule";
        Width = WidgetGridLayout.ModuleWidth;
        // Border plus padding keeps the earlier 11/10 horizontal and 8 vertical insets, so
        // selecting an account never moves its content or changes the module size.
        Padding = SurfacePadding;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(SurfaceBorder);
        Focusable = false;
        _selectionPill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        ApplySurface();

        NameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        RingValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        RingUsedLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        RingTargetText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        StatusAgeText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        StatusDetailText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        StatusWarningIcon.SetResourceReference(Shape.StrokeProperty, "StaleBrush");
        StatusActivityIcon.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        StatusInfoIcon.SetResourceReference(Shape.StrokeProperty, "MutedBrush");
        StatusActivityIcon.RenderTransform = _statusRotation;
        Loaded += (_, _) => UpdateStatusAnimation();
        // A hidden widget is not rebound, so its rotation stops here and resumes when shown.
        IsVisibleChanged += (_, _) => UpdateStatusAnimation();
        Unloaded += (_, _) =>
        {
            UpdateStatusAnimation();
            StatusTooltip.IsOpen = false;
        };
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
        DockPanel.SetDock(StatusArea, Dock.Right);
        _badge.Margin = new Thickness(6, 0, 0, 0);
        identity.Children.Add(_avatarHost);
        identity.Children.Add(_badge);
        identity.Children.Add(StatusArea);
        identity.Children.Add(NameText);

        var body = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_ringHost);
        _periodPanel.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(_periodPanel, 1);
        body.Children.Add(_periodPanel);

        var content = _content;
        content.Children.Add(identity);
        content.Children.Add(body);
        StatusArea.Children.Add(StatusWarningIcon);
        StatusArea.Children.Add(StatusActivityIcon);
        StatusArea.Children.Add(StatusInfoIcon);
        var statusDetails = new StackPanel();
        statusDetails.Children.Add(StatusText);
        statusDetails.Children.Add(StatusAgeText);
        statusDetails.Children.Add(StatusDetailText);
        StatusTooltip.Content = statusDetails;
        StatusArea.ToolTip = StatusTooltip;
        ToolTipService.SetInitialShowDelay(StatusArea, 150);
        var surface = new Grid();
        surface.Children.Add(content);
        surface.Children.Add(_selectionPill);
        Child = surface;
    }

    public bool IsSelected { get; private set; }
    internal FrameworkElement SelectionPill => _selectionPill;

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        ApplySurface();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ApplySurface();
    }

    // Rest is the widget ground; hover and selected each lift it one small step. The neutral
    // border belongs to the selected surface only, and the pill never changes size on hover.
    private void ApplySurface()
    {
        var hover = IsMouseOver;
        if (IsSelected || hover)
            SetResourceReference(BackgroundProperty, IsSelected
                ? hover ? "WidgetModuleSelectedHoverBrush" : "WidgetModuleSelectedBrush"
                : "WidgetModuleHoverBrush");
        // Transparent, not null, so the whole module still takes clicks and drags.
        else Background = System.Windows.Media.Brushes.Transparent;
        if (IsSelected) SetResourceReference(BorderBrushProperty, "LineBrush");
        else BorderBrush = System.Windows.Media.Brushes.Transparent;
        _selectionPill.Visibility = IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Bind(CodexAccountView account, WidgetAccountModel model)
    {
        Model = model;
        ProfileId = model.ProfileId;
        var avatar = AccountSummary.AvatarFor(account, 22, _avatarHost.Child);
        if (!ReferenceEquals(_avatarHost.Child, avatar)) _avatarHost.Child = avatar;
        _badge.Provider = model.Provider;
        NameText.Text = model.DisplayName;
        // The module width is fixed, so a long nickname is trimmed here and stays whole in the tooltip.
        NameText.ToolTip = model.DisplayName;

        ApplyRing(model);
        ApplyPeriods(model);

        StatusText.Text = model.StatusText;
        StatusText.Visibility = model.ShowStatusRow ? Visibility.Visible : Visibility.Collapsed;
        StatusArea.Visibility = model.ShowStatusRow ? Visibility.Visible : Visibility.Collapsed;
        var status = model.StatusPresentation;
        var warning = status?.IsWarning ?? model.IsStale;
        StatusAgeText.Text = status?.AgeText ?? "";
        StatusAgeText.Visibility = model.ShowStatusRow && !string.IsNullOrEmpty(StatusAgeText.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        StatusWarningIcon.Visibility = model.ShowStatusRow && warning ? Visibility.Visible : Visibility.Collapsed;
        var active = model.ShowStatusRow && !warning
            && (account.Snapshot.Status == CodexQuotaStatus.Refreshing || account.IsSigningIn);
        StatusActivityIcon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusInfoIcon.Visibility = model.ShowStatusRow && !warning && !active ? Visibility.Visible : Visibility.Collapsed;
        var details = status?.DetailText ?? model.Tooltip;
        StatusDetailText.Text = string.Join(Environment.NewLine, details.Split(Environment.NewLine)
            .Where(line => line != model.StatusText));
        StatusText.ToolTip = details;
        StatusAgeText.ToolTip = details;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, status?.BrushKey ?? (warning ? "StaleBrush" : "MutedBrush"));
        StatusText.FontWeight = warning ? FontWeights.SemiBold : FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetName(StatusArea, model.StatusText);
        System.Windows.Automation.AutomationProperties.SetHelpText(StatusArea, details);
        UpdateStatusAnimation();
        if (!model.ShowStatusRow) StatusTooltip.IsOpen = false;

        IsSelected = model.IsSelected;
        if (IsSelected)
        {
            if (Trend is null) { Trend = new(compact: true); _content.Children.Add(Trend); }
            Trend.Bind(account, model.Ring.Window, WidgetStatusPresentation.HidesQuota(account.Snapshot));
        }
        else Trend?.Bind(null, null, hidden: true);
        ApplySurface();

        ToolTip = model.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(this, AutomationText(model));
        System.Windows.Automation.AutomationProperties.SetHelpText(this, status?.DetailText ?? model.Tooltip);

        // BindAccounts measures immediately, before WPF propagates quota-row changes.
        ((FrameworkElement)Child).InvalidateMeasure();
        InvalidateMeasure();
    }

    private void UpdateStatusAnimation()
    {
        if (IsLoaded && IsVisible && StatusActivityIcon.Visibility == Visibility.Visible)
        {
            if (_statusClock is null)
            {
                _statusClock = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock();
                _statusRotation.ApplyAnimationClock(RotateTransform.AngleProperty, _statusClock);
            }
        }
        else
        {
            FlyoutWindow.StopClock(ref _statusClock);
            _statusRotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);
            _statusRotation.Angle = 0;
        }
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
            UsageRingBands.WithLabel(model.Ring.RemainingSubLabel.Replace(Environment.NewLine, " ")
                + " " + model.Ring.RemainingValueText, model.Ring)
        }.Concat(periods).Append(model.StatusText).Append(model.StatusPresentation?.AgeText)
            .Where(part => !string.IsNullOrEmpty(part)))
            + (model.IsSelected ? UiText.T(" · Selected", " · 선택됨") : "");
    }

    private void ApplyRing(WidgetAccountModel model)
    {
        var ring = model.Ring;
        // The ring fills with what is left of the represented limit, so its value is the
        // widget-precision remainder. An exhausted limit names itself instead of an empty ring.
        RingValueText.Text = model.RingRemainingValueText;
        RingUsedLabel.Text = ring.IsExhausted ? UsageRingBands.Label(UsageRingBand.Exhausted) : UiText.CodexLegendRemaining;
        RingUsedLabel.SetResourceReference(TextBlock.ForegroundProperty, ring.IsExhausted ? "RingExhaustedBrush" : "MutedBrush");
        RingUsedLabel.FontWeight = ring.IsExhausted ? FontWeights.SemiBold : FontWeights.Normal;
        // The short "Left" caption can be read at 10; the longer limit-reached label keeps the
        // smaller size that fits inside the ring.
        RingUsedLabel.FontSize = ring.IsExhausted ? 8.5 : 10;
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
        // The tooltip keeps both precise values: what is left (the fill) and what was used.
        _ringHost.ToolTip = UsageRingBands.WithLabel(
            ring.RemainingSubLabel.Replace(Environment.NewLine, " ") + " " + ring.RemainingValueText
                + " · " + UiText.CodexLegendUsed + " " + ring.CenterValueText, ring);
        // Quota colors describe the last received values. Freshness has its own header icon.
        RingValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var arcBrushKey = UsageRingBands.ArcBrushKey(ring.Band);
        _ringArc.SetResourceReference(Shape.StrokeProperty, arcBrushKey);
        _ringFull.SetResourceReference(Shape.StrokeProperty, arcBrushKey);
        _ringTrack.SetResourceReference(Shape.StrokeProperty, ring.IsAvailable ? "LineBrush" : "DisabledBrush");

        var arc = RingGeometry.ComputeFillArc(ring.RemainingPercent, RingCenter, RingCenter, RingRadius);
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
            Periods[i].Bind(line, isCursor, startsGroup);
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
    // A limit that is not in the ring gets a small remaining bar in the otherwise empty
    // space left of its reset countdown, so the line keeps its height.
    private readonly Grid _foot = new();
    // One length everywhere, so a bar beside a countdown and a bar on its own row read as the
    // same mark; the fill inside it is the remainder. Its column gives way to a long countdown
    // instead of clipping the bar, so the fill always stays proportional.
    internal const double PeriodBarWidth = 56;
    private readonly Grid _bar = new() { Height = 4, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 6, 0), Tag = "WidgetPeriodBar", Visibility = Visibility.Collapsed };
    private readonly Border _barFill = new() { CornerRadius = new CornerRadius(2) };

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
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        Grid.SetColumnSpan(track, 2);
        _bar.ColumnDefinitions.Add(new ColumnDefinition());
        _bar.ColumnDefinitions.Add(new ColumnDefinition());
        _bar.Children.Add(track);
        _bar.Children.Add(_barFill);
        // [bar, at most its fixed length] [spare space] [countdown, right-aligned as before]
        // The heavy weight lets the bar column fill up to its cap before the spare column gets any.
        _foot.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1000, GridUnitType.Star), MaxWidth = PeriodBarWidth + 18
        });
        _foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _foot.Children.Add(_bar);
        Grid.SetColumn(ResetText, 2);
        _foot.Children.Add(ResetText);
        Children.Add(CadenceText);
        Children.Add(_head);
        Children.Add(_foot);
    }

    public void Bind(WidgetPeriodLine line, bool isCursor = false, bool startsGroup = false)
    {
        PeriodText.Text = line.PeriodLabel;
        // Match the named allowance to the ring visually as well as in its tooltip.
        // Other providers retain their existing period typography.
        PeriodText.FontWeight = isCursor && line.IsRepresentative ? FontWeights.SemiBold : FontWeights.Normal;
        PeriodText.SetResourceReference(TextBlock.ForegroundProperty,
            isCursor && line.IsRepresentative ? "AccentBrush" : "MutedBrush");
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
        RemainingText.Text = line.DisplayRemainingText;
        RemainingText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        ResetText.Text = line.ResetText;
        ResetText.Visibility = string.IsNullOrEmpty(line.ResetText) ? Visibility.Collapsed : Visibility.Visible;
        // Every limit that is not in the ring gets the same bar when its remainder is known.
        // A missing reset time only removes the countdown text; the bar row then stands alone.
        var showBar = !line.IsRepresentative && line.RemainingPercent is not null;
        _bar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        // Beside a countdown the bar shares its row; alone it takes a thin row under the value.
        _bar.Margin = ResetText.Visibility == Visibility.Visible ? new Thickness(12, 0, 6, 0) : new Thickness(12, 2, 6, 0);
        if (showBar)
        {
            var left = line.RemainingPercent!.Value;
            _bar.ColumnDefinitions[0].Width = new GridLength(left, GridUnitType.Star);
            _bar.ColumnDefinitions[1].Width = new GridLength(100 - left, GridUnitType.Star);
            _barFill.SetResourceReference(Border.BackgroundProperty, line.BarBrushKey);
        }
        // The countdown stays readable; the exact local reset time is one hover away.
        ResetText.ToolTip = line.ResetTooltip is null ? null : UiText.WidgetReset + " " + line.ResetTooltip;
        // Reserve the marker gutter so both period names start in the same column.
        _representative.Visibility = line.IsRepresentative ? Visibility.Visible : Visibility.Hidden;
        _representative.ToolTip = line.IsRepresentative
            ? UiText.T("Shown in the ring", "링에 표시되는 기간") : null;
    }
}
