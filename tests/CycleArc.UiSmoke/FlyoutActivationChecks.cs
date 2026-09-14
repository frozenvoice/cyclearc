using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class FlyoutActivationChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(App app, string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-flyout-" + Guid.NewGuid().ToString("N"));
        var fields = new[] { "_settings", "_codex", "_log", "_flyout", "_widgetController" }
            .Select(name => typeof(App).GetField(name, PrivateInstance)!).ToArray();
        var original = fields.Select(field => field.GetValue(app)).ToArray();
        var applyWidget = typeof(App).GetMethod("ApplyWidget", PrivateInstance)!;
        var toggle = typeof(App).GetMethod("ToggleFlyout", PrivateInstance)!;
        var provider = new PassiveFixture();
        var profile = new CodexAccountProfile("default", Path.Combine(root, "synthetic-home"), "Synthetic");
        var store = new CodexAccountStore(root);
        store.Save(new(1, profile.Id, [profile]));
        var manager = new CodexAccountManager(store, profile.HomePath, [provider]);
        var count = 0;
        var focusWindow = new Window { Width = 600, Height = 800, ShowActivated = false, ShowInTaskbar = false,
            Title = "CycleArc synthetic foreground fixture", Left = 100, Top = 60 };
        try
        {
            Set("_codex", manager); Set("_log", new AppLog(Path.Combine(root, "logs")));
            focusWindow.Show();
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                UiText.SetLanguage(language);
                typeof(App).GetMethod("ApplyTheme", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [theme]);
                var settings = new AppSettings { FloatingWidgetEnabled = true, WidgetAlwaysOnTop = true,
                    WidgetLeft = 20, WidgetTop = 20, FlyoutPinned = false, FlyoutPositionConfigured = true,
                    FlyoutLeft = 100, FlyoutTop = 60 };
                var flyout = new FlyoutWindow { ShowActivated = false };
                Set("_settings", settings); Set("_flyout", flyout); Set("_widgetController", null);
                flyout.BindAccounts(manager.Accounts, manager.SelectedId, false);
                try
                {
                    applyWidget.Invoke(app, null); Pump();
                    var controller = (FloatingWidgetController)fields.Single(f => f.Name == "_widgetController").GetValue(app)!;
                    var widget = controller.CurrentWindow!;
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "First widget click did not open a hidden popup.");

                    // Unpinned means a normal window: another window can cover it without Hide().
                    SetWindowPos(new WindowInteropHelper(focusWindow).Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
                    SetActiveWindow(new WindowInteropHelper(focusWindow).Handle);
                    Pump();
                    Require(flyout.IsVisible && !flyout.IsActive && !flyout.Topmost, "Covered-popup fixture is invalid.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "First widget click hid the covered popup instead of revealing it.");
                    Require(flyout.IsActive && GetActiveWindow() == new WindowInteropHelper(flyout).Handle,
                        "First widget click did not activate the covered popup.");
                    if (directory is not null)
                        AccountUiChecks.Render(flyout, 440, null, Path.Combine(directory, $"reopened-{language}-{theme}.png"));
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "Repeated inspect click hid the popup.");

                    flyout.WindowState = WindowState.Minimized;
                    Pump();
                    Click(widget); Pump();
                    Require(flyout.WindowState == WindowState.Normal && flyout.IsVisible && flyout.IsActive
                        && GetActiveWindow() == new WindowInteropHelper(flyout).Handle,
                        "First widget click did not restore and activate the minimized popup.");

                    settings.FlyoutPinned = true;
                    flyout.ApplyWindowSettings(settings);
                    SetActiveWindow(new WindowInteropHelper(focusWindow).Handle);
                    Click(widget); Pump();
                    Require(flyout.IsVisible && flyout.Topmost, "Inspect click hid or unpinned the pinned popup.");

                    // Explicit close and tray toggle must still close, and widget inspection reopens.
                    ((Button)flyout.FindName("CloseFlyoutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!flyout.IsVisible, "Explicit close failed.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "Widget did not reopen an explicitly closed popup.");
                    toggle.Invoke(app, null);
                    Require(!flyout.IsVisible, "Tray toggle no longer closes the popup.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "Widget did not reopen after tray toggle.");
                    Require(manager.SelectedId == profile.Id && provider.RefreshCalls == 0,
                        "Popup inspection changed the account or fetched usage.");
                    count++;
                }
                finally
                {
                    ((FloatingWidgetController?)fields.Single(f => f.Name == "_widgetController").GetValue(app))?.Dispose();
                    flyout.Close();
                }
            }
        }
        finally
        {
            focusWindow.Close();
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(app, original[i]);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS: {count} production widget/popup interaction scenarios; covered unpinned popup activates on first click, minimized restoration, repeated/pinned inspection, close/tray toggle, no quota calls.");

        void Set(string name, object? value) => fields.Single(field => field.Name == name).SetValue(app, value);
    }

    private static void Click(FloatingWidget widget)
    {
        // Complete the production click gesture without moving or capturing the user's pointer.
        SetActiveWindow(new WindowInteropHelper(widget).Handle);
        typeof(FloatingWidget).GetField("_drag", PrivateInstance)!.SetValue(widget,
            new WidgetDragSession(widget.Left, widget.Top, 0, 0));
        typeof(FloatingWidget).GetMethod("FinishDrag", PrivateInstance)!.Invoke(widget, [true]);
    }

    private static void Pump() => AccountUiChecks.PumpUntil(
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PassiveFixture : IUsageProvider, IUsageAccountService
    {
        public UsageProviderId Id => UsageProviderId.Codex;
        public IUsageAccountService Create(CodexAccountProfile profile) => this;
        public CodexQuotaSnapshot Snapshot { get; } = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
        {
            LastSuccessfulRefresh = DateTimeOffset.Now,
            Windows = [new("weekly", 25, 10080, DateTimeOffset.Now.AddDays(5), CodexWindowKind.Weekly)]
        };
        public string? Email => null;
        public string? IdentityFingerprint => null;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => true;
        public int RefreshCalls { get; private set; }
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
        {
            RefreshCalls++;
            throw new InvalidOperationException("Inspection must not request quota.");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
