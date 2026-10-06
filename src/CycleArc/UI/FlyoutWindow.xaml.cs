using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Reflection;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;

namespace CycleArc.UI;

public partial class FlyoutWindow : Window
{
    public event Action? SyncRequested;
    public event Action? AccountsRequested;
    public event Action<string>? AccountSelected;
    public event Action<UsagePeriodPreference>? UsagePeriodChanged;
    public UsagePeriodPreference UsagePeriod { get; private set; } = UsagePeriodPreference.Auto;
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
    private bool _bindingUsagePeriod;
    private bool _bindingAccountSelector;
    private AccountChoice[] _accountChoices = [];
    private readonly DisplayedAccountIdentities _displayedIdentities = new();
    // Each row stays with its account: created when the account appears, rewritten only when
    // its displayed output changes, moved when the order changes and dropped when it leaves.
    private readonly Dictionary<string, System.Windows.Controls.Button> _accountRows = new(StringComparer.Ordinal);
    private bool _resetCreditsCompact;
    private sealed record AccountChoice(string Id, string AccountName, string ProviderName)
    {
        public string DisplayName { get; } = AccountName + " · " + ProviderName;
    }
    // Folded by default: the summary line names the count and the nearest expiry.
    private bool _creditsExpanded;
    private System.Windows.Controls.ToolTip? _creditHelpTip;
    private WindowEdgeAnchors _edgeAnchors;
    private bool _snapWindowsToScreenEdges = true;
    private bool _dragActive;
    private bool _dragMoved;
    private double _dragStartLeft;
    private double _dragStartTop;
    private bool _positionInitialized;
    private bool _applyingPosition;
    private (int X, int Y)? _savedPixels;
    private bool _pixelRestorePending;
    private bool _relayoutAfterDrag;
    private bool _sizeRelayoutQueued;
    private bool _sizeRelayouting;
    private bool _suppressSizeRelayout;
    private (int Percent, bool Notify)? _deferredZoom;
    private (double Left, double Top, WindowEdgeAnchors Anchors)? _lastPersistedPosition;

    public WindowEdgeAnchors EdgeAnchors => _edgeAnchors;

