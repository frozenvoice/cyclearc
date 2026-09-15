using System.Windows.Automation;
using System.Windows.Input;

namespace CycleArc.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public event Action<AppSettings>? Saved;
    public event Action? OpenLogsRequested;
    public event Action? AccountsRequested;
    public bool ResetWidgetPositionOnSave { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Title = UiText.ProductName + " · " + UiText.Settings;
        WindowHeading.Text = Title;
        GeneralTab.Header = UiText.T("General", "일반");
        WidgetTab.Header = UiText.T("Widget", "위젯");
        ConnectionTab.Header = UiText.T("Connection", "연결");
        AppearanceTitle.Text = UiText.T("Make it yours", "표시와 동작");
        AppearanceHint.Text = UiText.T("Choose how CycleArc looks and starts.", "화면과 시작 방식을 설정하세요.");
        ThemeLabel.Text = UiText.T("Theme", "테마");
        ThemeBox.ItemsSource = new[] { UiText.T("System", "시스템"), UiText.T("Light", "밝게"), UiText.T("Dark", "어둡게") };
        ThemeBox.SelectedIndex = (int)settings.Theme;
        LanguageLabel.Text = UiText.T("Language", "언어");
        LanguageBox.ItemsSource = new[] { "한국어", "English" };
        LanguageBox.SelectedIndex = settings.UiLanguage == UiLanguage.Korean ? 0 : 1;
        IconLabel.Text = UiText.T("Tray icon", "트레이 아이콘");
        IconBox.ItemsSource = new[] { UiText.T("Usage number", "사용률 숫자"), UiText.T("Usage ring", "사용률 링") };
        IconBox.SelectedIndex = (int)settings.TrayIconStyle;
        StartupLabel.Text = UiText.StartWithWindows;
        StartupBox.IsChecked = settings.StartWithWindows;
        TrayHint.Text = UiText.T(
            "The tray shows used percent (e.g. 67%) with no color background. Text follows your Windows taskbar theme. Unknown usage shows ?. Check the tooltip or detail card for status. The ring style shows usage as progress.",
            "트레이는 색 배경 없이 사용률(예: 67%)을 표시합니다. 글자색은 Windows 작업표시줄 테마에 맞춰 바뀌며, 알 수 없는 값은 ?로 표시합니다. 상태는 툴팁이나 상세 카드에서 확인하세요. 링은 같은 값을 진행률로 표시합니다.");
        WidgetTitle.Text = UiText.T("Desktop widget", "바탕화면 위젯");
        ResetWidgetPositionButton.Content = UiText.T("Reset widget position", "위젯 위치 초기화");
        ResetWidgetPositionButton.ToolTip = UiText.T("Move the widget to the primary screen when you save.", "저장하면 위젯을 기본 화면으로 이동합니다.");
        WidgetHint.Text = UiText.T("Keep a small usage display on your desktop. Drag it to move.", "작은 사용률 표시를 바탕화면에 둡니다. 드래그해서 위치를 옮길 수 있습니다.");
        WidgetLabel.Text = UiText.T("Show widget", "위젯 표시");
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        WidgetOpacityLabel.Text = UiText.T("Opacity", "불투명도");
        WidgetOpacityBox.Value = settings.WidgetOpacity;
        UpdateOpacityText();
        WidgetTopLabel.Text = UiText.T("Always on top", "항상 위에 표시");
        WidgetTopBox.IsChecked = settings.WidgetAlwaysOnTop;
        WidgetClickThroughLabel.Text = UiText.T("Click through", "클릭 통과");
        WidgetClickThroughBox.IsChecked = settings.WidgetClickThrough;
        WidgetClickThroughBox.ToolTip = UiText.T("Mouse clicks pass to the window behind the widget.", "마우스 클릭이 위젯 뒤의 창에 전달됩니다.");
        CodexTitle.Text = UiText.T("Codex connection", "Codex 연결");
        CodexHint.Text = UiText.T("CycleArc uses the Codex CLI installed on this PC for sign-in and quota checks. Codex CLI must be installed separately.",
            "CycleArc는 이 PC에 설치된 Codex CLI로 로그인과 사용량 조회를 진행합니다. Codex CLI는 별도로 설치되어 있어야 합니다.");
        ConnectionSteps.Text = UiText.T("1. Open Manage Codex accounts → Add an account · Connection guide. Choose New account sign-in for your first or another account; choose Find accounts on this PC for an existing Codex login.\n2. For a new login, select the intended ChatGPT account in the browser, then return to CycleArc.\n3. Check each account's usage. Select a card for the tray/widget; set a nickname and use ↑ / ↓ to change the display order.",
            "1. 아래 Codex 계정 관리 → 계정 추가 · 연결 방법을 여세요. 처음이거나 다른 계정을 추가하려면 새 계정 로그인, 이미 Codex에 로그인했다면 이 PC의 계정 찾기를 선택하세요.\n2. 새 로그인은 브라우저에서 사용할 ChatGPT 계정을 선택한 뒤 CycleArc로 돌아오세요.\n3. 계정별 사용량을 확인하세요. 카드를 누르면 트레이·위젯에 표시되며, 별명을 정하고 ↑ / ↓로 표시 순서를 바꿀 수 있습니다.");
        ManageAccountsButton.Content = UiText.T("Manage Codex and Claude accounts", "Codex·Claude 계정 관리");
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
        RefreshScheduleHint.Text = UiText.T("Check Codex accounts at this interval. Claude receives updates through its statusLine connection while Claude Code is in use.",
            "Codex 계정의 사용량을 이 간격으로 확인합니다. Claude는 Claude Code 사용 중 statusLine 연결로 업데이트를 받습니다.");
        LogsButton.Content = UiText.T("Open logs", "로그 열기");
        SaveButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = UiText.Save, Foreground = (Brush)FindResource("OnAccentBrush")
        };
        CancelButton.Content = UiText.T("Cancel", "취소");
        CloseSettingsButton.ToolTip = UiText.Close;
        SetName(CloseSettingsButton, UiText.Close);
        SetName(ThemeBox, ThemeLabel.Text); SetName(LanguageBox, LanguageLabel.Text);
        SetName(IconBox, IconLabel.Text); SetName(StartupBox, StartupLabel.Text);
        SetName(WidgetBox, WidgetLabel.Text); SetName(WidgetTopBox, WidgetTopLabel.Text);
        SetName(WidgetClickThroughBox, WidgetClickThroughLabel.Text);
        SetName(WidgetOpacityBox, WidgetOpacityLabel.Text); SetName(CodexExeBox, CodexExeLabel.Text);
        SourceInitialized += (_, _) => FitWorkArea();
    }

    private static void SetName(DependencyObject control, string text) => AutomationProperties.SetName(control, text);

    private void FitWorkArea()
    {
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, work.Height - 24);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Height = Math.Min(Height, MaxHeight);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.CodexExePath = string.IsNullOrWhiteSpace(CodexExeBox.Text) ? null : CodexExeBox.Text.Trim();
        _settings.CodexRefreshIntervalMinutes = AppSettings.CodexRefreshIntervals[
            Math.Clamp(RefreshIntervalBox.SelectedIndex, 0, AppSettings.CodexRefreshIntervals.Count - 1)];
        _settings.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
        _settings.UiLanguage = LanguageBox.SelectedIndex == 0 ? UiLanguage.Korean : UiLanguage.English;
        _settings.TrayIconStyle = (TrayIconStyle)Math.Clamp(IconBox.SelectedIndex, 0, 1);
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.TaskbarStatusEnabled = false;
        _settings.FloatingWidgetEnabled = WidgetBox.IsChecked == true;
        _settings.WidgetOpacity = WidgetOpacityBox.Value;
        _settings.WidgetAlwaysOnTop = WidgetTopBox.IsChecked == true;
        _settings.WidgetClickThrough = WidgetClickThroughBox.IsChecked == true;
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateOpacityText();
    private void UpdateOpacityText()
    {
        if (WidgetOpacityValue is not null && WidgetOpacityBox is not null)
            WidgetOpacityValue.Text = $"{Math.Round(WidgetOpacityBox.Value * 100)}%";
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnResetWidgetPosition(object sender, RoutedEventArgs e)
    {
        ResetWidgetPositionOnSave = true;
        ResetWidgetPositionButton.Content = UiText.T("Position will reset on save", "저장 시 위치가 초기화됩니다");
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();
    private void OnAccounts(object sender, RoutedEventArgs e) => AccountsRequested?.Invoke();
}
