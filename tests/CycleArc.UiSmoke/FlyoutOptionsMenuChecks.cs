using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class FlyoutOptionsMenuChecks
{
    internal static void Run(string? directory = null, bool baseline = false)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        Console.WriteLine($"Host high contrast={SystemParameters.HighContrast}; opened Popup HWND DPI will be reported separately from simulated layout DPI.");
        var count = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ApplyTheme(language, theme);
            var flyout = CreateFlyout();
            try
            {
                flyout.Show();
                Pump();
                var menu = Open(flyout);
                var hostSource = (HwndSource)PresentationSource.FromVisual(menu)!;
                var hostDpi = VisualTreeHelper.GetDpi(menu);
                Console.WriteLine($"Host Popup HWND=0x{hostSource.Handle.ToInt64():X}; native DPI={GetDpiForWindow(hostSource.Handle)}; WPF DPI={hostDpi.DpiScaleX * 100:0}/{hostDpi.DpiScaleY * 100:0}%; {language}/{theme}.");
                var item = (MenuItem)flyout.FindName("PinMenuItem");
                item.Focus();
                Pump();
                if (directory is not null)
                    Capture(menu, Path.Combine(directory, $"menu-{language}-{theme}-checked-highlight.png"));
                WriteDiagnostics(menu, item, directory, $"menu-{language}-{theme}");
                menu.IsOpen = false;
                Pump();
                if (!baseline) count += CheckStates(flyout, language, theme, directory);
            }
            finally { flyout.Close(); }
        }
        Console.WriteLine(baseline
            ? "PASS: baseline production WindowOptionsMenu opened and captured separately from its Flyout HWND, synthetic accounts only."
            : $"PASS: {count} production window-options state/layout checks, EN/KO Dark/Light, pin/unpin, keyboard, reopen, disabled and high-contrast presentation; separate Popup captures, synthetic accounts only.");
    }

    private static int CheckStates(FlyoutWindow flyout, UiLanguage language, AppTheme theme, string? directory)
    {
        var pin = (MenuItem)flyout.FindName("PinMenuItem");
        var close = (MenuItem)flyout.FindName("CloseFlyoutMenuItem");
        var button = (Button)flyout.FindName("WindowOptionsButton");
        var changes = new List<bool>();
        flyout.PinChanged += changes.Add;
        try
        {
            var menu = Open(flyout);
            CheckTemplate(menu, pin, close);
            Require(pin.IsChecked && flyout.Pinned, "Checked menu did not reflect pinned state.");
            Require(pin.Focus(), "Pin item refused keyboard focus.");
            Pump();
            CheckHighlight(pin, highContrast: false);
            Require(pin.IsKeyboardFocused, "Pin keyboard focus was lost.");
            SendKeyEvent(pin, Key.Down);
            Require(close.IsKeyboardFocused && close.IsHighlighted, "Down did not navigate to Close across the separator.");
            CheckHighlight(close, highContrast: false);
            if (directory is not null) Capture(menu, Path.Combine(directory, $"menu-{language}-{theme}-close-highlight.png"));
            SendKeyEvent(close, Key.Up);
            Require(pin.IsKeyboardFocused, "Up did not return to Pin.");
            SendKeyEvent(pin, Key.Escape);
            Require(!menu.IsOpen && flyout.IsVisible && flyout.Pinned && changes.Count == 0,
                "Escape closed the Flyout or changed pin state.");
            Require(button.IsKeyboardFocused, "Dismissed options did not restore keyboard focus to their button.");

            menu = Open(flyout);
            pin.Focus(); Pump();
            SendKeyEvent(pin, Key.Enter);
            Require(!flyout.Pinned && !pin.IsChecked && changes.SequenceEqual([false]) && !menu.IsOpen,
                "Enter did not unpin exactly once and close options.");
            menu = Open(flyout);
            Require(!pin.IsChecked && !flyout.Topmost, "Reopening options changed unpinned state.");
            pin.Focus(); Pump();
            if (directory is not null) Capture(menu, Path.Combine(directory, $"menu-{language}-{theme}-unchecked-highlight.png"));
            SendKeyEvent(pin, Key.Enter);
            Require(flyout.Pinned && pin.IsChecked && changes.SequenceEqual([false, true]),
                "Enter did not pin exactly once.");

            menu = Open(flyout);
            pin.IsEnabled = false;
            Pump();
            Require(Same(pin.Foreground, Brush("DisabledBrush")), "Disabled menu item lost its theme foreground.");
            pin.IsEnabled = true;
            menu.Resources[SystemParameters.HighContrastKey] = true;
            Pump();
            var chrome = Part<Border>(menu, "OptionsMenuChrome");
            Require(Same(chrome.Background, SystemColors.MenuBrush), "High-contrast menu did not use the system menu background.");
            pin.Focus(); Pump();
            CheckHighlight(pin, highContrast: true);
            if (directory is not null) Capture(menu, Path.Combine(directory, $"menu-{language}-{theme}-high-contrast.png"));
            menu.Resources.Remove(SystemParameters.HighContrastKey);
            menu.IsOpen = false; Pump();

            ApplyTheme(language, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            menu = Open(flyout);
            CheckTemplate(menu, pin, close);
            Require(Same(Part<Border>(menu, "OptionsMenuChrome").Background, Brush("CardBrush")),
                "Reopened Popup retained the previous theme background.");
            menu.IsOpen = false; Pump();
            ApplyTheme(language, theme);
            var count = CheckDpiAndZoom(flyout, directory, $"{language}-{theme}");
            menu = Open(flyout);
            close.Focus(); Pump();
            SendKeyEvent(close, Key.Enter);
            Require(!flyout.IsVisible && !menu.IsOpen && changes.SequenceEqual([false, true]),
                "Close did not hide only the Flyout, or changed pin state.");
            flyout.Show(); Pump();
            menu = Open(flyout);
            Require(flyout.Pinned && pin.IsChecked, "Close/reopen lost pinned state.");
            menu.IsOpen = false; Pump();
            return count + 12;
        }
        finally { flyout.PinChanged -= changes.Add; }
    }

    private static int CheckDpiAndZoom(FlyoutWindow flyout, string? directory, string suffix)
    {
        var count = 0;
        foreach (var zoom in new[] { FlyoutZoom.MinPercent, 100, FlyoutZoom.MaxPercent })
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = true, FlyoutZoomPercent = zoom });
            var menu = Open(flyout);
            var pin = (MenuItem)flyout.FindName("PinMenuItem");
            var context = $"{suffix}/zoom={zoom}/simulated DPI={scale * 100:0}";
            var popupRoot = ((HwndSource)PresentationSource.FromVisual(menu)!).RootVisual;
            VisualTreeHelper.SetRootDpi(popupRoot, new DpiScale(scale, scale));
            menu.InvalidateMeasure(); menu.UpdateLayout();
            pin.Focus(); Pump();
            Require(Math.Abs(pin.FontSize - 13) < 0.001, "App zoom/DPI multiplied menu font size: " + context);
            Require(Math.Abs(VisualTreeHelper.GetDpi(pin).DpiScaleX - scale) < 0.001,
                "Popup child did not receive the independently simulated DPI: " + context);
            var check = Part<System.Windows.Shapes.Path>(pin, "OptionsCheck");
            Require(check.ActualWidth is > 0 and <= 16 && check.ActualHeight is > 0 and <= 16,
                "Vector check was enlarged/cropped: " + context);
            Require(pin.ActualHeight >= 32 && Part<Border>(pin, "OptionsItemChrome").ActualWidth > 0,
                "Menu row did not retain its theme layout: " + context);
            CheckHighlight(pin, highContrast: false);
            if (directory is not null) Capture(menu, Path.Combine(directory, $"menu-{suffix}-zoom{zoom}-dpi{scale * 100:0}.png"));
            menu.IsOpen = false; Pump();
            count++;
        }
        flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = true, FlyoutZoomPercent = 100 });
        Console.WriteLine($"PASS: {suffix} independent Popup visual-layout DPI 100/125/150/200% × app zoom 80/100/150%; simulated DPI, not additional physical monitors.");
        return count;
    }

    private static void CheckTemplate(ContextMenu menu, MenuItem pin, MenuItem close)
    {
        Require(menu.Style == menu.TryFindResource("FlyoutOptionsContextMenu"), "Options menu did not receive its scoped style.");
        Require(pin.Style == pin.TryFindResource("FlyoutOptionsMenuItem") && close.Style == pin.Style,
            "Options items did not receive their scoped style.");
        var chrome = Part<Border>(menu, "OptionsMenuChrome");
        Require(Same(chrome.Background, Brush("CardBrush")) && Same(chrome.BorderBrush, Brush("LineBrush")),
            "Opened Popup did not resolve theme surface/border resources.");
        foreach (var item in new[] { pin, close })
        {
            Part<Border>(item, "OptionsItemChrome");
            var check = Part<System.Windows.Shapes.Path>(item, "OptionsCheck");
            Require(check.Data is not null && check.StrokeThickness > 0, "Check must be a stroked vector.");
        }
        var separator = menu.Items.OfType<Separator>().Single();
        Require(Same(Part<Border>(separator, "OptionsSeparatorLine").Background, Brush("LineBrush")),
            "Separator did not resolve the theme's thin border brush.");
    }

    private static void CheckHighlight(MenuItem item, bool highContrast)
    {
        Require(item.IsHighlighted, "Focused menu item did not enter standard WPF highlighted state.");
        Require(Same(Part<Border>(item, "OptionsItemChrome").Background,
            highContrast ? SystemColors.HighlightBrush : Brush("GhostBrush")), "Menu highlight retained a system/default background.");
        Require(Same(item.Foreground, highContrast ? SystemColors.HighlightTextBrush : Brush("TextBrush")),
            "Menu highlight text lost its theme/high-contrast foreground.");
        if (item.IsKeyboardFocused)
        {
            var chrome = Part<Border>(item, "OptionsItemChrome");
            Require(chrome.BorderThickness.Left > 0 && Same(chrome.BorderBrush,
                highContrast ? SystemColors.HighlightTextBrush : Brush("AccentBrush")),
                "Keyboard focus must retain one visible themed border.");
        }
        if (item.IsChecked)
            Require(Part<System.Windows.Shapes.Path>(item, "OptionsCheck").Visibility == Visibility.Visible
                && Same(Part<System.Windows.Shapes.Path>(item, "OptionsCheck").Stroke,
                    highContrast ? SystemColors.HighlightTextBrush : Brush("TextBrush")),
                "Checked+highlighted vector was missing or used the wrong brush.");
    }

    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        control.Template.FindName(name, control) as T
        ?? throw new InvalidOperationException($"Opened {control.GetType().Name} template lacks {name}.");

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static bool Same(Brush? actual, Brush expected) => actual is SolidColorBrush a && expected is SolidColorBrush b && a.Color == b.Color;

    private static void SendKeyEvent(UIElement target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
    }

    private static void WriteDiagnostics(ContextMenu menu, MenuItem item, string? directory, string stem)
    {
        if (directory is null) return;
        var lines = new List<string>
        {
            $"Popup source={PresentationSource.FromVisual(menu)?.GetType().Name}; menu font={menu.FontSize}; item font={item.FontSize}; checked={item.IsChecked}; highlighted={item.IsHighlighted}",
            $"Menu template source={DependencyPropertyHelper.GetValueSource(menu, Control.TemplateProperty).BaseValueSource}; item template source={DependencyPropertyHelper.GetValueSource(item, Control.TemplateProperty).BaseValueSource}"
        };
        foreach (var border in AccountUiChecks.Descendants<Border>(menu))
            lines.Add($"Border name={border.Name} background={border.Background} border={border.BorderBrush} thickness={border.BorderThickness}");
        foreach (var path in AccountUiChecks.Descendants<System.Windows.Shapes.Path>(menu))
            lines.Add($"Path name={path.Name} fill={path.Fill} stroke={path.Stroke} visibility={path.Visibility}");
        File.WriteAllLines(Path.Combine(directory, stem + "-diagnostics.txt"), lines);
    }

    private static FlyoutWindow CreateFlyout()
    {
        var now = new DateTimeOffset(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now,
            null, null, 0, [new("five", 38, 300, now.AddHours(2), CodexWindowKind.FiveHour)], null);
        var account = WidgetFixture.Synthetic("menu-fixture", "Synthetic account", snapshot);
        var flyout = new FlyoutWindow { ShowActivated = false, Left = 100, Top = 60 };
        flyout.BindAccounts([account], account.Profile.Id, false);
        flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = true });
        return flyout;
    }

    private static ContextMenu Open(FlyoutWindow flyout)
    {
        flyout.Activate();
        var button = (Button)flyout.FindName("WindowOptionsButton");
        button.Focus();
        Pump();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var menu = (ContextMenu)flyout.FindName("WindowOptionsMenu");
        Require(menu.IsOpen && menu.ActualWidth > 0 && menu.ActualHeight > 0, "The actual window options Popup did not open/layout.");
        Require(PresentationSource.FromVisual(menu) is HwndSource menuSource
            && menuSource.Handle != new WindowInteropHelper(flyout).Handle,
            "WindowOptionsMenu must have its own Popup HWND.");
        Require(flyout.IsVisible, "Opening window options hid the Flyout.");
        return menu;
    }

    private static void Capture(ContextMenu menu, string path)
    {
        menu.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(menu);
        var size = menu.RenderSize;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void ApplyTheme(UiLanguage language, AppTheme theme)
    {
        UiText.SetLanguage(language);
        typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [theme]);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Explicit local desktop check: real input is never sent by the default gate.
    internal static void RunNative(string directory)
    {
        Directory.CreateDirectory(directory);
        Require(GetCursorPos(out var original), "Could not save the pointer position.");
        var lines = new List<string> { $"Host high contrast={SystemParameters.HighContrast}; native input is opt-in; synthetic account only." };
        try
        {
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                ApplyTheme(language, theme);
                var flyout = CreateFlyout();
                var changes = new List<bool>();
                flyout.PinChanged += changes.Add;
                try
                {
                    flyout.Show(); flyout.Activate(); Pump();
                    var hwnd = new WindowInteropHelper(flyout).Handle;
                    var work = screen.WorkingArea;
                    // Move across real monitor/DPI boundaries, then put the options button
                    // beside the work area's right edge so native Popup correction is exercised.
                    Require(SetWindowPos(hwnd, IntPtr.Zero, work.Left + 60, work.Top + 60, 0, 0, 0x0015), "Could not place synthetic Flyout on monitor.");
                    Pump();
                    Require(GetWindowRect(hwnd, out var windowRect), "Could not read synthetic Flyout bounds.");
                    Require(SetWindowPos(hwnd, IntPtr.Zero, work.Right - (windowRect.Right - windowRect.Left) - 2,
                        work.Top + 40, 0, 0, 0x0015), "Could not place synthetic Flyout by right work-area edge.");
                    Pump();
                    var name = $"{language}-{theme}-{screen.DeviceName.Replace("\\", "").Replace(".", "")}";
                    var button = (Button)flyout.FindName("WindowOptionsButton");
                    var pin = (MenuItem)flyout.FindName("PinMenuItem");
                    var close = (MenuItem)flyout.FindName("CloseFlyoutMenuItem");
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = true, FlyoutZoomPercent = zoom });
                        Pump();
                        NativeClick(button, hwnd);
                        var menu = (ContextMenu)flyout.FindName("WindowOptionsMenu");
                        Require(menu.IsOpen && flyout.IsVisible, "Real options-button click did not retain/open the Flyout menu.");
                        var menuHwnd = ((HwndSource)PresentationSource.FromVisual(menu)!).Handle;
                        Require(GetWindowRect(menuHwnd, out var popup), "Could not read actual Popup bounds.");
                        Require(popup.Left >= work.Left && popup.Top >= work.Top && popup.Right <= work.Right && popup.Bottom <= work.Bottom,
                            $"Actual menu was clipped outside {screen.DeviceName} work area at {zoom}% zoom.");
                        NativeHover(pin, menuHwnd);
                        Require(pin.IsMouseOver && pin.IsChecked && pin.IsHighlighted, "Real pointer did not produce checked+hover state.");
                        CheckHighlight(pin, highContrast: SystemParameters.HighContrast);
                        Capture(menu, Path.Combine(directory, $"native-{name}-zoom{zoom}-checked-hover.png"));
                        NativeHover(close, menuHwnd);
                        Require(close.IsMouseOver && close.IsHighlighted, "Real pointer did not highlight Close.");
                        Capture(menu, Path.Combine(directory, $"native-{name}-zoom{zoom}-close-hover.png"));
                        NativeKey(flyout, menuHwnd, 0x26); // Up selects Pin.
                        Require(pin.IsKeyboardFocused && pin.IsHighlighted, "Real Up key did not select Pin.");
                        Capture(menu, Path.Combine(directory, $"native-{name}-zoom{zoom}-keyboard.png"));
                        NativeKey(flyout, menuHwnd, 0x1B); // Escape closes only options.
                        Require(!menu.IsOpen && flyout.IsVisible && flyout.Pinned, "Real Escape changed Flyout visibility or pin state.");
                        NativeClick(button, hwnd);
                        menuHwnd = ((HwndSource)PresentationSource.FromVisual(menu)!).Handle;
                        NativeClick(pin, menuHwnd);
                        Require(!flyout.Pinned && !pin.IsChecked && !menu.IsOpen, "Real Pin click did not unpin and dismiss.");
                        Require(changes.Count == (zoom switch { 80 => 1, 100 => 2, _ => 3 }), "A real Pin click emitted duplicate actions.");
                        NativeClick(button, hwnd);
                        menuHwnd = ((HwndSource)PresentationSource.FromVisual(menu)!).Handle;
                        // Click the synthetic body's own interior, outside the Popup.
                        var outside = flyout.PointToScreen(new Point(flyout.ActualWidth / 2, Math.Min(flyout.ActualHeight - 10, 100)));
                        NativeClickPoint(outside, hwnd);
                        Require(!menu.IsOpen && flyout.IsVisible && !flyout.Pinned, "Outside click changed Flyout/pin state or left options open.");
                        var dpi = VisualTreeHelper.GetDpi(flyout);
                        lines.Add($"PASS {name}: OS DPI={dpi.DpiScaleX * 100:0}/{dpi.DpiScaleY * 100:0}%, app zoom={zoom}%, Popup physical={popup.Left},{popup.Top},{popup.Right},{popup.Bottom}; check-hover, Close-hover, native Up/Escape, one Pin action, outside dismissal; work={work}.");
                    }
                    NativeClick(button, hwnd);
                    var lastMenu = (ContextMenu)flyout.FindName("WindowOptionsMenu");
                    NativeClick(close, ((HwndSource)PresentationSource.FromVisual(lastMenu)!).Handle);
                    Require(!flyout.IsVisible && !lastMenu.IsOpen, "Real Close click did not hide only the synthetic Flyout.");
                }
                finally { flyout.PinChanged -= changes.Add; flyout.Close(); }
            }
        }
        finally { SetCursorPos(original.X, original.Y); }
        File.WriteAllLines(Path.Combine(directory, "native-menu-results.txt"), lines);
        foreach (var line in lines) Console.WriteLine(line);
    }

    private static void NativeHover(FrameworkElement element, IntPtr expectedHwnd)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        Require(WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) == expectedHwnd,
            "Native menu input target is obscured or belongs to another HWND.");
        Require(SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "Could not move the pointer to synthetic menu.");
        AccountUiChecks.PumpUntil(Task.Delay(120)); Pump();
    }

    private static void NativeClick(FrameworkElement element, IntPtr expectedHwnd) =>
        NativeClickPoint(element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)), expectedHwnd);

    private static void NativeClickPoint(Point point, IntPtr expectedHwnd)
    {
        Require(WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) == expectedHwnd,
            "Native click target is obscured or belongs to another HWND.");
        Require(SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "Could not move the pointer to synthetic click target.");
        Send(new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0002 } } });
        try { AccountUiChecks.PumpUntil(Task.Delay(40)); }
        finally { Send(new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0004 } } }); }
        AccountUiChecks.PumpUntil(Task.Delay(120)); Pump();
    }

    private static void NativeKey(FlyoutWindow flyout, IntPtr popupHwnd, ushort key)
    {
        var foreground = GetForegroundWindow();
        Require(foreground == new WindowInteropHelper(flyout).Handle || foreground == popupHwnd,
            "Refusing keyboard input because the foreground HWND is not the synthetic Flyout/menu.");
        Send(new NativeInput { Type = 1, Data = new NativeInputData { Keyboard = new NativeKeyboardInput { VirtualKey = key } } });
        Send(new NativeInput { Type = 1, Data = new NativeInputData { Keyboard = new NativeKeyboardInput { VirtualKey = key, Flags = 2 } } });
        AccountUiChecks.PumpUntil(Task.Delay(120)); Pump();
    }

    private static void Send(NativeInput input) => Require(SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1,
        "Interactive desktop rejected synthetic input.");
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputData
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
