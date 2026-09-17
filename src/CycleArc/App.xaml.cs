using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;
using CycleArc.Updates;

namespace CycleArc;

public partial class App : Application
{
    public DesktopInstanceLease? InstanceLease { get; set; }
    private DesktopInstanceServer? _instanceServer;
    private AppSettings _settings = AppSettings.CreateDefaults();
    private SettingsStore _settingsStore = null!;
    private AppLog _log = null!;
    private TrayController _tray = null!;
    private CodexAccountManager _codex = null!;
    private CodexRefreshCoordinator _refresh = null!;
    private readonly CodexExecutableLocator _codexLocator = new(new WindowsCodexFileSystem());
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _codexTimer = new();
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _passiveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AppUpdateCoordinator? _updates;
    private UpdateWindow? _updateWindow;
    private Task _updateTask = Task.CompletedTask;
    private Task _updateWindowTask = Task.CompletedTask;
    private string? _notifiedVersion;
    private Task _passiveTask = Task.CompletedTask;
    private Task _creditUseTask = Task.CompletedTask;
    private FlyoutWindow? _flyout;
    private AccountsWindow? _accountsWindow;
    private Task _discoveryTask = Task.CompletedTask;
    private Task _claudeIdentityTask = Task.CompletedTask;
    private ClaudeConnectionService _claudeConnections = null!;
    private ClaudeConnectionWindow? _claudeWindow;
    private FloatingWidgetController? _widgetController;
    private DesktopEnvironmentMonitor? _environment;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if CYCLEARC_TEST_FAIL_STARTUP
        // Test-only build flavour: quit before this desktop can report readiness, so the
        // installed-app verification can prove that the real update supervisor detects a
        // failed start and restores the previous installation. Never compiled into a
        // shipped build; see scripts/Verify-InstalledUpdate.ps1.
        Environment.Exit(3);
#endif
        if (InstanceLease is null) { Shutdown(1); return; }
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        var firstUse = !_settings.FirstRunCompleted;
        UiText.SetLanguage(_settings.UiLanguage);
        _log = new AppLog();
        if (_settingsStore.RecoveredFromBackup) _log.Warn("Settings restored from backup");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        // Retain existing preferences and history files, but never start retired collectors.
        _settings.TaskbarStatusEnabled = false; // Retired overlay: Windows owns notification icon placement.
        _settings.AutoSync = false;
        _settings.CompanionConnectOptIn = false;
        _settings.FirstRunCompleted = true;
        _settingsStore.Save(_settings);
        LegacyCompanionCleanup.Unregister(_log.Warn);
        StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
        ApplyTheme(_settings.Theme);
        _tray = new TrayController();
        _tray.RebuildMenu(_settings.StartWithWindows);
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ShowMain;
        _tray.SyncRequested += () => _ = RefreshCodexAsync();
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenLogsRequested += OpenLogs;
        _updates = new AppUpdateCoordinator(new VelopackUpdateClient());
        _updates.Changed += OnUpdateChanged;
        _tray.UpdatesRequested += ShowUpdates;
        _tray.AboutRequested += () => new AboutWindow(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            "Codex App Server · Claude subscription usage",
            InstalledApp.IsManaged ? null : DesktopBootstrap.InstallSelectedVersion, ShowUpdates).Show();
        _tray.StartupToggled += enabled =>
        {
            _settings.StartWithWindows = enabled;
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
            _settingsStore.Save(_settings);
        };
        _tray.ExitRequested += ExitApp;
        _tray.CloseWidgetRequested += CloseWidget;
        var accounts = new CodexAccountStore();
        _claudeConnections = new ClaudeConnectionService(accounts, callbackExecutable: InstalledApp.CallbackPath);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        try
        {
            _codex = new CodexAccountManager(accounts, CodexHomeDiscovery.DefaultHome,
                [new CodexUsageProvider(profile => new CodexQuotaService(_codexLocator, new CodexAppServerClient(),
                    new CodexSnapshotStore(accounts.SnapshotPath(profile)), version, _log.Info, profile: profile),
                    () => _settings.CodexExePath), new ClaudeUsageProvider(accounts, connections: _claudeConnections,
                        desktopFactory: profile => new ClaudeDesktopUsageCollector(accounts, profile.Id,
                            _claudeConnections.VerifyUsageIdentityAsync),
                        liveFactory: profile => new ClaudeLiveUsageCollector(accounts, profile.Id,
                            new ClaudeOAuthUsageClient(new ClaudeDesktopCredentialReader()))) ]);
        }
        catch
        {
            _log.Warn("Account registry could not be loaded; preserved for recovery");
            System.Windows.MessageBox.Show(UiText.T("The account list could not be loaded. Its files have been preserved. Restore codex-accounts.json from a valid backup and restart.",
                "계정 목록을 불러오지 못했습니다. 파일은 보존했습니다. 유효한 백업으로 codex-accounts.json을 복원한 뒤 다시 시작하세요."), UiText.ProductName);
            _tray.Dispose();
            Shutdown();
            return;
        }
        if (accounts.RecoveredFromBackup) _log.Warn("Account registry restored from backup");
        _refresh = _codex.Refresh;
        _codex.Changed += () => Dispatcher.BeginInvoke(RefreshSnapshot);
        _refresh.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_refresh.IsRefreshing && !IsExiting) ApplyRefreshSchedule();
            RefreshSnapshot();
        });
        ApplyRefreshSchedule();
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync(automatic: true);

        _displayTimer.Tick += (_, _) => RefreshSnapshot();
        _displayTimer.Start();
        _passiveTimer.Tick += (_, _) =>
        {
            if (!IsExiting) _widgetController?.MaintainVisibility();
            if (!IsExiting && _passiveTask.IsCompleted) _passiveTask = ReadPassiveUsageAsync();
        };
        _passiveTimer.Start();
        RefreshSnapshot();
        ApplyWidget();
        _environment = new DesktopEnvironmentMonitor(Dispatcher, OnSystemThemeChanged, OnDisplayChanged,
            desktopRestored: OnDesktopRestored);
        if (!e.Args.Contains("--autorun", StringComparer.Ordinal)
            && (firstUse || InstalledApp.IsFirstRun || e.Args.Contains("--show", StringComparer.Ordinal))) ShowMain();
        _ = RefreshCodexAsync();
        _discoveryTask = DiscoverStartupAsync();
        var claudeProfiles = _codex.Accounts.Where(account => account.Profile.Provider == UsageProviderId.Claude)
            .Select(account => account.Profile.Id).ToArray();
        _claudeIdentityTask = Task.Run(async () =>
        {
            foreach (var id in claudeProfiles)
            {
                if (_lifetime.IsCancellationRequested) break;
                try
                {
                    if (new ClaudeConnectionStore(accounts, id).Read().Binding is { Disconnected: false })
                        await _claudeConnections.InspectAsync(id, _lifetime.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { _log.Warn("Claude login metadata unavailable"); }
            }
        });
        if (e.Args.Contains("--accounts", StringComparer.Ordinal)) Dispatcher.BeginInvoke(() => ShowAccounts());
        // Readiness means settings/accounts/tray and the UI dispatcher are initialized.
        _instanceServer = new DesktopInstanceServer();
        _ = _instanceServer.StartAsync(
            onActivate: () => { Dispatcher.BeginInvoke(() => { if (!IsExiting) ShowMain(); }); return Task.CompletedTask; },
            onShutdown: () => { Dispatcher.BeginInvoke(ExitApp); return Task.CompletedTask; });
        _ = MigrateTrayAsync();
        if (InstalledApp.SupportsUpdates)
        {
            _updateTimer.Tick += (_, _) =>
            {
                if (!IsExiting && _updateTask.IsCompleted) _updateTask = CheckUpdatesAsync(TimeSpan.Zero);
            };
            _updateTimer.Start();
            _updateTask = CheckUpdatesAsync(TimeSpan.FromSeconds(20));
        }
#if CYCLEARC_TEST_E2E
        if (Environment.GetEnvironmentVariable("CYCLEARC_TEST_UPDATE_DRIVE") == "1") StartTestUpdateDrive();
#endif
    }

#if CYCLEARC_TEST_E2E
    // Test-only build flavour: press the same production buttons a person presses, so the
    // installed-app verification exercises the real coordinator, client and supervisor
    // instead of calling Update.exe directly. Never compiled into a shipped build.
    private void StartTestUpdateDrive() => _ = Task.Run(async () =>
    {
        try
        {
            await Dispatcher.InvokeAsync(ShowUpdates);
            if (!await WaitForTestUpdateStateAsync(AppUpdateState.Available, TimeSpan.FromMinutes(2)))
            {
                _log.Warn("test update drive: no release became available");
                return;
            }
            await Dispatcher.InvokeAsync(() => _updateWindow?.ClickAction());
            if (!await WaitForTestUpdateStateAsync(AppUpdateState.Ready, TimeSpan.FromMinutes(5)))
            {
                _log.Warn("test update drive: the download did not become ready");
                return;
            }
            await Dispatcher.InvokeAsync(() => _updateWindow?.ClickAction());
        }
        catch (Exception ex) { _log.Warn("test update drive stopped: " + ex.GetType().Name); }
    });

    private async Task<bool> WaitForTestUpdateStateAsync(AppUpdateState state, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (IsExiting) return false;
            if (_updates?.State == state) return true;
            if (_updates?.State == AppUpdateState.Failed) return false;
            await Task.Delay(200);
        }
        return false;
    }
