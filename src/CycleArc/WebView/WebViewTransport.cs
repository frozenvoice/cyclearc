using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.WebView;

public sealed class WebViewTransport : IChatGptTransport, IWebViewInteractiveLogin, IDisposable
{
    private readonly AppLog _log;
    private readonly Window _host;
    private readonly Grid _root;
    private readonly Border _overlay;
    private readonly TextBlock _overlayText;
    private readonly WebViewInitializationCoordinator _coordinator = new();
    private readonly WebViewHostSession _session = new();
    private readonly SessionAuthCoordinator _auth = new();
    private readonly LoginNavigationMachine _login = new();
    private readonly WebViewNavigationWait _navigation = new();
    private WebView2 _webView;
    private Action? _detachNavigationHandler;
    private int _probeGeneration;
    private bool _coreReady;
    private bool _ensureAttempted;
    private bool _handlersAttached;
    private bool _shownForLogin;
    private bool _holdHostForDiagnostic;

    public WebViewTransport(AppLog log)
    {
        _log = log;
        _webView = CreateWebView();
        _overlayText = new TextBlock
        {
            Text = UiText.CheckingChatGptSession,
            Foreground = BrushOr("TextBrush", 0xEE, 0xF1, 0xF6),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24)
        };
        _overlay = new Border
        {
            Background = BrushOr("BgBrush", 0x14, 0x14, 0x14),
            Child = _overlayText,
            Visibility = Visibility.Collapsed
        };
        _root = new Grid
        {
            Background = BrushOr("BgBrush", 0x14, 0x14, 0x14)
        };
        _root.Children.Add(_webView);
        _root.Children.Add(_overlay);
        _host = new Window
        {
            Title = UiText.ProductName,
            Width = 980,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = _root,
            ShowInTaskbar = true,
            Background = BrushOr("BgBrush", 0x14, 0x14, 0x14)
        };
        if (Application.Current?.TryFindResource("AppWindow") is Style appWindow)
        {
            _host.Style = appWindow;
        }

