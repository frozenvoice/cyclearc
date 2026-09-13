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
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            CheckLifecycle(directory, language, theme, screen);
        }
        Console.WriteLine($"PASS: widget lifecycle recovery in both languages/dark/light on {System.Windows.Forms.Screen.AllScreens.Length} monitor(s); native hide/minimize/topmost, closed window, resume/display recreation, saved preferences, account availability and shutdown.");
    }

    private static void CheckLifecycle(string? directory, UiLanguage language, AppTheme theme, System.Windows.Forms.Screen screen)
    {
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
        FloatingWidgetController? controller = null;
        controller = new FloatingWidgetController(window =>
        {
            configured++;
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
            Check(Visible(window) && !window.ShowActivated, "Enabled widget was not shown without activation.");
            var savedPosition = window.PixelPosition;
            for (var i = 0; i < 3; i++) { controller.Update(settings, overview); controller.MaintainVisibility(); }
            Check(configured == 1 && window.PixelPosition == savedPosition, "Routine quota updates recreated or moved the widget.");

            // Win32 visibility can disagree with WPF's public IsVisible property.
            ShowWindow(hwnd, 0); // HIDE
            Check(!IsWindowVisible(hwnd), "Native-hide fixture did not hide the window.");
            var foreground = GetForegroundWindow();
            controller.MaintainVisibility();
            Check(Visible(window), "Native-hidden widget remained invisible with its setting enabled.");
            Check(GetForegroundWindow() == foreground, "Native visibility repair took keyboard focus.");

            window.WindowState = WindowState.Minimized;
            Pump();
            Check(window.IsVisible && IsIconic(hwnd), "Minimized fixture did not reproduce a logically visible but minimized widget.");
            window.RecoverPosition();
            Check(settings.WidgetPixelLeft == savedPosition!.Value.X && settings.WidgetPixelTop == savedPosition.Value.Y,
                "Minimized coordinates corrupted the saved position.");
            foreground = GetForegroundWindow();
            controller.MaintainVisibility();
            Pump();
            Check(Visible(window) && window.WindowState == WindowState.Normal && !IsIconic(hwnd),
                "Minimized widget did not return to its normal visible state.");
            Check(window.PixelPosition == savedPosition, "Minimize/restore lost the saved location.");
            Check(GetForegroundWindow() == foreground, "Minimize recovery took keyboard focus.");

            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013); // Lose native TOPMOST.
            Check((GetWindowLong(hwnd, -20) & 8) == 0, "Native topmost-loss fixture failed.");
            controller.MaintainVisibility();
            Check((GetWindowLong(hwnd, -20) & 8) != 0, "Always-on-top preference was not restored.");
            settings.WidgetAlwaysOnTop = false;
            settings.WidgetClickThrough = true;
            controller.Update(settings, overview, applySettings: true);
            controller.MaintainVisibility();
            Check((GetWindowLong(hwnd, -20) & 8) == 0 && (GetWindowLong(hwnd, -20) & 0x20) != 0,
                "Recovery ignored the topmost/click-through preferences.");

            controller.RecoverAfterEnvironmentChange();
            controller.RecoverAfterEnvironmentChange();
            foreground = GetForegroundWindow();
            Pump();
            window = controller.CurrentWindow!;
            Check(configured == 2 && Handle(window) != hwnd && Visible(window),
                "Repeated desktop notifications did not coalesce into a replacement window.");
            Check(GetForegroundWindow() == foreground, "Desktop recovery took keyboard focus.");
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

            if (directory is not null && screen.Primary)
                AccountUiChecks.Render(window, 245, null, Path.Combine(directory, $"widget-recovery-{language}-{theme}.png"));
            controller.RecoverAfterEnvironmentChange();
            controller.Dispose();
            Pump();
            controller.MaintainVisibility();
            Check(controller.CurrentWindow is null && configured == 3, "Recovery recreated a widget after app shutdown.");
        }
    }

    private static IntPtr Handle(FloatingWidget window) => new WindowInteropHelper(window).Handle;
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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