#endif

    private async Task CheckUpdatesAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token);
            await Task.Run(ManagedUpdateSupervisor.CleanupCompleted, _lifetime.Token);
            if (!IsExiting && _updates is not null) await _updates.CheckAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
    }

    private void OnUpdateChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnUpdateChanged); return; }
        if (IsExiting || _updates?.State != AppUpdateState.Available || _updates.Release is not { } release
            || _notifiedVersion == release.Version) return;
        _notifiedVersion = release.Version;
        if (_updateWindow is null)
            _tray.Balloon(UiText.T("CycleArc update available", "CycleArc 새 버전이 있습니다"),
                UiText.T($"Version {release.Version} is ready to download. Click to review.",
                    $"{release.Version} 버전을 다운로드할 수 있습니다. 클릭해서 확인하세요."));
    }

    private void ShowUpdates()
    {
        if (IsExiting || _updates is null) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        _updateWindow = new UpdateWindow(_updates, ExitApp, _lifetime.Token);
        _updateWindow.OperationStarted += operation => _updateWindowTask = operation;
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        if (_updates.Release is null) _updateTask = _updates.CheckAsync(_lifetime.Token);
    }

    private async Task MigrateTrayAsync()
    {
        var executable = InstalledApp.CallbackPath ?? DesktopBootstrap.ExecutablePath;
        if (!string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase)) return;
        var oldPaths = DesktopBootstrap.ReadMigrationPaths()
            .Concat(InstalledApp.IsManaged ? [DesktopBootstrap.ExecutablePath] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (oldPaths.Length == 0) return;
        for (var attempt = 0; attempt < 5 && !IsExiting; attempt++)
        {
            if (TrayInstallationMigration.Run(executable, oldPaths, _log.Warn)) return;
            try { await Task.Delay(1000, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyRefreshSchedule()
    {
        _codexTimer.Stop();
        _codexTimer.Interval = TimeSpan.FromMinutes(_settings.CodexRefreshIntervalMinutes);
        if (!IsExiting) _codexTimer.Start();
    }

    private async Task ReadPassiveUsageAsync()
    {
        try { await Task.Run(() => _codex.RefreshPassiveAsync(_lifetime.Token), _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { _log.Warn("Passive usage inbox could not be read"); }
    }

    private async Task RefreshCodexAsync(bool automatic = false)
    {
        if (IsExiting) return;
        var interval = _codexTimer.Interval;
        if (automatic && !_codex.ShouldRefresh(DateTimeOffset.Now, interval)) return;
        try { await Task.Run(() => automatic ? _codex.RefreshAutomaticallyAsync(interval, _lifetime.Token)
            : _codex.RefreshManuallyAsync(_lifetime.Token), _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("Codex refresh failed", ex); }
    }

    private void RefreshSnapshot()
    {
        if (IsExiting || _codex is null || _refresh is null) return;
        var accounts = _codex.Accounts;
        var overview = UsageAccountOverview.Create(accounts, _codex.SelectedId, _settings.UsagePeriod);
        _tray.Update(overview, _settings.TrayIconStyle);
        _flyout?.BindAccounts(overview.Accounts, overview.SelectedId, _refresh.IsRefreshing, overview.Preference);
        _accountsWindow?.Bind(accounts, overview.SelectedId);
        _widgetController?.Update(_settings, overview);
    }

    private void ToggleFlyout()
    {
        EnsureFlyout();
        if (_flyout!.IsVisible) { _flyout.Hide(); return; }
        _flyout.ApplyWindowSettings(_settings);
        RefreshSnapshot();
        _flyout.Show();
        PlaceFlyout(_flyout);
        _flyout.Activate();
        if (_codex.ShouldRefresh(DateTimeOffset.Now, _codexTimer.Interval))
            _ = RefreshCodexAsync(automatic: true);
    }

    private void EnsureFlyout()
    {
        if (_flyout is not null) return;
        _flyout = new FlyoutWindow();
        _flyout.RedeemAccountCredit = async (profileId, creditId) =>
        {
            if (IsExiting) return CreditRedemptionOutcome.Unavailable;
            var useTask = Task.Run(() => _codex.ConsumeCreditAsync(profileId, creditId, _lifetime.Token));
            _creditUseTask = useTask;
            var outcome = await useTask;
            if (IsExiting) return outcome;
            await _refresh.WaitForIdleAsync();
            await RefreshCodexAsync();
            return outcome;
        };
        _flyout.SyncRequested += () => _ = RefreshCodexAsync();
        _flyout.AccountsRequested += () => ShowAccounts();
        _flyout.AccountSelected += id => _codex.Select(id);
        _flyout.UsagePeriodChanged += preference =>
        {
            if (IsExiting || _settings.UsagePeriod == preference) return;
            var previous = _settings.UsagePeriod;
            _settings.UsagePeriod = preference;
            try { _settingsStore.Save(_settings); }
            catch
            {
                _settings.UsagePeriod = previous;
                _flyout.ApplyUsagePeriod(previous);
                throw;
            }
            RefreshSnapshot();
        };
        _flyout.SettingsRequested += ShowSettings;
        _flyout.PinChanged += pinned => { _settings.FlyoutPinned = pinned; _settingsStore.Save(_settings); };
        _flyout.ZoomChanged += percent => { _settings.FlyoutZoomPercent = percent; _settingsStore.Save(_settings); };
        _flyout.PositionChanged += (left, top) =>
        {
            _settings.FlyoutLeft = left;
            _settings.FlyoutTop = top;
            _settings.FlyoutPositionConfigured = true;
            _settingsStore.Save(_settings);
        };
    }

    private void ShowMain()
    {
        EnsureFlyout();
        if (_flyout!.IsVisible)
        {
            if (_flyout.WindowState == WindowState.Minimized) _flyout.WindowState = WindowState.Normal;
            _flyout.Activate();
            return;
        }
        ToggleFlyout();
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings);
        window.AccountsRequested += () => ShowAccounts(window);
        if (_flyout?.IsVisible == true) window.Owner = _flyout;
        window.Saved += settings =>
        {
            if (window.ResetWidgetPositionOnSave)
            {
                var work = SystemParameters.WorkArea;
                settings.WidgetPixelLeft = null;
                settings.WidgetPixelTop = null;
                settings.WidgetLeft = work.Left + 40;
                settings.WidgetTop = work.Top + 40;
            }
            _settings = settings;
            _settingsStore.Save(settings);
            ApplyRefreshSchedule();
            UiText.SetLanguage(settings.UiLanguage);
            ApplyTheme(settings.Theme);
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), settings);
            _tray.RebuildMenu(settings.StartWithWindows);
            _flyout?.ApplyWindowSettings(settings);
            if (window.ResetWidgetPositionOnSave) ApplyWidgetWithReset();
            else ApplyWidget();
            RefreshSnapshot();
            _ = RefreshCodexAsync();
        };
        window.OpenLogsRequested += OpenLogs;
        window.ShowDialog();
    }

    private async Task DiscoverStartupAsync()
    {
        try
        {
            var result = await Task.Run(() => _codex.DiscoverAsync(CodexHomeDiscovery.Candidates(), true, _lifetime.Token));
            if (result.Failed > 0) _log.Warn("Existing Codex account discovery incomplete");
            if (result.Added > 0)
            {
                await _refresh.WaitForIdleAsync();
                await RefreshCodexAsync(automatic: true);
            }
        }
        catch (OperationCanceledException) { }
        catch { _log.Warn("Existing Codex account discovery failed"); }
    }

    private void ShowAccounts(Window? owner = null)
    {
        if (_accountsWindow is not null) { _accountsWindow.Activate(); return; }
        var window = new AccountsWindow();
        _accountsWindow = window;
        if (owner is not null) window.Owner = owner;
        else if (_flyout?.IsVisible == true) window.Owner = _flyout;
        window.LogFailure = _log.Warn;
        window.SelectAccount = id => _codex.Select(id);
        window.RenameAccount = (id, label) => _codex.Rename(id, label);
        window.MoveAccount = (id, direction) => _codex.Move(id, direction);
        window.RemoveAccount = id => _codex.Remove(id);
        window.AddClaudeAccount = label => _codex.ConfigureNewClaude(label, profile => window.ConfigureClaude?.Invoke(profile.Id));
        window.ConfigureClaude = id =>
        {
            var profile = _codex.Accounts.FirstOrDefault(account => account.Profile.Id == id)?.Profile;
            if (profile?.Provider == UsageProviderId.Claude && Environment.ProcessPath is { } executable)
            {
                _claudeWindow = new ClaudeConnectionWindow(profile, executable, _claudeConnections, _lifetime.Token) { Owner = window };
                try { _claudeWindow.ShowDialog(); }
                finally { _claudeWindow = null; }
                _ = ReadPassiveUsageAsync();
            }
        };
        window.SignIn = async (id, label, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await Task.Run(() => _codex.LoginAsync(id, label, async (uri, ct) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    window.BrowserOpened();
                });
            }, linked.Token), linked.Token);
        };
        window.Discover = async (home, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            var result = await Task.Run(() => _codex.DiscoverAsync(home is null ? CodexHomeDiscovery.Candidates() : [home], false, linked.Token), linked.Token);
            if (result.Added > 0)
            {
                await _refresh.WaitForIdleAsync().WaitAsync(linked.Token);
                await Task.Run(() => _codex.RefreshManuallyAsync(linked.Token), linked.Token);
            }
            return result;
        };
        window.Bind(_codex.Accounts, UsageAccountOverview.Create(_codex.Accounts, _codex.SelectedId, _settings.UsagePeriod).SelectedId);
        window.Closed += (_, _) => _accountsWindow = null;
        window.ShowDialog();
    }

    private void PlaceFlyout(FlyoutWindow flyout)
    {
        if (FlyoutWindowState.UseSavedPosition(_settings.FlyoutPositionConfigured))
        {
            flyout.RestorePosition(_settings.FlyoutLeft, _settings.FlyoutTop);
            return;
        }

        flyout.PlaceNearTaskbar();
    }

    private void OpenLogs()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.DirectoryPath,
            UseShellExecute = true
        });
    }

    private void CloseWidget()
    {
        _settings.FloatingWidgetEnabled = false;
        _settingsStore.Save(_settings);
        ApplyWidget();
    }

    private void ApplyWidget() => ApplyWidgetCore(false);
    private void ApplyWidgetWithReset() => ApplyWidgetCore(true);

    private void ApplyWidgetCore(bool recreateWidget)
    {
        _widgetController ??= new FloatingWidgetController(widget =>
        {
            widget.Moved += (left, top) =>
            {
                if (IsExiting || !ReferenceEquals(_widgetController?.CurrentWindow, widget)) return;
                _settings.WidgetLeft = left;
                _settings.WidgetTop = top;
                if (widget.PixelPosition is { } pixels)
                {
                    _settings.WidgetPixelLeft = pixels.X;
                    _settings.WidgetPixelTop = pixels.Y;
                }
                _settingsStore.Save(_settings);
            };
            widget.FlyoutRequested += ShowMain;
            widget.RefreshRequested += () => _ = RefreshCodexAsync();
            widget.ContextMenuRequested += () => _tray.ShowWidgetContextMenu();
        }, _log.Info);
        var overview = UsageAccountOverview.Create(_codex.Accounts, _codex.SelectedId, _settings.UsagePeriod);
        if (recreateWidget)
            _widgetController.Recreate(_settings, overview);
        else
            _widgetController.Update(_settings, overview, applySettings: true);
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark
            || (theme == AppTheme.System && IsSystemDark());
        var app = Current;
        app.Resources["BgBrush"] = new SolidColorBrush(dark ? MediaColor(18, 20, 24) : MediaColor(245, 247, 250));
        app.Resources["CardBrush"] = new SolidColorBrush(dark ? MediaColor(27, 31, 39) : MediaColor(255, 255, 255));
        app.Resources["PanelBrush"] = new SolidColorBrush(dark ? MediaColor(33, 38, 50) : MediaColor(248, 249, 251));
        app.Resources["TextBrush"] = new SolidColorBrush(dark ? MediaColor(238, 241, 246) : MediaColor(23, 27, 34));
        app.Resources["MutedBrush"] = new SolidColorBrush(dark ? MediaColor(139, 147, 167) : MediaColor(90, 98, 114));
        app.Resources["StaleBrush"] = new SolidColorBrush(dark ? MediaColor(251, 191, 36) : MediaColor(146, 79, 0));
        app.Resources["LineBrush"] = new SolidColorBrush(dark ? MediaColor(42, 49, 64) : MediaColor(213, 218, 227));
        app.Resources["ControlBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(255, 255, 255));
        app.Resources["GhostBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(232, 236, 242));
        app.Resources["SelectionBrush"] = new SolidColorBrush(dark ? MediaColor(29, 78, 216) : MediaColor(191, 219, 254));
        app.Resources["DisabledBrush"] = new SolidColorBrush(dark ? MediaColor(107, 114, 128) : MediaColor(154, 163, 178));
        app.Resources["CheckBoxBackgroundBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(255, 255, 255));
        app.Resources["CheckBoxBorderBrush"] = new SolidColorBrush(dark ? MediaColor(139, 147, 167) : MediaColor(90, 98, 114));
        app.Resources["CheckBoxDisabledCheckedBrush"] = new SolidColorBrush(dark ? MediaColor(59, 82, 122) : MediaColor(147, 197, 253));
        app.Resources["ProviderBadgeBackgroundBrush"] = new SolidColorBrush(dark ? MediaColor(36, 53, 77) : MediaColor(229, 238, 255));
        app.Resources["ProviderBadgeBorderBrush"] = new SolidColorBrush(dark ? MediaColor(54, 84, 123) : MediaColor(172, 195, 228));
        app.Resources["ProviderBadgeTextBrush"] = new SolidColorBrush(dark ? MediaColor(217, 231, 255) : MediaColor(36, 74, 128));
    }

    private void OnSystemThemeChanged()
    {
        if (IsExiting) return;
        // Tray contrast follows Windows even when the app theme is fixed.
        if (_settings.Theme == AppTheme.System) ApplyTheme(AppTheme.System);
        RefreshSnapshot();
    }

    private void OnDisplayChanged()
    {
        if (IsExiting) return;
        _widgetController?.RecoverAfterEnvironmentChange();
        _flyout?.RefreshWorkArea();
    }

    private void OnDesktopRestored()
    {
        if (!IsExiting) _widgetController?.RecoverAfterEnvironmentChange();
    }

    private static Color MediaColor(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    private async void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        try
        {
            RunExitCleanup(() => _widgetController?.Dispose(), "Widget shutdown failed");
            RunExitCleanup(() => _environment?.Dispose(), "Environment monitor shutdown failed");
            RunExitCleanup(_codexTimer.Stop, "Codex timer shutdown failed");
            RunExitCleanup(_displayTimer.Stop, "Display timer shutdown failed");
            RunExitCleanup(_passiveTimer.Stop, "Passive timer shutdown failed");
            RunExitCleanup(_updateTimer.Stop, "Update timer shutdown failed");
            RunExitCleanup(_lifetime.Cancel, "Lifetime cancellation failed");
            RunExitCleanup(() => _accountsWindow?.CancelOperation(), "Account cancellation failed");
            RunExitCleanup(() => _claudeWindow?.CancelOperation(), "Claude cancellation failed");
            RunExitCleanup(() => _flyout?.Hide(), "Popup shutdown failed");
            // Let the existing bounded client stop and reap its app-server process.
            await Task.WhenAll(_refresh.WaitForIdleAsync(), _creditUseTask, _discoveryTask, _passiveTask, _claudeIdentityTask, _updateTask, _updateWindowTask,
                _claudeWindow?.ActiveOperation ?? Task.CompletedTask,
                _accountsWindow?.ActiveOperation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { LogExitFailure("App shutdown wait timed out"); }
        catch (Exception ex) { LogExitFailure("App shutdown wait failed", ex); }
        finally
        {
            try { RunExitCleanup(() => _tray?.Dispose(), "Tray shutdown failed"); }
            finally { Shutdown(); }
        }
    }

    private void RunExitCleanup(Action cleanup, string failure)
    {
        try { cleanup(); }
        catch (Exception ex) { LogExitFailure(failure, ex); }
    }

    private void LogExitFailure(string message, Exception? error = null)
    {
        try { _log?.Error(message, error); }
        catch { /* A log write failure must not prevent process shutdown. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RunExitCleanup(() => _widgetController?.Dispose(), "Widget shutdown failed");
        RunExitCleanup(() => _environment?.Dispose(), "Environment monitor shutdown failed");
        RunExitCleanup(() => _instanceServer?.DisposeAsync().AsTask().GetAwaiter().GetResult(), "Desktop IPC shutdown failed");
        RunExitCleanup(() => InstanceLease?.Dispose(), "Single-instance mutex release failed");
        InstanceLease = null;
        base.OnExit(e);
    }
}
