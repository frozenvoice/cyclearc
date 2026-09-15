using CycleArc.Codex;
using CycleArc.Providers.Usage;
using System.Windows.Forms;

namespace CycleArc.UI;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;
    private ContextMenuStrip? _widgetMenu;
    private bool _startWithWindows;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? SyncRequested;
    public event Action? SettingsRequested;
    public event Action? OpenLogsRequested;
    public event Action<bool>? StartupToggled;
    public event Action? AboutRequested;
    public event Action? ExitRequested;
    public event Action? CloseWidgetRequested;

    public TrayController()
    {
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = NotifyIconText.Safe(UiText.ProductName)
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                LeftClick?.Invoke();
            }
        };
        RebuildMenu(startWithWindows: true);
    }

    public void RebuildMenu(bool startWithWindows)
    {
        _startWithWindows = startWithWindows;
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = CreateMenu(startWithWindows, false);
        old?.Dispose();
        _widgetMenu?.Dispose();
        _widgetMenu = CreateMenu(startWithWindows, true);
    }

    private ContextMenuStrip CreateMenu(bool startWithWindows, bool forWidget)
    {
        var menu = new ContextMenuStrip();
        if (forWidget)
        {
            menu.Items.Add(UiText.T("Close widget", "위젯 닫기"), null, (_, _) => CloseWidgetRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(UiText.OpenApp, null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(UiText.RefreshAll, null, (_, _) => SyncRequested?.Invoke());
        menu.Items.Add(UiText.Settings, null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(UiText.OpenLogs, null, (_, _) => OpenLogsRequested?.Invoke());
        var startup = new ToolStripMenuItem(UiText.StartWithWindows) { Checked = startWithWindows, CheckOnClick = true };
        menu.Opening += (_, _) => startup.Checked = _startWithWindows;
        startup.CheckedChanged += (_, _) =>
        {
            if (_startWithWindows == startup.Checked) return;
            _startWithWindows = startup.Checked;
            StartupToggled?.Invoke(startup.Checked);
        };
        menu.Items.Add(startup);
        menu.Items.Add(UiText.About, null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.Exit, null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    public void Update(UsageAccountOverview overview, TrayIconStyle style)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _icon.Text = NotifyIconText.Safe(overview.Tooltip);
            // Render at the current Windows small-icon metric so the native
            // notification slot does not resample a 32px icon down to 16px.
            var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            var next = TrayIconRenderer.Render(overview.Snapshot, style, size,
                claudeAwaitingUsage: overview.Selected?.IsAwaitingUsage == true, lightTaskbar: IsLightTaskbar(), preference: overview.Preference);
            _icon.Icon = next;
            _current?.Dispose();
            _current = next;
        });
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public void ShowWidgetContextMenu() => _widgetMenu?.Show(System.Windows.Forms.Control.MousePosition);

    public void ShowContextMenu()
    {
        var menu = _icon.ContextMenuStrip;
        if (menu is null)
        {
            return;
        }

        menu.Show(System.Windows.Forms.Control.MousePosition);
    }

    public void Balloon(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _widgetMenu?.Dispose();
        _icon.Dispose();
        _current?.Dispose();
    }
}
