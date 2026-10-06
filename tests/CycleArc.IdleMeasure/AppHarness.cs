using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.IdleMeasure;

// Same boundary used by UiSmoke: never invoke App.OnStartup. Explicit fixture fields
// feed production refresh/presentation/window methods, with the production timer cadence.
internal sealed class AppHarness : App
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly SyntheticAccounts _fixture;
    private readonly Func<AppHarness, Task> _run;
    private readonly AppSettings _fixtureSettings;
    private readonly SettingsStore _fixtureSettingsStore;
    private readonly Action _refreshSnapshot;
    private readonly Action _applyRefreshSchedule;
    private readonly Action _applyWidget;
    private readonly Action _toggleFlyout;
    private readonly Func<bool, Task> _refreshCodex;
    private readonly Func<Task> _readPassive;
    private readonly Action<bool> _setExiting;
    private readonly DispatcherTimer _accountTimer;
    private readonly DispatcherTimer _displayTimer;
    private readonly DispatcherTimer _passiveTimer;
    private readonly CancellationTokenSource _lifetime;
    private readonly FieldInfo _passiveTaskField;
    private readonly FieldInfo _widgetField;
    private readonly FieldInfo _flyoutField;
    private readonly UiCreationObserver _ui;
    private TrayController? _fixtureTray;
    private DesktopEnvironmentMonitor? _fixtureEnvironment;
    private Window? _guardedWidget;
    private Window? _guardedFlyout;
    private bool _initialized;
    private bool _stopped;

    public long PassiveTicks { get; private set; }
    public long AutomaticTicks { get; private set; }
    public long DisplayTicks { get; private set; }
    public UiCreationCounts UiCounts => _ui.Counts;
    public long RefreshSnapshotCalls { get; private set; }

    public AppHarness(SyntheticAccounts fixture, Func<AppHarness, Task> run)
    {
        _fixture = fixture;
        _run = run;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _fixtureSettings = new AppSettings
        {
            FirstRunCompleted = true,
            Theme = AppTheme.Dark,
            UiLanguage = UiLanguage.English,
            CodexRefreshIntervalMinutes = 1,
            UsageAlertsEnabled = false,
            FlyoutPinned = true,
            FloatingWidgetEnabled = false,
            WidgetAlwaysOnTop = true,
            StartWithWindows = false,
            AutoSync = false,
            CompanionConnectOptIn = false,
            TaskbarStatusEnabled = false,
            FlyoutZoomPercent = 100,
            WidgetZoomPercent = 100,
            WidgetLeft = 40,
            WidgetTop = 40
        };
        _fixtureSettingsStore = new SettingsStore(fixture.SettingsPath);
        _ui = new UiCreationObserver(() => Flyout, () => Widget, () => _fixtureTray);
        var refreshSnapshot = Method<Action>("RefreshSnapshot");
        _refreshSnapshot = Observed(() => { RefreshSnapshotCalls++; refreshSnapshot(); });
        _applyRefreshSchedule = Method<Action>("ApplyRefreshSchedule");
        _applyWidget = Observed(Method<Action>("ApplyWidget"));
        _toggleFlyout = Observed(Method<Action>("ToggleFlyout"));
        _refreshCodex = Method<Func<bool, Task>>("RefreshCodexAsync");
        _readPassive = Method<Func<Task>>("ReadPassiveUsageAsync");
        _setExiting = Method<Action<bool>>("set_IsExiting");
        _accountTimer = Get<DispatcherTimer>("_codexTimer");
        _displayTimer = Get<DispatcherTimer>("_displayTimer");
        _passiveTimer = Get<DispatcherTimer>("_passiveTimer");
        _lifetime = Get<CancellationTokenSource>("_lifetime");
        _passiveTaskField = Field("_passiveTask");
        _widgetField = Field("_widgetController");
        _flyoutField = Field("_flyout");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // The supplied runner owns result/error reporting and calls Shutdown after StopAsync.
        // No base call: production startup would touch desktop data/IPC/installation state.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () => await _run(this)));
    }

    public async Task InitializeAsync()
    {
        Dispatcher.VerifyAccess();
        if (_initialized || _stopped) throw new InvalidOperationException("Fixture initialization must run once.");
        _initialized = true;
        Set("_settings", _fixtureSettings);
        Set("_settingsStore", _fixtureSettingsStore);
        Set("_log", new AppLog(_fixture.LogsDirectory));
        _fixtureSettingsStore.Save(_fixtureSettings);
        UiText.SetLanguage(_fixtureSettings.UiLanguage);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(App).FullName, "ApplyTheme");
        applyTheme.CreateDelegate<Action<AppTheme>>()(_fixtureSettings.Theme);

        _fixtureTray = new TrayController();
        Set("_tray", _fixtureTray);
        _fixtureTray.RebuildMenu(startWithWindows: false);
        _fixtureTray.LeftClick += () => ShowFlyout(Flyout?.IsVisible != true);
        _fixtureTray.OpenRequested += () => ShowFlyout(true);
        _fixtureTray.SyncRequested += () => _ = _refreshCodex(false);
        _fixtureTray.CloseWidgetRequested += () => ShowWidget(false);
        _fixtureTray.ResetWidgetZoomRequested += () => Widget?.CurrentWindow?.SetZoom(100);
        // Startup/install/update/settings controls have no external action in the fixture.
        Set("_codex", _fixture.Manager);
        Set("_refresh", _fixture.Manager.Refresh);
        _fixture.Manager.Changed += OnAccountsChanged;
        _fixture.Manager.Refresh.StateChanged += OnRefreshStateChanged;

        _applyRefreshSchedule();
        _accountTimer.Tick += OnAutomaticTick;
        _displayTimer.Tick += OnDisplayTick;
        _displayTimer.Start();
        _passiveTimer.Tick += OnPassiveTick;
        _passiveTimer.Start();
        _refreshSnapshot();
        _applyWidget();
        _fixtureEnvironment = new DesktopEnvironmentMonitor(Dispatcher,
            Method<Action>("OnSystemThemeChanged"), Method<Action>("OnDisplayChanged"),
            desktopRestored: Method<Action>("OnDesktopRestored"));
        Set("_environment", _fixtureEnvironment);

        // Real initial active and passive paths, using only synthetic external adapters.
        await _fixture.InitializeAsync(_lifetime.Token);
        await _fixture.Manager.WaitForObservationsIdleAsync();
        _refreshSnapshot();
        AssertVisibility(flyout: false, widget: false);
    }

    public async Task RefreshAsync()
    {
        Dispatcher.VerifyAccess();
        EnsureRunning();
        await _refreshCodex(false);
        await _fixture.Manager.Refresh.WaitForIdleAsync();
        _fixture.AssertLatestAndIsolation();
    }

    public async Task AssertDataWhenIdleAsync()
    {
        Dispatcher.VerifyAccess();
        // Expected fixture values change as a response is produced, before its commit.
        // Join rather than suppress a refresh that overlaps a measurement boundary.
        do
        {
            await Task.WhenAll(_fixture.Manager.Refresh.WaitForIdleAsync(), PassiveTask);
        } while (_fixture.Manager.Refresh.IsRefreshing || !PassiveTask.IsCompleted);
        _fixture.AssertLatestAndIsolation();
    }

    public async Task PublishAndReadPassiveAsync()
    {
        Dispatcher.VerifyAccess();
        EnsureRunning();
        await PassiveTask;
        await _fixture.PublishPassiveUpdateAsync(_lifetime.Token);
        // A normal timer tick may have started during fixture publishing. Join it, then
        // perform the requested production read; never leave overlapping reads pending.
        await PassiveTask;
        var read = _readPassive();
        _passiveTaskField.SetValue(this, read);
        await read;
        _fixture.AssertLatestAndIsolation();
        _refreshSnapshot();
    }

    public void ShowFlyout(bool visible)
    {
        Dispatcher.VerifyAccess();
        EnsureRunning();
        if (visible)
        {
            if (Flyout?.IsVisible != true) _toggleFlyout();
            GuardFixtureWindowControls();
        }
        else Flyout?.Hide();
    }

    public void ShowWidget(bool visible)
    {
        Dispatcher.VerifyAccess();
        EnsureRunning();
        _fixtureSettings.FloatingWidgetEnabled = visible;
        _fixtureSettingsStore.Save(_fixtureSettings);
        _applyWidget();
        GuardFixtureWindowControls();
    }

    /// <summary>Selects the next account in management order through the production manager,
    /// as the popup selector and widget module handlers do.</summary>
    public void SelectNextAccount()
    {
        Dispatcher.VerifyAccess();
        EnsureRunning();
        var ids = _fixture.ProfileIds;
        var index = ids.ToList().IndexOf(_fixture.Manager.SelectedId);
        _fixture.Manager.Select(ids[(index + 1) % ids.Count]);
    }

    public string SelectedAccountId => _fixture.Manager.SelectedId;

    public void AssertVisibility(bool flyout, bool widget)
    {
        Dispatcher.VerifyAccess();
        if ((Flyout?.IsVisible == true) != flyout || (Widget?.CurrentWindow?.IsVisible == true) != widget)
            throw new InvalidOperationException("Synthetic native-window visibility does not match its stage.");
        if (_fixtureSettings.FloatingWidgetEnabled != widget)
            throw new InvalidOperationException("Widget enabled state diverged from the fixture stage.");
    }

    public async Task StopAsync()
    {
        Dispatcher.VerifyAccess();
        if (_stopped) return;
        _stopped = true;
        var shutdownTime = System.Diagnostics.Stopwatch.StartNew();
        _setExiting(true);
        _accountTimer.Stop();
        _displayTimer.Stop();
        _passiveTimer.Stop();
        Get<DispatcherTimer>("_updateTimer").Stop();
        _fixture.Manager.Changed -= OnAccountsChanged;
        _fixture.Manager.Refresh.StateChanged -= OnRefreshStateChanged;
        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_fixture.Manager.Refresh.WaitForIdleAsync(), PassiveTask).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            await _fixture.Manager.StopObservationsAsync(TimeSpan.FromMilliseconds(
                Math.Max(1, 15_000 - shutdownTime.Elapsed.TotalMilliseconds)));
            Widget?.Dispose();
            _fixtureEnvironment?.Dispose();
            Flyout?.Close();
            _fixtureTray?.Dispose();
        }
        // Keep the dispatcher alive until the supplied runner has written its report.
    }

    private void OnAccountsChanged()
    {
        if (!IsExiting && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(_refreshSnapshot);
    }

    private void OnRefreshStateChanged()
    {
        if (IsExiting || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_fixture.Manager.Refresh.IsRefreshing && !IsExiting) _applyRefreshSchedule();
            _refreshSnapshot();
        }));
    }

    private async void OnAutomaticTick(object? sender, EventArgs e)
    {
        AutomaticTicks++;
        await _refreshCodex(true);
    }

    private void OnDisplayTick(object? sender, EventArgs e)
    {
        DisplayTicks++;
        _refreshSnapshot();
    }

    private void OnPassiveTick(object? sender, EventArgs e)
    {
        PassiveTicks++;
        if (!IsExiting) Widget?.MaintainVisibility();
        _ui.Observe();
        GuardFixtureWindowControls();
        if (!IsExiting && PassiveTask.IsCompleted) _passiveTaskField.SetValue(this, _readPassive());
    }

    private void GuardFixtureWindowControls()
    {
        // Production presentation subscribes its account/settings menus to startup/login
        // flows. Those interactive external controls have no role in this fixture; retain
        // all refresh/selection/window handlers. Check only when a window is created.
        if (Widget?.CurrentWindow is { } widget && !ReferenceEquals(widget, _guardedWidget))
        {
            DisableEvent(widget, "SettingsRequested");
            _guardedWidget = widget;
        }
        if (Flyout is { } flyout && !ReferenceEquals(flyout, _guardedFlyout))
        {
            DisableEvent(flyout, "SettingsRequested");
            DisableEvent(flyout, "AccountsRequested");
            _guardedFlyout = flyout;
        }
    }

    private static void DisableEvent(Window window, string eventName)
    {
        var field = window.GetType().GetField(eventName, PrivateInstance)
            ?? throw new MissingFieldException(window.GetType().FullName, eventName);
        field.SetValue(window, null);
    }

    private Action Observed(Action action) => () =>
    {
        action();
        _ui.Observe();
    };

    private Task PassiveTask => (Task)(_passiveTaskField.GetValue(this)
        ?? throw new InvalidOperationException("Production passive task is missing."));
    private FloatingWidgetController? Widget => (FloatingWidgetController?)_widgetField.GetValue(this);
    private FlyoutWindow? Flyout => (FlyoutWindow?)_flyoutField.GetValue(this);
    private void EnsureRunning()
    {
        if (!_initialized || _stopped) throw new InvalidOperationException("The synthetic app is not running.");
    }
    private static FieldInfo Field(string name) => typeof(App).GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(typeof(App).FullName, name);
    private T Get<T>(string name) => (T)(Field(name).GetValue(this)
        ?? throw new InvalidOperationException("Production field is missing: " + name));
    private void Set(string name, object value) => Field(name).SetValue(this, value);
    private T Method<T>(string name) where T : Delegate => (typeof(App).GetMethod(name, PrivateInstance)
        ?? throw new MissingMethodException(typeof(App).FullName, name)).CreateDelegate<T>(this);
}
