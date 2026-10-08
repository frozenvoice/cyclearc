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
                CheckStableAccountChoices(manager.Accounts[0], provider);
                Pump();
                var settings = new AppSettings { FloatingWidgetEnabled = true, WidgetAlwaysOnTop = true,
                    WidgetLeft = 20, WidgetTop = 20, FlyoutPinned = false, FlyoutPositionConfigured = true,
                    FlyoutLeft = 100, FlyoutTop = 60 };
                var flyout = new FlyoutWindow { ShowActivated = false };
                Set("_settings", settings); Set("_flyout", flyout); Set("_widgetController", null);
                try
                {
                    flyout.BindAccounts(manager.Accounts, manager.SelectedId, false);
                    applyWidget.Invoke(app, null); Pump();
                    var controller = (FloatingWidgetController)fields.Single(f => f.Name == "_widgetController").GetValue(app)!;
                    var widget = controller.CurrentWindow!;
                    var openRequests = 0;
                    var activeAfterOpenRequest = false;
                    widget.FlyoutRequested += () => { openRequests++; activeAfterOpenRequest = flyout.IsActive; };
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "First widget click did not open a hidden popup.");

                    // Unpinned means a normal window: another window can cover it without Hide().
                    SetWindowPos(new WindowInteropHelper(focusWindow).Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
                    SetActiveWindow(new WindowInteropHelper(focusWindow).Handle);
                    Pump();
                    Require(flyout.IsVisible && !flyout.IsActive && !flyout.Topmost, "Covered-popup fixture is invalid.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "First widget click hid the covered popup instead of revealing it.");
                    var active = GetActiveWindow();
                    var flyoutHandle = new WindowInteropHelper(flyout).Handle;
                    Require(flyout.IsActive && active == flyoutHandle,
                        $"First widget click did not activate the covered popup: active={flyout.IsActive}, "
                        + $"threadWindow={active.ToInt64():X}, flyout={flyoutHandle.ToInt64():X}, "
                        + $"widget={new WindowInteropHelper(widget).Handle.ToInt64():X}, "
                        + $"fixture={new WindowInteropHelper(focusWindow).Handle.ToInt64():X}, "
                        + $"foreground={GetForegroundWindow().ToInt64():X}, openRequests={openRequests}, "
                        + $"activeAfterOpenRequest={activeAfterOpenRequest}.");
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
                    ((Button)flyout.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!flyout.IsVisible, "Explicit close failed.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "Widget did not reopen an explicitly closed popup.");
                    toggle.Invoke(app, null);
                    Require(!flyout.IsVisible, "Tray toggle no longer closes the popup.");
                    Click(widget); Pump();
                    Require(flyout.IsVisible, "Widget did not reopen after tray toggle.");
                    // A press on the header or empty chrome is not an inspect gesture: it
                    // focuses the widget and leaves the popup exactly as it was. Opening and
                    // activating the popup here is what used to move the keyboard off the
                    // widget, so a shortcut typed at the widget reached the popup instead.
                    var beforeChrome = flyout.IsVisible;
                    ClickChrome(widget); Pump();
                    Require(flyout.IsVisible == beforeChrome,
                        "A header click changed whether the popup was shown.");
                    Require(GetActiveWindow() == new WindowInteropHelper(widget).Handle && widget.IsActive,
                        "A header click did not leave the keyboard on the widget.");
                    Require(!flyout.IsActive, "A header click activated the popup.");

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
        Console.WriteLine($"PASS: {count} production widget/popup interaction scenarios; covered unpinned popup activates on first account click, minimized restoration, repeated/pinned inspection, header click focusing the widget without touching the popup, close/tray toggle, stable account choices across quota/refresh binds with rename/order/membership/provider invalidation, no quota calls.");

        void Set(string name, object? value) => fields.Single(field => field.Name == name).SetValue(app, value);
    }

    private static void CheckStableAccountChoices(CodexAccountView first, PassiveFixture provider)
    {
        // Keep quota/refresh/membership transitions out of the native activation fixture.
        // This window is never shown and is closed before the foreground scenario starts.
        var flyout = new FlyoutWindow { ShowActivated = false };
        var second = first with { Profile = first.Profile with { Id = "selector-second", Label = "Second account" } };
        var selections = new List<string>();
        var refreshRequests = 0;
        void Select(string id) => selections.Add(id);
        void Refresh() => refreshRequests++;
        flyout.AccountSelected += Select;
        flyout.SyncRequested += Refresh;
        try
        {
            flyout.BindAccounts([first, second], first.Profile.Id, false);
            var selector = (ComboBox)flyout.FindName("AccountSelector");
            var originalSource = selector.ItemsSource;
            var originalFirstChoice = selector.Items[0];
            Require(originalSource is not null && selector.Items.Count == 2 && selections.Count == 0,
                "Initial account choices are missing or emitted a selection.");

            var updated = first with
            {
                Snapshot = first.Snapshot with
                {
                    Windows = first.Snapshot.Windows.Select(window => window with { UsedPercent = 42 }).ToArray(),
                    LastSuccessfulRefresh = first.Snapshot.LastSuccessfulRefresh?.AddMinutes(1)
                }
            };
            flyout.BindAccounts([updated, second], first.Profile.Id, false);
            Require(ReferenceEquals(originalSource, selector.ItemsSource)
                && ReferenceEquals(originalFirstChoice, selector.Items[0]),
                "A quota update rebuilt unchanged account choices.");
            flyout.BindAccounts([updated with { Snapshot = updated.Snapshot with { Status = CodexQuotaStatus.Refreshing } }, second],
                first.Profile.Id, true);
            flyout.BindAccounts([updated, second], first.Profile.Id, false);
            Require(ReferenceEquals(originalSource, selector.ItemsSource) && selections.Count == 0,
                "Refresh status rebuilt account choices or emitted selection.");

            // User selection emits once; the following model bind updates the selection
            // independently from the unchanged source and must not emit another event.
            selector.SelectedValue = second.Profile.Id;
            Require(selections.SequenceEqual([second.Profile.Id]), "Account selection did not emit exactly once.");
            flyout.BindAccounts([updated, second], second.Profile.Id, false);
            Require(ReferenceEquals(originalSource, selector.ItemsSource)
                && (string?)selector.SelectedValue == second.Profile.Id && flyout.SelectedProfileId == second.Profile.Id
                && selections.Count == 1,
                "Selection binding replaced options or emitted a duplicate selection.");
            flyout.BindAccounts([updated, second], first.Profile.Id, false);
            Require(ReferenceEquals(originalSource, selector.ItemsSource)
                && (string?)selector.SelectedValue == first.Profile.Id && selections.Count == 1,
                "External display selection did not update independently of account choices.");

            var renamed = updated with { Profile = updated.Profile with { Label = "Renamed account" } };
            flyout.BindAccounts([renamed, second], first.Profile.Id, false);
            Require(!ReferenceEquals(originalSource, selector.ItemsSource)
                && ChoiceText(selector.Items[0]) == "Renamed account · Codex",
                "A renamed account retained an outdated option label.");
            var renamedSource = selector.ItemsSource;
            flyout.BindAccounts([renamed, second], first.Profile.Id, false);
            Require(ReferenceEquals(renamedSource, selector.ItemsSource), "Unchanged renamed options rebuilt again.");

            flyout.BindAccounts([second, renamed], first.Profile.Id, false);
            Require(!ReferenceEquals(renamedSource, selector.ItemsSource)
                && ChoiceText(selector.Items[0]) == "Second account · Codex"
                && (string?)selector.SelectedValue == first.Profile.Id,
                "Account reordering retained an outdated source or lost selection.");
            var reorderedSource = selector.ItemsSource;
            var anotherProvider = renamed with
            {
                Profile = renamed.Profile with { Provider = UsageProviderId.Claude },
                Snapshot = renamed.Snapshot with { Provider = UsageProviderId.Claude }, IsConnected = true
            };
            flyout.BindAccounts([second, anotherProvider], first.Profile.Id, false);
            Require(!ReferenceEquals(reorderedSource, selector.ItemsSource)
                && ChoiceText(selector.Items[1]) == "Renamed account · Claude",
                "A changed provider retained an outdated option label.");

            var providerSource = selector.ItemsSource;
            var third = first with { Profile = first.Profile with { Id = "selector-third", Label = "Third account" } };
            flyout.BindAccounts([second, anotherProvider, third], first.Profile.Id, false);
            Require(!ReferenceEquals(providerSource, selector.ItemsSource) && selector.Items.Count == 3,
                "Adding an account did not replace the option source.");
            var addedSource = selector.ItemsSource;
            var replacement = third with { Profile = third.Profile with { Id = "selector-replacement" } };
            flyout.BindAccounts([second, anotherProvider, replacement], first.Profile.Id, false);
            Require(!ReferenceEquals(addedSource, selector.ItemsSource),
                "Same-count membership replacement retained the option source.");
            var replacementSource = selector.ItemsSource;
            flyout.BindAccounts([second], second.Profile.Id, false);
            Require(!ReferenceEquals(replacementSource, selector.ItemsSource) && selector.Items.Count == 1
                && selector.Visibility == Visibility.Collapsed && (string?)selector.SelectedValue == second.Profile.Id,
                "Removing accounts retained options or lost the remaining selection.");
            var singleSource = selector.ItemsSource;
            flyout.BindAccounts([second], second.Profile.Id, false);
            Require(ReferenceEquals(singleSource, selector.ItemsSource), "An unchanged single-account source rebuilt.");
            flyout.BindAccounts([], "", false);
            var emptySource = selector.ItemsSource;
            flyout.BindAccounts([], "", false);
            Require(selector.Items.Count == 0 && ReferenceEquals(emptySource, selector.ItemsSource)
                && selections.Count == 1 && refreshRequests == 0 && provider.RefreshCalls == 0,
                "Empty binding rebuilt options, emitted selection or requested quota.");
        }
        finally
        {
            flyout.AccountSelected -= Select;
            flyout.SyncRequested -= Refresh;
            flyout.Close();
        }
    }

    private static string? ChoiceText(object choice) =>
        choice.GetType().GetProperty("DisplayName")?.GetValue(choice) as string;

    /// <summary>
    /// Completes the production click gesture on an account module without moving or capturing
    /// the user's pointer. Only a press that landed on a module opens the popup; a press on the
    /// header or empty chrome is a separate gesture, covered by <see cref="ClickChrome"/>.
    /// </summary>
    private static void Click(FloatingWidget widget)
    {
        Press(widget, widget.Modules[0].ProfileId);
    }

    private static void ClickChrome(FloatingWidget widget) => Press(widget, null);

    private static void Press(FloatingWidget widget, string? profileId)
    {
        SetActiveWindow(new WindowInteropHelper(widget).Handle);
        typeof(FloatingWidget).GetField("_pressedProfileId", PrivateInstance)!.SetValue(widget, profileId);
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
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
