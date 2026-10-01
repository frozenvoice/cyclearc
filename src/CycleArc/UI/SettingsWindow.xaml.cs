using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CycleArc.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public event Action<AppSettings>? Saved;
    /// The slider position while dragging, and the applied value again when the window closes
    /// without applying it. Only the widget ground is previewed; nothing is saved.
    public event Action<double>? WidgetOpacityPreviewed;
    public event Action? OpenLogsRequested;
    public event Action? AccountsRequested;
    public bool ResetWidgetPositionOnSave { get; private set; }
    private bool _ready;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Title = UiText.ProductName + " · " + UiText.Settings;
        WindowHeading.Text = Title;
        var buildVersion = AppVersionDisplay.Full(typeof(SettingsWindow).Assembly);
        VersionCaption.Text = UiText.T("Current version", "현재 실행 버전");
        VersionText.Text = "v" + AppVersionDisplay.Short(typeof(SettingsWindow).Assembly);
        VersionText.ToolTip = buildVersion;
        AutomationProperties.SetName(VersionText, VersionCaption.Text + " " + buildVersion);
        GeneralTab.Header = UiText.T("General", "일반");
        WidgetTab.Header = UiText.T("Widget", "위젯");
        ConnectionTab.Header = UiText.T("Connection", "연결");
        AppearanceTitle.Text = UiText.T("Make it yours", "표시와 동작");
        AppearanceHint.Text = UiText.T("Choose how CycleArc looks and starts.", "화면과 시작 방식을 설정하세요.");
        BehaviorTitle.Text = UiText.T("Windows & placement", "Windows와 창 배치");
        ThemeLabel.Text = UiText.T("Theme", "테마");
        ThemeBox.ItemsSource = new[] { UiText.T("System", "시스템"), UiText.T("Light", "밝게"), UiText.T("Dark", "어둡게") };
        ThemeBox.SelectedIndex = (int)settings.Theme;
        LanguageLabel.Text = UiText.T("Language", "언어");
        LanguageBox.ItemsSource = new[] { "한국어", "English" };
        LanguageBox.SelectedIndex = settings.UiLanguage == UiLanguage.Korean ? 0 : 1;
        IconLabel.Text = UiText.T("Tray icon", "트레이 아이콘");
        IconBox.ItemsSource = new[] { UiText.T("Usage number", "사용률 숫자"), UiText.T("Usage ring", "사용률 링"), UiText.T("Left number", "남은 양 숫자") };
        IconBox.SelectedIndex = (int)settings.TrayIconStyle;
        TraySummary.Text = UiText.T("Usage at a glance in your taskbar.", "작업표시줄에서 사용률을 한눈에.");
        TrayDetails.Header = UiText.T("How the tray icon works", "트레이 아이콘 표시 안내");
        StartupLabel.Text = UiText.StartWithWindows;
        StartupBox.IsChecked = settings.StartWithWindows;
        EdgeSnapLabel.Text = UiText.T("Snap windows to screen edges", "화면 가장자리에 자동 정렬");
        EdgeSnapBox.IsChecked = settings.SnapWindowsToScreenEdges;
        UsageAlertsLabel.Text = UiText.T("Usage alerts", "사용량 알림");
        UsageAlertsHint.Text = UiText.T("Notify once when a limit reaches 85% and again at 100%, per account and period.",
            "계정·기간마다 한도가 85%에 닿을 때와 100%일 때 한 번씩 알립니다.");
        UsageAlertsBox.IsChecked = settings.UsageAlertsEnabled;
        EdgeSnapBox.ToolTip = UiText.T("Applies to the widget and detail popup. Hold Shift when releasing a drag to skip snapping.",
            "위젯과 상세 팝업에 적용합니다. Shift를 누른 채 드래그를 끝내면 이번 정렬을 생략합니다.");
        EdgeSnapHint.Text = UiText.T("Widget and detail popup. Hold Shift to skip a snap.", "위젯과 상세 팝업에 적용 · Shift를 누르면 이번 정렬 생략");
        TrayHint.Text = UiText.T(
            "The tray shows the used percentage as large digits without the % sign (67 means 67%); the background is transparent. Text follows your Windows taskbar theme. Unknown usage shows ?. Check the tooltip or detail card for status. The ring style shows usage as progress. The left number shows what is left instead (33 means 33% left), like the popup and widget rings.",
            "트레이는 % 기호 없이 사용률 숫자를 크게 표시합니다(67은 67% 사용). 배경은 투명합니다. 글자색은 Windows 작업표시줄 테마에 맞춰 바뀌며, 알 수 없는 값은 ?로 표시합니다. 상태는 툴팁이나 상세 카드에서 확인하세요. 링은 같은 값을 진행률로 표시합니다. 남은 양 숫자는 팝업·위젯 링처럼 남은 양을 표시합니다(33은 33% 남음).");
        WidgetTitle.Text = UiText.T("Desktop widget", "바탕화면 위젯");
        ResetWidgetPositionButton.Content = UiText.T("Reset widget position", "위젯 위치 초기화");
        ResetWidgetPositionButton.ToolTip = UiText.T("Move the widget to the primary screen when you apply or save.", "적용하거나 저장하면 위젯을 기본 화면으로 이동합니다.");
        WidgetHint.Text = UiText.T("Keep a small usage display on your desktop. Drag it to move.", "작은 사용률 표시를 바탕화면에 둡니다. 드래그해서 위치를 옮길 수 있습니다.");
        WidgetLabel.Text = UiText.T("Show widget", "위젯 표시");
        WidgetEnabledHint.Text = UiText.T("Your accounts, together on the desktop.", "계정별 사용량을 바탕화면에서 나란히 확인하세요.");
        WidgetBehaviorTitle.Text = UiText.T("Visibility & interaction", "표시와 마우스 동작");
        ResetWidgetHint.Text = UiText.T("Bring it back to the primary display on apply or save.", "적용하거나 저장하면 기본 화면으로 위치를 되돌립니다.");
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        WidgetOpacityLabel.Text = UiText.T("Opacity", "불투명도");
        WidgetOpacityBox.Value = settings.WidgetOpacity;
        UpdateOpacityText();
        WidgetTopLabel.Text = UiText.T("Always on top", "항상 위에 표시");
        WidgetTopBox.IsChecked = settings.WidgetAlwaysOnTop;
        WidgetClickThroughLabel.Text = UiText.T("Click through", "클릭 통과");
        WidgetClickThroughBox.IsChecked = settings.WidgetClickThrough;
        WidgetClickThroughBox.ToolTip = UiText.T("Mouse clicks pass to the window behind the widget.", "마우스 클릭이 위젯 뒤의 창에 전달됩니다.");
        WidgetClickThroughHint.Text = (string)WidgetClickThroughBox.ToolTip;
        ConnectionTitle.Text = UiText.T("Accounts & refresh", "계정과 자동 확인");
        ConnectionHint.Text = UiText.T("Connect your accounts and keep their limits up to date.", "계정을 연결하고 최신 사용량을 확인하세요.");
        AccountSummary.Text = UiText.T("Connect, rename and arrange your accounts. Each keeps its own limits.", "계정을 연결하고 별명과 순서를 정하세요. 계정별 한도는 각각 유지됩니다.");
        RefreshSectionTitle.Text = UiText.T("Keep up to date", "사용량 확인 주기");
        ConnectionHelp.Header = UiText.T("Connection guide", "계정 연결 안내");
        CodexTitle.Text = UiText.T("Codex connection", "Codex 연결");
        CodexHint.Text = UiText.T("CycleArc uses the Codex CLI installed on this PC for sign-in and quota checks. Codex CLI must be installed separately.",
            "CycleArc는 이 PC에 설치된 Codex CLI로 로그인과 사용량 조회를 진행합니다. Codex CLI는 별도로 설치되어 있어야 합니다.");
        ConnectionSteps.Text = UiText.T("1. Open account management → Add an account. Codex can sign in or find an existing local login; Cursor reads the account already signed in on this Windows PC.\n2. For a new Codex login, select the intended ChatGPT account in the browser, then return to CycleArc.\n3. Check each account's provider-specific usage. Select a card for the tray/widget; set a nickname and use ↑ / ↓ to change the display order.",
            "1. 계정 관리 → 계정 추가를 여세요. Codex는 로그인하거나 이 PC의 기존 로그인을 찾을 수 있고, Cursor는 Windows에 이미 로그인된 계정을 읽습니다.\n2. 새 Codex 로그인은 브라우저에서 사용할 ChatGPT 계정을 선택한 뒤 CycleArc로 돌아오세요.\n3. 서비스별 사용량을 확인하세요. 카드를 누르면 트레이·위젯에 표시되며, 별명을 정하고 ↑ / ↓로 표시 순서를 바꿀 수 있습니다.");
        ManageAccountsButton.Content = UiText.T("Manage Codex, Claude and Cursor accounts", "Codex·Claude·Cursor 계정 관리");
        CodexExeLabel.Text = UiText.CodexExecutable;
        CodexExeBox.Text = settings.CodexExePath ?? "";
        AutoDetectHint.Text = UiText.T("Detect automatically", "자동으로 찾기");
        CodexPathHint.Text = UiText.T("Usually leave this empty for automatic detection. If Codex cannot be found, enter the full path to codex.exe or codex.cmd. Save a changed path before reopening account management.",
            "보통은 비워 두면 자동으로 찾습니다. Codex를 찾지 못할 때 codex.exe 또는 codex.cmd의 전체 경로를 입력하세요. 경로를 바꿨다면 저장한 뒤 계정 관리를 다시 여세요.");
        RefreshScheduleTitle.Text = UiText.T("Automatic refresh", "자동 확인");
        RefreshIntervalBox.ItemsSource = AppSettings.CodexRefreshIntervals.Select(minutes =>
            minutes == 5 ? UiText.T("5 minutes (default)", "5분 (기본값)") :
            minutes == 1 ? UiText.T("1 minute", "1분") : UiText.T($"{minutes} minutes", $"{minutes}분")).ToArray();
        RefreshIntervalBox.SelectedIndex = AppSettings.CodexRefreshIntervals.ToList().IndexOf(settings.CodexRefreshIntervalMinutes);
        SetName(RefreshIntervalBox, RefreshScheduleTitle.Text);
        RefreshScheduleHint.Text = UiText.T("Actively check Codex, Claude and Cursor accounts at this interval. Manual refresh checks Claude's shared subscription quota and the existing Cursor login; no Claude Code model request is needed.",
            "이 간격으로 Codex·Claude·Cursor 계정을 적극적으로 확인합니다. 수동 새로고침도 Claude의 공유 구독 한도와 기존 Cursor 로그인을 확인하며, Claude Code 모델 요청은 필요하지 않습니다.");
        LogsButton.Content = UiText.T("Open logs", "로그 열기");
        SaveButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = UiText.Save, Foreground = (Brush)FindResource("OnAccentBrush")
        };
        CancelButton.Content = UiText.T("Cancel", "취소");
        ApplyButton.Content = UiText.T("Apply", "적용");
        ApplyButton.ToolTip = UiText.T("Apply the changes and keep this window open.", "변경 사항을 적용하고 이 창을 열어 둡니다.");
        CloseSettingsButton.ToolTip = UiText.Close;
        SetName(CloseSettingsButton, UiText.Close);
        SetName(ThemeBox, ThemeLabel.Text); SetName(LanguageBox, LanguageLabel.Text);
        SetName(IconBox, IconLabel.Text); SetName(StartupBox, StartupLabel.Text);
        SetName(EdgeSnapBox, EdgeSnapLabel.Text);
        SetName(UsageAlertsBox, UsageAlertsLabel.Text);
        SetName(WidgetBox, WidgetLabel.Text); SetName(WidgetTopBox, WidgetTopLabel.Text);
        SetName(WidgetClickThroughBox, WidgetClickThroughLabel.Text);
        SetName(WidgetOpacityBox, WidgetOpacityLabel.Text); SetName(CodexExeBox, CodexExeLabel.Text);
        SourceInitialized += (_, _) => FitWorkArea();
        // Any edit re-evaluates Apply, so it is enabled exactly while something differs.
        RoutedEventHandler changed = (_, _) => UpdateApplyState();
        AddHandler(ToggleButton.CheckedEvent, changed);
        AddHandler(ToggleButton.UncheckedEvent, changed);
        AddHandler(Selector.SelectionChangedEvent, new System.Windows.Controls.SelectionChangedEventHandler((_, _) => UpdateApplyState()));
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new System.Windows.Controls.TextChangedEventHandler((_, _) => UpdateApplyState()));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => UpdateApplyState()));
        // Closing without applying takes the widget back to the opacity it actually has.
        Closed += (_, _) => WidgetOpacityPreviewed?.Invoke(_settings.WidgetOpacity);
        _ready = true;
        UpdateApplyState();
    }

    private string? EditedCodexExePath => string.IsNullOrWhiteSpace(CodexExeBox.Text) ? null : CodexExeBox.Text.Trim();
    private int EditedRefreshInterval => AppSettings.CodexRefreshIntervals[
        Math.Clamp(RefreshIntervalBox.SelectedIndex, 0, AppSettings.CodexRefreshIntervals.Count - 1)];
    private AppTheme EditedTheme => (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
    private UiLanguage EditedLanguage => LanguageBox.SelectedIndex == 0 ? UiLanguage.Korean : UiLanguage.English;
    private TrayIconStyle EditedTrayIconStyle => (TrayIconStyle)Math.Clamp(IconBox.SelectedIndex, 0, 2);

    internal bool HasPendingChanges =>
        ResetWidgetPositionOnSave
        || EditedCodexExePath != _settings.CodexExePath
        || EditedRefreshInterval != _settings.CodexRefreshIntervalMinutes
        || EditedTheme != _settings.Theme
        || EditedLanguage != _settings.UiLanguage
        || EditedTrayIconStyle != _settings.TrayIconStyle
        || (StartupBox.IsChecked == true) != _settings.StartWithWindows
        || (WidgetBox.IsChecked == true) != _settings.FloatingWidgetEnabled
        // The slider cannot go below the applied floor, so an older lower value is not an edit.
        || Math.Abs(WidgetOpacityBox.Value - Math.Clamp(_settings.WidgetOpacity, WidgetOpacityBox.Minimum, 1)) > 0.001
        || (WidgetTopBox.IsChecked == true) != _settings.WidgetAlwaysOnTop
        || (WidgetClickThroughBox.IsChecked == true) != _settings.WidgetClickThrough
        || (EdgeSnapBox.IsChecked == true) != _settings.SnapWindowsToScreenEdges
        || (UsageAlertsBox.IsChecked == true) != _settings.UsageAlertsEnabled;

    private void UpdateApplyState()
    {
        if (_ready) ApplyButton.IsEnabled = HasPendingChanges;
    }

    private static void SetName(DependencyObject control, string text) => AutomationProperties.SetName(control, text);

    private void FitWorkArea()
    {
        var work = SystemParameters.WorkArea;
        MaxWidth = Math.Max(320, work.Width - 24);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        Width = Math.Min(Width, MaxWidth);
        MaxHeight = Math.Max(320, work.Height - 24);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Height = Math.Min(Height, MaxHeight);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Commit();
        Close();
    }

    // Apply runs the same save path as Save and keeps the window open, so the widget, tray and
    // popup can be checked before closing. Cancel afterwards keeps what was applied.
    private void OnApply(object sender, RoutedEventArgs e)
    {
        Commit();
        ResetWidgetPositionOnSave = false;
        ResetWidgetPositionButton.Content = UiText.T("Reset widget position", "위젯 위치 초기화");
        UpdateApplyState();
    }

    private void Commit()
    {
        _settings.CodexExePath = EditedCodexExePath;
        _settings.CodexRefreshIntervalMinutes = EditedRefreshInterval;
        _settings.Theme = EditedTheme;
        _settings.UiLanguage = EditedLanguage;
        _settings.TrayIconStyle = EditedTrayIconStyle;
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.TaskbarStatusEnabled = false;
        _settings.FloatingWidgetEnabled = WidgetBox.IsChecked == true;
        _settings.WidgetOpacity = WidgetOpacityBox.Value;
        _settings.WidgetAlwaysOnTop = WidgetTopBox.IsChecked == true;
        _settings.WidgetClickThrough = WidgetClickThroughBox.IsChecked == true;
        _settings.SnapWindowsToScreenEdges = EdgeSnapBox.IsChecked == true;
        _settings.UsageAlertsEnabled = UsageAlertsBox.IsChecked == true;
        if (!_settings.SnapWindowsToScreenEdges) _settings.ClearWindowEdgeAnchors();
        Saved?.Invoke(_settings);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
        if (_ready) WidgetOpacityPreviewed?.Invoke(WidgetOpacityBox.Value);
    }
    private void UpdateOpacityText()
    {
        if (WidgetOpacityValue is not null && WidgetOpacityBox is not null)
            WidgetOpacityValue.Text = $"{Math.Round(WidgetOpacityBox.Value * 100)}%";
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnResetWidgetPosition(object sender, RoutedEventArgs e)
    {
        ResetWidgetPositionOnSave = true;
        ResetWidgetPositionButton.Content = UiText.T("Position will reset on apply or save", "적용·저장 시 위치가 초기화됩니다");
        UpdateApplyState();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();
    private void OnAccounts(object sender, RoutedEventArgs e) => AccountsRequested?.Invoke();
}
