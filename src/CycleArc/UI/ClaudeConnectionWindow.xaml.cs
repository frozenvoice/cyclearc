using System.Diagnostics;
using System.Windows.Input;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.UI;

public partial class ClaudeConnectionWindow : Window
{
    private string _activeProfileId;
    private readonly string _executable;
    private readonly IClaudeConnectionActions? _connections;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _operation;
    private ClaudeConnectionOverview? _overview;
    private bool _closed;
    private bool _closing;
    public Task ActiveOperation { get; private set; } = Task.CompletedTask;
    internal Action<string>? OpenExternalForTest { get; set; }

    public ClaudeConnectionWindow(CodexAccountProfile profile, string executable,
        IClaudeConnectionActions? connections = null, CancellationToken lifetime = default)
    {
        if (profile.Provider != UsageProviderId.Claude) throw new ArgumentException("Wrong usage provider.");
        _activeProfileId = profile.Id; _executable = executable; _connections = connections; _lifetime = lifetime;
        InitializeComponent();
        Title = UiText.ProductName + " · Claude";
        Heading.Text = UiText.T("Connect Claude usage", "Claude 사용량 연결");
        ProfileName.Text = new CodexAccountView(profile, CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable)).DisplayName;
        SetupSteps.Text = UiText.T("Connect a Claude Code terminal login to receive the subscription quota shared by Web, Desktop and Code.",
            "Claude Code 터미널 로그인을 연결해 Web·Desktop·Code가 공유하는 구독 한도를 받습니다.");
        ConnectExistingButton.Content = UiText.T("Connect current login", "현재 로그인 연결");
        LoginButton.Content = UiText.T("Sign in to Claude", "Claude 로그인");
        ReauthenticateButton.Content = UiText.T("Sign in again", "다시 로그인");
        OpenClaudeButton.Content = UiText.T("Open Claude Code terminal…", "Claude Code 터미널 열기…");
        FreshnessHint.Text = UiText.T("Use Claude Code in the connected terminal to receive usage after a response. CycleArc does not support receiving usage from the Desktop Code tab or Web. Limits stay unknown until received; saved values retain their original receipt time while idle.",
            "연결된 터미널에서 Claude Code를 사용하면 응답 후 사용량을 받을 수 있습니다. CycleArc는 데스크톱 Code 탭이나 Web에서 사용량을 받는 기능을 지원하지 않습니다. 첫 수신 전에는 미확인이며, 이후 새 수신이 없어도 기존 값과 원래 수신 시각을 유지합니다.");
        UsagePageButton.Content = ClaudeUsagePresentation.UsagePageLabel;
        UsagePageHint.Text = ClaudeUsagePresentation.UsagePageHint;
        AdvancedDetails.Header = UiText.T("Connection details", "연결 상세 설정");
        ExistingStatusLineHint.Text = UiText.T("Your other settings and existing status line are preserved. Disconnect restores the previous status line. Authentication stays in the official Claude CLI; CycleArc does not read credential files.",
            "다른 설정과 기존 상태 표시줄을 유지합니다. 연결을 해제하면 이전 상태 표시줄로 복원합니다. 인증은 공식 Claude CLI가 관리하며 CycleArc는 인증 파일을 읽지 않습니다.");
        DocsButton.Content = UiText.T("Official Claude Code guide", "Claude Code 공식 안내");
        DisconnectButton.Content = UiText.T("Disconnect", "연결 해제");
        DoneButton.Content = UiText.Close;
        CancelButton.Content = UiText.T("Cancel", "취소");
        ConnectionState.Text = UiText.T("Checking Claude login…", "Claude 로그인 확인 중…");
        Loaded += (_, _) => Begin(InspectAsync, UiText.T("Checking current login…", "현재 로그인 확인 중…"));
        Closing += (_, e) =>
        {
            if (_operation is null) return;
            e.Cancel = true; _closing = true; CancelOperation();
        };
        Closed += (_, _) => _closed = true;
        SourceInitialized += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            MaxHeight = Math.Max(400, work.Height - 24); Height = Math.Min(Height, MaxHeight);
            MaxWidth = Math.Max(470, work.Width - 24); Width = Math.Min(Width, MaxWidth);
        };
        UpdateButtons();
    }

    private async Task InspectAsync(CancellationToken token)
    {
        if (_connections is null) return;
        var overview = await Task.Run(() => _connections.InspectAsync(_activeProfileId, token), token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() => ApplyOverview(overview));
    }

    private void ApplyOverview(ClaudeConnectionOverview overview)
    {
        _overview = overview;
        var auth = overview.Authentication;
        var binding = overview.Binding;
        var linked = overview.Installed && auth.Status == ClaudeAuthStatus.SignedIn
            && auth.Fingerprint == binding?.IdentityFingerprint;
        var failure = overview.FailureKind;
        var reauthenticate = binding is { Disconnected: false } && failure is
            ClaudeFailureKind.AuthRequired or ClaudeFailureKind.IdentityMismatch;
        ConnectionState.Text = failure == ClaudeFailureKind.None
            ? linked ? UiText.T("Connected", "연결됨") : AuthText(auth.Status)
            : ClaudeUsagePresentation.FailureLabel(ClaudeFailureClassification.TechnicalDetail(failure)) ?? FailureText(failure);
        AccountIdentity.Text = auth.Status == ClaudeAuthStatus.SignedIn
            ? auth.Email + (auth.Plan is { Length: > 0 } plan ? " · " + plan : "") : "";
        ConfigPath.Text = UiText.T("Claude settings: ", "Claude 설정 위치: ") + overview.ConfigDirectory;
        OpenClaudeButton.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        ReauthenticateButton.Visibility = reauthenticate ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.Visibility = binding is { Disconnected: false } ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.Content = auth.Status == ClaudeAuthStatus.SignedIn
            ? UiText.T("Sign in to another account", "다른 계정으로 로그인") : UiText.T("Sign in to Claude", "Claude 로그인");
        OperationStatus.Text = failure != ClaudeFailureKind.None
            ? FailureText(failure)
            : linked ? UiText.T("Connected. Use Open Claude Code terminal to receive usage during normal use.", "연결됨. Claude Code 터미널 열기로 실행해 사용하면 사용량이 수신됩니다.") : "";
        UpdateButtons();
    }
    private void Begin(Func<CancellationToken, Task> action, string progress)
    {
        if (_closed || _operation is not null || _connections is null) return;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        OperationStatus.Text = progress;
        UpdateButtons();
        ActiveOperation = RunAsync(action, _operation);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationTokenSource operation)
    {
        try { await action(operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { await Dispatcher.InvokeAsync(() => OperationStatus.Text = AuthText(ClaudeAuthStatus.Cancelled)); }
        catch (ClaudeDisconnectCleanupException)
        {
            // The binding was durably revoked before cleanup started. Refresh the local
            // view without starting another authentication process during shutdown.
            await Dispatcher.InvokeAsync(() =>
            {
                ApplyDisconnectedView();
                OperationStatus.Text = UiText.ClaudeDisconnectCleanupIncomplete;
            });
        }
        catch (ClaudeSetupException ex) { await Dispatcher.InvokeAsync(() => OperationStatus.Text = FailureText(ex.Failure)); }
        catch { await Dispatcher.InvokeAsync(() => OperationStatus.Text = UiText.T("Could not connect. Try again or check the official guide.", "연결하지 못했습니다. 다시 시도하거나 공식 안내를 확인하세요.")); }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _operation = null; operation.Dispose(); UpdateButtons();
                if (_closing && !_closed) Close();
            });
        }
    }

    private async Task ReauthenticateAsync(CancellationToken token)
    {
        var result = await Task.Run(() => _connections!.ReauthenticateAsync(_activeProfileId, _executable, token), token).ConfigureAwait(false);
        if (!result.Success)
        {
            await Dispatcher.InvokeAsync(() => OperationStatus.Text = result.Failure is { } failure
                ? FailureText(failure) : AuthText(result.Authentication.Status));
            return;
        }
        await InspectAsync(token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
            OperationStatus.Text = UiText.T("Authentication renewed for this Claude settings folder. Use Claude Code and wait for a new response to receive usage; the last received value is kept until then.",
                "이 Claude 설정의 인증을 갱신했습니다. Claude Code에서 새 응답을 받으면 사용량이 수신되며, 그 전까지 마지막 수신값을 유지합니다."));
    }
    private async Task ConnectAsync(bool login, CancellationToken token)
    {
        var result = await Task.Run(() => _connections!.ConnectAsync(_activeProfileId, _executable, login, null, token), token).ConfigureAwait(false);
        if (result.Success)
        {
            if (result.Binding is { } binding && binding.ProfileId != _activeProfileId)
            {
                _activeProfileId = binding.ProfileId;
                await Dispatcher.InvokeAsync(() => ProfileName.Text = result.Authentication.Email ?? "Claude");
            }
            await InspectAsync(token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_overview?.Installed == true && _overview.Authentication.Fingerprint == result.Binding?.IdentityFingerprint)
                    OperationStatus.Text = UiText.T("Connected. Use Open Claude Code terminal to receive usage during normal use.",
                        "연결됨. Claude Code 터미널 열기로 실행해 사용하면 사용량을 받을 수 있습니다.");
            });
        }
        else await Dispatcher.InvokeAsync(() => OperationStatus.Text = result.Failure is { } failure ? FailureText(failure) : AuthText(result.Authentication.Status));
    }

    private void ApplyDisconnectedView()
    {
        if (_overview is { Binding: { } binding } overview)
            ApplyOverview(overview with { Binding = binding with { Disconnected = true }, Installed = false,
                FailureKind = ClaudeFailureKind.None });
    }

    private void UpdateButtons()
    {
        var busy = _operation is not null;
        ConnectExistingButton.IsEnabled = !busy && _connections is not null && _overview?.Authentication.Status == ClaudeAuthStatus.SignedIn;
        LoginButton.IsEnabled = OpenClaudeButton.IsEnabled = DisconnectButton.IsEnabled = ReauthenticateButton.IsEnabled = !busy && _connections is not null;
        OperationProgress.Visibility = CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AuthText(ClaudeAuthStatus status) => status switch
    {
        ClaudeAuthStatus.SignedIn => UiText.T("A Claude login is available", "연결할 Claude 로그인이 있습니다"),
        ClaudeAuthStatus.SignedOut => UiText.T("Sign in to connect Claude", "Claude 로그인 후 연결할 수 있습니다"),
        ClaudeAuthStatus.NotInstalled => UiText.T("Install Claude Code using the official guide, then reopen this window.", "공식 안내에서 Claude Code를 설치한 뒤 이 창을 다시 여세요."),
        ClaudeAuthStatus.Unsupported => UiText.T("Use a Claude subscription login to receive account limits.", "계정 한도를 받으려면 Claude 구독 계정으로 로그인하세요."),
        ClaudeAuthStatus.InvalidResponse => UiText.T("Update Claude Code, then try again.", "Claude Code를 업데이트한 뒤 다시 시도하세요."),
        ClaudeAuthStatus.TimedOut => UiText.T("Login timed out. Try again.", "로그인 시간이 초과되었습니다. 다시 시도하세요."),
        ClaudeAuthStatus.Cancelled => UiText.T("Cancelled", "취소했습니다"),
        _ => UiText.T("Claude login failed. Try again.", "Claude 로그인에 실패했습니다. 다시 시도하세요.")
    };
    private static string FailureText(ClaudeFailureKind failure) => failure switch
    {
        ClaudeFailureKind.AuthRequired => UiText.T("Sign-in required. Claude authentication expired or was rejected. Sign in again with this Claude settings folder. The last received value is kept until a new Code response arrives.", "로그인 필요. Claude 인증이 만료되었거나 거부되었습니다. 이 Claude 설정에서 다시 로그인하세요. 새 Code 응답을 받을 때까지 마지막 수신값을 유지합니다."),
        ClaudeFailureKind.IdentityMismatch => UiText.T("The Claude account no longer matches this connection. Sign in again with the bound account or connect a different account separately.", "Claude 계정이 이 연결과 일치하지 않습니다. 연결된 계정으로 다시 로그인하거나 다른 계정을 별도로 연결하세요."),
        ClaudeFailureKind.RequestFailed => UiText.T("Claude Code reported a failed request. Try the terminal again; the last received value is kept until a new response arrives.", "Claude Code 요청이 실패했습니다. 터미널에서 다시 시도하세요. 새 응답을 받을 때까지 마지막 수신값을 유지합니다."),
        ClaudeFailureKind.BridgeUnavailable => UiText.T("Claude Code could not deliver usage to CycleArc. Check the Claude Code installation and try again. The last received value is kept until a new response arrives.", "Claude Code가 CycleArc로 사용량을 전달하지 못했습니다. Claude Code 설치를 확인한 뒤 다시 시도하세요. 새 응답을 받을 때까지 마지막 수신값을 유지합니다."),
        _ => UiText.T("Claude connection needs attention. Use Claude Code again or sign in again.", "Claude 연결을 확인해야 합니다. Claude Code를 다시 사용하거나 다시 로그인하세요.")
    };
    private static string FailureText(ClaudeSetupFailure failure) => failure switch
    {
        ClaudeSetupFailure.AlreadyLinked => UiText.T("This Claude settings folder is linked to another profile. Open that profile or sign in separately.", "이 Claude 설정은 다른 프로필에 연결되어 있습니다. 해당 프로필을 열거나 별도로 로그인하세요."),
        ClaudeSetupFailure.InvalidSettings => UiText.T("Claude settings could not be safely updated. Check the settings file in connection details.", "Claude 설정을 수정하지 못했습니다. 연결 상세 설정에 표시된 설정 파일을 확인하세요."),
        ClaudeSetupFailure.SettingsChanged => UiText.T("Claude settings changed during setup. Try again.", "연결 중 Claude 설정이 변경되었습니다. 다시 시도하세요."),
        ClaudeSetupFailure.CommandTooLong => UiText.ClaudeStatusLineCommandTooLong,
        ClaudeSetupFailure.DisconnectCleanupIncomplete => UiText.ClaudeDisconnectCleanupIncomplete,
        _ => UiText.T("Connection settings are unavailable. Check file permissions and try again.", "연결 설정에 접근할 수 없습니다. 파일 권한을 확인한 뒤 다시 시도하세요.")
    };

    public void CancelOperation() => _operation?.Cancel();
    private void OnReauthenticate(object sender, RoutedEventArgs e) => Begin(token => ReauthenticateAsync(token), UiText.T("Renewing Claude authentication…", "Claude 인증 갱신 중…"));
    private void OnConnectExisting(object sender, RoutedEventArgs e) => Begin(token => ConnectAsync(false, token), UiText.T("Connecting current login…", "현재 로그인 연결 중…"));
    private void OnLogin(object sender, RoutedEventArgs e) => Begin(token => ConnectAsync(true, token), UiText.T("Finish signing in in your browser. Settings will be applied automatically.", "브라우저에서 로그인을 완료하세요. 설정은 자동으로 적용됩니다."));
    private void OnDisconnect(object sender, RoutedEventArgs e) => Begin(async token =>
    {
        await Task.Run(() => _connections!.DisconnectAsync(_activeProfileId, token), token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            ApplyDisconnectedView();
            OperationStatus.Text = UiText.ClaudeDisconnected;
        });
    }, UiText.T("Disconnecting…", "연결 해제 중…"));
    private void OnOpenClaude(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = UiText.T("Choose a folder for Claude Code", "Claude Code에서 사용할 폴더 선택") };
        if (picker.ShowDialog(this) != true) return;
        try { _connections!.OpenClaude(_activeProfileId, picker.FolderName); }
        catch { OperationStatus.Text = UiText.T("Could not open Claude Code.", "Claude Code를 열지 못했습니다."); }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => CancelOperation();
    private void OnUsagePage(object sender, RoutedEventArgs e) => ClaudeUsagePage.Open(this, OpenExternalForTest);
    private void OnDocs(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://code.claude.com/docs/en/setup") { UseShellExecute = true });
    private void OnDone(object sender, RoutedEventArgs e) => Close();
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
