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
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                FocusFixture(focusWindow);
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                CheckLifecycle(directory, language, theme, screen, focusWindow);
            }
        }
        finally { focusWindow.Close(); }
        Console.WriteLine($"PASS: widget lifecycle recovery in both languages/dark/light on {System.Windows.Forms.Screen.AllScreens.Length} monitor(s); native hide/minimize/topmost, closed window, resume/display recreation, saved preferences, account availability and shutdown.");
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
        FloatingWidgetController? controller = null;
        controller = new FloatingWidgetController(window =>
        {
            configured++;
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
            controller.Update(settings, overview);
            Pump();
            var window = controller.CurrentWindow!;
            var hwnd = Handle(window);
            Check(Visible(window) && !window.ShowActivated && activations == 0 && GetActiveWindow() == focusHwnd,
                "Enabled widget was not shown without activation.");
            var savedPosition = window.PixelPosition;
            FocusFixture(focusWindow);
            var positioningForeground = GetForegroundWindow();
            var positioningActivations = activations;
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
            Check(GetForegroundWindow() == positioningForeground && GetActiveWindow() == focusHwnd
                && activations == positioningActivations && IsWindowEnabled(hwnd),
                $"DPI-style positioning: foregroundSame={GetForegroundWindow() == positioningForeground}, activeSame={GetActiveWindow() == focusHwnd}, activationCounts={positioningActivations}/{activations}, enabled={IsWindowEnabled(hwnd)}.");
            // Activation must work again after both successful and exceptional updates.
            SetActiveWindow(hwnd);
            Check(GetActiveWindow() == hwnd, "Widget update leaked its activation guard.");
            FocusFixture(focusWindow);
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
            controller.MaintainVisibility();
            Check(Visible(window), "Native-hidden widget remained invisible with its setting enabled.");
            Check(GetForegroundWindow() == foreground && GetActiveWindow() == focusHwnd && activations == activationStart,
                $"Native visibility repair took keyboard focus: afterWidget={GetForegroundWindow() == hwnd}, activations={activationStart}/{activations}.");

            window.WindowState = WindowState.Minimized;
            Pump();
            Check(window.IsVisible && IsIconic(hwnd), "Minimized fixture did not reproduce a logically visible but minimized widget.");
            window.RecoverPosition();
            Check(settings.WidgetPixelLeft == savedPosition!.Value.X && settings.WidgetPixelTop == savedPosition.Value.Y,
                "Minimized coordinates corrupted the saved position.");
            FocusFixture(focusWindow);
            foreground = GetForegroundWindow();
            activationStart = activations;
            controller.MaintainVisibility();
            Pump();
            Check(Visible(window) && window.WindowState == WindowState.Normal && !IsIconic(hwnd),
                "Minimized widget did not return to its normal visible state.");
            Check(window.PixelPosition == savedPosition, "Minimize/restore lost the saved location.");
            var afterRestore = GetForegroundWindow();
            Check(afterRestore == foreground && GetActiveWindow() == focusHwnd && activations == activationStart,
                $"Minimize recovery took keyboard focus: beforeWidget={foreground == hwnd}, afterWidget={afterRestore == hwnd}, active={window.IsActive}, activations={activationStart}/{activations}.");

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
                controller.MaintainVisibility();
                Pump();
                Check(!HasVisiblePredecessor(hwnd, ordinaryHwnd) && (GetWindowLong(hwnd, -20) & 8) != 0,
                    "Visibility maintenance did not repair stale TOPMOST z-order.");
                Check(GetForegroundWindow() == foreground && GetActiveWindow() == focusHwnd && activations == activationStart,
                    "TOPMOST z-order repair took keyboard focus.");
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
            Pump();
            window = controller.CurrentWindow!;
            Check(configured == 2 && Handle(window) != hwnd && Visible(window),
                "Repeated desktop notifications did not coalesce into a replacement window.");
            Check(GetForegroundWindow() == foreground && GetActiveWindow() == focusHwnd && activations == activationStart,
                $"Desktop recovery took keyboard focus: afterWidget={GetForegroundWindow() == Handle(window)}, active={window.IsActive}, activations={activationStart}/{activations}.");
            Check(window.PixelPosition == savedPosition && Math.Abs(window.Opacity - .92) < .001
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
            controller.Recreate(settings, overview);
            Pump();
            window = controller.CurrentWindow!;
            var resetHwnd = Handle(window);
            Check(configured == 4 && resetHwnd != previousResetHwnd && !IsWindowVisible(previousResetHwnd)
                && Visible(window) && Math.Abs(window.Opacity - settings.WidgetOpacity) < .001
                && !window.Topmost && (GetWindowLong(resetHwnd, -20) & 0x20) != 0,
                "Position reset did not recreate the widget with its saved display settings.");
            Check(GetForegroundWindow() == foreground && GetActiveWindow() == focusHwnd && activations == activationStart,
                "Position reset took keyboard focus.");
            ((Action?)clickEvent.GetValue(window))?.Invoke();
            Check(clicks == 2, "Position reset lost the widget input subscription.");

            if (directory is not null && screen.Primary)
                AccountUiChecks.Render(window, 245, null, Path.Combine(directory, $"widget-recovery-{language}-{theme}.png"));
            controller.RecoverAfterEnvironmentChange();
            controller.Dispose();
            Pump();
            controller.MaintainVisibility();
            Check(controller.CurrentWindow is null && configured == 4, "Recovery recreated a widget after app shutdown.");
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
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
