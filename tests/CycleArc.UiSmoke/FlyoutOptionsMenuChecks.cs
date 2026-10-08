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
            var fixtureMenu = (ContextMenu)flyout.FindName("WindowOptionsMenu");
            try
            {
                flyout.Show();
                Pump();
                var menu = Open(flyout);
                if (!baseline) SetMenuHighContrastMode(menu, false);
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
            finally
            {
                fixtureMenu.Resources.Remove(SystemParameters.HighContrastKey);
                flyout.Close();
            }
        }
        Console.WriteLine(baseline
            ? "PASS: baseline production WindowOptionsMenu opened and captured separately from its Flyout HWND, synthetic accounts only."
            : $"PASS: {count} production window-options state/layout checks, EN/KO Dark/Light, pin/unpin, keyboard, reopen, disabled and independently simulated normal/high-contrast presentation; separate Popup captures, synthetic accounts only.");
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
            SetMenuHighContrastMode(menu, false);
            CheckTemplate(menu, pin, close, highContrast: false);
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
            var presentationCases = CheckPresentationModes(menu, pin, close, directory, $"{language}-{theme}");
            menu.IsOpen = false; Pump();

            ApplyTheme(language, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            menu = Open(flyout);
            CheckTemplate(menu, pin, close, highContrast: false);
            Require(Same(Part<Border>(menu, "OptionsMenuChrome").Background, Brush("CardBrush")),
                "Reopened Popup retained the previous theme background.");
            pin.Focus(); Pump();
            CheckHighlight(pin, highContrast: false);
            SetMenuHighContrastMode(menu, true);
            CheckTemplate(menu, pin, close, highContrast: true);
            CheckHighlight(pin, highContrast: true);
            SetMenuHighContrastMode(menu, false);
            CheckTemplate(menu, pin, close, highContrast: false);
            CheckHighlight(pin, highContrast: false);
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
            return count + 12 + presentationCases;
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
            SetMenuHighContrastMode(menu, false);
            var pin = (MenuItem)flyout.FindName("PinMenuItem");
            var context = $"{suffix}/zoom={zoom}/simulated DPI={scale * 100:0}";
            var popupRoot = ((HwndSource)PresentationSource.FromVisual(menu)!).RootVisual;
            VisualTreeHelper.SetRootDpi(popupRoot, new DpiScale(scale, scale));
            menu.InvalidateMeasure(); menu.UpdateLayout();
            Require(pin.Focus(), "Pin item refused DPI-case keyboard focus: " + context);
            Pump();
            Require(pin.IsKeyboardFocused && pin.IsHighlighted,
                $"DPI-case selection changed: {context}; focused={pin.IsKeyboardFocused}, highlighted={pin.IsHighlighted}, menu open={menu.IsOpen}, foreground=0x{GetForegroundWindow().ToInt64():X}, flyout=0x{new WindowInteropHelper(flyout).Handle.ToInt64():X}, popup=0x{((HwndSource)PresentationSource.FromVisual(menu)!).Handle.ToInt64():X}.");
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

    private static void SetMenuHighContrastMode(ContextMenu menu, bool enabled)
    {
        // Only this synthetic Popup owns the override; Windows and Application resources stay untouched.
        menu.Resources[SystemParameters.HighContrastKey] = enabled;
        Pump();
        Require(menu.Tag is bool effective && effective == enabled,
            "The opened Popup did not resolve its fixture-local presentation mode.");
    }

    private static int CheckPresentationModes(ContextMenu menu, MenuItem pin, MenuItem close, string? directory, string suffix)
    {
        // A merged dictionary supplies a fallback at the resource-resolution boundary.
        // It stands in for either host value without changing the real OS/session.
        var fallback = new ResourceDictionary();
        menu.Resources.MergedDictionaries.Add(fallback);
        try
        {
            foreach (var fallbackHighContrast in new[] { true, false })
            {
                fallback[SystemParameters.HighContrastKey] = fallbackHighContrast;
                menu.Resources.Remove(SystemParameters.HighContrastKey);
                Pump();
                Require(menu.Tag is bool effective && effective == fallbackHighContrast,
                    "Fixture fallback did not exercise the Popup's HighContrastKey resolution.");

                CheckPresentationState(menu, pin, close, highContrast: false);
                CheckPresentationState(menu, pin, close, highContrast: true);
                if (directory is not null)
                    Capture(menu, Path.Combine(directory, $"menu-{suffix}-high-contrast.png"));
                CheckPresentationState(menu, pin, close, highContrast: false);
            }
        }
        finally
        {
            menu.Resources.MergedDictionaries.Remove(fallback);
            SetMenuHighContrastMode(menu, false);
        }
        Console.WriteLine($"PASS: {suffix} fixture fallback HC=true/false → local normal/HC/normal; exact surface, separator, checked/disabled, highlight and keyboard-focus brushes.");
        return 6;
    }

    private static void CheckPresentationState(ContextMenu menu, MenuItem pin, MenuItem close, bool highContrast)
    {
        SetMenuHighContrastMode(menu, highContrast);
        CheckTemplate(menu, pin, close, highContrast);
        Require(pin.IsChecked && pin.Focus(), "Presentation transition lost the checked state or Pin focus.");
        Pump();
        Require(pin.IsKeyboardFocused, "Presentation transition lost keyboard focus.");
        CheckHighlight(pin, highContrast);
        close.Focus(); Pump();
        CheckHighlight(close, highContrast);
        pin.IsEnabled = false;
        try
        {
            Pump();
            var foreground = highContrast ? SystemColors.GrayTextBrush : Brush("DisabledBrush");
            var check = Part<System.Windows.Shapes.Path>(pin, "OptionsCheck");
            var chrome = Part<Border>(pin, "OptionsItemChrome");
            Require(Same(pin.Foreground, foreground) && Same(check.Stroke, foreground)
                && check.Visibility == Visibility.Visible && pin.IsChecked,
                "Disabled checked item lost its normal/high-contrast foreground or vector state.");
            Require(Same(chrome.Background, Brushes.Transparent) && Same(chrome.BorderBrush, Brushes.Transparent),
                "Disabled item retained a highlight/focus border.");
        }
        finally { pin.IsEnabled = true; }
        pin.Focus(); Pump();
        CheckHighlight(pin, highContrast);
    }

    private static void CheckTemplate(ContextMenu menu, MenuItem pin, MenuItem close, bool highContrast)
    {
        Require(menu.Style == menu.TryFindResource("FlyoutOptionsContextMenu"), "Options menu did not receive its scoped style.");
        Require(pin.Style == pin.TryFindResource("FlyoutOptionsMenuItem") && close.Style == pin.Style,
            "Options items did not receive their scoped style.");
        var chrome = Part<Border>(menu, "OptionsMenuChrome");
        Require(menu.Tag is bool effective && effective == highContrast,
            "Opened Popup presentation mode disagrees with the explicit expectation.");
        Require(Same(chrome.Background, highContrast ? SystemColors.MenuBrush : Brush("CardBrush"))
            && Same(chrome.BorderBrush, highContrast ? SystemColors.MenuTextBrush : Brush("LineBrush"))
            && Same(menu.Foreground, highContrast ? SystemColors.MenuTextBrush : Brush("TextBrush")),
            "Opened Popup did not resolve its normal/high-contrast surface, text and border resources.");
        foreach (var item in new[] { pin, close })
        {
            Part<Border>(item, "OptionsItemChrome");
            var check = Part<System.Windows.Shapes.Path>(item, "OptionsCheck");
            Require(check.Data is not null && check.StrokeThickness > 0, "Check must be a stroked vector.");
            Require(check.Visibility == (item.IsChecked ? Visibility.Visible : Visibility.Hidden),
                "Presentation transition changed checked/unchecked vector visibility.");
        }
        var separator = menu.Items.OfType<Separator>().Single();
        var separatorLine = Part<Border>(separator, "OptionsSeparatorLine");
        Require(separatorLine.IsVisible && separatorLine.ActualHeight > 0
            && Same(separatorLine.Background, highContrast ? SystemColors.MenuTextBrush : Brush("LineBrush")),
            "Separator did not remain visible with its normal/high-contrast brush.");
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

    // Reuse the existing HWND-checked mouse helper. This entry point is opt-in only;
    // it never participates in Run() or the argument-free suite.
    internal static void RunHeaderNative(string directory)
    {
        Directory.CreateDirectory(directory);
        Require(GetCursorPos(out var originalPointer), "Could not save the pointer position.");
        var originalForeground = GetForegroundWindow();
        var previousLanguage = UiText.Language;
        var lines = new List<string> { "Opt-in native direct-Close checks; synthetic production windows only; restoration results recorded below." };
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            foreach (var zoom in new[] { 100, 150 })
            foreach (var pinned in new[] { false, true })
            {
                ApplyTheme(language, theme);
                var flyout = new FlyoutWindow { ShowActivated = false, Left = 40, Top = 40 };
                var widget = new FloatingWidget { ShowActivated = false, Topmost = false, Left = 20, Top = 20 };
                try
                {
                    var accounts = FlyoutHeaderChecks.Accounts();
                    widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop);
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = pinned, FlyoutZoomPercent = zoom });
                    flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
                    widget.Show(); flyout.Show(); flyout.Activate(); Pump();
                    var hwnd = new WindowInteropHelper(flyout).Handle;
                    Require(SetWindowPos(hwnd, IntPtr.Zero, screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40, 0, 0, 0x0015),
                        "Could not place the synthetic popup on its real monitor.");
                    Pump(); flyout.Activate(); Pump();
                    var position = flyout.PixelPosition;
                    var anchors = flyout.EdgeAnchors;
                    var selected = flyout.SelectedProfileId;
                    var pinEvents = 0; var selectionEvents = 0; var refreshes = 0; var moves = 0;
                    flyout.PinChanged += _ => pinEvents++;
                    flyout.AccountSelected += _ => selectionEvents++;
                    flyout.SyncRequested += () => refreshes++;
                    flyout.PositionChanged += (_, _) => moves++;
                    flyout.SetRefreshPresentation(new(false, true, UiText.RefreshAllProgress)); Pump();
                    var close = (Button)flyout.FindName("CloseButton");
                    var closePoint = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2));
                    Require(GetForegroundWindow() == hwnd, "Refusing native click because the foreground is not the synthetic popup.");
                    var dpi = VisualTreeHelper.GetDpi(flyout);
                    NativeHeaderClick(close, hwnd);
                    Require(!flyout.IsVisible && widget.IsVisible && flyout.Pinned == pinned && flyout.SelectedProfileId == selected
                        && flyout.PixelPosition == position && flyout.EdgeAnchors == anchors && !flyout.RefreshIndicator.IsAnimating
                        && pinEvents == 0 && selectionEvents == 0 && refreshes == 0 && moves == 0,
                        "Native direct Close changed another surface, pin, selection, placement, refresh or drag state.");
                    flyout.Show(); Pump();
                    Require(flyout.SelectedProfileId == selected && flyout.Pinned == pinned && flyout.PixelPosition == position
                        && flyout.RefreshIndicator.IsAnimating, "Native close/reopen did not restore the ongoing refresh and popup state.");
                    flyout.SetRefreshPresentation(new(true, false, "")); Pump();
                    lines.Add($"PASS {language}/{theme}/{screen.DeviceName}: actual WPF DPI={dpi.DpiScaleX * 100:0}/{dpi.DpiScaleY * 100:0}%, app zoom={zoom}%, pinned={pinned}; click physical={closePoint.X:0},{closePoint.Y:0}; popup only hidden, widget survives, selection/pin/position/anchors retained, refresh stopped/resumed.");
                }
                finally { flyout.Close(); widget.Close(); }
            }
        }
        finally
        {
            UiText.SetLanguage(previousLanguage);
            typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [AppTheme.Dark]);
            var pointerRestored = SetCursorPos(originalPointer.X, originalPointer.Y)
                && GetCursorPos(out var restoredPointer) && restoredPointer.X == originalPointer.X && restoredPointer.Y == originalPointer.Y;
            lines.Add(pointerRestored ? "PASS: original pointer position restored and reread." : "FAIL: original pointer position could not be restored and verified.");
            var foregroundRestored = false;
            if (originalForeground != IntPtr.Zero && IsWindow(originalForeground))
            {
                if (GetForegroundWindow() != originalForeground) SetForegroundWindow(originalForeground);
                foregroundRestored = GetForegroundWindow() == originalForeground;
                lines.Add(foregroundRestored ? "PASS: original foreground HWND restored and reread." : "FAIL: original foreground HWND could not be restored and verified.");
            }
            else lines.Add("GAP: original foreground HWND was absent or disappeared; foreground restoration could not be verified.");
            File.WriteAllLines(Path.Combine(directory, "native-header-results.txt"), lines);
            Require(pointerRestored, "Native header check could not verify restoration of the original pointer position.");
            Require(originalForeground == IntPtr.Zero || !IsWindow(originalForeground) || foregroundRestored,
                "Native header check could not verify restoration of the original foreground HWND.");
        }
        foreach (var line in lines) Console.WriteLine(line);
    }

    private static void NativeHeaderClick(FrameworkElement element, IntPtr expectedHwnd)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var target = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        Require(SetCursorPos(target.X, target.Y), "Could not place the pointer on the synthetic direct-Close target.");
        Require(GetCursorPos(out var pointer) && pointer.X == target.X && pointer.Y == target.Y
            && WindowFromPoint(pointer) == expectedHwnd && GetForegroundWindow() == expectedHwnd,
            "Refusing direct-Close input because its pointer target or foreground is not the synthetic popup.");
        var down = new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0002 } } };
        var up = new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0004 } } };
        var written = SendInput(2, [down, up], Marshal.SizeOf<NativeInput>());
        if (written != 2)
        {
            if (written == 1 && GetForegroundWindow() == expectedHwnd
                && (GetCapture() == expectedHwnd || GetCursorPos(out var current) && WindowFromPoint(current) == expectedHwnd))
                Require(SendInput(1, [up], Marshal.SizeOf<NativeInput>()) == 1, "Synthetic direct-Close partial click release was rejected.");
            throw new InvalidOperationException($"Interactive desktop wrote {written}/2 direct-Close mouse inputs; guarded synthetic-only release attempted when still owned.");
        }
        AccountUiChecks.PumpUntil(Task.Delay(120)); Pump();
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
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
