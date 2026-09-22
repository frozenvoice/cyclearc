using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

// Explicit local hardware check. Uses only synthetic profiles and production windows;
// never starts App startup, authenticates, installs, or reads the user's settings.
internal static class NativeEdgeSnapChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        UiText.SetLanguage(UiLanguage.English);
        typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [AppTheme.Dark]);
        var now = new DateTimeOffset(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now,
            null, null, 0, [new("five", 38, 300, now.AddHours(2), CodexWindowKind.FiveHour)], null);
        var account = WidgetFixture.Synthetic("edge-native", "Synthetic account", snapshot);
        var lines = new List<string>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var work = screen.WorkingArea;
            var screenName = screen.DeviceName.Replace("\\", "").Replace(".", "");
            foreach (var kind in new[] { "widget", "flyout" })
            {
                Window window;
                if (kind == "widget")
                {
                    var widget = new FloatingWidget();
                    widget.BindAccounts([account], account.Profile.Id);
                    widget.Apply(new AppSettings { WidgetPixelLeft = work.Left + 80, WidgetPixelTop = work.Top + 80 });
                    window = widget;
                }
                else
                {
                    var flyout = new FlyoutWindow();
                    flyout.BindAccounts([account], account.Profile.Id, false);
                    flyout.ApplyWindowSettings(new AppSettings());
                    window = flyout;
                }
                try
                {
                    window.Show();
                    Pump(window);
                    SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                        work.Left + 80, work.Top + 80, 0, 0, 0x0015); // size/z-order/activation unchanged
                    Pump(window);
                    if (!screen.Primary && System.Windows.Forms.Screen.PrimaryScreen is { } primary)
                    {
                        // Cross the real DPI boundary with the pointer while detached, then
                        // verify release is on the destination display (no boundary magnet).
                        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                            primary.WorkingArea.Left + 80, primary.WorkingArea.Top + 80, 0, 0, 0x0015);
                        Pump(window);
                        var crossingBounds = Bounds(window);
                        var crossingStart = HeaderPoint(window);
                        EdgeSnapNativeInput.Drag(window, crossingStart,
                            new Point(work.Left + 160 + crossingStart.X - crossingBounds.Left,
                                work.Top + 120 + crossingStart.Y - crossingBounds.Top), shift: true);
                        Check(System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).DeviceName == screen.DeviceName,
                            kind + " was trapped at the monitor boundary.");
                        Check(!Anchors(window).IsAttached, kind + " cross-monitor Shift drag attached unexpectedly.");
                    }
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var initial = Bounds(window);
                    var start = HeaderPoint(window);
                    var targetLeft = work.Right - initial.Width - (2 + 6) * dpi.DpiScaleX;
                    var targetTop = work.Bottom - initial.Height - (2 + 6) * dpi.DpiScaleY;
                    Console.WriteLine($"Native drop {kind}: initial={initial.Left},{initial.Top},{initial.Width},{initial.Height}; pointer={start}; target={targetLeft},{targetTop}; DPI={dpi.DpiScaleX}/{dpi.DpiScaleY}");
                    EdgeSnapNativeInput.Drag(window, start,
                        new Point(start.X + targetLeft - initial.Left, start.Y + targetTop - initial.Top));
                    AssertAttached(window, work, "native release");
                    Capture(window, work, Path.Combine(directory, $"{kind}-{screenName}-100.png"));

                    // A native click below the drag threshold must retain the attachment.
                    EdgeSnapNativeInput.Click(window, HeaderPoint(window));
                    AssertAttached(window, work, "header click");
                    for (var i = 0; i < 5; i++)
                        ClickButton(window, kind == "widget" ? "WidgetZoomInButton" : "FlyoutZoomInButton");
                    Check(Zoom(window) == 150, kind + " native + clicks did not reach 150%.");
                    AssertAttached(window, work, "150 percent");
                    Capture(window, work, Path.Combine(directory, $"{kind}-{screenName}-150.png"));
                    for (var i = 0; i < 7; i++)
                        ClickButton(window, kind == "widget" ? "WidgetZoomOutButton" : "FlyoutZoomOutButton");
                    Check(Zoom(window) == 80, kind + " native - clicks did not reach 80%.");
                    AssertAttached(window, work, "80 percent");
                    Capture(window, work, Path.Combine(directory, $"{kind}-{screenName}-80.png"));

                    CheckPassiveUpdate(window, account, work);
                    CheckRecreation(window, account, work);
                    dpi = VisualTreeHelper.GetDpi(window);
                    // Release near the inset with Shift: still safe, but detached on both axes.
                    var before = Bounds(window);
                    start = HeaderPoint(window);
                    EdgeSnapNativeInput.Drag(window, start, new Point(start.X - 6 * dpi.DpiScaleX, start.Y - 6 * dpi.DpiScaleY), shift: true);
                    Check(!Anchors(window).IsAttached, kind + " Shift drop kept an attachment.");
                    Check(Bounds(window).Left < before.Left, kind + " native Shift drag did not move.");
                    if (window is FlyoutWindow popup)
                        Check(!popup.Pinned && !popup.Topmost, "Snapping changed flyout pin/topmost state.");
                    lines.Add($"PASS {kind} {screen.DeviceName}: physical work={work}, OS DPI={dpi.DpiScaleX * 100:0}/{dpi.DpiScaleY * 100:0}%; native drag/release, header click, 100/150/80% header-button zoom, passive size/content update without focus theft, legacy 8-DIP pixel/anchor recreation at 2 DIP, Shift detach. No monitor geometry was injected.");
                }
                finally { window.Close(); }
            }
        }
        File.WriteAllLines(Path.Combine(directory, "native-monitors.txt"), lines);
        foreach (var line in lines) Console.WriteLine(line);
    }

    private static void CheckRecreation(Window original, CodexAccountView account, System.Drawing.Rectangle work)
    {
        var bounds = Bounds(original);
        var updated = account with { Snapshot = account.Snapshot with
        {
            Status = CodexQuotaStatus.Stale, TechnicalDetail = "Synthetic refresh failure for height verification."
        } };
        var anchors = Anchors(original);
        // Recreate an already attached window whose saved coordinates still have the old
        // 8-DIP inset. The current policy must place it 6 DIP closer, on the same monitor.
        var dpi = VisualTreeHelper.GetDpi(original);
        var oldPixelLeft = bounds.Left - (int)Math.Round(6 * dpi.DpiScaleX);
        var oldPixelTop = bounds.Top - (int)Math.Round(6 * dpi.DpiScaleY);
        var settings = new AppSettings
        {
            WidgetLeft = original.Left - 6, WidgetTop = original.Top - 6,
            WidgetPixelLeft = oldPixelLeft, WidgetPixelTop = oldPixelTop,
            WidgetHorizontalAnchor = anchors.Horizontal, WidgetVerticalAnchor = anchors.Vertical, WidgetZoomPercent = Zoom(original),
            FlyoutLeft = original.Left - 6, FlyoutTop = original.Top - 6,
            FlyoutPixelLeft = oldPixelLeft, FlyoutPixelTop = oldPixelTop,
            FlyoutHorizontalAnchor = anchors.Horizontal, FlyoutVerticalAnchor = anchors.Vertical, FlyoutZoomPercent = Zoom(original),
            FlyoutPositionConfigured = true
        };
        Window recreated;
        if (original is FloatingWidget)
        {
            var widget = new FloatingWidget { ShowActivated = false };
            widget.SetZoom(settings.WidgetZoomPercent, notify: false);
            widget.BindAccounts([updated], account.Profile.Id);
            widget.Apply(settings);
            recreated = widget;
        }
        else
        {
            var flyout = new FlyoutWindow { ShowActivated = false };
            flyout.ApplyWindowSettings(settings);
            flyout.BindAccounts([updated], account.Profile.Id, false);
            recreated = flyout;
        }
        try
        {
            recreated.Show();
            if (recreated is FlyoutWindow flyout) flyout.RestorePosition(settings.FlyoutLeft, settings.FlyoutTop);
            Pump(recreated);
            AssertAttached(recreated, work, "native monitor recreation");
            var after = Bounds(recreated);
            Check(Math.Abs(after.Left - bounds.Left) <= 2 && Math.Abs(after.Top - bounds.Top) <= 2,
                "Recreated window lost its physical monitor position.");
        }
        finally { recreated.Close(); }
    }

    private static void CheckPassiveUpdate(Window window, CodexAccountView account, System.Drawing.Rectangle work)
    {
        var guard = new Window
        {
            Title = "Synthetic focus guard", Width = 240, Height = 100,
            ShowInTaskbar = false, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        try
        {
            guard.Show();
            guard.Activate();
            Pump(guard);
            var guardHandle = new WindowInteropHelper(guard).Handle;
            Check(GetForegroundWindow() == guardHandle, "Cannot establish synthetic foreground guard.");
            var changedAccount = account with { Snapshot = account.Snapshot with
            {
                Status = CodexQuotaStatus.Stale, TechnicalDetail = "Synthetic refresh failure for height verification."
            } };
            if (window is FloatingWidget widget)
            {
                widget.SetZoom(100);
                widget.BindAccounts([changedAccount], account.Profile.Id);
            }
            else
            {
                var flyout = (FlyoutWindow)window;
                flyout.ApplyWindowSettings(new AppSettings
                {
                    FlyoutZoomPercent = 100,
                    FlyoutHorizontalAnchor = flyout.EdgeAnchors.Horizontal,
                    FlyoutVerticalAnchor = flyout.EdgeAnchors.Vertical
                });
                flyout.BindAccounts([changedAccount], account.Profile.Id, false);
            }
            Pump(window);
            Check(GetForegroundWindow() == guardHandle, "Passive edge placement stole foreground focus.");
            AssertAttached(window, work, "passive content/size update");
        }
        finally { guard.Close(); }
    }

    private static Point HeaderPoint(Window window)
    {
        var target = (FrameworkElement)window.FindName(window is FloatingWidget ? "WidgetHeader" : "TitleText");
        return target.PointToScreen(new Point(Math.Min(10, target.ActualWidth / 2), Math.Min(10, target.ActualHeight / 2)));
    }

    private static void ClickButton(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        EdgeSnapNativeInput.Click(window, button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2)));
    }

    private static int Zoom(Window window) => window is FloatingWidget widget ? widget.ZoomPercent : ((FlyoutWindow)window).ZoomPercent;

    private static WindowEdgeAnchors Anchors(Window window) => window is FloatingWidget widget ? widget.EdgeAnchors : ((FlyoutWindow)window).EdgeAnchors;

    private static void AssertAttached(Window window, System.Drawing.Rectangle work, string stage)
    {
        var anchors = Anchors(window);
        var bounds = Bounds(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        Check(anchors == new WindowEdgeAnchors(HorizontalEdgeAnchor.Right, VerticalEdgeAnchor.Bottom),
            $"{window.GetType().Name} {stage}: expected right/bottom, got {anchors}; bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, work={work}.");
        Check(Math.Abs(work.Right - bounds.Right - 2 * dpi.DpiScaleX) <= 2
            && Math.Abs(work.Bottom - bounds.Bottom - 2 * dpi.DpiScaleY) <= 2,
            $"{window.GetType().Name} {stage}: wrong native inset; right={work.Right - bounds.Right}, bottom={work.Bottom - bounds.Bottom}, DPI={dpi.DpiScaleX}/{dpi.DpiScaleY}.");
    }

    internal static void Capture(Window window, System.Drawing.Rectangle work, string path)
    {
        // PrintWindow captures only this synthetic HWND. The neutral work-area surround
        // records its measured native position without capturing any other application.
        var bounds = Bounds(window);
        using var capture = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(capture))
        {
            var dc = graphics.GetHdc();
            try { Check(PrintWindow(new WindowInteropHelper(window).Handle, dc, 2), "PrintWindow could not capture the synthetic window."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        using var canvas = new System.Drawing.Bitmap(work.Width, work.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(canvas))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(44, 48, 54));
            graphics.DrawImageUnscaled(capture, bounds.Left - work.Left, bounds.Top - work.Top);
        }
        canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        // Record actual HWND geometry so before/after captures can verify that only
        // the outside position changed, with equal window sizes at each app zoom.
        var dpi = VisualTreeHelper.GetDpi(window);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WorkLeft = work.Left, WorkTop = work.Top, WorkRight = work.Right, WorkBottom = work.Bottom,
            dpi.DpiScaleX, dpi.DpiScaleY, Zoom = Zoom(window)
        }));
    }

    private static NativeRect Bounds(Window window)
    {
        Check(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "GetWindowRect failed.");
        return rect;
    }
    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