    public (int X, int Y)? PixelPosition
    {
        get
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect)
                ? (rect.Left, rect.Top)
                : null;
        }
    }

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
        SourceInitialized += (_, _) => FitContentToWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(RefreshWorkArea);
        LocationChanged += (_, _) => TrackNativeDragMovement();
        SizeChanged += (_, _) => QueueSizeRelayout();
        ToolTipService.SetIsEnabled(CreditHelpButton, false);
        IsVisibleChanged += (_, _) =>
        {
            ApplyRefreshVisuals();
            if (!IsVisible)
            {
                if (_creditHelpTip is not null) _creditHelpTip.IsOpen = false;
                WindowOptionsMenu.IsOpen = false;
                AccountSelector.IsDropDownOpen = false;
            }
        };
        Activated += (_, _) => ApplyRefreshVisuals();
        ContentRendered += (_, _) => ApplyRefreshVisuals();
        Closed += (_, _) =>
        {
            _refreshActive = false;
            ApplyRefreshVisuals();
        };
    }

    public void ApplyEdgeSnapSettings(AppSettings settings)
    {
        var previousAnchors = _edgeAnchors;
        _snapWindowsToScreenEdges = settings.SnapWindowsToScreenEdges;
        _edgeAnchors = _snapWindowsToScreenEdges
            ? new WindowEdgeAnchors(settings.FlyoutHorizontalAnchor, settings.FlyoutVerticalAnchor).Normalize()
            : default;
        // A later drop may reattach at identical coordinates after settings clear the anchors.
        if (previousAnchors != _edgeAnchors) _lastPersistedPosition = null;
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        ApplyUsageCardSettings(settings);
        ApplyEdgeSnapSettings(settings);
        if (!_positionInitialized && !_pixelRestorePending
            && settings.FlyoutPixelLeft is { } pixelLeft && settings.FlyoutPixelTop is { } pixelTop)
        {
            _savedPixels = (pixelLeft, pixelTop);
            _pixelRestorePending = true;
        }
        SetZoom(settings.FlyoutZoomPercent, notify: false);
        Pinned = settings.FlyoutPinned;
        Topmost = FlyoutWindowState.IsTopmost(Pinned);
        ApplyPinGlyph();
    }

    public void RestorePosition(double left, double top) =>
        RestorePositionCore(left, top, preferNativeWorkArea: false);

    private void RestorePositionCore(double left, double top, bool preferNativeWorkArea)
    {
        if (_dragActive) { _relayoutAfterDrag = true; return; }
        var wasInitialized = _positionInitialized;
        preferNativeWorkArea |= _pixelRestorePending && _savedPixels is not null;
        (left, top) = RestoreSavedPixels(left, top);
        var work = SelectWorkArea(left, top, Width, Math.Max(ActualHeight, 1), preferNativeWorkArea);
        PlaceWithin(work, left, top, persist: wasInitialized);
    }

    // One target owns fitting, final measurement, recovery and optional drop detection.
    private void PlaceWithin(ScreenRect work, double left, double top,
        bool detectDrop = false, bool bypass = false, bool persist = true)
    {
        var before = (Left, Top);
        _suppressSizeRelayout = true;
        try
        {
            FitContentToWorkArea(work);
            UpdateLayout();
            var height = Math.Max(ActualHeight, 1);
            if (detectDrop)
                _edgeAnchors = WindowEdgeSnap.Detect(left, top, Width, height, work,
                    _snapWindowsToScreenEdges, bypass);
            var placed = _edgeAnchors.IsAttached || detectDrop
                ? WindowEdgeSnap.Place(left, top, Width, height, work, _edgeAnchors)
                : FlyoutPlacement.ClampToWorkArea(left, top, Width, height, work);
            ApplyPosition(placed.Left, placed.Top);
            _positionInitialized = true;
        }
        finally { _suppressSizeRelayout = false; }
        if (persist && (Math.Abs(before.Left - Left) > .01 || Math.Abs(before.Top - Top) > .01))
            PersistPosition();
    }

    private (double Left, double Top) RestoreSavedPixels(double left, double top)
    {
        if (!_pixelRestorePending || _savedPixels is not { } pixels)
            return (left, top);

        _pixelRestorePending = false;
        SetPixelPosition(pixels.X, pixels.Y);
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var restored = fromDevice.Transform(new System.Windows.Point(pixels.X, pixels.Y));
        return double.IsFinite(restored.X) && double.IsFinite(restored.Y)
            ? (restored.X, restored.Y)
            : (left, top);
    }

    private ScreenRect SelectWorkArea(
        double left, double top, double width, double height, bool preferNative)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (preferNative && hwnd != IntPtr.Zero)
        {
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            return ToDipWorkArea(screen.WorkingArea);
        }

        return FlyoutPlacement.SelectWorkArea(left, top, width, height, EnumerateWorkAreas());
    }

    private ScreenRect ToDipWorkArea(System.Drawing.Rectangle area)
    {
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
        return new ScreenRect(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)),
            Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y)));
    }

    private void ApplyPosition(double left, double top)
    {
        _applyingPosition = true;
        try
        {
            Left = left;
            Top = top;
        }
        finally { _applyingPosition = false; }
    }

    private void QueueSizeRelayout()
    {
        if (_suppressSizeRelayout || _applyingPosition || !IsVisible || !_positionInitialized)
            return;
        if (_dragActive)
        {
            _relayoutAfterDrag = true;
            return;
        }
        if (_sizeRelayoutQueued || _sizeRelayouting) return;
        _sizeRelayoutQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(RelayoutAfterSize));
    }

    private void RelayoutAfterSize()
    {
        _sizeRelayoutQueued = false;
        if (_suppressSizeRelayout || _applyingPosition || !IsVisible || !_positionInitialized)
            return;
        if (_dragActive)
        {
            _relayoutAfterDrag = true;
            return;
        }

        _sizeRelayouting = true;
        try
        {
            var work = SelectWorkArea(Left, Top, Width, Math.Max(ActualHeight, 1),
                preferNative: _edgeAnchors.IsAttached);
            PlaceWithin(work, Left, Top);
        }
        finally { _sizeRelayouting = false; }
    }

    private void FlushDeferredAfterDrag()
    {
        var deferredZoom = _deferredZoom;
        var relayout = _relayoutAfterDrag;
        _deferredZoom = null;
        _relayoutAfterDrag = false;
        if (deferredZoom is { } zoom) SetZoom(zoom.Percent, zoom.Notify);
        else if (relayout) RefreshWorkArea();
    }

    public void Bind(CodexQuotaSnapshot snapshot, bool refreshing = false, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        _observationAccount = null;
        BindSnapshot(snapshot, refreshing, preference);
    }

    private void BindSnapshot(CodexQuotaSnapshot snapshot, bool refreshing, UsagePeriodPreference preference)
    {
        // Keep action/status metadata while defending every selected-detail projection,
        // including legacy reset credits, against accidentally retained account values.
        if (UsageCreditPresentation.Hidden(snapshot)) snapshot = snapshot with
        {
            Windows = [], UsageCredits = null, ExtraUsage = null,
            ResetCreditsAvailable = null, ResetCreditExpirations = null, RedeemableCredits = []
        };
        SelectedAccountHeader.Visibility = SelectedProviderBadge.Visibility = CodexCard.Visibility = Visibility.Visible;
        _creditSnapshot = snapshot;
        UsagePeriod = preference;
        _refreshActive = refreshing;
        SelectedProviderBadge.Provider = snapshot.Provider;
        ResetCreditsCard.Visibility = snapshot.Provider == UsageProviderId.Codex ? Visibility.Visible : Visibility.Collapsed;
        ApplyLocalizedTexts();
        StatusText.Text = snapshot.Status == CodexQuotaStatus.Available && snapshot.Provider == UsageProviderId.Codex
            ? UiText.T("Up to date", "정상 작동 중") : CycleArcPresentation.StatusLabel(snapshot);
        StatusDot.Fill = (Brush)FindResource(refreshing ? "AccentBrush" : snapshot.Status == CodexQuotaStatus.Available ? "OkBrush" : "MutedBrush");
        BindCodex(snapshot);
        BindCreditCard(snapshot);
        BindUsageCard(snapshot);
        SetRefreshPresentation(new FlyoutRefreshPresentation(!refreshing, refreshing,
            refreshing ? UiText.CodexRefreshing : ""));
    }

    public void BindAccounts(IReadOnlyList<CodexAccountView> accounts, string selectedId, bool refreshing, UsagePeriodPreference preference = UsagePeriodPreference.Auto)
    {
        var overview = UsageAccountOverview.Create(accounts, selectedId, preference);
        accounts = overview.Accounts;
        selectedId = overview.SelectedId;
        SelectedProfileId = selectedId.Length == 0 ? null : selectedId;
        var selected = overview.Selected;
        _observationAccount = selected;
        BindSnapshot(selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut), refreshing, overview.Preference);
        _displayedIdentities.Record(accounts);
        AccountSection.Visibility = Visibility.Visible;
        ManageAccountsButton.Content = UiText.T("Manage accounts", "계정 관리");
        AccountsHeading.Text = UiText.T($"Accounts · {accounts.Count}", $"계정 · {accounts.Count}");
        BindAccountRows(accounts.Count > 1 ? accounts : [], selectedId);
        AccountSelectionHint.Text = accounts.Count > 1
            ? UiText.T("Select an account for details, tray and widget.", "계정을 선택하면 상세 카드·트레이·위젯에 표시됩니다.")
            : UiText.T("Connect accounts in Manage accounts to show them here.", "계정 관리에서 연결한 계정이 여기에 표시됩니다.");
        AccountSelectionHint.Visibility = accounts.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        SelectedAccountText.Text = selected?.DisplayName ?? UiText.T("Add your first account", "첫 계정을 추가하세요");
        SelectedAvatarHost.Content = selected is null ? null : AccountSummary.AvatarFor(selected, 30, SelectedAvatarHost.Content);
        SelectedAccountText.Visibility = Visibility.Visible;
        SelectedAccountText.ToolTip = selected?.Email ?? selected?.DisplayName;
        BindAccountChoices(accounts, selectedId);
        AccountSelector.Visibility = accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectedAccountText.Visibility = accounts.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
        var selectionHelp = UiText.T("Select the account shown in CycleArc details, tray and widget.",
            "CycleArc 상세·트레이·위젯에 표시할 계정을 선택합니다.");
        AccountSelector.ToolTip = selectionHelp;
        System.Windows.Automation.AutomationProperties.SetName(AccountSelector,
            UiText.T("Display account", "표시 계정"));
        System.Windows.Automation.AutomationProperties.SetHelpText(AccountSelector, selectionHelp);
        var failed = accounts.Count(a => a.Snapshot.Status != CodexQuotaStatus.Available && !a.IsAwaitingUsage);
        var waiting = accounts.Count(a => a.IsAwaitingUsage);
        // Only a Claude statusLine or Desktop history sample is "received"; a live server check
        // is as current as a Codex or Cursor check.
        var received = accounts.Any(a => a.Profile.Provider == UsageProviderId.Claude
            && !WidgetAccountModel.HasHealthyServerSample(a.Snapshot));
        if (accounts.Count > 1 && !refreshing)
            StatusText.Text = failed > 0 ? UiText.T($"{failed} need attention", $"{failed}개 확인 필요")
                : waiting > 0 ? UiText.T($"{waiting} awaiting usage", $"{waiting}개 수신 대기")
                : received ? UiText.T("Received", "수신값 포함")
                : UiText.T("All updated", "전체 최신");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            refreshing ? "AccentBrush" : accounts.Count > 0 && failed == 0 && waiting == 0 ? "OkBrush" : "MutedBrush");
        if (selected is null)
        {
            SelectedAccountHeader.Visibility = SelectedProviderBadge.Visibility = CodexCard.Visibility = ResetCreditsCard.Visibility = UsageCreditsCard.Visibility = Visibility.Collapsed;
            StatusText.Text = UiText.T("No usage yet", "사용량 대기");
        }
    }

    private void BindAccountRows(IReadOnlyList<CodexAccountView> accounts, string selectedId)
    {
        foreach (var id in _accountRows.Keys.ToArray())
        {
            if (accounts.Any(account => account.Profile.Id == id)) continue;
            AccountOverview.Items.Remove(_accountRows[id]);
            _accountRows.Remove(id);
        }
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var id = account.Profile.Id;
            if (_accountRows.TryGetValue(id, out var row))
                AccountSummary.Update(row, account, id == selectedId, compactNotice: true);
            else
                _accountRows[id] = row = AccountSummary.Create(account, id == selectedId,
                    () => { if (!_redeemingCredit) AccountSelected?.Invoke(id); }, compactNotice: true);
            if (index < AccountOverview.Items.Count && ReferenceEquals(AccountOverview.Items[index], row)) continue;
            if (AccountOverview.Items.IndexOf(row) >= 0) AccountOverview.Items.Remove(row);
            AccountOverview.Items.Insert(index, row);
        }
    }

    /// <summary>
    /// True when this window still displays an account that was removed, signed in as another
    /// email or became identity-protected since its last bind, even while it is hidden.
    /// </summary>
    public bool RetainsWithdrawnAccount(IReadOnlyList<CodexAccountView> accounts) =>
        _displayedIdentities.Withdrawn(accounts);

    private void OnAccountsClick(object sender, RoutedEventArgs e) => AccountsRequested?.Invoke();

    private void BindAccountChoices(IReadOnlyList<CodexAccountView> accounts, string selectedId)
    {
        var changed = _accountChoices.Length != accounts.Count || AccountSelector.ItemsSource is null;
        for (var index = 0; !changed && index < accounts.Count; index++)
        {
            var account = accounts[index];
            var choice = _accountChoices[index];
            changed = choice.Id != account.Profile.Id || choice.AccountName != account.DisplayName
                || choice.ProviderName != account.Profile.Provider.Name();
        }
        _bindingAccountSelector = true;
        try
        {
            // Quota and refresh changes do not change the options. Reusing their source
            // avoids replacing the ComboBox's items and selection on every quota event.
            if (changed)
            {
                _accountChoices = new AccountChoice[accounts.Count];
                for (var index = 0; index < accounts.Count; index++)
                {
                    var account = accounts[index];
                    _accountChoices[index] = new(account.Profile.Id, account.DisplayName, account.Profile.Provider.Name());
                }
                AccountSelector.ItemsSource = _accountChoices;
            }
            if (!Equals(AccountSelector.SelectedValue, selectedId)) AccountSelector.SelectedValue = selectedId;
        }
        finally { _bindingAccountSelector = false; }
    }

    private void OnAccountSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingAccountSelector || AccountSelector.SelectedItem is not AccountChoice choice
            || choice.Id == SelectedProfileId) return;
        if (_redeemingCredit)
        {
            _bindingAccountSelector = true;
            try { AccountSelector.SelectedValue = SelectedProfileId; }
            finally { _bindingAccountSelector = false; }
            return;
        }
        AccountSelected?.Invoke(choice.Id);
    }
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
        VersionText.Text = UiText.VersionPrefix + AppVersionDisplay.Short(Assembly.GetExecutingAssembly());
        ResetCreditsTitle.Text = UiText.ResetCredits;
        ApplyCreditExpansion();
        var helpText = UiText.T("Reset credits can renew your Codex usage limits. Select Use reset beside a credit to redeem it after confirmation.", "리셋권으로 Codex 사용 한도를 갱신할 수 있습니다. 리셋권 옆의 리셋권 사용을 누르고 확인하면 해당 리셋권을 사용합니다.");
        _creditHelpTip ??= MakeTooltip(helpText);
        _creditHelpTip.Content = helpText;
        CreditHelpButton.ToolTip = _creditHelpTip;
        System.Windows.Automation.AutomationProperties.SetName(CreditHelpButton, UiText.T("Reset credit count and help", "리셋권 보유 수와 안내"));
        SettingsButton.ToolTip = UiText.Settings;
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, UiText.Settings);
        RefreshAllButton.ToolTip = UiText.RefreshAll;
        System.Windows.Automation.AutomationProperties.SetName(RefreshAllButton, UiText.RefreshAll);
        ApplyPinGlyph();
        CloseFlyoutMenuItem.Header = UiText.Close;
        System.Windows.Automation.AutomationProperties.SetName(CloseFlyoutMenuItem, UiText.Close);
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
        _edgeAnchors = default;
        _pixelRestorePending = false;
        ApplyPosition(
            Math.Max(topLeft.X + 8, bottomRight.X - Width - 12),
            Math.Max(topLeft.Y + 8, bottomRight.Y - ActualHeight - 12));
        _positionInitialized = true;
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
        _edgeAnchors = default;
        _pixelRestorePending = false;
        ApplyPosition(left, top);
        _positionInitialized = true;
    }

    private const double CodexRingDiameter = 112;
    private const double CodexRingStrokeThickness = 10;
    private const double CodexRingRadius = (CodexRingDiameter - CodexRingStrokeThickness) / 2;
    private const double CodexRingCenter = CodexRingDiameter / 2;

    private void BindCodex(CodexQuotaSnapshot snapshot)
    {
        var stale = ClaudeUsagePresentation.IsStale(snapshot)
            || (CursorUsagePresentation.IsCursor(snapshot) && snapshot.Status == CodexQuotaStatus.Stale);
        ClaudeUsageHeader.Visibility = ClaudeUsagePageButton.Visibility = snapshot.Provider == UsageProviderId.Claude
            ? Visibility.Visible : Visibility.Collapsed;
        ClaudeUsageTitle.Text = ClaudeUsagePresentation.Title;
        ClaudeUsageScope.Text = ClaudeUsagePresentation.SharedScope;
        System.Windows.Automation.AutomationProperties.SetName(ClaudeUsageHeader,
            ClaudeUsagePresentation.Title + " · " + ClaudeUsagePresentation.SharedScope);
        ClaudeUsagePageButton.Content = ClaudeUsagePresentation.UsagePageLabel;
        ClaudeUsagePageButton.ToolTip = MakeTooltip(ClaudeUsagePresentation.UsagePageHint);
        CodexStatusText.Text = snapshot.Status == CodexQuotaStatus.Refreshing ? "" : CodexDisplayFormatting.StatusText(snapshot);
        CodexStatusText.SetResourceReference(TextBlock.ForegroundProperty, stale ? "StaleBrush" : "MutedBrush");
        CodexStatusText.FontWeight = stale ? FontWeights.SemiBold : FontWeights.Normal;
        CodexStatusText.FontSize = stale ? 12 : 11;
        // A healthy server check needs no sentence: its time is in the "Last checked" row and
        // the sentence stays in the scope tooltip. Received samples, stale data and failures
        // keep their notice, using the same rule as the widget status row.
        var healthy = WidgetAccountModel.HasHealthyServerSample(snapshot);
        CodexStatusText.Visibility = string.IsNullOrWhiteSpace(CodexStatusText.Text) || healthy
            ? Visibility.Collapsed
            : Visibility.Visible;
        ClaudeUsageScope.ToolTip = healthy && !string.IsNullOrWhiteSpace(CodexStatusText.Text)
            ? MakeTooltip(CodexStatusText.Text) : null;
        CodexRows.Items.Clear();
        CodexSecondaryRows.Items.Clear();
        var cursor = CursorUsagePresentation.IsCursor(snapshot.Provider);
        // The ring's limit sits beside the ring; other limits and the check time go full width below.
        var detailSnapshot = cursor ? snapshot with
        {
            Windows = snapshot.Windows.Where(window => window.LimitId != "cursor-on-demand").ToArray()
        } : snapshot;
        var (primary, secondary, primaryStart) = CodexDisplayFormatting.DetailSections(detailSnapshot,
            CodexRingPresentation.FromDetail(snapshot, UsagePeriod).Window);
        var beside = primary.Select(item => AddDetailRow(CodexRows, item, stacked: cursor)).ToArray();
        CodexRowsEmptyText.Text = UiText.T("No usage values yet", "아직 사용량 값이 없습니다");
        CodexRowsEmptyText.Visibility = beside.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var below = secondary.Select(item => AddDetailRow(CodexSecondaryRows, item, stacked: false)).ToArray();
        DetailRows = below.Take(primaryStart).Concat(beside).Concat(below.Skip(primaryStart)).ToArray();
        CodexSecondaryRowsHost.Visibility = secondary.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyCodexRing(snapshot);
        BindObservedTrend(snapshot);
    }

    // One detail row: label and value, with the value's detail (reset countdown, time) below it.
    // Stacked puts the label above the value, for long Cursor names in the narrow ring-side column.
    private Border AddDetailRow(ItemsControl target, CodexDisplayRow item, bool stacked)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 7), MinHeight = 18 };
        if (item.Tooltip is not null)
        {
            row.ToolTip = item.Tooltip;
        }
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (stacked)
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var label = new TextBlock
        {
            Text = item.Label,
            Margin = new Thickness(0, 0, 12, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedBrush")
        };
        if (stacked)
        {
            label.TextWrapping = TextWrapping.Wrap;
            label.TextTrimming = TextTrimming.None;
            label.Margin = new Thickness(0, 0, 0, 3);
            Grid.SetColumnSpan(label, 2);
        }
        row.Children.Add(label);
        var values = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetColumn(values, 1);
        if (stacked)
        {
            Grid.SetColumn(values, 0);
            Grid.SetColumnSpan(values, 2);
            Grid.SetRow(values, 1);
        }
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
        var border = new Border
        {
            Child = row, BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = target.Items.Count == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0)
        };
        target.Items.Add(border);
        return border;
    }

    // Every detail row in CodexDisplayFormatting.Rows order, whichever list shows it.
    internal IReadOnlyList<Border> DetailRows { get; private set; } = [];

    private void BindCreditCard(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Provider != UsageProviderId.Codex)
        {
            CreditExpiryRows.Items.Clear();
            if (_creditHelpTip is not null) _creditHelpTip.IsOpen = false;
            return;
        }
        var credits = CodexCreditCard.From(snapshot, DateTimeOffset.Now);
        _resetCreditsCompact = snapshot.Status == CodexQuotaStatus.Available && snapshot.ResetCreditsAvailable is null
            && !UsageCreditPresentation.Hidden(snapshot)
            && !WidgetStatusPresentation.From(snapshot, snapshot.LastAttemptedRefresh ?? DateTimeOffset.UtcNow).IsWarning;
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
                Content = _redeemingCredit ? UiText.T("Processing…", "처리 중…") : UiText.T("Use reset", "리셋권 사용"),
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
        // The first row is the nearest expiry; without rows the notice explains why.
        CreditSummaryText.Text = credits.Rows.Count > 0 ? credits.Rows[0].Text : credits.Notice ?? "";
        CreditSummaryText.ToolTip = string.IsNullOrEmpty(CreditSummaryText.Text) ? null : CreditSummaryText.Text;
        System.Windows.Automation.AutomationProperties.SetName(ResetCreditsCard,
            ResetCreditsTitle.Text + ": " + credits.CountText + " · " + CreditSummaryText.Text);
        ApplyCreditExpansion();
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
                ?? (System.Windows.MessageBox.Show(this, prompt, UiText.T("Use reset", "리셋권 사용"),
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
        ApplyAuxiliaryCardAppearance(ResetCreditsCard, ResetCreditsTitle, _resetCreditsCompact && !_creditsExpanded);
        CreditDetails.Visibility = _creditsExpanded ? Visibility.Visible : Visibility.Collapsed;
        CreditExpandChevron.Data = Geometry.Parse(_creditsExpanded ? "M1,7 L7,1 L13,7" : "M1,1 L7,7 L13,1");
        var label = _creditsExpanded ? UiText.T("Collapse reset credits", "리셋권 접기") : UiText.T("Expand reset credits", "리셋권 펼치기");
        CreditExpandButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CreditExpandButton, label);
        if (!_creditsExpanded && _creditHelpTip is not null) _creditHelpTip.IsOpen = false;
    }

    private ScreenRect CurrentFitWorkArea()
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? System.Windows.Forms.Screen.FromPoint(cursor)
            : System.Windows.Forms.Screen.FromHandle(handle);
        return ToDipWorkArea(screen.WorkingArea);
    }

    private void FitContentToWorkArea(ScreenRect? target = null)
    {
        var work = target ?? CurrentFitWorkArea();
        var size = new System.Windows.Vector(work.Width, work.Height);
        var scale = Math.Min(ZoomPercent / 100d, Math.Max(1, size.X - 24) / 440d);
        FlyoutScale.ScaleX = FlyoutScale.ScaleY = scale;
        Width = 440 * scale;
        FlyoutContentScroll.MaxHeight = Math.Max(0, (size.Y - 24) / scale - 104);
    }

    public void RefreshWorkArea()
    {
        if (_dragActive) { _relayoutAfterDrag = true; return; }
        if (IsVisible) RestorePositionCore(Left, Top, preferNativeWorkArea: _edgeAnchors.IsAttached);
        else FitContentToWorkArea();
    }

    private void SetZoom(int percent, bool notify)
    {
        var next = FlyoutZoom.Normalize(percent);
        if (_dragActive) { _deferredZoom = (next, notify); return; }
        // Select before the size changes, then use only this work area for the pass.
        var target = IsVisible
            ? SelectWorkArea(Left, Top, Width, Math.Max(ActualHeight, 1), preferNative: _edgeAnchors.IsAttached)
            : (ScreenRect?)null;
        var changed = next != ZoomPercent;
        ZoomPercent = next;
        UpdateZoomPresentation();
        if (target is { } work) PlaceWithin(work, Left, Top, persist: _positionInitialized);
        else FitContentToWorkArea();
        if (changed && notify) ZoomChanged?.Invoke(ZoomPercent);
    }

    private void UpdateZoomPresentation()
    {
        TitleText.ToolTip = UiText.T($"Size {ZoomPercent}% · Ctrl + / Ctrl - · Ctrl 0 to reset",
            $"크기 {ZoomPercent}% · Ctrl + / Ctrl - · Ctrl 0으로 초기화");
        UpdateZoomControls();
    }

    private void UpdateZoomControls()
    {
        FlyoutZoomOutButton.IsEnabled = ZoomPercent > FlyoutZoom.MinPercent;
        FlyoutZoomInButton.IsEnabled = ZoomPercent < FlyoutZoom.MaxPercent;
        var zoomIn = UiText.ZoomInHint(ZoomPercent);
        var zoomOut = UiText.ZoomOutHint(ZoomPercent);
        FlyoutZoomInButton.ToolTip = zoomIn;
        FlyoutZoomOutButton.ToolTip = zoomOut;
        System.Windows.Automation.AutomationProperties.SetName(FlyoutZoomInButton, zoomIn);
        System.Windows.Automation.AutomationProperties.SetName(FlyoutZoomOutButton, zoomOut);
    }

    // Same path as the shortcut, so button and keyboard cannot drift apart.
    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetZoom(FlyoutZoom.Adjust(ZoomPercent, increase: true), notify: true);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetZoom(FlyoutZoom.Adjust(ZoomPercent, increase: false), notify: true);
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
        var ring = CodexRingPresentation.FromDetail(snapshot, UsagePeriod);
        UpdatePeriodControls(snapshot, ring);
        // The ring fills with what is left. Its color is still the usage band of the unrounded
        // value, and an exhausted limit names itself rather than relying on an empty ring.
        CodexRingValueText.Text = ring.RemainingValueText;
        CodexRingSubLabel.Text = ring.IsExhausted ? UsageRingBands.Label(UsageRingBand.Exhausted) : ring.RemainingSubLabel;
        CodexRingSubLabel.SetResourceReference(TextBlock.ForegroundProperty,
            ring.IsExhausted ? "RingExhaustedBrush" : "MutedBrush");
        CodexRingSubLabel.FontWeight = ring.IsExhausted ? FontWeights.SemiBold : FontWeights.Normal;

        CodexRingValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var arcColor = (Brush)FindResource(UsageRingBands.ArcBrushKey(ring.Band));
        CodexRingArcPath.Stroke = arcColor;
        CodexRingFullCircle.Stroke = arcColor;
        CodexRingTrack.Stroke = (Brush)FindResource(ring.IsAvailable ? "LineBrush" : "DisabledBrush");

        var arc = RingGeometry.ComputeFillArc(ring.RemainingPercent, CodexRingCenter, CodexRingCenter, CodexRingRadius);
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

    private void UpdatePeriodControls(CodexQuotaSnapshot snapshot, CodexRingPresentation ring)
    {
        var cursor = CursorUsagePresentation.IsCursor(snapshot);
        UsagePeriodLabel.Text = UiText.T("Display period", "표시 기간");
        AutoPeriodButton.Content = UiText.T("Auto", "자동");
        FiveHourPeriodButton.Content = UiText.T("5 hours", "5시간");
        WeeklyPeriodButton.Content = UiText.T("Weekly", "주간");
        _bindingUsagePeriod = true;
        try
        {
            AutoPeriodButton.IsChecked = UsagePeriod == UsagePeriodPreference.Auto;
            FiveHourPeriodButton.IsChecked = UsagePeriod == UsagePeriodPreference.FiveHour;
            WeeklyPeriodButton.IsChecked = UsagePeriod == UsagePeriodPreference.Weekly;
        }
        finally { _bindingUsagePeriod = false; }
        var shownPeriod = ring.Window?.Kind switch
        {
            CodexWindowKind.FiveHour => UiText.T("5-hour", "5시간"),
            CodexWindowKind.Weekly => UiText.T("Weekly", "주간"),
            _ => ring.Window is null ? UiText.T("Unavailable", "미제공") : ring.RemainingSubLabel
        };
        UsagePeriodHint.Text = UiText.T($"Showing {shownPeriod}", $"{shownPeriod} 표시")
            + (ring.Window is not null && !ring.IsAvailable ? UiText.T(" · Unknown", " · 알 수 없음") : "")
            + (UsagePeriod == UsagePeriodPreference.Auto ? UiText.T(" · Auto", " · 자동") : "");
        var requested = UsagePeriod == UsagePeriodPreference.FiveHour ? CodexWindowKind.FiveHour
            : UsagePeriod == UsagePeriodPreference.Weekly ? CodexWindowKind.Weekly : (CodexWindowKind?)null;
        UsagePeriodFallback.Text = requested is not null && ring.IsAvailable && ring.Window?.Kind != requested
            ? UiText.T(
                $"{(requested == CodexWindowKind.FiveHour ? "5-hour" : "Weekly")} value unavailable. Showing {ring.RemainingSubLabel.ToLowerInvariant()}.",
                $"{(requested == CodexWindowKind.FiveHour ? "5시간" : "주간")} 값이 없어 {ring.RemainingSubLabel}을 표시합니다.")
            : "";
        UsagePeriodFallback.Visibility = UsagePeriodFallback.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsagePeriodLabel.Visibility = cursor ? Visibility.Collapsed : Visibility.Visible;
        AutoPeriodButton.Visibility = cursor ? Visibility.Collapsed : Visibility.Visible;
        FiveHourPeriodButton.Visibility = cursor ? Visibility.Collapsed : Visibility.Visible;
        WeeklyPeriodButton.Visibility = cursor ? Visibility.Collapsed : Visibility.Visible;
        UsagePeriodHint.Visibility = cursor ? Visibility.Collapsed : Visibility.Visible;
        UsagePeriodFallback.Visibility = cursor ? Visibility.Collapsed : UsagePeriodFallback.Visibility;
        CyclePeriodButton.IsEnabled = ring.IsAvailable
            && !cursor && HasKnownWindow(snapshot, CodexWindowKind.FiveHour) && HasKnownWindow(snapshot, CodexWindowKind.Weekly);
        var switchText = ring.Window?.Kind == CodexWindowKind.FiveHour
            ? UiText.T("Show weekly usage", "주간 사용량 표시") : UiText.T("Show 5-hour usage", "5시간 사용량 표시");
        CyclePeriodButton.ToolTip = switchText;
        System.Windows.Automation.AutomationProperties.SetName(CyclePeriodButton,
            UsageRingBands.WithLabel($"{ring.RemainingValueText} {ring.RemainingSubLabel.Replace(Environment.NewLine, " ")}", ring)
                + $". {switchText}");
    }

    private static bool HasKnownWindow(CodexQuotaSnapshot snapshot, CodexWindowKind kind) =>
        snapshot.Windows.Any(window => window.Kind == kind && window.UsedPercent is { } used && double.IsFinite(used));

    private void OnUsagePeriodChecked(object sender, RoutedEventArgs e)
    {
        if (_bindingUsagePeriod) return;
        var preference = ReferenceEquals(sender, FiveHourPeriodButton) ? UsagePeriodPreference.FiveHour
            : ReferenceEquals(sender, WeeklyPeriodButton) ? UsagePeriodPreference.Weekly : UsagePeriodPreference.Auto;
        SelectUsagePeriod(preference);
    }

    private void OnCyclePeriodClick(object sender, RoutedEventArgs e)
    {
        if (!CyclePeriodButton.IsEnabled || _creditSnapshot is null) return;
        SelectUsagePeriod(_creditSnapshot.DisplayWindow(UsagePeriod)?.Kind == CodexWindowKind.FiveHour
            ? UsagePeriodPreference.Weekly : UsagePeriodPreference.FiveHour);
    }

    private void SelectUsagePeriod(UsagePeriodPreference preference)
    {
        if (UsagePeriod == preference || _creditSnapshot is null) return;
        ApplyUsagePeriod(preference);
        UsagePeriodChanged?.Invoke(preference);
    }

    public void ApplyUsagePeriod(UsagePeriodPreference preference)
    {
        UsagePeriod = preference;
        // The ring's limit decides which rows sit beside it, so rebuild the rows with the ring.
        if (_creditSnapshot is not null) BindCodex(_creditSnapshot);
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

    private void OnWindowOptionsClick(object sender, RoutedEventArgs e)
    {
        WindowOptionsMenu.PlacementTarget = WindowOptionsButton;
        WindowOptionsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        WindowOptionsMenu.IsOpen = true;
    }

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

        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragMoved = false;
        _dragActive = true;
        var bypassSnap = false;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            // LocationChanged normally observes native WM_MOVING. The final comparison also
            // covers the last native move when WPF delivers it after DragMove returns.
            if (!_dragMoved && HasMovedPastDragThreshold())
            {
                _dragMoved = true;
                _edgeAnchors = default;
            }
            bypassSnap = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _dragActive = false;
        }

        if (!_dragMoved)
        {
            // A click on the header is still only a click. In particular, it must not snap or
            // detach an already attached window.
            FlushDeferredAfterDrag();
            return;
        }

        var deferredZoom = _deferredZoom;
        var zoomChanged = deferredZoom is { } pending && pending.Percent != ZoomPercent;
        if (deferredZoom is { } zoom)
        {
            ZoomPercent = zoom.Percent;
            UpdateZoomPresentation();
        }
        _deferredZoom = null;
        _relayoutAfterDrag = false;
        CompleteDragSnap(bypassSnap);
        PersistPosition();
        if (zoomChanged && deferredZoom is { Notify: true }) ZoomChanged?.Invoke(ZoomPercent);
    }

    private bool HasMovedPastDragThreshold() =>
        double.IsFinite(Left) && double.IsFinite(Top)
        && WidgetInteraction.IsDrag(_dragStartLeft, _dragStartTop, Left, Top);

    private void TrackNativeDragMovement()
    {
        if (!_dragActive || _applyingPosition || _dragMoved) return;
        if (!HasMovedPastDragThreshold()) return;
        _dragMoved = true;
        _edgeAnchors = default;
    }

    private void CompleteDragSnap(bool bypassSnap)
    {
        var work = SelectWorkArea(Left, Top, Width, Math.Max(ActualHeight, 1), preferNative: true);
        PlaceWithin(work, Left, Top, detectDrop: true, bypass: bypassSnap, persist: false);
    }

    private void PersistPosition()
    {
        var current = (Left, Top, _edgeAnchors);
        if (_lastPersistedPosition == current) return;
        _lastPersistedPosition = current;
        PositionChanged?.Invoke(Left, Top);
    }

    private void ApplyPinGlyph()
    {
        var label = Pinned ? UiText.Unpin : UiText.Pin;
        PinMenuItem.Header = label;
        PinMenuItem.IsChecked = Pinned;
        System.Windows.Automation.AutomationProperties.SetName(PinMenuItem, label);
        var options = UiText.T("Window options", "창 옵션");
        WindowOptionsButton.ToolTip = Pinned ? options + UiText.T(" · Pinned", " · 고정됨") : options;
        System.Windows.Automation.AutomationProperties.SetName(WindowOptionsButton, (string)WindowOptionsButton.ToolTip);
    }

    private bool HeaderSourceIsInteractive(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, FlyoutHeaderGrid))
        {
            if (source is System.Windows.Controls.Button
                || ReferenceEquals(source, StatusText)
                || ReferenceEquals(source, RefreshProgressText)
                || ReferenceEquals(source, RefreshAllButton)
                || ReferenceEquals(source, WindowOptionsButton))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SetPixelPosition(int x, int y)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

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
