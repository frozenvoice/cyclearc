using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class WidgetRecoveryChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Own a stable active-window target. With only non-activating widget HWNDs, closing
        // one fixture can leave Windows choosing the next fixture as its active window.
        var focusWindow = new Window { Title = "CycleArc recovery test", Width = 220, Height = 80,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            focusWindow.Show();
            CheckRefreshStateRecovery();
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                FocusFixture(focusWindow);
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                CheckLifecycle(directory, language, theme, screen, focusWindow);
                FocusFixture(focusWindow);
                CheckFractionalPositionPreservation(language, theme, screen);
            }
        }
        finally { focusWindow.Close(); }
        Console.WriteLine($"PASS: widget lifecycle recovery in both languages/dark/light on {System.Windows.Forms.Screen.AllScreens.Length} monitor(s); native activation/focus/foreground-event proof rejects transient widget activation; native hide/minimize/topmost, closed window, resume/display recreation, fractional saved coordinates through three off/on cycles with a locked settings backup, saved preferences, account availability and shutdown.");
    }

    private static void CheckRefreshStateRecovery()
    {
        var settings = new AppSettings { FloatingWidgetEnabled = true, WidgetLeft = 100, WidgetTop = 100 };
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now, now, null, null, null,
            [new("weekly", 25, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
        var account = new CodexAccountView(new CodexAccountProfile("synthetic-refresh-recovery", "", "Work"), snapshot);
        var overview = UsageAccountOverview.Create([account], account.Profile.Id);
        using var controller = new FloatingWidgetController(_ => { });
        foreach (var refreshing in new[] { true, false })
        {
            controller.Update(settings, overview, refreshing: refreshing);
            var previous = controller.CurrentWindow;
            controller.RecoverAfterEnvironmentChange();
            Pump();
            Check(!ReferenceEquals(previous, controller.CurrentWindow), "Environment recovery did not replace its widget.");
            CheckRefreshButton();

            previous = controller.CurrentWindow;
            controller.Recreate(settings, overview);
            Pump();
            Check(!ReferenceEquals(previous, controller.CurrentWindow), "Explicit recreation did not replace its widget.");
            CheckRefreshButton();

            void CheckRefreshButton()
            {
                var button = (Button)controller.CurrentWindow!.FindName("WidgetRefreshButton");
                var expected = refreshing ? UiText.CodexRefreshing : UiText.RefreshAll;
                Check(button.IsEnabled == !refreshing && Equals(button.ToolTip, expected)
                    && System.Windows.Automation.AutomationProperties.GetName(button) == expected,
                    "Recreated widget lost the shared refresh button state or accessible label.");
            }
        }
    }

    public static void RunControlledForeground()
    {
        var focusWindow = new Window { Title = "CycleArc controlled recovery focus", Width = 220, Height = 80,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            focusWindow.Show();
            CheckControlledForegroundRecovery(focusWindow);
        }
        finally { focusWindow.Close(); }
    }

    private static void CheckFractionalPositionPreservation(UiLanguage language, AppTheme theme,
        System.Windows.Forms.Screen screen)
    {
        // Explicit fixture path: never use the real user's default SettingsStore/AppPaths.
        // Reuse one file across all monitor/language/theme cases and remove only files we own.
        var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "widget-recovery-settings.json"));
        var store = new SettingsStore(path);
        Check(!File.Exists(path) && !File.Exists(store.BackupPath) && !File.Exists(store.RecoveryBackupPath),
            "Refusing to overwrite an existing widget position settings fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixelLeft = screen.WorkingArea.Left + 100;
        var pixelTop = screen.WorkingArea.Top + 100;
        var savedLeft = pixelLeft + .25;
        var savedTop = pixelTop + .4;
        var settings = new AppSettings
        {
            FloatingWidgetEnabled = true, WidgetLeft = savedLeft, WidgetTop = savedTop,
            WidgetPixelLeft = pixelLeft, WidgetPixelTop = pixelTop, WidgetZoomPercent = 90,
            WidgetOpacity = .73, WidgetAlwaysOnTop = true, WidgetClickThrough = false,
            SnapWindowsToScreenEdges = true, WidgetHorizontalAnchor = HorizontalEdgeAnchor.None,
            WidgetVerticalAnchor = VerticalEdgeAnchor.None, FlyoutZoomPercent = 130, FlyoutPinned = true,
            StartWithWindows = true, UiLanguage = language, Theme = theme
        };
        var now = DateTimeOffset.Now;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now, now,
            null, null, null, [new("weekly", 25, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
        var account = new CodexAccountView(new CodexAccountProfile("synthetic-widget-position", "", "Work"), snapshot);
        var overview = UsageAccountOverview.Create([account], account.Profile.Id);
        var configured = 0;
        var moved = 0;
        var trace = $"{language}/{theme}/{screen.DeviceName}";
        FloatingWidgetController? controller = null;
        FileStream? backupReader = null;
        try
        {
            store.Save(settings);
            var originalJson = File.ReadAllText(path);
            settings = store.Load();
            controller = new FloatingWidgetController(window =>
            {
                configured++;
                // The production app persists this event, including both DIP and native
                // coordinates. An unconditional restore notification would round the DIP
                // value and rewrite the user's settings even without any actual movement.
                window.Moved += (left, top) =>
                {
                    if (!ReferenceEquals(controller?.CurrentWindow, window)) return;
                    moved++;
                    settings.WidgetLeft = left;
                    settings.WidgetTop = top;
                    settings.WidgetHorizontalAnchor = window.EdgeAnchors.Horizontal;
                    settings.WidgetVerticalAnchor = window.EdgeAnchors.Vertical;
                    if (window.PixelPosition is { } pixels)
                    {
                        settings.WidgetPixelLeft = pixels.X;
                        settings.WidgetPixelTop = pixels.Y;
                    }
                    store.Save(settings);
                };
            });

            void CheckPreserved(string stage, bool enabled)
            {
                var loaded = store.Load();
                var window = controller.CurrentWindow!;
                Check(settings.WidgetLeft == savedLeft && settings.WidgetTop == savedTop
                    && loaded.WidgetLeft == savedLeft && loaded.WidgetTop == savedTop,
                    $"{trace}/{stage}: saved fractional DIP position changed "
                    + $"({savedLeft:R},{savedTop:R} -> {loaded.WidgetLeft:R},{loaded.WidgetTop:R}).");
                Check(loaded.WidgetPixelLeft == pixelLeft && loaded.WidgetPixelTop == pixelTop
                    && window.PixelPosition == (pixelLeft, pixelTop),
                    $"{trace}/{stage}: saved/native physical position changed.");
                Check(loaded.FloatingWidgetEnabled == enabled && loaded.WidgetZoomPercent == 90
                    && loaded.WidgetOpacity == .73 && loaded.WidgetAlwaysOnTop && !loaded.WidgetClickThrough
                    && loaded.SnapWindowsToScreenEdges && loaded.WidgetHorizontalAnchor == HorizontalEdgeAnchor.None
                    && loaded.WidgetVerticalAnchor == VerticalEdgeAnchor.None
                    && window.EdgeAnchors == default && window.ZoomPercent == 90
                    && window.Opacity == 1 && Math.Abs(window.BackgroundOpacity - .73) < .001,
                    $"{trace}/{stage}: saved or applied widget preferences changed.");
                Check(moved == 0, $"{trace}/{stage}: restoring a position reported {moved} movement(s).");
                if (enabled)
                    Check(File.ReadAllText(path) == originalJson,
                        $"{trace}/{stage}: re-enabling/restoring changed unrelated persisted settings.");
            }

            controller.Update(settings, overview, applySettings: true);
            Pump();
            var window = controller.CurrentWindow!;
            var hwnd = Handle(window);
            Check(Visible(window), $"{trace}: fractional-coordinate widget was not visible initially.");
            CheckPreserved("initial show", enabled: true);
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                settings = store.Load();
                settings.FloatingWidgetEnabled = false;
                store.Save(settings);
                controller.Update(settings, overview, applySettings: true);
                Pump();
                controller.MaintainVisibility();
                Check(Handle(controller.CurrentWindow!) == hwnd && !IsWindowVisible(hwnd),
                    $"{trace}/off {cycle}: disabling did not hide and retain the same HWND.");
                CheckPreserved($"off {cycle}", enabled: false);

                settings = store.Load();
                settings.FloatingWidgetEnabled = true;
                // Reproduce the build-local failure at the real persistence boundary:
                // a reader retains the canonical backup without allowing deletion.
                if (cycle == 1)
                    backupReader = new FileStream(store.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                store.Save(settings);
                Check(store.BackupRotationDeferred && File.Exists(store.RecoveryBackupPath),
                    $"{trace}/on {cycle}: a locked backup did not retain the previous valid recovery copy.");
                controller.Update(settings, overview, applySettings: true);
                Pump();
                controller.MaintainVisibility();
                Check(Handle(controller.CurrentWindow!) == hwnd && configured == 1 && Visible(window),
                    $"{trace}/on {cycle}: re-enabling recreated or failed to show the retained HWND.");
                CheckPreserved($"on {cycle}", enabled: true);
            }

            controller.RecoverAfterEnvironmentChange();
            controller.RecoverAfterEnvironmentChange();
            Pump();
            window = controller.CurrentWindow!;
            Check(configured == 2 && Handle(window) != hwnd && Visible(window),
                $"{trace}: environment recovery did not recreate the widget once.");
            CheckPreserved("environment recreation", enabled: true);

            // Removing the restore notification must not silence genuine recovery writes.
            // Put the actual HWND outside every work area, then invoke production recovery.
            hwnd = Handle(window);
            var screens = System.Windows.Forms.Screen.AllScreens;
            var outsideX = screens.Max(s => s.WorkingArea.Right) + 400;
            var outsideY = screens.Max(s => s.WorkingArea.Bottom) + 400;
            Check(SetWindowPos(hwnd, IntPtr.Zero, outsideX, outsideY, 0, 0, 0x0015),
                $"{trace}: offscreen movement fixture failed."); // NOSIZE | NOZORDER | NOACTIVATE
            Check(window.PixelPosition == (outsideX, outsideY), $"{trace}: offscreen fixture did not move the HWND.");
            window.RecoverPosition();
            Pump();
            var recovered = store.Load();
            Check(moved > 0 && window.PixelPosition is { } recoveredPixels
                && recovered.WidgetPixelLeft == recoveredPixels.X && recovered.WidgetPixelTop == recoveredPixels.Y
                && recovered.WidgetLeft == window.Left && recovered.WidgetTop == window.Top,
                $"{trace}: genuine offscreen recovery did not persist the corrected DIP/native position.");
            Check(window.PixelPosition != (outsideX, outsideY) && Visible(window)
                && recovered.WidgetZoomPercent == 90 && recovered.WidgetOpacity == .73
                && recovered.FlyoutZoomPercent == 130 && recovered.FlyoutPinned && recovered.StartWithWindows,
                $"{trace}: offscreen recovery lost visibility or unrelated preferences.");
        }
        finally
        {
            backupReader?.Dispose();
            controller?.Dispose();
            File.Delete(path);
            File.Delete(store.BackupPath);
            File.Delete(store.RecoveryBackupPath);
        }
    }

    private static void CheckLifecycle(string? directory, UiLanguage language, AppTheme theme, System.Windows.Forms.Screen screen, Window focusWindow)
    {
        var focusHwnd = new WindowInteropHelper(focusWindow).Handle;
        var now = DateTimeOffset.Now;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now, now,
            null, null, null, [new("weekly", 25, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
        var account = new CodexAccountView(new CodexAccountProfile("synthetic-widget", "", "Work"), snapshot);
        var overview = UsageAccountOverview.Create([account], account.Profile.Id);
        var empty = UsageAccountOverview.Create([], "");
        var settings = new AppSettings { FloatingWidgetEnabled = true, WidgetPixelLeft = screen.WorkingArea.Left + 100,
            WidgetPixelTop = screen.WorkingArea.Top + 100, WidgetLeft = 100, WidgetTop = 100, WidgetOpacity = .92,
            WidgetAlwaysOnTop = true, WidgetClickThrough = false };
        var configured = 0;
        var clicks = 0;
        var activations = 0;
        using var nativeFocus = new WidgetFocusTrace();
        FloatingWidgetController? controller = null;
        controller = new FloatingWidgetController(window =>
        {
            configured++;
            nativeFocus.Observe(window);
            window.Activated += (_, _) => activations++;
            window.Moved += (left, top) =>
            {
                if (!ReferenceEquals(controller?.CurrentWindow, window)) return;
                settings.WidgetLeft = left; settings.WidgetTop = top;
                if (window.PixelPosition is { } pixels)
                {
                    settings.WidgetPixelLeft = pixels.X; settings.WidgetPixelTop = pixels.Y;
                }
            };
            window.FlyoutRequested += () => clicks++;
        });
        using (controller)
        {
            var creationForeground = GetForegroundWindow();
            controller.Update(settings, overview);
            Pump();
            var window = controller.CurrentWindow!;
            var hwnd = Handle(window);
            Check(Visible(window) && !window.ShowActivated, "Enabled widget was not shown without activation.");
            CheckPassiveFocus("Enabled widget initial show", creationForeground, focusHwnd, hwnd,
                0, activations, 0, nativeFocus);
            var savedPosition = window.PixelPosition;
            FocusFixture(focusWindow);
            var positioningForeground = GetForegroundWindow();
            var positioningActivations = activations;
            var positioningNativeFocus = nativeFocus.Events;
            // Simulate WPF's nested DPI resize even on single-monitor CI desktops.
            var injectDpiResize = true;
            HwndSourceHook dpiResize = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message == 0x0046 && injectDpiResize) // WM_WINDOWPOSCHANGING
                {
                    injectDpiResize = false;
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0007); // Deliberately omit NOACTIVATE.
                }
                return IntPtr.Zero;
            };
            var source = HwndSource.FromHwnd(hwnd)!;
            source.AddHook(dpiResize);
            try { window.Apply(settings); Pump(); }
            finally { source.RemoveHook(dpiResize); }
            Check(!injectDpiResize, "The nested DPI resize fixture did not run.");
            CheckPassiveFocus("DPI-style positioning", positioningForeground, focusHwnd, hwnd,
                positioningActivations, activations, positioningNativeFocus, nativeFocus);
            Check(IsWindowEnabled(hwnd), "DPI-style positioning left the widget disabled.");
            // Activation must work again after both successful and exceptional updates.
            var injectionForeground = GetForegroundWindow();
            var injectionActivations = activations;
            var injectionNativeFocus = nativeFocus.Events;
            SetActiveWindow(hwnd);
            Check(GetActiveWindow() == hwnd, "Widget update leaked its activation guard.");
            FocusFixture(focusWindow);
            var detectedTransientActivation = false;
            try { CheckPassiveFocus("Injected transient widget activation", injectionForeground, focusHwnd, hwnd,
                injectionActivations, activations, injectionNativeFocus, nativeFocus); }
            catch (InvalidOperationException) { detectedTransientActivation = true; }
            Check(detectedTransientActivation && nativeFocus.Events > injectionNativeFocus,
                "The native focus detector accepted transient widget activation after focus was returned.");
            var rejected = false;
            try { window.Apply(null!); }
            catch (NullReferenceException) { rejected = true; }
            Check(rejected, "The exceptional-update fixture did not fail.");
            SetActiveWindow(hwnd);
            Check(GetActiveWindow() == hwnd, "Failed widget update leaked its activation guard.");
            FocusFixture(focusWindow);
            window.Apply(settings);
            Pump();
            for (var i = 0; i < 3; i++) { controller.Update(settings, overview); controller.MaintainVisibility(); }
            Check(configured == 1 && window.PixelPosition == savedPosition, "Routine quota updates recreated or moved the widget.");

            // Win32 visibility can disagree with WPF's public IsVisible property.
            ShowWindow(hwnd, 0); // HIDE
            Check(!IsWindowVisible(hwnd), "Native-hide fixture did not hide the window.");
            FocusFixture(focusWindow);
            var foreground = GetForegroundWindow();
            var activationStart = activations;
            var nativeFocusStart = nativeFocus.Events;
            controller.MaintainVisibility();
            Check(Visible(window), "Native-hidden widget remained invisible with its setting enabled.");
            CheckPassiveFocus("Native visibility repair", foreground, focusHwnd, hwnd,
                activationStart, activations, nativeFocusStart, nativeFocus);

            window.WindowState = WindowState.Minimized;
            Pump();
            Check(window.IsVisible && IsIconic(hwnd), "Minimized fixture did not reproduce a logically visible but minimized widget.");
            window.RecoverPosition();
            Check(settings.WidgetPixelLeft == savedPosition!.Value.X && settings.WidgetPixelTop == savedPosition.Value.Y,
                "Minimized coordinates corrupted the saved position.");
            FocusFixture(focusWindow);
            foreground = GetForegroundWindow();
            activationStart = activations;
            nativeFocusStart = nativeFocus.Events;
            controller.MaintainVisibility();
            Pump();
            Check(Visible(window) && window.WindowState == WindowState.Normal && !IsIconic(hwnd),
                "Minimized widget did not return to its normal visible state.");
            Check(window.PixelPosition == savedPosition, "Minimize/restore lost the saved location.");
            CheckPassiveFocus("Minimize recovery", foreground, focusHwnd, hwnd,
                activationStart, activations, nativeFocusStart, nativeFocus);

            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013); // Lose native TOPMOST.
            Check((GetWindowLong(hwnd, -20) & 8) == 0, "Native topmost-loss fixture failed.");
            controller.MaintainVisibility();
            Check((GetWindowLong(hwnd, -20) & 8) != 0, "Always-on-top preference was not restored.");

            // Exercise the native-order detector independently of the style bit.
            // Windows does not reliably allow forcing a stale TOPMOST bit directly.
            var ordinary = new Window { Title = "CycleArc ordinary z-order fixture", Width = 220, Height = 80,
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            try
            {
                ordinary.Show();
                Pump();
                var ordinaryHwnd = new WindowInteropHelper(ordinary).Handle;
                SetWindowPos(ordinaryHwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0053); // TOP | NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
                SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013); // HWND_NOTOPMOST
                SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0013); // HWND_BOTTOM
                var detectDisplacement = typeof(FloatingWidget).GetMethod("IsDisplacedByOrdinaryWindow",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                Check(HasVisiblePredecessor(hwnd, ordinaryHwnd)
                    && (bool)detectDisplacement.Invoke(null, [hwnd])!,
                    "The native-order detector missed an ordinary visible window above the widget.");
                FocusFixture(focusWindow);
                foreground = GetForegroundWindow();
                activationStart = activations;
                nativeFocusStart = nativeFocus.Events;
                controller.MaintainVisibility();
                Pump();
                Check(!HasVisiblePredecessor(hwnd, ordinaryHwnd) && (GetWindowLong(hwnd, -20) & 8) != 0,
                    "Visibility maintenance did not repair stale TOPMOST z-order.");
                CheckPassiveFocus("TOPMOST z-order repair", foreground, focusHwnd, hwnd,
                    activationStart, activations, nativeFocusStart, nativeFocus);
                // Once the widget is back in the topmost band, a legitimate
                // topmost sibling must stay above it on ordinary timer checks.
                SetWindowPos(ordinaryHwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                Check(HasVisiblePredecessor(hwnd, ordinaryHwnd), "Topmost sibling fixture was not above the widget.");
                Check(!(bool)detectDisplacement.Invoke(null, [hwnd])! && !window.EnsureVisible(true),
                    "A legitimate topmost sibling incorrectly triggered native repair.");
                controller.MaintainVisibility();
                Check(HasVisiblePredecessor(hwnd, ordinaryHwnd), "Routine maintenance displaced a legitimate topmost sibling.");
            }
            finally { ordinary.Close(); }
            settings.WidgetAlwaysOnTop = false;
            settings.WidgetClickThrough = true;
            controller.Update(settings, overview, applySettings: true);
            controller.MaintainVisibility();
            Check((GetWindowLong(hwnd, -20) & 8) == 0 && (GetWindowLong(hwnd, -20) & 0x20) != 0,
                "Recovery ignored the topmost/click-through preferences.");

            FocusFixture(focusWindow);
            controller.RecoverAfterEnvironmentChange();
            controller.RecoverAfterEnvironmentChange();
            foreground = GetForegroundWindow();
            activationStart = activations;
            nativeFocusStart = nativeFocus.Events;
            Pump();
            window = controller.CurrentWindow!;
            Check(configured == 2 && Handle(window) != hwnd && Visible(window),
                "Repeated desktop notifications did not coalesce into a replacement window.");
            CheckPassiveFocus("Desktop recovery", foreground, focusHwnd, Handle(window),
                activationStart, activations, nativeFocusStart, nativeFocus);
            Check(window.PixelPosition == savedPosition && window.Opacity == 1 && Math.Abs(window.BackgroundOpacity - .92) < .001
                && !window.Topmost && (GetWindowLong(Handle(window), -20) & 0x20) != 0,
                "Resume recovery lost saved position, opacity or interaction settings.");

            window.Close();
            controller.MaintainVisibility();
            Pump();
            window = controller.CurrentWindow!;
            Check(configured == 3 && Visible(window) && settings.FloatingWidgetEnabled,
                "Unexpected window closure left an enabled widget missing.");
            // Each recreated instance must have one usable set of click handlers.
            var clickEvent = typeof(FloatingWidget).GetField("FlyoutRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Action?)clickEvent.GetValue(window))?.Invoke();
            Check(clicks == 1, "Recreated widget lost or duplicated its input subscription.");

            controller.Update(settings, empty);
            controller.MaintainVisibility();
            Check(!IsWindowVisible(Handle(window)), "Recovery showed an accountless widget.");
            controller.Update(settings, overview);
            Check(Visible(window), "Widget did not return when an account became available.");

            // Adding and removing accounts reflows the same window instead of recreating it.
            var configuredBeforeAccounts = configured;
            var second = account with { Profile = account.Profile with { Id = "synthetic-widget-2", Label = "Lab" } };
            var third = account with { Profile = account.Profile with { Id = "synthetic-widget-3", Label = "Kakao" },
                Snapshot = snapshot with { Provider = UsageProviderId.Claude } };
            controller.Update(settings, UsageAccountOverview.Create([account, second, third], account.Profile.Id));
            Pump();
            Check(configured == configuredBeforeAccounts && ReferenceEquals(controller.CurrentWindow, window),
                "Adding accounts recreated the widget window.");
            Check(window.Modules.Count == 3 && window.Modules.Select(m => m.ProfileId)
                    .SequenceEqual(new[] { account.Profile.Id, second.Profile.Id, third.Profile.Id }),
                "The widget did not follow the managed account list and order.");
            controller.Update(settings, UsageAccountOverview.Create([account, third], account.Profile.Id));
            Pump();
            Check(configured == configuredBeforeAccounts && window.Modules.Count == 2
                && window.Modules.All(m => m.ProfileId != second.Profile.Id),
                "A removed account stayed in the widget.");
            controller.Update(settings, overview);
            Check(window.Modules.Count == 1, "The widget did not return to the single displayable account.");

            controller.RecoverAfterEnvironmentChange();
            settings.FloatingWidgetEnabled = false;
            controller.Update(settings, overview);
            Pump();
            controller.MaintainVisibility();
            Check(configured == 3 && !IsWindowVisible(Handle(window)), "Pending recovery overrode explicit widget disable.");
            settings.FloatingWidgetEnabled = true;
            controller.Update(settings, overview, applySettings: true);
            Pump();
            Check(Visible(window), "Re-enabling did not show the retained widget.");

            // An explicit settings position reset must replace the HWND while keeping
            // display preferences, subscriptions, and the current focus untouched.
            FocusFixture(focusWindow);
            var previousResetWindow = window;
            var previousResetHwnd = Handle(previousResetWindow);
            settings.WidgetPixelLeft = null;
            settings.WidgetPixelTop = null;
            settings.WidgetLeft = screen.WorkingArea.Left + 40;
            settings.WidgetTop = screen.WorkingArea.Top + 40;
            foreground = GetForegroundWindow();
            activationStart = activations;
            nativeFocusStart = nativeFocus.Events;
            controller.Recreate(settings, overview);
            Pump();
            window = controller.CurrentWindow!;
            var resetHwnd = Handle(window);
            Check(configured == 4 && resetHwnd != previousResetHwnd && !IsWindowVisible(previousResetHwnd)
                && Visible(window) && window.Opacity == 1 && Math.Abs(window.BackgroundOpacity - settings.WidgetOpacity) < .001
                && !window.Topmost && (GetWindowLong(resetHwnd, -20) & 0x20) != 0,
                "Position reset did not recreate the widget with its saved display settings.");
            var resetForeground = GetForegroundWindow();
            var resetActive = GetActiveWindow();
            var resetFocusTrace = $"{language}/{theme}/{screen.DeviceName}: "
                + $"foreground={WindowIdentity(foreground)}->{WindowIdentity(resetForeground)}, "
                + $"active={WindowIdentity(resetActive)}, fixture={WindowIdentity(focusHwnd)}, "
                + $"widget={WindowIdentity(resetHwnd)}, activations={activationStart}->{activations}";
            Console.WriteLine("Position reset focus: " + resetFocusTrace);
            CheckPassiveFocus("Position reset", foreground, focusHwnd, resetHwnd,
                activationStart, activations, nativeFocusStart, nativeFocus);
            ((Action?)clickEvent.GetValue(window))?.Invoke();
            Check(clicks == 2, "Position reset lost the widget input subscription.");

            if (directory is not null && screen.Primary)
                WidgetFixture.RenderWidget(window, Path.Combine(directory, $"widget-recovery-{language}-{theme}.png"));
            controller.RecoverAfterEnvironmentChange();
            controller.Dispose();
            Pump();
            controller.MaintainVisibility();
            Check(controller.CurrentWindow is null && configured == 4, "Recovery recreated a widget after app shutdown.");
        }
    }

    private sealed class WidgetFocusTrace : IDisposable
    {
        private readonly HashSet<IntPtr> _widgets = [];
        private readonly List<FloatingWidget> _windows = [];
        private readonly WinEventCallback _foregroundCallback;
        private readonly IntPtr _foregroundHook;
        public int Events { get; private set; }
        // A closed widget can keep WPF IsActive after Close when this thread never
        // receives a matching deactivate. That is not a visible focus change. Native
        // activation messages still count for every instance, including ones later closed.
        public bool HasActiveWidget => _windows.Any(window => window.IsVisible && window.IsActive);
        public string ActiveWidgets => string.Join(", ", _windows.Select(window =>
            $"0x{Handle(window).ToInt64():X}/visible={window.IsVisible}/active={window.IsActive}/loaded={window.IsLoaded}"));

        public WidgetFocusTrace()
        {
            _foregroundCallback = (_, _, hwnd, _, _, _, _) =>
            {
                if (_widgets.Contains(hwnd)) Events++;
            };
            _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback,
                (uint)Environment.ProcessId, 0, 0); // EVENT_SYSTEM_FOREGROUND, OUTOFCONTEXT
            Check(_foregroundHook != IntPtr.Zero, "Cannot observe native widget foreground events.");
        }

        public void Observe(FloatingWidget window)
        {
            _windows.Add(window);
            window.SourceInitialized += (_, _) =>
            {
                _widgets.Add(Handle(window));
                HwndSource.FromHwnd(Handle(window))!.AddHook(ObserveMessage);
            };
        }

        private IntPtr ObserveMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Record transient native focus as well as WPF Activated. A widget that takes
            // and then releases focus must fail even if another app is foreground by the end.
            if (message == 0x0007 || (message == 0x0006 && (wParam.ToInt64() & 0xFFFF) != 0)) Events++;
            return IntPtr.Zero;
        }

        public void Dispose() => UnhookWinEvent(_foregroundHook);
    }

    private static void CheckPassiveFocus(string phase, IntPtr beforeForeground, IntPtr fixture,
        IntPtr widget, int beforeActivations, int activations, int beforeNativeFocus, WidgetFocusTrace nativeFocus)
    {
        var afterForeground = GetForegroundWindow();
        var active = GetActiveWindow();
        var focus = GetFocus();
        var ourThread = GetWindowThreadProcessId(fixture, out _);
        var foregroundThread = GetWindowThreadProcessId(afterForeground, out _);
        // SetActiveWindow establishes this thread's target, not the user's foreground.
        // Another input thread may legitimately become foreground while Pump dispatches.
        // Require exact ownership when the synthetic fixture really was foreground.
        // Background checks use native widget events, not equality between unrelated
        // user windows. Never forgive activation/focus messages on any widget instance.
        var externalChurn = afterForeground != IntPtr.Zero && foregroundThread != ourThread
            && (afterForeground != beforeForeground && active == fixture || active == IntPtr.Zero);
        var guarded = beforeForeground == fixture;
        var preserved = !guarded || afterForeground == fixture && active == fixture;
        var child = IsChild(widget, focus);
        var detail = $"{phase}: foreground={WindowIdentity(beforeForeground)}->{WindowIdentity(afterForeground)}, "
            + $"active={WindowIdentity(active)}, focus={WindowIdentity(focus)}, fixture={WindowIdentity(fixture)}, "
            + $"widget={WindowIdentity(widget)}, activations={beforeActivations}->{activations}, "
            + $"nativeFocus={beforeNativeFocus}->{nativeFocus.Events}, guarded={guarded}, externalChurn={externalChurn}, "
            + $"child={child}, wpfActive=[{nativeFocus.ActiveWidgets}]";
        Check(preserved && afterForeground != widget && active != widget
            && focus != widget && !IsChild(widget, focus) && activations == beforeActivations
            && !nativeFocus.HasActiveWidget && nativeFocus.Events == beforeNativeFocus,
            "Passive widget focus invariant failed. " + detail);
        if (externalChurn) Console.WriteLine("External foreground change without widget activation: " + detail);
    }

    private static void CheckControlledForegroundRecovery(Window focusWindow)
    {
        var originalForeground = GetForegroundWindow();
        var target = new TextBox { Text = "Synthetic keyboard focus target" };
        focusWindow.Content = target;
        var fixture = new WindowInteropHelper(focusWindow).Handle;
        using var trace = new WidgetFocusTrace();
        var activations = 0;
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, now, now, null, null,
            null, [new("weekly", 25, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
        var account = new CodexAccountView(new CodexAccountProfile("synthetic-focus", "", "Focus"), snapshot);
        var overview = UsageAccountOverview.Create([account], account.Profile.Id);
        var settings = new AppSettings { FloatingWidgetEnabled = true, WidgetAlwaysOnTop = true,
            WidgetLeft = 100, WidgetTop = 100 };
        using var controller = new FloatingWidgetController(window =>
        {
            trace.Observe(window);
            window.Activated += (_, _) => activations++;
        });
        try
        {
            controller.Update(settings, overview);
            Pump();
            var window = controller.CurrentWindow!;
            var hwnd = Handle(window);
            void EstablishForeground()
            {
                if (!SetForegroundWindow(fixture)) ClickSyntheticForeground(focusWindow, target);
                focusWindow.Activate();
                Keyboard.Focus(target);
                Pump();
                Check(GetForegroundWindow() == fixture && GetActiveWindow() == fixture
                    && GetFocus() != IntPtr.Zero && ReferenceEquals(Keyboard.FocusedElement, target),
                    "Controlled fixture does not own real foreground/native/WPF keyboard focus.");
            }
            void Verify(Action repair, string phase)
            {
                EstablishForeground();
                var nativeFocus = GetFocus();
                var before = trace.Events;
                var activationStart = activations;
                repair();
                Pump();
                Check(GetForegroundWindow() == fixture && GetActiveWindow() == fixture
                    && GetFocus() == nativeFocus && ReferenceEquals(Keyboard.FocusedElement, target)
                    && trace.Events == before && activations == activationStart,
                    "Controlled foreground/keyboard focus changed during " + phase);
            }
            window.WindowState = WindowState.Minimized;
            Pump();
            Verify(controller.MaintainVisibility, "minimize recovery");
            Check(Visible(window), "Controlled minimized widget was not recovered.");
            ShowWindow(hwnd, 0);
            Verify(controller.MaintainVisibility, "native hide recovery");
            Verify(controller.RecoverAfterEnvironmentChange, "environment recreation");
            window = controller.CurrentWindow!;
            hwnd = Handle(window);

            // Deterministically reproduce the old failure predicate in background mode:
            // another input thread switches between its two foreground windows while
            // the main Dispatcher processes recovery. No widget receives focus.
            using var external = new OtherInputThreadFixture();
            window.WindowState = WindowState.Minimized;
            Pump();
            external.Activate();
            Pump();
            FocusFixture(focusWindow);
            var beforeForeground = GetForegroundWindow();
            var beforeNative = trace.Events;
            var beforeActivations = activations;
            controller.MaintainVisibility();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(external.ActivateSecond));
            Pump();
            Check(GetForegroundWindow() == external.SecondHandle && GetForegroundWindow() != beforeForeground
                && trace.Events == beforeNative && activations == beforeActivations,
                "The external foreground-change reproduction did not run without widget activation.");
            CheckPassiveFocus("Controlled external foreground change", beforeForeground, fixture, hwnd,
                beforeActivations, activations, beforeNative, trace);

            // Prove external churn cannot mask a genuine (including transient) widget
            // activation. This explicit test action is outside passive production updates.
            SetActiveWindow(hwnd);
            FocusFixture(focusWindow);
            var rejected = false;
            try { CheckPassiveFocus("Injected widget activation", beforeForeground, fixture, hwnd,
                beforeActivations, activations, beforeNative, trace); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && trace.Events > beforeNative,
                "The focus detector accepted injected widget activation during external churn.");
            Console.WriteLine("PASS: controlled real foreground/native/WPF keyboard focus preserved through hide, minimize and recreation; external foreground churn reproduced and genuine transient widget activation rejected.");
        }
        finally
        {
            controller.Dispose();
            if (originalForeground != IntPtr.Zero && GetForegroundWindow() == fixture)
                SetForegroundWindow(originalForeground);
            focusWindow.Content = null;
            FocusFixture(focusWindow);
        }
    }

    private sealed class OtherInputThreadFixture : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher? _dispatcher;
        private Window? _window;
        private Window? _secondWindow;
        public IntPtr Handle { get; private set; }
        public IntPtr SecondHandle { get; private set; }

        public OtherInputThreadFixture()
        {
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            _thread = new Thread(() =>
            {
                try
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _window = new Window { Title = "Synthetic external input thread", Width = 220, Height = 80,
                        ShowActivated = false, ShowInTaskbar = false };
                    _window.Show();
                    Handle = new WindowInteropHelper(_window).Handle;
                    _secondWindow = new Window { Title = "Synthetic external input thread second target",
                        Width = 220, Height = 80, ShowActivated = false, ShowInTaskbar = false };
                    _secondWindow.Show();
                    SecondHandle = new WindowInteropHelper(_secondWindow).Handle;
                }
                catch (Exception error) { failure = error; }
                finally { ready.Set(); }
                if (failure is null) Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Check(ready.Wait(TimeSpan.FromSeconds(5)) && failure is null && Handle != IntPtr.Zero,
                "Cannot create controlled external input-thread fixture: " + failure);
        }

        public void Activate() => _dispatcher!.Invoke(() =>
        {
            Check(SetForegroundWindow(Handle), "Cannot foreground controlled external input thread.");
            _window!.Activate();
        });

        public void ActivateSecond() => _dispatcher!.Invoke(() =>
        {
            Check(SetForegroundWindow(SecondHandle), "Cannot switch controlled external input-thread foreground.");
            _secondWindow!.Activate();
        });

        public void Dispose()
        {
            _dispatcher!.Invoke(() => { _secondWindow!.Close(); _window!.Close();
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); });
            Check(_thread.Join(TimeSpan.FromSeconds(5)), "External input-thread fixture did not stop.");
        }
    }

    private static void ClickSyntheticForeground(Window window, FrameworkElement target)
    {
        // Foreground-lock policy may reject SetForegroundWindow when a developer is
        // using another app. Exercise the same safe native-fixture input as the button
        // suite: no input is sent unless the pointer resolves to our synthetic HWND.
        Check(new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }
            .All(key => (GetAsyncKeyState(key) & 0x8000) == 0),
            "Cannot acquire synthetic foreground while mouse buttons or modifiers are held.");
        Check(GetCursorPos(out var original), "Cannot read cursor for controlled focus fixture.");
        var previousTopmost = window.Topmost;
        NativePoint pointer = default;
        var moved = false;
        try
        {
            window.Topmost = true;
            var work = (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => !screen.Primary)
                ?? System.Windows.Forms.Screen.PrimaryScreen!).WorkingArea;
            Check(SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1),
                work.Left + 40, work.Top + 40, 0, 0, 0x0051),
                "Cannot place owned synthetic foreground guard in native topmost band.");
            Pump();
            var point = target.PointToScreen(new Point(10, 10));
            pointer = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
            Check(SetCursorPos(pointer.X, pointer.Y), "Cannot position cursor over synthetic foreground target.");
            moved = true;
            Pump();
            Check(GetCursorPos(out var actual) && actual.X == pointer.X && actual.Y == pointer.Y
                && WindowFromPoint(actual) == new WindowInteropHelper(window).Handle,
                $"Refusing native focus click outside the synthetic guard: requested={pointer.X},{pointer.Y}; "
                + $"actual={actual.X},{actual.Y}; hit={WindowIdentity(WindowFromPoint(actual))}; "
                + $"guard={WindowIdentity(new WindowInteropHelper(window).Handle)}; "
                + $"visible={window.IsVisible}; target={target.ActualWidth:F1}x{target.ActualHeight:F1}.");
            var inputs = new[] { new NativeInput { Data = new NativeInputData { Mouse = new NativeMouse { Flags = 2 } } },
                new NativeInput { Data = new NativeInputData { Mouse = new NativeMouse { Flags = 4 } } } };
            var written = SendInput(2, inputs, Marshal.SizeOf<NativeInput>());
            if (written == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<NativeInput>());
            Check(written == 2, "Interactive desktop rejected the synthetic foreground click.");
            Pump();
        }
        finally
        {
            window.Topmost = previousTopmost;
            if (moved && GetCursorPos(out var current) && current.X == pointer.X && current.Y == pointer.Y)
                SetCursorPos(original.X, original.Y);
        }
    }

    private static void FocusFixture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        // Establish this thread's active window without stealing the user's foreground.
        SetActiveWindow(hwnd);
        Pump();
        Check(GetActiveWindow() == hwnd, "The synthetic active-window fixture did not activate.");
    }

    private static IntPtr Handle(FloatingWidget window) => new WindowInteropHelper(window).Handle;
    private static string WindowIdentity(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return $"0x{hwnd.ToInt64():X}/pid={processId}";
    }
    private static bool HasVisiblePredecessor(IntPtr hwnd, IntPtr target)
    {
        for (var above = GetWindow(hwnd, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
            if (above == target) return IsWindowVisible(above) && !IsIconic(above);
        return false;
    }
    private static bool Visible(FloatingWidget window) => window.IsVisible && IsWindowVisible(Handle(window)) && !IsIconic(Handle(window));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
        int childId, uint threadId, uint eventTime);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputData
    {
        [FieldOffset(0)] public NativeMouse Mouse;
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
