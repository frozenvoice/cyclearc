using CycleArc.Codex;
using CycleArc.Providers.Usage;
using System.Windows.Forms;

namespace CycleArc.UI;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;
    private TrayIconView _currentView;
    private ContextMenuStrip? _widgetMenu;
    private bool _startWithWindows;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? ShowWidgetRequested;
    public event Action? SyncRequested;
    public event Action? SettingsRequested;
    public event Action? OpenLogsRequested;
    public event Action<bool>? StartupToggled;
    public event Action? AboutRequested;
    public event Action? UpdatesRequested;
    private Action? _balloonClick;
    public event Action? ExitRequested;
    public event Action? CloseWidgetRequested;
    public event Action? ResetWidgetZoomRequested;
    public event Action<string>? AccountSelected;
    public event Action? ManageAccountsRequested;
    public event Action<bool>? WidgetClickThroughToggled;
    public event Action<bool>? WidgetTopmostToggled;
    /// <summary>The accounts offered under Accounts, read each time the menu opens.</summary>
    public Func<IReadOnlyList<TrayAccountChoice>>? AccountsProvider { get; set; }
    /// <summary>The widget options offered under Widget, read each time the menu opens.</summary>
    public Func<TrayWidgetState>? WidgetStateProvider { get; set; }
    /// <summary>Follows the app theme; WinForms menus do not follow WPF resources by themselves.</summary>
    public static bool DarkMenus { get; set; }

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
        // A usage alert opens its account; the update notice (no action of its own) opens updates.
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonClick is { } click) click();
            else UpdatesRequested?.Invoke();
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
            menu.Items.Add(UiText.ResetWidgetSize, null, (_, _) => ResetWidgetZoomRequested?.Invoke());
            menu.Items.Add(UiText.T("Close widget", "위젯 닫기"), null, (_, _) => CloseWidgetRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(UiText.OpenApp, null, (_, _) => OpenRequested?.Invoke());
        if (!forWidget)
            menu.Items.Add(UiText.T("Show widget", "위젯 표시"), null, (_, _) => ShowWidgetRequested?.Invoke());
        menu.Items.Add(UiText.RefreshAll, null, (_, _) => SyncRequested?.Invoke());
        if (!forWidget)
        {
            // Switching the displayed account and reaching the widget's options work from the
            // tray too, including while a click-through widget cannot be right-clicked.
            var accounts = new ToolStripMenuItem(UiText.T("Accounts", "계정")) { Name = "TrayAccounts" };
            accounts.DropDownItems.Add(new ToolStripMenuItem("…"));
            accounts.DropDownOpening += (_, _) => FillAccounts(accounts);
            menu.Items.Add(accounts);
            var widget = new ToolStripMenuItem(UiText.T("Widget", "위젯")) { Name = "TrayWidget" };
            widget.DropDownItems.Add(new ToolStripMenuItem("…"));
            widget.DropDownOpening += (_, _) => FillWidget(widget);
            menu.Items.Add(widget);
        }
        menu.Items.Add(UiText.Settings, null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(UiText.OpenLogs, null, (_, _) => OpenLogsRequested?.Invoke());
        var startup = new ToolStripMenuItem(UiText.StartWithWindows)
        {
            Checked = startWithWindows, CheckOnClick = true, Enabled = !InstalledApp.IsPortable,
            ToolTipText = InstalledApp.IsPortable ? UiText.T(
                "Configure Windows startup from the installed app.", "Windows 자동 시작은 설치형 앱에서 설정하세요.") : ""
        };
        menu.Opening += (_, _) => startup.Checked = _startWithWindows;
        startup.CheckedChanged += (_, _) =>
        {
            if (_startWithWindows == startup.Checked) return;
            _startWithWindows = startup.Checked;
            StartupToggled?.Invoke(startup.Checked);
        };
        menu.Items.Add(startup);
        menu.Items.Add(UiText.About, null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(UiText.T("Check for updates…", "업데이트 확인…"), null, (_, _) => UpdatesRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.Exit, null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) => TrayMenuTheme.Apply(menu, DarkMenus);
        TrayMenuTheme.Apply(menu, DarkMenus);
        return menu;
    }

    internal void FillAccounts(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var choices = AccountsProvider?.Invoke() ?? [];
        foreach (var choice in choices)
        {
            var id = choice.Id;
            var item = new ToolStripMenuItem(choice.Name)
            {
                Checked = choice.Selected,
                ToolTipText = choice.Selected ? UiText.T("Shown in the tray, popup and widget", "트레이·팝업·위젯에 표시 중") : ""
            };
            item.Click += (_, _) => { if (!choice.Selected) AccountSelected?.Invoke(id); };
            parent.DropDownItems.Add(item);
        }
        if (choices.Count == 0)
            parent.DropDownItems.Add(new ToolStripMenuItem(UiText.T("No accounts yet", "아직 계정이 없습니다")) { Enabled = false });
        parent.DropDownItems.Add(new ToolStripSeparator());
        parent.DropDownItems.Add(UiText.T("Manage accounts…", "계정 관리…"), null, (_, _) => ManageAccountsRequested?.Invoke());
        TrayMenuTheme.Apply(parent.DropDown, DarkMenus);
    }

    internal void FillWidget(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var state = WidgetStateProvider?.Invoke() ?? default;
        parent.DropDownItems.Add(state.Enabled
            ? new ToolStripMenuItem(UiText.T("Close widget", "위젯 닫기"), null, (_, _) => CloseWidgetRequested?.Invoke())
            : new ToolStripMenuItem(UiText.T("Show widget", "위젯 표시"), null, (_, _) => ShowWidgetRequested?.Invoke()));
        var clickThrough = new ToolStripMenuItem(UiText.T("Click through", "클릭 통과")) { Checked = state.ClickThrough, Enabled = state.Enabled };
        clickThrough.Click += (_, _) => WidgetClickThroughToggled?.Invoke(!state.ClickThrough);
        parent.DropDownItems.Add(clickThrough);
        var topmost = new ToolStripMenuItem(UiText.T("Always on top", "항상 위에 표시")) { Checked = state.AlwaysOnTop, Enabled = state.Enabled };
        topmost.Click += (_, _) => WidgetTopmostToggled?.Invoke(!state.AlwaysOnTop);
        parent.DropDownItems.Add(topmost);
        parent.DropDownItems.Add(new ToolStripMenuItem(UiText.ResetWidgetSize, null, (_, _) => ResetWidgetZoomRequested?.Invoke())
        { Enabled = state.Enabled });
        TrayMenuTheme.Apply(parent.DropDown, DarkMenus);
    }

    public void Update(UsageAccountOverview overview, TrayIconStyle style)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _icon.Text = NotifyIconText.Safe(overview.Tooltip);
            // Render at the current Windows small-icon metric so the native
            // notification slot does not resample a 32px icon down to 16px.
            var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            var view = TrayIconRenderer.Describe(overview.Snapshot, style, size,
                claudeAwaitingUsage: overview.Selected?.IsAwaitingUsage == true, lightTaskbar: _lightTaskbar ??= IsLightTaskbar(), preference: overview.Preference);
            // The tooltip above follows every update; the icon is redrawn only when its pixels change.
            if (_current is not null && view == _currentView) return;
            var next = TrayIconRenderer.Render(view);
            _icon.Icon = next;
            _current?.Dispose();
            _current = next;
            _currentView = view;
        });
    }

    // Read once and again only after Windows reports a theme change, not on every update.
    private bool? _lightTaskbar;

    public void InvalidateTaskbarTheme() => _lightTaskbar = null;

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

    public void Balloon(string title, string body, Action? onClick = null)
    {
        _balloonClick = onClick;
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

public sealed record TrayAccountChoice(string Id, string Name, bool Selected);

public readonly record struct TrayWidgetState(bool Enabled, bool ClickThrough, bool AlwaysOnTop);
