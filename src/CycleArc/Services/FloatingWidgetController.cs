using System.Windows.Threading;
using CycleArc.Providers.Usage;

namespace CycleArc.Services;

/// <summary>Keeps the enabled widget's native window in step with its saved preferences.</summary>
public sealed class FloatingWidgetController(Action<FloatingWidget> configure, Action<string>? log = null) : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private FloatingWidget? _window;
    private AppSettings? _settings;
    private UsageAccountOverview? _overview;
    private bool _recoveryPending;
    private bool _refreshing;
    private bool _disposed;

    public FloatingWidget? CurrentWindow => _window;
    private bool ShouldShow => !_disposed && _settings?.FloatingWidgetEnabled == true && _overview?.Selected is not null;

    public void Update(AppSettings settings, UsageAccountOverview overview, bool applySettings = false,
        bool refreshing = false)
    {
        if (_disposed) return;
        _settings = settings;
        _overview = overview;
        _refreshing = refreshing;
        if (!ShouldShow) { _window?.Hide(); return; }
        var created = EnsureWindow();
        // Every displayable account, in account-management order, not only the selected one.
        _window!.BindAccounts(overview.Accounts, overview.SelectedId, overview.Preference);
        // A window recreated mid-refresh still shows the shared state, not a stale idle button.
        _window.SetRefreshing(refreshing);
        if (created || applySettings) _window.Apply(settings);
        _window.EnsureVisible(settings.WidgetAlwaysOnTop);
        if (created) log?.Invoke("Widget window created and shown");
    }

    // This also runs when no quota changes arrive, without requesting new account data.
    public void MaintainVisibility()
    {
        if (!ShouldShow) return;
        if (_window is null)
        {
            Update(_settings!, _overview!, refreshing: _refreshing);
            log?.Invoke("Widget restored after its window closed");
        }
        else if (_window.EnsureVisible(_settings!.WidgetAlwaysOnTop))
            log?.Invoke("Widget native visibility restored");
    }

    // Recreate the native widget after an explicit position reset so a stale HWND
    // cannot retain broken surface or z-order state.
    public void Recreate(AppSettings settings, UsageAccountOverview overview)
    {
        if (_disposed) return;
        _settings = settings;
        _overview = overview;
        CloseCurrentWindow();
        Update(settings, overview, applySettings: true);
    }

    public void RecoverAfterEnvironmentChange()
    {
        if (!ShouldShow || _recoveryPending || _dispatcher.HasShutdownStarted) return;
        _recoveryPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _recoveryPending = false;
            if (!ShouldShow) return;
            // A transparent HWND can retain visible flags after its rendered surface is lost.
            // Replace it after resume/unlock/display changes instead of only moving its bounds.
            CloseCurrentWindow();
            Update(_settings!, _overview!);
            log?.Invoke("Widget window recreated after desktop environment change");
        }));
    }

    private bool EnsureWindow()
    {
        if (_window is not null) return false;
        var window = new FloatingWidget { ShowActivated = false };
        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
        };
        configure(window); // Subscribe exactly once for each new window.
        return true;
    }

    private void CloseCurrentWindow()
    {
        var previous = _window;
        _window = null;
        previous?.CloseWithoutActivation();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseCurrentWindow();
    }
}
