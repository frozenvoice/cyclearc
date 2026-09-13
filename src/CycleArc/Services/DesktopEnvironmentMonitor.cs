using System.Windows.Threading;
using Microsoft.Win32;

namespace CycleArc.Services;

public sealed class DesktopEnvironmentMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _themeChanged;
    private readonly Action _displayChanged;
    private readonly Action _desktopRestored;
    private readonly bool _subscribed;
    private volatile bool _disposed;

    public DesktopEnvironmentMonitor(Dispatcher dispatcher, Action themeChanged, Action displayChanged,
        bool subscribeSystemEvents = true, Action? desktopRestored = null)
    {
        _dispatcher = dispatcher;
        _themeChanged = themeChanged;
        _displayChanged = displayChanged;
        _desktopRestored = desktopRestored ?? displayChanged;
        _subscribed = subscribeSystemEvents;
        if (!_subscribed) return;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => NotifyThemeChanged();
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => NotifyDisplayChanged();
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) NotifyDesktopRestored();
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect) NotifyDesktopRestored();
    }
    public void NotifyThemeChanged() => Post(_themeChanged);
    public void NotifyDisplayChanged() => Post(_displayChanged);
    public void NotifyDesktopRestored() => Post(_desktopRestored);

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _ = _dispatcher.BeginInvoke(() => { if (!_disposed) action(); }, DispatcherPriority.DataBind);
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_subscribed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
