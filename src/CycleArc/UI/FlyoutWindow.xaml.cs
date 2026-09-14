using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;

namespace CycleArc.UI;

public partial class FlyoutWindow : Window
{
    public event Action? SyncRequested;
    public event Action? AccountsRequested;
    public event Action<string>? AccountSelected;
    public string? SelectedProfileId { get; private set; }
    public Func<string, Task<CreditRedemptionOutcome>>? RedeemCredit { get; set; }
    public Func<string, string, Task<CreditRedemptionOutcome>>? RedeemAccountCredit { get; set; }
    private bool _redeemingCredit;
    private CodexQuotaSnapshot? _creditSnapshot;
    // Injectable only for offline UI tests. Production always asks the user.
    internal Func<string, bool>? ConfirmCreditForTest { get; set; }
    internal Action<string>? OpenExternalForTest { get; set; }
    public event Action? SettingsRequested;
    public event Action<bool>? PinChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<int>? ZoomChanged;
    public int ZoomPercent { get; private set; } = FlyoutZoom.DefaultPercent;
    public bool Pinned { get; private set; }
    private readonly RefreshIndicatorController _refreshIndicator = new();
    private bool _refreshActive;
    private bool _creditsExpanded = true;
    private System.Windows.Controls.ToolTip? _creditHelpTip;

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
        SourceInitialized += (_, _) => FitContentToWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(RefreshWorkArea);
        ToolTipService.SetIsEnabled(CreditHelpButton, false);
        IsVisibleChanged += (_, _) =>
        {
            ApplyRefreshVisuals();
            if (!IsVisible && _creditHelpTip is not null) _creditHelpTip.IsOpen = false;
        };
        Activated += (_, _) => ApplyRefreshVisuals();
        ContentRendered += (_, _) => ApplyRefreshVisuals();
        Closed += (_, _) =>
        {
            _refreshActive = false;
            ApplyRefreshVisuals();
        };
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        SetZoom(settings.FlyoutZoomPercent, notify: false);
        Pinned = settings.FlyoutPinned;
        Topmost = FlyoutWindowState.IsTopmost(Pinned);
        ApplyPinGlyph();
    }

    public void RestorePosition(double left, double top)
    {
        UpdateLayout();
        var height = Math.Max(ActualHeight, 1);
        var work = FlyoutPlacement.SelectWorkArea(left, top, Width, height, EnumerateWorkAreas());
        var clamped = FlyoutPlacement.ClampToWorkArea(left, top, Width, height, work);
        Left = clamped.Left;
        Top = clamped.Top;
    }

    public void Bind(CodexQuotaSnapshot snapshot, bool refreshing = false)
    {
        SelectedAccountHeader.Visibility = SelectedProviderBadge.Visibility = CodexCard.Visibility = Visibility.Visible;
        _creditSnapshot = snapshot;
        _refreshActive = refreshing;
        SelectedProviderBadge.Provider = snapshot.Provider;
        ResetCreditsCard.Visibility = snapshot.Provider == UsageProviderId.Codex ? Visibility.Visible : Visibility.Collapsed;
        ApplyLocalizedTexts();
        StatusText.Text = snapshot.Status == CodexQuotaStatus.Available && snapshot.Provider == UsageProviderId.Codex
            ? UiText.T("Up to date", "정상 작동 중") : CycleArcPresentation.StatusLabel(snapshot);
        StatusDot.Fill = (Brush)FindResource(refreshing ? "AccentBrush" : snapshot.Status == CodexQuotaStatus.Available ? "OkBrush" : "MutedBrush");
        BindCodex(snapshot);
        BindCreditCard(snapshot);
        SetRefreshPresentation(new FlyoutRefreshPresentation(!refreshing, refreshing,
            refreshing ? UiText.CodexRefreshing : ""));
    }

    public void BindAccounts(IReadOnlyList<CodexAccountView> accounts, string selectedId, bool refreshing)
    {
        var overview = UsageAccountOverview.Create(accounts, selectedId);
        accounts = overview.Accounts;
        selectedId = overview.SelectedId;
        SelectedProfileId = selectedId.Length == 0 ? null : selectedId;
        var selected = overview.Selected;
        Bind(selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut), refreshing);
        AccountSection.Visibility = Visibility.Visible;
        ManageAccountsButton.Content = UiText.T("Manage accounts", "계정 관리");
        AccountsHeading.Text = UiText.T($"Accounts · {accounts.Count}", $"계정 · {accounts.Count}");
        AccountOverview.Items.Clear();
        if (accounts.Count > 1)
            foreach (var account in accounts)
                AccountOverview.Items.Add(AccountSummary.Create(account, account.Profile.Id == selectedId,
                    () => { if (!_redeemingCredit) AccountSelected?.Invoke(account.Profile.Id); }));
        AccountSelectionHint.Text = accounts.Count > 1
            ? UiText.T("Select an account for details, tray and widget.", "계정을 선택하면 상세 카드·트레이·위젯에 표시됩니다.")
            : UiText.T("Connect accounts in Manage accounts to show them here.", "계정 관리에서 연결한 계정이 여기에 표시됩니다.");
        AccountSelectionHint.Visibility = accounts.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        SelectedAccountText.Text = selected?.DisplayName ?? UiText.T("Add your first account", "첫 계정을 추가하세요");
        SelectedAccountText.Visibility = Visibility.Visible;
        SelectedAccountText.ToolTip = selected?.Email ?? selected?.DisplayName;
        var failed = accounts.Count(a => a.Snapshot.Status != CodexQuotaStatus.Available && !a.IsAwaitingUsage);
        var waiting = accounts.Count(a => a.IsAwaitingUsage);
        if (accounts.Count > 1 && !refreshing)
            StatusText.Text = failed > 0 ? UiText.T($"{failed} need attention", $"{failed}개 확인 필요")
                : waiting > 0 ? UiText.T($"{waiting} awaiting usage", $"{waiting}개 수신 대기")
                : accounts.Any(a => a.Profile.Provider == UsageProviderId.Claude) ? UiText.T("Samples received", "수신값 표시")
                : UiText.T("All updated", "전체 최신");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            refreshing ? "AccentBrush" : accounts.Count > 0 && failed == 0 && waiting == 0 ? "OkBrush" : "MutedBrush");
        if (selected is null)
        {
            SelectedAccountHeader.Visibility = SelectedProviderBadge.Visibility = CodexCard.Visibility = ResetCreditsCard.Visibility = Visibility.Collapsed;
            StatusText.Text = UiText.T("No usage yet", "사용량 대기");
        }
    }

    private void OnAccountsClick(object sender, RoutedEventArgs e) => AccountsRequested?.Invoke();
    private void OnClaudeUsagePage(object sender, RoutedEventArgs e) => ClaudeUsagePage.Open(this, OpenExternalForTest);

    public void SetRefreshPresentation(FlyoutRefreshPresentation presentation)
    {
        RefreshAllButton.IsEnabled = presentation.Enabled;
        RefreshAllIcon.Stroke = presentation.Active
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextBrush");
        RefreshProgressText.Text = presentation.ProgressText;
        StatusText.Visibility = presentation.ShowNormalStatus ? Visibility.Visible : Visibility.Collapsed;
        RefreshProgressText.Visibility = presentation.ShowRefreshProgress ? Visibility.Visible : Visibility.Collapsed;
        SetRefreshing(presentation.Active);
    }

    public RefreshIndicatorController RefreshIndicator => _refreshIndicator;

    private void SetRefreshing(bool refreshing)
    {
        _refreshActive = refreshing;
        ApplyRefreshVisuals();
    }

    private void ApplyRefreshVisuals()
    {
        var state = FlyoutRefreshVisualState.Create(_refreshActive, IsVisible);
        RefreshAllIcon.Visibility = state.IdleIconVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.Visibility = state.SpinnerVisible ? Visibility.Visible : Visibility.Collapsed;
        if (state.RunAnimation)
        {
            if (_refreshIndicator.Apply(true) == RefreshIndicatorTransition.Started)
            {
                StartRefreshAnimations();
            }
        }
        else if (_refreshIndicator.Reset() == RefreshIndicatorTransition.Stopped)
        {
            StopRefreshAnimations();
        }
    }

    private void StartRefreshAnimations()
    {
        var spinner = LiveSpinnerRotate();
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(RefreshIndicatorController.DurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        spinner.BeginAnimation(RotateTransform.AngleProperty, spin, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopRefreshAnimations()
    {
        var spinner = LiveSpinnerRotate();
        spinner.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Angle = 0;
    }

    private RotateTransform LiveSpinnerRotate()
    {
        if (RefreshSpinner.RenderTransform is RotateTransform current && !current.IsFrozen)
        {
            return current;
        }

        var live = RefreshSpinnerRotate.IsFrozen
            ? (RotateTransform)RefreshSpinnerRotate.Clone()
            : RefreshSpinnerRotate;
        RefreshSpinner.RenderTransform = live;
        RefreshSpinner.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        return live;
    }

    public void ApplyLocalizedTexts()
    {
        Title = UiText.ProductName;
        ResetCreditsTitle.Text = UiText.ResetCredits;
        ApplyCreditExpansion();
        var helpText = UiText.T("Reset credits can renew your Codex usage limits. Select Use reset beside a credit to redeem it after confirmation.", "리셋권으로 Codex 사용 한도를 갱신할 수 있습니다. 리셋권 옆의 초기화 사용을 누르고 확인하면 해당 리셋권을 사용합니다.");
        _creditHelpTip ??= MakeTooltip(helpText);
        _creditHelpTip.Content = helpText;
        CreditHelpButton.ToolTip = _creditHelpTip;
        System.Windows.Automation.AutomationProperties.SetName(CreditHelpButton, UiText.T("Reset credit count and help", "리셋권 보유 수와 안내"));
        SettingsButton.ToolTip = UiText.Settings;
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, UiText.Settings);
        RefreshAllButton.ToolTip = UiText.RefreshAll;
        System.Windows.Automation.AutomationProperties.SetName(RefreshAllButton, UiText.RefreshAll);
        ApplyPinGlyph();
        CloseFlyoutButton.ToolTip = UiText.Close;
        System.Windows.Automation.AutomationProperties.SetName(CloseFlyoutButton, UiText.Close);
    }

    public void PlaceNearTaskbar()
    {
        UpdateLayout();
        var cursor = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
        Left = Math.Max(topLeft.X + 8, bottomRight.X - Width - 12);
        Top = Math.Max(topLeft.Y + 8, bottomRight.Y - ActualHeight - 12);
    }

    public void PlaceNear(Rect anchor, TaskbarEdge edge)
    {
        UpdateLayout();
        var work = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var workTopLeft = fromDevice.Transform(new System.Windows.Point(work.Left, work.Top));
        var workBottomRight = fromDevice.Transform(new System.Windows.Point(work.Right, work.Bottom));
        var (left, top) = FlyoutPlacement.PlaceNear(
            new ScreenRect((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height),
            edge,
            Width,
            ActualHeight,
            new ScreenRect(
                (int)workTopLeft.X,
                (int)workTopLeft.Y,
                (int)(workBottomRight.X - workTopLeft.X),
                (int)(workBottomRight.Y - workTopLeft.Y)));
        Left = left;
        Top = top;
    }

    private const double CodexRingDiameter = 112;
    private const double CodexRingStrokeThickness = 10;
    private const double CodexRingRadius = (CodexRingDiameter - CodexRingStrokeThickness) / 2;
    private const double CodexRingCenter = CodexRingDiameter / 2;

    private void BindCodex(CodexQuotaSnapshot snapshot)
    {
        var stale = ClaudeUsagePresentation.IsStale(snapshot);
        ClaudeUsageHeader.Visibility = ClaudeUsagePageButton.Visibility = snapshot.Provider == UsageProviderId.Claude
            ? Visibility.Visible : Visibility.Collapsed;
        ClaudeUsageTitle.Text = ClaudeUsagePresentation.Title;
        ClaudeUsageScope.Text = ClaudeUsagePresentation.SharedScope;
        ClaudeUsagePageButton.Content = ClaudeUsagePresentation.UsagePageLabel;
        ClaudeUsagePageButton.ToolTip = MakeTooltip(ClaudeUsagePresentation.UsagePageHint);
        CodexStatusText.Text = snapshot.Status == CodexQuotaStatus.Refreshing ? "" : CodexDisplayFormatting.StatusText(snapshot);
        CodexStatusText.SetResourceReference(TextBlock.ForegroundProperty, stale ? "StaleBrush" : "MutedBrush");
        CodexStatusText.FontWeight = stale ? FontWeights.SemiBold : FontWeights.Normal;
        CodexStatusText.FontSize = stale ? 12 : 11;
        CodexStatusText.Visibility = string.IsNullOrWhiteSpace(CodexStatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CodexRows.Items.Clear();
        foreach (var item in CodexDisplayFormatting.Rows(snapshot, includeResetCredits: false))
        {
            var row = new Grid { Margin = new Thickness(0, 7, 0, 7), MinHeight = 18 };
            if (item.Tooltip is not null)
            {
                row.ToolTip = item.Tooltip;
            }
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Margin = new Thickness(0, 0, 12, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush")
            });
            var values = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Grid.SetColumn(values, 1);
            values.Children.Add(new TextBlock
            {
                Text = item.Value, FontSize = 14,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("FlyoutValueText"),
                Foreground = item.EmphasizeDanger ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextBrush")
            });
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                values.Children.Add(new TextBlock
                {
                    Text = item.Detail, FontSize = 11, Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
                    Foreground = (Brush)FindResource("MutedBrush")
                });
            }
            row.Children.Add(values);
            CodexRows.Items.Add(new Border
            {
                Child = row, BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = CodexRows.Items.Count == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0)
            });
        }

        ApplyCodexRing(snapshot);
    }

    private void BindCreditCard(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Provider != UsageProviderId.Codex)
        {
            CreditExpiryRows.Items.Clear();
            if (_creditHelpTip is not null) _creditHelpTip.IsOpen = false;
            return;
        }
        var credits = CodexCreditCard.From(snapshot, DateTimeOffset.Now);
        ResetCreditsCount.Text = credits.CountText;
        CreditExpiryRows.Items.Clear();
        for (var index = 0; index < credits.Rows.Count; index++)
        {
            var item = credits.Rows[index];
            var row = new Grid { MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,5 L19,5 L19,20 L3,20 Z M3,9 L19,9 M7,2 L7,6 M15,2 L15,6"),
                Width = 19, Height = 20, Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("MutedBrush"), StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            });
            var text = new TextBlock
            {
                Text = item.Text, FontSize = 12, Margin = new Thickness(6, 6, 4, 6),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush")
            };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var use = new System.Windows.Controls.Button
            {
                Content = _redeemingCredit ? UiText.T("Processing…", "처리 중…") : UiText.T("Use reset", "초기화 사용"),
                Style = (Style)FindResource("CreditUseButton"), FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 2, 0, 2),
                IsEnabled = !_redeemingCredit && !_refreshActive && snapshot.Status == CodexQuotaStatus.Available
                    && item.CreditId is not null && (RedeemCredit is not null || RedeemAccountCredit is not null),
                ToolTip = item.CreditId is null ? UiText.T("Refresh to enable use.", "새로고침 후 사용할 수 있습니다.") : item.Text
            };
            use.Click += async (_, _) => await UseCreditAsync(item);
            Grid.SetColumn(use, 2); row.Children.Add(use);
            row.ToolTip = MakeTooltip(item.Tooltip);
            CreditExpiryRows.Items.Add(new Border
            {
                Child = row, BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = index + 1 < credits.Rows.Count ? new Thickness(0, 0, 0, 1) : new Thickness(0)
            });
        }
        CreditListBorder.Visibility = credits.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CreditExpiryNotice.Text = credits.Notice;
        CreditExpiryNotice.Visibility = credits.Notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task UseCreditAsync(CodexCreditExpiryRow item)
    {
        if (_redeemingCredit || _refreshActive || item.CreditId is null || (RedeemCredit is null && RedeemAccountCredit is null)
            || _creditSnapshot?.Status != CodexQuotaStatus.Available) return;
        var profileId = SelectedProfileId;
        var accountName = SelectedAccountText.Text;
        _redeemingCredit = true; // Own the guard before opening a modal nested dispatcher.
        try
        {
            BindCreditCard(_creditSnapshot);
            var prompt = UiText.T($@"Use this reset credit?
{item.Text}
One credit will be consumed.",
                $@"이 리셋권으로 사용 한도를 초기화할까요?
{item.Text}
리셋권 1개가 소모됩니다.");
            if (profileId is not null) prompt = accountName + Environment.NewLine + prompt;
            var confirmed = ConfirmCreditForTest?.Invoke(prompt)
                ?? (System.Windows.MessageBox.Show(this, prompt, UiText.T("Use reset", "초기화 사용"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
            if (!confirmed) return;
            var outcome = profileId is not null && RedeemAccountCredit is not null
                ? await RedeemAccountCredit(profileId, item.CreditId)
                : RedeemCredit is not null ? await RedeemCredit(item.CreditId) : CreditRedemptionOutcome.Unavailable;
            CreditExpiryNotice.Text = outcome switch
            {
                CreditRedemptionOutcome.Reset or CreditRedemptionOutcome.AlreadyRedeemed =>
                    UiText.T("Reset applied. Check the current limit status above.", "초기화가 적용되었습니다. 갱신된 상태를 위에서 확인하세요."),
                CreditRedemptionOutcome.NothingToReset => UiText.T("No eligible usage limit to reset.", "초기화할 수 있는 사용 한도가 없습니다."),
                CreditRedemptionOutcome.NoCredit => UiText.T("This reset credit is no longer available.", "이 리셋권은 더 이상 사용할 수 없습니다."),
                CreditRedemptionOutcome.Unknown => UiText.T("Result unconfirmed. Refresh before retrying this credit.", "처리 결과를 확인하지 못했습니다. 새로고침 후 같은 리셋권을 재확인하세요."),
                _ => UiText.T("Could not use the reset. Refresh and try again.", "초기화를 실행하지 못했습니다. 새로고침 후 다시 시도하세요.")
            };
            CreditExpiryNotice.Visibility = Visibility.Visible;
        }
        catch
        {
            CreditExpiryNotice.Text = UiText.T("Result unconfirmed. Refresh before retrying.", "처리 결과를 확인하지 못했습니다. 새로고침 후 재확인하세요.");
            CreditExpiryNotice.Visibility = Visibility.Visible;
        }
        finally
        {
            _redeemingCredit = false;
            // Preserve the result notice while restoring row button state.
            var notice = CreditExpiryNotice.Text;
            var visibility = CreditExpiryNotice.Visibility;
            if (_creditSnapshot is not null) BindCreditCard(_creditSnapshot);
            CreditExpiryNotice.Text = notice;
            CreditExpiryNotice.Visibility = visibility;
        }
    }
    private System.Windows.Controls.ToolTip MakeTooltip(string text) => new()
    {
        Content = text
    };

    private void OnCreditHelpClick(object sender, RoutedEventArgs e)
    {
        if (CreditHelpButton.ToolTip is System.Windows.Controls.ToolTip tip)
        {
            tip.PlacementTarget = CreditHelpButton;
            tip.IsOpen = !tip.IsOpen;
        }
    }

    private void OnCreditExpandClick(object sender, RoutedEventArgs e)
    {
        _creditsExpanded = !_creditsExpanded;
        ApplyCreditExpansion();
    }

    private void ApplyCreditExpansion()
    {
        CreditDetails.Visibility = _creditsExpanded ? Visibility.Visible : Visibility.Collapsed;
        CreditExpandChevron.Data = Geometry.Parse(_creditsExpanded ? "M1,7 L7,1 L13,7" : "M1,1 L7,7 L13,1");
        var label = _creditsExpanded ? UiText.T("Collapse reset credits", "리셋권 접기") : UiText.T("Expand reset credits", "리셋권 펼치기");
        CreditExpandButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CreditExpandButton, label);
        if (!_creditsExpanded && _creditHelpTip is not null) _creditHelpTip.IsOpen = false;
    }

    private void FitContentToWorkArea()
    {
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = System.Windows.Forms.Control.MousePosition;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? System.Windows.Forms.Screen.FromPoint(cursor)
            : System.Windows.Forms.Screen.FromHandle(handle);
        var work = screen.WorkingArea;
        var size = fromDevice.Transform(new System.Windows.Vector(work.Width, work.Height));
        var scale = Math.Min(ZoomPercent / 100d, Math.Max(1, size.X - 24) / 440d);
        FlyoutScale.ScaleX = FlyoutScale.ScaleY = scale;
        Width = 440 * scale;
        FlyoutContentScroll.MaxHeight = Math.Max(0, (size.Y - 24) / scale - 104);
    }

    public void RefreshWorkArea()
    {
        FitContentToWorkArea();
        if (IsVisible) RestorePosition(Left, Top);
    }

    private void SetZoom(int percent, bool notify)
    {
        var next = FlyoutZoom.Normalize(percent);
        var changed = next != ZoomPercent;
        ZoomPercent = next;
        FitContentToWorkArea();
        TitleText.ToolTip = UiText.T($"Size {ZoomPercent}% · Ctrl + / Ctrl - · Ctrl 0 to reset",
            $"크기 {ZoomPercent}% · Ctrl + / Ctrl - · Ctrl 0으로 초기화");
        if (IsVisible) RestorePosition(Left, Top);
        if (changed && notify) ZoomChanged?.Invoke(ZoomPercent);
    }

    public bool TryHandleZoomShortcut(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0
            || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        var next = key switch
        {
            Key.OemPlus or Key.Add => FlyoutZoom.Adjust(ZoomPercent, increase: true),
            Key.OemMinus or Key.Subtract => FlyoutZoom.Adjust(ZoomPercent, increase: false),
            Key.D0 or Key.NumPad0 => FlyoutZoom.DefaultPercent,
            _ => (int?)null
        };
        if (next is null) return false;
        SetZoom(next.Value, notify: true);
        return true;
    }

    private void ApplyCodexRing(CodexQuotaSnapshot snapshot)
    {
        var ring = CodexRingPresentation.From(snapshot);
        CodexRingValueText.Text = ring.CenterValueText;
        CodexRingSubLabel.Text = ring.CenterSubLabel;

        var stale = ClaudeUsagePresentation.IsStale(snapshot);
        CodexRingValueText.SetResourceReference(TextBlock.ForegroundProperty, stale ? "StaleBrush" : "TextBrush");
        var arcColor = (Brush)FindResource(stale ? "StaleBrush" : ring.IsDangerLevel ? "DangerBrush" : "AccentBrush");
        CodexRingArcPath.Stroke = arcColor;
        CodexRingFullCircle.Stroke = arcColor;
        CodexRingTrack.Stroke = (Brush)FindResource(ring.IsAvailable ? "LineBrush" : "DisabledBrush");

        var arc = RingGeometry.ComputeUsedArc(ring.UsedPercent, CodexRingCenter, CodexRingCenter, CodexRingRadius);
        CodexRingArcPath.Visibility = arc.Visible ? Visibility.Visible : Visibility.Collapsed;
        CodexRingFullCircle.Visibility = arc.IsFullCircle ? Visibility.Visible : Visibility.Collapsed;
        if (arc.Visible)
        {
            CodexRingFigure.StartPoint = new System.Windows.Point(arc.Start.X, arc.Start.Y);
            CodexRingArcSegment.Point = new System.Windows.Point(arc.End.X, arc.End.Y);
            CodexRingArcSegment.Size = new System.Windows.Size(CodexRingRadius, CodexRingRadius);
            CodexRingArcSegment.IsLargeArc = arc.IsLargeArc;
        }

    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (TryHandleZoomShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void OnRefreshAllClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Pinned = !Pinned;
        Topmost = FlyoutWindowState.IsTopmost(Pinned);
        ApplyPinGlyph();
        PinChanged?.Invoke(Pinned);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!FlyoutWindowState.AllowsHeaderDrag || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (HeaderSourceIsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }

        PersistPosition();
    }

    private void PersistPosition()
    {
        RestorePosition(Left, Top);
        PositionChanged?.Invoke(Left, Top);
    }

    private void ApplyPinGlyph()
    {
        PinFilled.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed;
        PinOutline.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        var label = Pinned ? UiText.Unpin : UiText.Pin;
        PinButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, label);
    }

    private bool HeaderSourceIsInteractive(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, FlyoutHeaderGrid))
        {
            if (source is System.Windows.Controls.Button
                || ReferenceEquals(source, StatusText)
                || ReferenceEquals(source, RefreshProgressText)
                || ReferenceEquals(source, RefreshAllButton)
                || ReferenceEquals(source, PinButton)
                || ReferenceEquals(source, CloseFlyoutButton))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private IReadOnlyList<ScreenRect> EnumerateWorkAreas()
    {
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var areas = new List<ScreenRect>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
            var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
            areas.Add(new ScreenRect(
                (int)topLeft.X,
                (int)topLeft.Y,
                (int)Math.Max(1, bottomRight.X - topLeft.X),
                (int)Math.Max(1, bottomRight.Y - topLeft.Y)));
        }

        return areas;
    }

}
