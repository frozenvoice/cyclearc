using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexMeter.Codex;

namespace CodexMeter;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
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
    private readonly OnceEventSubscription _widgetEvents = new();
    private Task _creditUseTask = Task.CompletedTask;
    private FlyoutWindow? _flyout;
    private AccountsWindow? _accountsWindow;
    private Task _discoveryTask = Task.CompletedTask;
    private FloatingWidget? _widget;
    private DesktopEnvironmentMonitor? _environment;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Keep the legacy mutex name so an older executable cannot run beside this one.
        _mutex = new Mutex(true, LegacyInstallation.SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }
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
        _tray.AboutRequested += () => new AboutWindow(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            UiText.T("Codex App Server", "Codex App Server")).Show();
        _tray.StartupToggled += enabled =>
        {
            _settings.StartWithWindows = enabled;
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
            _settingsStore.Save(_settings);
        };
        _tray.ExitRequested += ExitApp;
        _tray.CloseWidgetRequested += CloseWidget;
        var accounts = new CodexAccountStore();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        try
        {
            _codex = new CodexAccountManager(accounts, CodexHomeDiscovery.DefaultHome,
                profile => new CodexQuotaService(_codexLocator, new CodexAppServerClient(),
                    new CodexSnapshotStore(accounts.SnapshotPath(profile)), version, _log.Info, profile: profile),
                () => _settings.CodexExePath);
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
        RefreshSnapshot();
        ApplyWidget();
        _environment = new DesktopEnvironmentMonitor(Dispatcher, OnSystemThemeChanged, OnDisplayChanged);
        if (firstUse || e.Args.Contains("--show", StringComparer.Ordinal)) ShowMain();
        _ = RefreshCodexAsync();
        _discoveryTask = DiscoverStartupAsync();
        if (e.Args.Contains("--accounts", StringComparer.Ordinal)) Dispatcher.BeginInvoke(() => ShowAccounts());
    }

    private void ApplyRefreshSchedule()
    {
        _codexTimer.Stop();
        _codexTimer.Interval = TimeSpan.FromMinutes(_settings.CodexRefreshIntervalMinutes);
        if (!IsExiting) _codexTimer.Start();
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
        _tray.Update(_codex.Snapshot, _settings.TrayIconStyle, accounts.Count > 1 ? _codex.Selected?.DisplayName : null);
        _flyout?.BindAccounts(accounts, _codex.SelectedId, _refresh.IsRefreshing);
        _accountsWindow?.Bind(accounts, _codex.SelectedId);
        _widget?.BindAccount(_codex.Selected);
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
        if (_flyout!.IsVisible) { _flyout.Activate(); return; }
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
            ApplyWidget();
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
        window.Bind(_codex.Accounts, _codex.SelectedId);
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

    private void ApplyWidget()
    {
        if (!_settings.FloatingWidgetEnabled)
        {
            _widget?.Hide();
            return;
        }

        _widget ??= new FloatingWidget();
        _widgetEvents.TrySubscribe(() =>
        {
            _widget.Moved += (left, top) =>
            {
                _settings.WidgetLeft = left;
                _settings.WidgetTop = top;
                if (_widget.PixelPosition is { } pixels)
                {
                    _settings.WidgetPixelLeft = pixels.X;
                    _settings.WidgetPixelTop = pixels.Y;
                }
                _settingsStore.Save(_settings);
            };
            _widget.FlyoutRequested += ToggleFlyout;
            _widget.RefreshRequested += () => _ = RefreshCodexAsync();
            _widget.ContextMenuRequested += () => _tray.ShowWidgetContextMenu();
        });
        _widget.Apply(_settings);
        _widget.BindAccount(_codex.Selected);
        _widget.Show();
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
        if (IsExiting || _settings.Theme != AppTheme.System) return;
        ApplyTheme(AppTheme.System);
        RefreshSnapshot();
    }

    private void OnDisplayChanged()
    {
        if (IsExiting) return;
        _widget?.RecoverPosition();
        _flyout?.RefreshWorkArea();
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
        _environment?.Dispose();
        _codexTimer.Stop();
        _displayTimer.Stop();
        _lifetime.Cancel();
        _accountsWindow?.CancelOperation();
        _flyout?.Hide();
        _widget?.Hide();
        // Let the existing bounded client stop and reap its app-server process.
        try { await Task.WhenAll(_refresh.WaitForIdleAsync(), _creditUseTask, _discoveryTask,
            _accountsWindow?.ActiveOperation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { _log.Warn("Codex shutdown wait timed out"); }
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _environment?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