        _host.Closing += (_, e) =>
        {
            if (Application.Current is App { IsExiting: true })
            {
                return;
            }

            e.Cancel = true;
            InvalidatePendingNavigation();
            HideLogin();
            _session.Cancel();
            ApplyHostPresentation();
            _login.Cancel();
        };
    }

    public bool IsLoginVisible => _shownForLogin && _host.IsVisible;

    public void RealizeDiagnosticHost()
    {
        _holdHostForDiagnostic = true;
        RunOnUi(() =>
        {
            _session.RealizeForDiagnostic();
            ApplyHostPresentation();
            EnsureHostHandle();
        });
    }

    public void HideDiagnosticHost()
    {
        _holdHostForDiagnostic = false;
        RunOnUi(() =>
        {
            _session.HideAfterSuccessfulSession();
            ApplyHostPresentation();
        });
    }

    public void PrepareLoginOnSameHost()
    {
        _holdHostForDiagnostic = true;
        RunOnUi(() =>
        {
            _session.ShowLoginOnSameHost();
            ApplyHostPresentation();
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await _coordinator.RunAsync(RunInitializationStagesAsync, cancellationToken);
        if (outcome.Success)
        {
            return;
        }

        LogStage(outcome.Stage);
        throw new WebViewInitializationException(outcome.Failure, outcome.Stage);
    }

    public async Task<bool> ShowLoginAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebViewInitializationException ex)
        {
            LogStage(ex.Stage);
            return false;
        }

        var (wait, started) = _login.BeginOrJoin();
        if (!started)
        {
            return await wait;
        }

        _auth.Invalidate();
        _probeGeneration = _login.Generation;
        _shownForLogin = true;
        _session.ShowLoginOnSameHost();
        await OnUiAsync(() =>
        {
            ApplyHostPresentation();
            _webView.CoreWebView2.Navigate(ChatGptEndpoints.LoginUrl);
        });
        using var reg = cancellationToken.Register(() =>
        {
            HideLogin();
            _login.Cancel();
        });

        try
        {
            return await wait.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _login.Cancel();
            return false;
        }
    }

    public async Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ShowLoginAsync(cancellationToken)
                ? WebViewInteractiveLoginResult.SignedIn
                : WebViewInteractiveLoginResult.Cancelled;
        }
        catch (OperationCanceledException)
        {
            return WebViewInteractiveLoginResult.Cancelled;
        }
        catch (Exception ex)
        {
            _log.Warn("webview interactive login unavailable: " + ex.GetType().Name);
            return WebViewInteractiveLoginResult.Unsupported;
        }
    }

    public void HideLogin()
    {
        _shownForLogin = false;
        RunOnUi(() =>
        {
            _session.Cancel();
            ApplyHostPresentation();
        });
    }

    public async Task<AccountStatus> ProbeSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!SessionProbePolicy.CanProbe(CurrentUri))
        {
            return new AccountStatus();
        }

        var response = await SendAsync("GET", ChatGptEndpoints.Session, cancellationToken: cancellationToken);
        if (!response.IsSuccess)
        {
            return new AccountStatus();
        }

        return AccountParser.ParseSession(ChatGptJson.ParseNode(response.Body));
    }

    public async Task<ProviderResponse> SendAsync(
        string method,
        string path,
        string? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogStage(WebViewDiagnosticStages.Cancelled);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Cancelled };
        }
        catch (WebViewInitializationException ex)
        {
            LogStage(ex.Stage);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.FromFailure(ex.Failure) };
        }

        if (!_holdHostForDiagnostic && !_shownForLogin)
        {
            HideDiagnosticHost();
        }

        if (!BackendTargetPolicy.TryValidate(path, out var safePath, out var targetError))
        {
            return new ProviderResponse { Status = 0, Error = targetError, SchemaMismatch = true };
        }

        if (_login.IsActive && !OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return new ProviderResponse { Status = 0, Error = "backend fetch blocked during interactive login" };
        }

        try
        {
            await EnsureOriginAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogStage(WebViewDiagnosticStages.Cancelled);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Cancelled };
        }
        catch (WebViewInitializationException ex)
        {
            LogStage(ex.Stage);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.FromFailure(ex.Failure) };
        }

        if (!OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return new ProviderResponse { Status = 0, Error = "unvalidated origin", SchemaMismatch = true };
        }

        return await _auth.SendAsync(ExecuteRawAsync, method, safePath, jsonBody, cancellationToken);
    }

    private async Task<WebViewInitializationOutcome> RunInitializationStagesAsync(CancellationToken cancellationToken)
    {
        LogStage(WebViewDiagnosticStages.InitializeStart);
        try
        {
            await OnUiAsync(() =>
            {
                if (!_coreReady && _ensureAttempted)
                {
                    RecreateWebViewControl();
                }

                if (!_session.IsRealized)
                {
                    _session.RealizeForDiagnostic();
                }

                ApplyHostPresentation();
                if (!EnsureHostHandle())
                {
                    throw new WebViewInitializationException(
                        WebViewInitializationFailure.HostNotRealized,
                        WebViewDiagnosticStages.HostInvalid);
                }
            });

            var host = WebViewInitializationCoordinator.RequireHostBeforeNavigation(_session.IsRealized);
            if (!host.Success)
            {
                LogStage(WebViewDiagnosticStages.HostInvalid);
                return host;
            }

            if (!_coreReady)
            {
                _ensureAttempted = true;
                CoreWebView2Environment env;
                try
                {
                    env = await WebViewBoundedWait.WaitAsync(
                        CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile),
                        WebViewBoundedWait.StageTimeout,
                        cancellationToken);
                }
                catch (WebView2RuntimeNotFoundException)
                {
                    LogStage(WebViewDiagnosticStages.RuntimeUnavailable);
                    return WebViewInitializationOutcome.RuntimeUnavailable();
                }

                LogStage(WebViewDiagnosticStages.EnvironmentReady);

                var ensure = await OnUiAsync(() =>
                {
                    if (!EnsureHostHandle())
                    {
                        throw new WebViewInitializationException(
                            WebViewInitializationFailure.HostNotRealized,
                            WebViewDiagnosticStages.HostInvalid);
                    }

                    return _webView.EnsureCoreWebView2Async(env);
                });
                await WebViewBoundedWait.WaitAsync(ensure, WebViewBoundedWait.StageTimeout, cancellationToken);

                await OnUiAsync(ConfigureCore);
                _coreReady = true;
                LogStage(WebViewDiagnosticStages.CoreReady);
            }

            LogStage(WebViewDiagnosticStages.HomeNavigationStart);
            var navigation = await OnUiAsync(() => StartNavigation(ChatGptEndpoints.HomeUrl));
            var navigated = await CompleteNavigationAsync(navigation, cancellationToken);
            if (!navigated.Succeeded)
            {
                LogNavigationFailure(navigated);
                return WebViewInitializationOutcome.NavigationFailed();
            }

            LogStage(WebViewDiagnosticStages.HomeNavigationComplete);
            return WebViewInitializationOutcome.Succeeded();
        }
        catch (WebViewInitializationException ex)
        {
            LogStage(ex.Stage);
            return new WebViewInitializationOutcome(false, ex.Failure, ex.Stage);
        }
        catch (OperationCanceledException)
        {
            LogStage(WebViewDiagnosticStages.Cancelled);
            return WebViewInitializationOutcome.Cancelled();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            LogStage(WebViewDiagnosticStages.RuntimeUnavailable);
            return WebViewInitializationOutcome.RuntimeUnavailable();
        }
        catch (Exception ex)
        {
            _log.Warn("webview initialization failed: " + ex.GetType().Name);
            return WebViewInitializationOutcome.EnvironmentFailed();
        }
    }

    private async Task EnsureOriginAsync(CancellationToken cancellationToken)
    {
        if (!_login.MayNavigateAwayToProbe())
        {
            return;
        }

        if (OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return;
        }

        await NavigateAndWaitAsync(ChatGptEndpoints.HomeUrl, cancellationToken);
    }

    private async Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken)
    {
        LogStage(WebViewDiagnosticStages.HomeNavigationStart);
        var navigation = await OnUiAsync(() => StartNavigation(url));
        var navigated = await CompleteNavigationAsync(navigation, cancellationToken);
        if (!navigated.Succeeded)
        {
            LogNavigationFailure(navigated);
            throw new WebViewInitializationException(navigated.Failure, navigated.Stage);
        }

        LogStage(WebViewDiagnosticStages.HomeNavigationComplete);
    }

    private async Task<WebViewNavigationResult> CompleteNavigationAsync(
        Task<WebViewNavigationResult> navigation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WebViewBoundedWait.WaitNavigationAsync(
                navigation,
                WebViewBoundedWait.StageTimeout,
                cancellationToken);
        }
        catch (WebViewInitializationException)
        {
            await OnUiAsync(InvalidatePendingNavigation);
            LogStage(WebViewDiagnosticStages.NavigationTimeout);
            throw;
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(InvalidatePendingNavigation);
            LogStage(WebViewDiagnosticStages.Cancelled);
            throw;
        }
    }

    private Task<WebViewNavigationResult> StartNavigation(string url)
    {
        InvalidatePendingNavigation();
        var (epoch, task) = _navigation.Begin();
        void OnEpochComplete(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var status = e.IsSuccess ? null : e.WebErrorStatus.ToString();
            _navigation.TryComplete(epoch, e.IsSuccess, status);
            RemoveNavigationHandler();
        }

        _detachNavigationHandler = () =>
        {
            if (_webView.CoreWebView2 is { } core)
            {
                core.NavigationCompleted -= OnEpochComplete;
            }
        };
        _webView.CoreWebView2.NavigationCompleted += OnEpochComplete;
        _webView.CoreWebView2.Navigate(url);
        return task;
    }

    private void RemoveNavigationHandler()
    {
        var detach = _detachNavigationHandler;
        _detachNavigationHandler = null;
        detach?.Invoke();
    }

    private void InvalidatePendingNavigation()
    {
        RemoveNavigationHandler();
        _navigation.Invalidate();
    }

    private void LogNavigationFailure(WebViewNavigationResult result)
    {
        if (result.WebErrorStatus is { Length: > 0 } status)
        {
            _log.Info("webview diagnostic stage=" + WebViewDiagnosticStages.HomeNavigationFailed + " status=" + status);
            return;
        }

        LogStage(WebViewDiagnosticStages.HomeNavigationFailed);
    }

    private void ConfigureCore()
    {
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        if (_handlersAttached)
        {
            return;
        }

        _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _webView.CoreWebView2.SourceChanged += OnSourceChanged;
        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _handlersAttached = true;
    }

    private void RecreateWebViewControl()
    {
        _root.Children.Remove(_webView);
        _webView.Dispose();
        _webView = CreateWebView();
        _root.Children.Insert(0, _webView);
        _handlersAttached = false;
        _coreReady = false;
    }

    private static WebView2 CreateWebView() =>
        new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(20, 20, 20) };

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        _login.Observe(CurrentUri, navigationCompleted: false);
        if (_login.ShouldStopProbe(CurrentUri))
        {
            _probeGeneration = -1;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_login.IsActive)
        {
            if (OriginPolicy.AllowsBackendFetch(e.Uri))
            {
                _webView.CoreWebView2.Navigate(e.Uri);
            }

            return;
        }

        if (OriginPolicy.AllowsInteractiveNavigation(e.Uri))
        {
            _webView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        var generation = _login.Generation;
        if (!_login.AcceptsProbe(generation, CurrentUri, navigationCompleted: true))
        {
            return;
        }

        try
        {
            foreach (var delay in SessionProbePolicy.Backoff)
            {
                if (generation != _login.Generation || _login.ShouldStopProbe(CurrentUri))
                {
                    return;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None);
                }

                if (generation != _login.Generation || !SessionProbePolicy.CanProbe(CurrentUri))
                {
                    return;
                }

                var status = await ProbeSessionAsync();
                if (status.IsSignedIn && generation == _login.Generation)
                {
                    HideLogin();
                    _login.Complete(true);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("login session probe failed: " + AppLog.Sanitize(ex.Message));
        }
    }

    private Task<ProviderResponse> ExecuteRawAsync(
        string method,
        string path,
        string? jsonBody,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!BackendTargetPolicy.TryValidate(path, out var safePath, out var error)
            || !OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return Task.FromResult(new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true });
        }

        return ExecuteScriptFetchAsync(method, safePath, jsonBody, accessToken, cancellationToken);
    }

    private async Task<ProviderResponse> ExecuteScriptFetchAsync(
        string method,
        string path,
        string? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var script = WebViewFetchScript.Build(method, path, body, accessToken);
        try
        {
            var execute = await OnUiAsync(() => _webView.ExecuteScriptAsync(script));
            string raw;
            try
            {
                raw = await WebViewBoundedWait.WaitRequestAsync(
                    execute,
                    WebViewFetchScript.ScriptGuardTimeout,
                    cancellationToken);
            }
            catch (WebViewInitializationException)
            {
                LogStage(WebViewDiagnosticStages.RequestTimeoutFor(WebViewFetchScript.SafeOperation(path)));
                return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.ScriptExecutionTimeout };
            }
            catch (OperationCanceledException)
            {
                LogStage(WebViewDiagnosticStages.Cancelled);
                return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Cancelled };
            }

            var response = WebViewFetchScript.MapScriptResult(DecodeScriptResult(raw));
            if (response.Error is WebViewInitializationCodes.RequestTimeout
                or WebViewInitializationCodes.ScriptExecutionTimeout)
            {
                LogStage(WebViewDiagnosticStages.RequestTimeoutFor(WebViewFetchScript.SafeOperation(path)));
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            LogStage(WebViewDiagnosticStages.Cancelled);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Cancelled };
        }
        catch (Exception ex)
        {
            _log.Warn("webview fetch failed: " + ex.GetType().Name);
            return new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestFailed };
        }
    }

    private string? CurrentUri => _webView.Source?.AbsoluteUri;

    private static string DecodeScriptResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw.Trim('"');
        }
        catch
        {
            return raw;
        }
    }

    private void ApplyHostPresentation()
    {
        _overlayText.Text = UiText.CheckingChatGptSession;
        _overlayText.Foreground = BrushOr("TextBrush", 0xEE, 0xF1, 0xF6);
        _overlay.Background = BrushOr("BgBrush", 0x14, 0x14, 0x14);
        _overlay.Visibility = _session.CheckingOverlayVisible ? Visibility.Visible : Visibility.Collapsed;
        _host.Title = _session.LoginSurfaceVisible
            ? "CycleArc — ChatGPT sign-in"
            : UiText.CheckingChatGptSession;

        if (_session.IsVisible)
        {
            if (!_host.IsVisible)
            {
                _host.Show();
            }

            if (_session.LoginSurfaceVisible)
            {
                _host.Activate();
            }
        }
        else if (_host.IsVisible)
        {
            _host.Hide();
        }
    }

    private bool EnsureHostHandle()
    {
        if (!_session.IsRealized)
        {
            return false;
        }

        if (!_host.IsVisible)
        {
            _host.Show();
        }

        var hwnd = new WindowInteropHelper(_host).EnsureHandle();
        return hwnd != IntPtr.Zero;
    }

    private void LogStage(string stage) => _log.Info("webview diagnostic stage=" + stage);

    private void RunOnUi(Action action)
    {
        var dispatcher = _host.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private Task OnUiAsync(Action action)
    {
        var dispatcher = _host.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var dispatcher = _host.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }

        return dispatcher.InvokeAsync(func).Task;
    }

    private static Brush BrushOr(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    public void Dispose()
    {
        _webView.Dispose();
    }
}
