using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;
using Forms = System.Windows.Forms;

namespace CycleArc.UiSmoke;

// Exercise the real menu and App widget path, without production startup or user data.
internal static class TrayWidgetChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(App app, string? directory = null)
    {
        var names = new[] { "_settings", "_settingsStore", "_log", "_tray", "_codex", "_refresh", "_widgetController" };
        var fields = names.Select(name => typeof(App).GetField(name, PrivateInstance)!).ToArray();
        var original = fields.Select(field => field.GetValue(app)).ToArray();
        var exiting = typeof(App).GetProperty(nameof(App.IsExiting))!;
        var originalExiting = app.IsExiting;
        var originalLanguage = UiText.Language;
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalTheme = ((AppSettings)original[0]!).Theme;
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "tray-widget-fixture"));
        Require(!Directory.Exists(root), "Refusing to overwrite an existing tray-widget fixture.");
        Directory.CreateDirectory(root);
        if (directory is not null) Directory.CreateDirectory(directory);
        void Set(string name, object? value) => fields[Array.IndexOf(names, name)].SetValue(app, value);
        FloatingWidgetController? Controller() => (FloatingWidgetController?)fields[^1].GetValue(app);
        void Call(string name) => typeof(App).GetMethod(name, PrivateInstance)!.Invoke(app, null);
        var provider = new OfflineProvider();
        var accountStore = new CodexAccountStore(root);
        var profiles = new[]
        {
            new CodexAccountProfile("00000000000000000000000000000001", Path.Combine(root, "home-one"), "Personal"),
            new CodexAccountProfile("00000000000000000000000000000002", Path.Combine(root, "home-two"), "Work")
        };
        accountStore.Save(new(1, profiles[1].Id, profiles));
        using var manager = new CodexAccountManager(accountStore, Path.Combine(root, "default-home"), [provider]);
        using var tray = new TrayController();
        var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
        var menuClicks = 0;
        var expectedRefreshCalls = 0;
        Task<CodexRefreshResult>? pendingRefresh = null;
        tray.ShowWidgetRequested += () => { menuClicks++; Call("ShowWidget"); };
        try
        {
            Set("_settingsStore", settingsStore); Set("_log", new AppLog(Path.Combine(root, "logs")));
            Set("_tray", tray); Set("_codex", manager); Set("_refresh", manager.Refresh);
            Set("_widgetController", null);
            exiting.SetValue(app, false);
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                Controller()?.Dispose();
                Set("_widgetController", null);
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                tray.RebuildMenu(startWithWindows: false);
                var menu = ((Forms.NotifyIcon)typeof(TrayController).GetField("_icon", PrivateInstance)!.GetValue(tray)!).ContextMenuStrip!;
                var widgetMenu = (Forms.ContextMenuStrip)typeof(TrayController).GetField("_widgetMenu", PrivateInstance)!.GetValue(tray)!;
                var label = UiText.T("Show widget", "위젯 표시");
                var show = menu.Items.Cast<Forms.ToolStripItem>().Single(item => item.Text == label);
                Require(menu.Items.IndexOf(show) == 1 && menu.Items[0].Text == UiText.OpenApp
                    && menu.Items[2].Text == UiText.RefreshAll && show.Enabled,
                    "The tray widget action must follow Open and precede Refresh in both languages.");
                Require(!widgetMenu.Items.Cast<Forms.ToolStripItem>().Any(item => item.Text == label),
                    "The widget menu unexpectedly offers Show widget.");
                if (directory is not null && theme == AppTheme.System)
                    CaptureMenu(menu, Path.Combine(directory, $"tray-widget-menu-{language}.png"));
                var area = Forms.Screen.PrimaryScreen!.WorkingArea;
                var settings = new AppSettings
                {
                    UiLanguage = language, Theme = theme, FloatingWidgetEnabled = false,
                    DisplayMode = DisplayMode.TrayOnly, UsageAlertsEnabled = false,
                    WidgetPixelLeft = area.Left + 100, WidgetPixelTop = area.Top + 100,
                    WidgetLeft = 100.25, WidgetTop = 100.4, WidgetZoomPercent = 90,
                    WidgetOpacity = .73, WidgetAlwaysOnTop = true, WidgetClickThrough = false,
                    FlyoutZoomPercent = 130, FlyoutPinned = true, StartWithWindows = false
                };
                Set("_settings", settings);
                settingsStore.Save(settings);
                Call("ApplyWidget");
                Require(Controller()!.CurrentWindow is null, "A disabled widget created a native window.");
                if (expectedRefreshCalls == 0)
                {
                    provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingRefresh = manager.Refresh.RefreshAsync(CancellationToken.None);
                    expectedRefreshCalls = profiles.Length;
                    Require(manager.Refresh.IsRefreshing && provider.RefreshCalls == expectedRefreshCalls,
                        "The deferred shared-refresh fixture did not enter its busy state.");
                }
                var clicksBefore = menuClicks;
                show.PerformClick();
                Pump();
                var widget = Controller()!.CurrentWindow!;
                var hwnd = new WindowInteropHelper(widget).Handle;
                Require(menuClicks == clicksBefore + 1 && hwnd != IntPtr.Zero && widget.IsVisible && IsWindowVisible(hwnd),
                    "Clicking the native tray menu did not raise one request and show the widget.");
                CheckPreferences(settingsStore.Load(), settings, enabled: true);
                Require(widget.Modules.Select(module => module.ProfileId).SequenceEqual(profiles.Select(profile => profile.Id))
                    && widget.Modules[1].Model!.IsSelected && manager.SelectedId == profiles[1].Id,
                    "Showing the widget changed account selection or order.");
                Require(widget.ZoomPercent == 90 && Math.Abs(widget.BackgroundOpacity - .73) < .001
                    && widget.PixelPosition == (settings.WidgetPixelLeft!.Value, settings.WidgetPixelTop!.Value),
                    "Showing the widget did not apply saved size, opacity or position.");
                for (var repeat = 0; repeat < 3; repeat++) show.PerformClick();
                Pump();
                Require(ReferenceEquals(Controller()!.CurrentWindow, widget) && new WindowInteropHelper(widget).Handle == hwnd,
                    "Repeated Show widget requests created another widget or HWND.");
                if (pendingRefresh is not null)
                {
                    var refreshButton = (System.Windows.Controls.Button)widget.FindName("WidgetRefreshButton");
                    Require(!refreshButton.IsEnabled && manager.Refresh.IsRefreshing
                        && provider.RefreshCalls == expectedRefreshCalls,
                        "Showing or re-showing a widget during shared refresh lost busy state or started another query.");
                    provider.Pending!.SetResult(true);
                    pendingRefresh.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    pendingRefresh = null;
                    provider.Pending = null;
                    Call("ApplyWidget");
                    Require(refreshButton.IsEnabled && !manager.Refresh.IsRefreshing,
                        "The widget stayed busy after its shared refresh completed.");
                }
                // The production close button disables and persists, while keeping its HWND reusable.
                ((System.Windows.Controls.Button)widget.FindName("WidgetCloseButton"))
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Pump();
                CheckPreferences(settingsStore.Load(), settings, enabled: false);
                Require(!IsWindowVisible(hwnd) && ReferenceEquals(Controller()!.CurrentWindow, widget),
                    "Closing the widget did not hide its retained HWND.");
                show.PerformClick();
                Pump();
                CheckPreferences(settingsStore.Load(), settings, enabled: true);
                Require(ReferenceEquals(Controller()!.CurrentWindow, widget) && new WindowInteropHelper(widget).Handle == hwnd
                    && IsWindowVisible(hwnd), "The tray action did not reopen the same widget HWND.");
                ShowWindow(hwnd, 0); // Native hide leaves WPF's IsVisible stale.
                Require(!IsWindowVisible(hwnd), "The native-hide fixture failed.");
                show.PerformClick();
                Pump();
                Require(IsWindowVisible(hwnd) && ReferenceEquals(Controller()!.CurrentWindow, widget),
                    "The tray action did not recover a natively hidden widget.");
                CheckPreferences(settingsStore.Load(), settings, enabled: true);

                Call("CloseWidget");
                Pump();
                var beforeExit = File.ReadAllText(Path.Combine(root, "settings.json"));
                exiting.SetValue(app, true);
                show.PerformClick();
                Pump();
                Require(!IsWindowVisible(hwnd) && !settings.FloatingWidgetEnabled
                    && File.ReadAllText(Path.Combine(root, "settings.json")) == beforeExit,
                    "A queued tray request revived or persisted a widget after exit began.");
                exiting.SetValue(app, false);
            }
            Controller()?.Dispose();
            Set("_widgetController", null);
            // An enabled preference with no displayable account must never create a HWND.
            foreach (var profile in profiles) manager.Remove(profile.Id);
            var accountlessSettings = new AppSettings { FloatingWidgetEnabled = false };
            Set("_settings", accountlessSettings);
            Call("ShowWidget");
            Pump();
            Require(accountlessSettings.FloatingWidgetEnabled && settingsStore.Load().FloatingWidgetEnabled
                && Controller()!.CurrentWindow is null, "Accountless Show widget created a native window or lost its enabled preference.");
            Require(provider.RefreshCalls == expectedRefreshCalls, "Showing or reopening the widget queried account usage.");
        }
        finally
        {
            try
            {
                provider.Pending?.TrySetResult(true);
                pendingRefresh?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            finally
            {
                Controller()?.Dispose();
                Pump();
                for (var index = 0; index < fields.Length; index++) fields[index].SetValue(app, original[index]);
                exiting.SetValue(app, originalExiting);
                UiText.SetLanguage(originalLanguage);
                applyTheme.Invoke(null, [originalTheme]);
                // Only this fixed fixture root, created above, is recursively removed.
                Require(root == Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "tray-widget-fixture")),
                    "Tray-widget fixture cleanup escaped its expected root.");
                Directory.Delete(root, recursive: true);
            }
        }
        Console.WriteLine("PASS: native tray Show widget in EN/KO and System/Dark/Light; persisted enable/close/reopen, retained HWND and preferences, native hide recovery, shared-refresh busy state, accountless/exit guards, no extra usage query or selection change.");
        CheckAccountAndWidgetMenus(directory);
    }

    // The tray reaches account switching and the widget's options, including a click-through
    // widget that cannot be right-clicked, and the menu follows the app's Dark ground.
    private static void CheckAccountAndWidgetMenus(string? directory)
    {
        var previousDark = TrayController.DarkMenus;
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var dark in new[] { false, true })
            {
                UiText.SetLanguage(language);
                TrayController.DarkMenus = dark;
                using var tray = new TrayController();
                var selected = new List<string>();
                var manage = 0;
                bool? clickThrough = null, topmost = null;
                var shows = 0; var closes = 0;
                tray.AccountsProvider = () => [new("a", "Personal · Codex", true), new("b", "Work · Claude", false)];
                var widgetState = new TrayWidgetState(true, true, false);
                tray.WidgetStateProvider = () => widgetState;
                tray.AccountSelected += selected.Add;
                tray.ManageAccountsRequested += () => manage++;
                tray.WidgetClickThroughToggled += value => clickThrough = value;
                tray.WidgetTopmostToggled += value => topmost = value;
                tray.ShowWidgetRequested += () => shows++;
                tray.CloseWidgetRequested += () => closes++;
                tray.RebuildMenu(startWithWindows: false);
                var menu = ((Forms.NotifyIcon)typeof(TrayController).GetField("_icon", PrivateInstance)!.GetValue(tray)!).ContextMenuStrip!;
                var accounts = (Forms.ToolStripMenuItem)menu.Items["TrayAccounts"]!;
                var widget = (Forms.ToolStripMenuItem)menu.Items["TrayWidget"]!;
                Require(menu.Items.IndexOf(accounts) == 3 && menu.Items.IndexOf(widget) == 4,
                    "Accounts and Widget must follow Refresh without moving Open, Show widget or Refresh.");
                tray.FillAccounts(accounts);
                var choices = accounts.DropDownItems.OfType<Forms.ToolStripMenuItem>().ToArray();
                Require(choices.Length == 3 && choices[0].Checked && !choices[1].Checked, "The account choices or selection mark are wrong.");
                choices[0].PerformClick();
                choices[1].PerformClick();
                choices[2].PerformClick();
                Require(selected.SequenceEqual(["b"]) && manage == 1, "Account switching selected the wrong account or Manage accounts did nothing.");
                tray.FillWidget(widget);
                var options = widget.DropDownItems.OfType<Forms.ToolStripMenuItem>().ToArray();
                Require(options[0].Text == UiText.T("Close widget", "위젯 닫기") && options[1].Checked && !options[2].Checked,
                    "The widget submenu does not reflect an open click-through widget.");
                options[0].PerformClick(); options[1].PerformClick(); options[2].PerformClick();
                Require(closes == 1 && clickThrough == false && topmost == true, "Widget options did not request the opposite state.");
                widgetState = new TrayWidgetState(false, false, true);
                tray.FillWidget(widget);
                options = widget.DropDownItems.OfType<Forms.ToolStripMenuItem>().ToArray();
                Require(options[0].Text == UiText.T("Show widget", "위젯 표시") && !options[1].Enabled && !options[2].Enabled,
                    "A closed widget offers options that cannot apply.");
                options[0].PerformClick();
                Require(shows == 1, "Show widget from the submenu did nothing.");
                var ground = menu.BackColor;
                Require(dark ? ground.GetBrightness() < 0.3f : ground.GetBrightness() > 0.6f, $"The tray menu ground does not follow {(dark ? "Dark" : "Light")}.");
                Require(dark == (menu.Items[0].ForeColor.GetBrightness() > 0.6f), "Tray menu text does not contrast with its ground.");
                if (directory is not null)
                    CaptureMenu(menu, Path.Combine(directory, $"tray-menu-{(language == UiLanguage.Korean ? "ko" : "en")}-{(dark ? "dark" : "light")}.png"));
            }
        }
        finally
        {
            TrayController.DarkMenus = previousDark;
            UiText.SetLanguage(previousLanguage);
        }
        Console.WriteLine("PASS: tray Accounts and Widget submenus in EN/KO Dark/Light; account switching, Manage accounts, click-through/topmost toggles and themed menu ground.");
    }

    private static void CheckPreferences(AppSettings loaded, AppSettings current, bool enabled) => Require(
        current.FloatingWidgetEnabled == enabled && loaded.FloatingWidgetEnabled == enabled
        && loaded.DisplayMode == (enabled ? DisplayMode.TrayAndWidget : DisplayMode.TrayOnly)
        && loaded.WidgetLeft == 100.25 && loaded.WidgetTop == 100.4
        && loaded.WidgetPixelLeft == current.WidgetPixelLeft && loaded.WidgetPixelTop == current.WidgetPixelTop
        && loaded.WidgetZoomPercent == 90 && loaded.WidgetOpacity == .73 && loaded.WidgetAlwaysOnTop
        && !loaded.WidgetClickThrough && loaded.FlyoutZoomPercent == 130 && loaded.FlyoutPinned
        && !loaded.StartWithWindows, "The widget action failed to persist its display mode or changed unrelated preferences.");

    private static void CaptureMenu(Forms.ContextMenuStrip menu, string path)
    {
        // Draw only this production menu; never capture another window or the desktop.
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        menu.Show(area.Left + 20, area.Top + 20);
        try
        {
            Pump();
            using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, menu.Size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { menu.Close(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class OfflineProvider : IUsageProvider
    {
        public int RefreshCalls { get; private set; }
        public TaskCompletionSource<bool>? Pending { get; set; }
        public UsageProviderId Id => UsageProviderId.Codex;
        public IUsageAccountService Create(CodexAccountProfile profile) => new OfflineService(this);

        private sealed class OfflineService(OfflineProvider owner) : IUsageAccountService
        {
            private static readonly DateTimeOffset Now = new(2035, 1, 15, 9, 30, 0, TimeSpan.Zero);
            public CodexQuotaSnapshot Snapshot { get; } = new(CodexQuotaStatus.Available, "pro", Now, Now,
                null, null, null, [new("weekly", 25, 10080, Now.AddDays(7), CodexWindowKind.Weekly)], null);
            public string? Email => null;
            public string? IdentityFingerprint => null;
            public bool IsRefreshing => false;
            public bool ReceivesPassiveUpdates => false;
            public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
            public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
            public async Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
            {
                owner.RefreshCalls++;
                if (owner.Pending is null)
                    throw new InvalidOperationException("The tray-widget fixture must not query usage.");
                await owner.Pending.Task.WaitAsync(token).ConfigureAwait(false);
                return new(Snapshot, false, null);
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
