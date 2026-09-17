using System.Threading;
using CycleArc.Updates;

namespace CycleArc.UI;

public partial class UpdateWindow : Window
{
    private readonly AppUpdateCoordinator _updates;
    private readonly Action _exit;
    private readonly CancellationTokenSource _operations;
    private bool _closed;
    public Task ActiveOperation { get; private set; } = Task.CompletedTask;
    public event Action<Task>? OperationStarted;

    public UpdateWindow(AppUpdateCoordinator updates, Action exit, CancellationToken lifetime = default)
    {
        _updates = updates;
        _exit = exit;
        _operations = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        InitializeComponent();
        _updates.Changed += Refresh;
        UiText.Changed += Refresh;
        Closed += (_, _) =>
        {
            _closed = true;
            _updates.Changed -= Refresh;
            UiText.Changed -= Refresh;
            _operations.Cancel();
            _operations.Dispose();
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        Title = Heading.Text = UiText.T("CycleArc updates", "CycleArc 업데이트");
        var state = _updates.State;
        var release = _updates.Release;
        VersionLabel.Text = release is null
            ? UiText.T($"Installed version {_updates.CurrentVersion}", $"현재 버전 {_updates.CurrentVersion}")
            : $"{_updates.CurrentVersion}  →  {release.Version}";
        StatusLabel.Text = state switch
        {
            AppUpdateState.Disabled => UiText.T("Use the stable Windows installer to receive updates in this app.", "정식 Windows 설치 프로그램으로 설치하면 앱에서 업데이트할 수 있습니다."),
            AppUpdateState.Checking => UiText.T("Checking for a new version…", "새 버전을 확인하고 있습니다…"),
            AppUpdateState.Available => UiText.T("A new version is available. Download it when you are ready.", "새 버전이 있습니다. 편할 때 다운로드하세요."),
            AppUpdateState.Downloading => UiText.T($"Downloading and verifying… {_updates.Progress}%", $"다운로드 및 검증 중… {_updates.Progress}%"),
            AppUpdateState.Ready => UiText.T("The update is verified and ready. Restart CycleArc to apply it.", "업데이트 검증이 끝났습니다. CycleArc를 다시 시작하면 적용됩니다."),
            AppUpdateState.Applying => UiText.T("Closing CycleArc to apply the update…", "업데이트를 적용하기 위해 CycleArc를 종료합니다…"),
            AppUpdateState.Failed when _updates.Error == AppUpdateError.Canceled => UiText.T("Canceled. Your current version is unchanged.", "취소했습니다. 현재 버전은 그대로 유지됩니다."),
            AppUpdateState.Failed when _updates.Error == AppUpdateError.ApplyFailed => UiText.T("The update could not be prepared. Your current version is unchanged. Try again.", "업데이트를 준비하지 못했습니다. 현재 버전은 그대로 유지됩니다. 다시 시도해 주세요."),
            AppUpdateState.Failed when _updates.Error == AppUpdateError.DownloadFailed => UiText.T("The update could not be downloaded or verified. Your current version is unchanged. Try downloading again.", "업데이트를 다운로드하거나 검증하지 못했습니다. 현재 버전은 그대로 유지됩니다. 다시 다운로드해 주세요."),
            AppUpdateState.Failed => UiText.T("Could not check for updates. Check your connection and try again later.", "새 버전을 확인하지 못했습니다. 연결을 확인하고 잠시 후 다시 시도하세요."),
            _ => UiText.T("You are up to date.", "최신 버전을 사용하고 있습니다.")
        };
        NotesLabel.Text = string.IsNullOrWhiteSpace(release?.Notes)
            ? UiText.T("New versions are checked at startup and every six hours. Downloading and restarting require your approval.",
                "시작할 때와 6시간마다 새 버전을 확인합니다. 다운로드와 재시작은 직접 선택한 경우에만 진행합니다.")
            : release.Notes.Length > 16000 ? release.Notes[..16000] : release.Notes;
        PreservationLabel.Text = UiText.T("Your accounts, preferences and Claude connection are kept. Only CycleArc restarts.",
            "계정·설정·Claude 연결은 유지됩니다. CycleArc만 다시 시작합니다.");
        var busy = state is AppUpdateState.Checking or AppUpdateState.Downloading or AppUpdateState.Applying;
        DownloadProgress.Visibility = state is AppUpdateState.Checking or AppUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = state == AppUpdateState.Checking;
        DownloadProgress.Value = _updates.Progress;
        CheckButton.Content = UiText.T("Check again", "다시 확인");
        CheckButton.IsEnabled = !busy && state is not (AppUpdateState.Disabled or AppUpdateState.Ready);
        LaterButton.Content = state == AppUpdateState.Downloading ? UiText.T("Cancel", "취소") : UiText.T("Later", "나중에");
        LaterButton.IsEnabled = state != AppUpdateState.Applying;
        ActionButton.Content = state == AppUpdateState.Ready ? UiText.T("Restart & update", "재시작 후 적용") : UiText.T("Download update", "업데이트 다운로드");
        ActionButton.Visibility = release is not null ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.IsEnabled = !busy && state is AppUpdateState.Available or AppUpdateState.Ready or AppUpdateState.Failed;
    }

    private async void OnCheck(object sender, RoutedEventArgs e) => await Track(_updates.CheckAsync(_operations.Token));
    private void OnLater(object sender, RoutedEventArgs e) => Close();
    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_updates.State == AppUpdateState.Ready)
        {
            // Hashing the full package and handing off to Update.exe must not block WPF.
            if (await Track(Task.Run(_updates.ApplyOnExit))) _exit();
        }
        else await Track(_updates.DownloadAsync(_operations.Token));
    }

#if CYCLEARC_TEST_E2E
    // Test-only build flavour: raise the production click event on the same button, so no
    // coordinator, client or supervisor step is bypassed. Never compiled into a shipped build.
    internal void ClickAction() => ActionButton.RaiseEvent(
        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
#endif

    private T Track<T>(T operation) where T : Task
    {
        ActiveOperation = operation;
        OperationStarted?.Invoke(operation);
        return operation;
    }
}
