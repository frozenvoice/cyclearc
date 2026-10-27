using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>Production widget geometry and input, using synthetic accounts and no app startup.</summary>
internal static class WidgetButtonChecks
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);
    private static readonly string[] ButtonNames = ["WidgetZoomOutButton", "WidgetZoomInButton", "WidgetRefreshButton", "WidgetSettingsButton", "WidgetCloseButton"];
    private static readonly MethodInfo ApplyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void Run(string? directory = null, bool baseline = false)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var measurements = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var accountCount in new[] { 1, 3, 5 })
            foreach (var zoom in new[] { 80, 100, 150 })
            {
                UiText.SetLanguage(language);
                ApplyTheme.Invoke(null, [theme]);
                var key = $"{language}-{theme}-{accountCount}-{zoom}".ToLowerInvariant();
                var widget = new FloatingWidget { ShowActivated = false, Left = 60, Top = 60 };
                try
                {
                    var accounts = Accounts(accountCount);
                    widget.SetZoom(zoom, notify: false);
                    widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
                    widget.Show();
                    Pump();
                    Measure(widget, key, measurements);
                    CheckTargets(widget, baseline ? 22 : 26, baseline ? 22 : 28, baseline, key, measurements);
                    if (directory is not null)
                        WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-buttons-{(baseline ? "before" : "after")}-{key}.png"));
                }
                finally { widget.CloseWithoutActivation(); }
            }
            if (directory is not null)
            {
                var path = Path.Combine(directory, $"widget-buttons-{(baseline ? "before" : "after")}.csv");
                File.WriteAllText(path, "measurement,value\n" + string.Join("\n", measurements.Select(pair =>
                    $"{pair.Key},{pair.Value.ToString("R", CultureInfo.InvariantCulture)}")) + "\n", new UTF8Encoding(false));
                if (!baseline) CompareBaseline(directory, measurements);
            }
            if (!baseline)
            {
                foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    UiText.SetLanguage(language);
                    ApplyTheme.Invoke(null, [theme]);
                    CheckKeyboardAndStates(directory, $"{language}-{theme}".ToLowerInvariant());
                }
                CheckInjectedDpi();
            }
        }
        finally
        {
            UiText.SetLanguage(previousLanguage);
            ApplyTheme.Invoke(null, [AppTheme.Dark]);
        }
        Console.WriteLine($"PASS: production widget button {(baseline ? "BEFORE baseline" : "geometry, WPF keyboard traversal/Enter/Space/disabled UIA, focus and local high-contrast resource fixtures")} in EN/KO Dark/Light; 1/3/5 accounts, 80/100/150% app zoom. OS high contrast={SystemParameters.HighContrast}; host DPI recorded from shown HWNDs. Local resource overrides do not establish actual OS high-contrast rendering.");
    }

    private static void Measure(FloatingWidget widget, string key, IDictionary<string, double> values)
    {
        var dpi = VisualTreeHelper.GetDpi(widget);
        values[key + "/dpi-x"] = dpi.DpiScaleX;
        values[key + "/dpi-y"] = dpi.DpiScaleY;
        values[key + "/widget-width"] = widget.ActualWidth;
        values[key + "/widget-height"] = widget.ActualHeight;
        values[key + "/layout-width"] = widget.LastLayout!.Width;
        values[key + "/layout-height"] = widget.LastLayout.Height;
        Check(GetWindowRect(new WindowInteropHelper(widget).Handle, out var native), "Could not read synthetic HWND bounds.");
        values[key + "/native-width"] = native.Right - native.Left;
        values[key + "/native-height"] = native.Bottom - native.Top;
        Box("header", (FrameworkElement)widget.FindName("WidgetHeader"));
        for (var i = 0; i < widget.Modules.Count; i++) Box("module-" + i, widget.Modules[i]);
        foreach (var name in ButtonNames) Box(name, (FrameworkElement)widget.FindName(name));
        void Box(string name, FrameworkElement element)
        {
            var box = element.TransformToAncestor(widget).TransformBounds(new Rect(element.RenderSize));
            values[$"{key}/{name}/x"] = box.X;
            values[$"{key}/{name}/y"] = box.Y;
            values[$"{key}/{name}/width"] = box.Width;
            values[$"{key}/{name}/height"] = box.Height;
        }
    }

    private static void CheckTargets(FloatingWidget widget, double width, double height, bool baseline, string key, IDictionary<string, double> measurements)
    {
        var header = (FrameworkElement)widget.FindName("WidgetHeader");
        Rect? previous = null;
        foreach (var name in ButtonNames)
        {
            var button = (Button)widget.FindName(name);
            // Layout rounding snaps the 26-DIP target to a device pixel. At 125% DPI that is
            // 32px, which comes back as 25.6 DIP. One physical pixel is the whole allowance.
            var dpi = VisualTreeHelper.GetDpi(button);
            var pixelX = 1 / Math.Max(dpi.DpiScaleX, 0.01);
            var pixelY = 1 / Math.Max(dpi.DpiScaleY, 0.01);
            Check(Math.Abs(button.ActualWidth - width) <= pixelX + 0.001
                && Math.Abs(button.ActualHeight - height) <= pixelY + 0.001,
                $"{name} click target is {button.ActualWidth}x{button.ActualHeight}, expected {width}x{height} DIP (one pixel {pixelX:0.###}x{pixelY:0.###}).");
            var hitWidth = button.ActualWidth;
            var hitHeight = button.ActualHeight;
            var box = button.TransformToAncestor(header).TransformBounds(new Rect(button.RenderSize));
            var widgetBox = button.TransformToAncestor(widget).TransformBounds(new Rect(button.RenderSize));
            Check(box.Left >= -0.01 && box.Right <= header.ActualWidth + 0.01
                && widgetBox.Top >= -0.01 && widgetBox.Bottom <= widget.ActualHeight + 0.01,
                $"{name} is clipped by the widget.");
            var firstModule = widget.Modules[0].TransformToAncestor(widget).TransformBounds(new Rect(widget.Modules[0].RenderSize));
            Check(widgetBox.Bottom <= firstModule.Top + 0.01, name + " overlaps account module input.");
            if (previous is { } before) Check(before.Right <= box.Left + 0.01, "Widget click targets overlap.");
            previous = box;
            Check(!FloatingWidget.ShouldBeginWindowDrag(button), name + " starts widget drag.");
            if (!baseline)
            {
                if (button.Content is System.Windows.Shapes.Path icon)
                    Check(icon.Width == 16 && icon.Height == 16, name + " enlarged its visual icon.");
                else if (button.Content is TextBlock glyph)
                    Check(glyph.FontSize == 12, name + " enlarged its visual glyph.");
            }
            // Disabled controls are deliberately excluded by WPF InputHitTest (the 80% zoom-out
            // and 150% zoom-in limits). Their template geometry is still measured above.
            if (!button.IsEnabled) continue;
            var misses = 0;
            foreach (var point in new[] { new Point(0.5, hitHeight / 2), new Point(hitWidth - 0.5, hitHeight / 2), new Point(hitWidth / 2, 0.5), new Point(hitWidth / 2, hitHeight - 0.5) })
            {
                var target = button.TranslatePoint(point, widget);
                var hit = widget.InputHitTest(target) as DependencyObject;
                if (!IsWithin(hit, button))
                {
                    misses++;
                    var detail = $"{key}/{name} target-edge miss local={point}, widget={target}, enabled={button.IsEnabled}, hit={hit?.GetType().Name}:{(hit as FrameworkElement)?.Name}";
                    if (baseline) Console.WriteLine("BASELINE: " + detail);
                    else Check(false, detail);
                }
            }
            measurements[$"{key}/{name}/edge-misses"] = misses;
        }
    }

    private static void CompareBaseline(string directory, IReadOnlyDictionary<string, double> after)
    {
        var path = Path.Combine(directory, "widget-buttons-before.csv");
        if (!File.Exists(path)) { Console.WriteLine("Widget size comparison unavailable: BEFORE CSV is absent."); return; }
        var before = File.ReadLines(path).Skip(1).Select(line => line.Split(',')).ToDictionary(parts => parts[0], parts => double.Parse(parts[1], CultureInfo.InvariantCulture));
        foreach (var (key, value) in after.Where(pair => pair.Key.Contains("/module-") || pair.Key.Contains("/layout-")
            || pair.Key.Contains("/widget-") || pair.Key.Contains("/native-") || pair.Key.Contains("/dpi-") || pair.Key.Contains("/header/")))
            Check(before.TryGetValue(key, out var old) && Math.Abs(old - value) < 0.1,
                $"Compact widget geometry changed: {key}, BEFORE={before.GetValueOrDefault(key)} AFTER={value}.");
        Console.WriteLine("PASS: native/DIP outer, header and module bounds match recorded production BEFORE baseline.");
    }

    private static void CheckKeyboardAndStates(string? directory, string suffix)
    {
        var widget = new FloatingWidget { ShowActivated = false, Left = 60, Top = 60 };
        var originalActive = GetActiveWindow();
        try
        {
            var accounts = Accounts(1);
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
            widget.Show(); Pump();
            SetActiveWindow(new WindowInteropHelper(widget).Handle); Pump();
            var buttons = ButtonNames.Select(name => (Button)widget.FindName(name)).ToArray();
            var refreshes = 0; var settings = 0; var closes = 0; var flyouts = 0;
            var selections = new List<string>();
            widget.RefreshRequested += () => refreshes++;
            widget.SettingsRequested += () => settings++;
            widget.CloseRequested += () => closes++;
            widget.FlyoutRequested += () => flyouts++;
            widget.AccountSelected += selections.Add;
            Check(buttons[0].Focus(), "First widget button refused focus."); Pump();
            for (var i = 1; i < buttons.Length; i++) Move(buttons[i - 1], FocusNavigationDirection.Next, buttons[i], "Tab");
            var module = widget.Modules[0];
            Move(buttons[^1], FocusNavigationDirection.Next, module, "Tab into account details");
            Move(module, FocusNavigationDirection.Previous, buttons[^1], "Shift+Tab from account details");
            for (var i = buttons.Length - 2; i >= 0; i--) Move(buttons[i + 1], FocusNavigationDirection.Previous, buttons[i], "Shift+Tab");
            foreach (var button in buttons)
            {
                // Leave both zoom steps away from a size limit: disabling a limit button
                // intentionally removes it from keyboard traversal and is covered separately.
                widget.SetZoom(button.Name == "WidgetZoomOutButton" ? 120 : 100, notify: false); Pump();
                Check(button.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), button.Name + " lacks accessible name or tooltip.");
                Check(button.Focus(), button.Name + " refused keyboard focus."); Pump();
                var clicks = 0;
                RoutedEventHandler count = (_, _) => clicks++;
                button.AddHandler(ButtonBase.ClickEvent, count, handledEventsToo: true);
                try
                {
                    SendKey(button, Key.Enter, Keyboard.KeyDownEvent);
                    SendKey(button, Key.Enter, Keyboard.KeyUpEvent);
                    Check(clicks == 1, button.Name + " Enter did not activate exactly once.");
                    Check(button.IsKeyboardFocused, button.Name + " lost focus after activation/zoom.");
                    SendKey(button, Key.Space, Keyboard.KeyDownEvent);
                    Check(button.IsPressed && clicks == 1, button.Name + " Space-down did not enter pressed without activation.");
                    CheckChrome(button, (bool)button.Tag);
                    if (directory is not null && button.Name == "WidgetRefreshButton")
                        WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-buttons-pressed-{suffix}.png"));
                    SendKey(button, Key.Space, Keyboard.KeyUpEvent);
                    Check(clicks == 2 && !button.IsPressed, button.Name + " Space-up did not activate exactly once.");
                    Pump();
                    Check(button.IsKeyboardFocused, button.Name + " lost keyboard focus after Space/zoom.");
                    foreach (var highContrast in new[] { false, true, false })
                    {
                        button.Resources[SystemParameters.HighContrastKey] = highContrast; Pump();
                        Check(Equals(button.Tag, highContrast), "Live high-contrast override did not resolve on " + button.Name);
                        CheckChrome(button, highContrast);
                        if (directory is not null && button.Name == "WidgetRefreshButton")
                            WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-buttons-focus-{suffix}-{(highContrast ? "hc-fixture" : "normal")}.png"));
                        button.IsEnabled = false; Pump(); CheckChrome(button, highContrast);
                        var rejected = false;
                        try { Invoke(button).Invoke(); } catch (ElementNotEnabledException) { rejected = true; }
                        Check(rejected && clicks == 2, button.Name + " disabled UIA input was accepted.");
                        button.IsEnabled = true;
                        Check(button.Focus(), "Focus failed after disabled restore."); Pump();
                    }
                }
                finally { button.RemoveHandler(ButtonBase.ClickEvent, count); button.Resources.Remove(SystemParameters.HighContrastKey); button.IsEnabled = true; }
            }
            Check(refreshes == 2 && settings == 2 && closes == 2 && flyouts == 0 && selections.Count == 0,
                "Header keyboard activation crossed into account/detail actions.");
            Check(module.Focus(), "Account module refused keyboard focus."); Pump();
            var position = (widget.Left, widget.Top);
            foreach (var highContrast in new[] { false, true, false })
            {
                module.Resources[SystemParameters.HighContrastKey] = highContrast; Pump();
                var expected = (Brush)module.FindResource(highContrast ? SystemColors.HighlightBrushKey : "AccentBrush");
                Check(SameBrush(module.BorderBrush, expected), "Account keyboard focus border is absent or stale.");
            }
            module.Resources.Remove(SystemParameters.HighContrastKey);
            SendKey(module, Key.Enter, Keyboard.KeyDownEvent);
            SendKey(module, Key.Enter, Keyboard.KeyUpEvent);
            Check(flyouts == 1 && selections.SequenceEqual([accounts[0].Profile.Id]), "Account Enter did not select/open exactly once.");
            SendKey(module, Key.Space, Keyboard.KeyDownEvent);
            Check(flyouts == 1, "Account Space activated before release.");
            SendKey(module, Key.Space, Keyboard.KeyUpEvent);
            Check(flyouts == 2 && selections.Count == 2, "Account Space did not select/open exactly once.");
            SendKey(module, Key.Space, Keyboard.KeyDownEvent);
            buttons[2].Focus();
            SendKey(module, Key.Space, Keyboard.KeyUpEvent);
            Check(flyouts == 2, "Account Space activated after focus left the account.");
            var modulePeer = UIElementAutomationPeer.CreatePeerForElement(module)!;
            Check(modulePeer.GetAutomationControlType() == AutomationControlType.Button
                && !string.IsNullOrWhiteSpace(modulePeer.GetName()), "Account details lacks a named UIA Button peer.");
            Invoke(module).Invoke(); Pump();
            Check(flyouts == 3 && selections.Count == 3 && selections[^1] == accounts[0].Profile.Id,
                "Enabled account UIA Invoke did not select/open exactly once.");
            module.IsEnabled = false;
            var disabledRejected = false;
            try { Invoke(module).Invoke(); } catch (ElementNotEnabledException) { disabledRejected = true; }
            Check(disabledRejected && flyouts == 3, "Disabled account UIA Invoke was accepted.");
            module.IsEnabled = true;
            var replacement = accounts[0] with { Profile = accounts[0].Profile with { Id = "widget-button-rebound" } };
            Invoke(module).Invoke();
            widget.BindAccounts([replacement], replacement.Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
            Pump();
            Check(flyouts == 3 && selections.Count == 3, "Queued account UIA Invoke opened a rebound profile.");
            Check(module.Focus(), "Rebound account refused keyboard focus."); Pump();
            SendKey(module, Key.Space, Keyboard.KeyDownEvent);
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
            SendKey(module, Key.Space, Keyboard.KeyUpEvent); Pump();
            Check(flyouts == 3 && selections.Count == 3, "Account Space release opened a rebound profile.");
            Check(widget.Left == position.Left && widget.Top == position.Top, "Keyboard activation moved the widget.");
            Check(FloatingWidget.ShouldBeginWindowDrag(module), "Keyboard detail access disabled account dragging.");
            widget.SetRefreshing(true); Pump();
            Check(!buttons[2].IsEnabled, "Refresh stayed enabled while busy.");
            widget.SetRefreshing(false); Pump();
            Check(buttons[2].IsEnabled, "Refresh did not recover after busy cleared.");
        }
        finally { widget.CloseWithoutActivation(); if (originalActive != IntPtr.Zero) SetActiveWindow(originalActive); }
    }

    private static void CheckChrome(Button button, bool highContrast)
    {
        button.ApplyTemplate();
        var chrome = (Border)button.Template.FindName("WidgetIconChrome", button);
        if (!button.IsEnabled)
        {
            Check(button.Opacity == (highContrast ? 1 : .45), "Disabled opacity changed the high-contrast contract.");
            if (highContrast) Check(SameBrush(button.Foreground, (Brush)button.FindResource(SystemColors.GrayTextBrushKey)), "Disabled high-contrast text is stale.");
            return;
        }
        Check(button.IsKeyboardFocused, "Focus fixture lost keyboard focus.");
        var focusBrush = (Brush)button.FindResource(highContrast ? SystemColors.HighlightTextBrushKey : "AccentBrush");
        Check(chrome.BorderThickness == new Thickness(highContrast && button.IsPressed ? 2 : 1)
            && SameBrush(chrome.BorderBrush, focusBrush), "Widget button focus border is invisible.");
        if (highContrast)
        {
            Check(SameBrush(chrome.Background, (Brush)button.FindResource(SystemColors.HighlightBrushKey))
                && SameBrush(button.Foreground, (Brush)button.FindResource(SystemColors.HighlightTextBrushKey)), "High-contrast focused/pressed colors are stale.");
        }
        else if (button.IsPressed)
            Check(SameBrush(chrome.Background, (Brush)button.FindResource("SelectionBrush"))
                && SameBrush(button.Foreground, (Brush)button.FindResource("TextBrush")),
                "Pressed button foreground/background do not preserve readable theme contrast.");
    }

    private static void CheckInjectedDpi()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        foreach (var zoom in new[] { 80, 100, 150 })
        {
            var widget = new FloatingWidget();
            try
            {
                VisualTreeHelper.SetRootDpi(widget, new DpiScale(scale, scale));
                var content = (FrameworkElement)widget.Content;
                VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                widget.SetZoom(zoom, notify: false);
                var accounts = Accounts(1);
                widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize)); content.UpdateLayout();
                var header = (FrameworkElement)widget.FindName("WidgetHeader");
                var module = widget.Modules[0];
                foreach (var name in ButtonNames)
                {
                    var button = (Button)widget.FindName(name);
                    var box = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    var moduleBox = module.TransformToAncestor(content).TransformBounds(new Rect(module.RenderSize));
                    Check(button.Width == 26 && button.Height == 28 && box.Top >= 0 && box.Bottom <= moduleBox.Top + .01,
                        $"Injected DPI {scale} zoom {zoom}: {name} clipped/overlapped module.");
                    Check(header.ActualWidth + 0.01 >= button.TransformToAncestor(header).TransformBounds(new Rect(button.RenderSize)).Right,
                        $"Injected DPI {scale} zoom {zoom}: header clipped {name}.");
                }
            }
            finally { widget.CloseWithoutActivation(); }
        }
        Console.WriteLine("PASS: button clipping at injected layout DPI 100/125/150/175/200%; simulated layout evidence, not real monitor traversal.");
    }

    private static IInvokeProvider Invoke(UIElement button) => (IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)!.GetPattern(PatternInterface.Invoke)!;

    // Explicit interactive mode: only synthetic windows; default suite never sends native input.
    public static void RunNative(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var key in new[] { 0x01, 0x02, 0x10, 0x11, 0x12, 0x5B, 0x5C })
            Check((GetAsyncKeyState(key) & 0x8000) == 0, "Native widget checks require released mouse/modifier keys.");
        var foreground = GetForegroundWindow();
        var widget = new FloatingWidget { ShowActivated = false, Left = 100, Top = 100 };
        try
        {
            var accounts = Accounts(1);
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, WidgetFixture.Desktop, Now);
            widget.Show(); Pump();
            Check(SetForegroundWindow(new WindowInteropHelper(widget).Handle), "Could not foreground synthetic widget for native input.");
            Pump();
            var refresh = (Button)widget.FindName("WidgetRefreshButton");
            var settings = (Button)widget.FindName("WidgetSettingsButton");
            var close = (Button)widget.FindName("WidgetCloseButton");
            var refreshed = 0; var configured = 0; var hidden = 0; var opened = 0;
            widget.RefreshRequested += () => refreshed++;
            widget.SettingsRequested += () => configured++;
            widget.CloseRequested += () => hidden++;
            widget.FlyoutRequested += () => opened++;
            foreach (var button in new[] { refresh, settings, close })
            {
                var clicked = 0;
                RoutedEventHandler count = (_, _) => clicked++;
                button.AddHandler(ButtonBase.ClickEvent, count, handledEventsToo: true);
                var position = (widget.Left, widget.Top);
                try
                {
                    // Former target was 22x22; these points use the newly added top/bottom area.
                    foreach (var y in new[] { 1.0, button.ActualHeight - 1.0 })
                        NativeMouseGesture(widget, button.PointToScreen(new Point(button.ActualWidth / 2, y)));
                    Check(clicked == 2, button.Name + " native edge clicks did not activate exactly once each.");
                    Check(widget.Left == position.Left && widget.Top == position.Top && opened == 0,
                        "Native button click moved widget or opened account details.");
                }
                finally { button.RemoveHandler(ButtonBase.ClickEvent, count); }
            }
            Check(refreshed == 2 && configured == 2 && hidden == 2, "Native button routing counts differ.");
            Check(refresh.Focus(), "Native refresh keyboard focus failed."); Pump();
            NativeKey(widget, 0x09); Check(settings.IsKeyboardFocused, "Native Tab did not reach Settings.");
            NativeKey(widget, 0x09, shift: true); Check(refresh.IsKeyboardFocused, "Native Shift+Tab did not return to Refresh.");
            NativeKey(widget, 0x0D); NativeKey(widget, 0x20);
            Check(refreshed == 4, "Native Enter/Space did not refresh exactly once each.");
            widget.SetRefreshing(true); Pump();
            NativeMouseGesture(widget, refresh.PointToScreen(new Point(13, 14)));
            Check(refreshed == 4, "Disabled native refresh button accepted click.");
            widget.SetRefreshing(false); Pump();
            var module = widget.Modules[0];
            Check(module.Focus(), "Native account focus failed."); Pump();
            NativeKey(widget, 0x0D); NativeKey(widget, 0x20);
            Check(opened == 2, "Native account Enter/Space did not open details once each.");
            var modulePoint = module.PointToScreen(new Point(module.ActualWidth / 2, module.ActualHeight / 2));
            var before = (widget.Left, widget.Top);
            NativeMouseGesture(widget, modulePoint, modulePoint + new Vector(70, 45));
            Check(Math.Abs(widget.Left - before.Left) > 10 && opened == 2,
                "Native account drag did not move the widget or accidentally opened details.");
            var header = (FrameworkElement)widget.FindName("ProductTitle");
            var headerPoint = header.PointToScreen(new Point(4, header.ActualHeight / 2));
            before = (widget.Left, widget.Top);
            NativeMouseGesture(widget, headerPoint, headerPoint + new Vector(65, 40));
            Check(Math.Abs(widget.Left - before.Left) > 10 && opened == 2, "Native header drag failed or opened details.");
            WidgetFixture.RenderWidget(widget, Path.Combine(directory, "widget-buttons-native.png"));
            Console.WriteLine($"PASS: native SendInput mouse edge clicks, disabled click, Tab/ShiftTab/Enter/Space and account/header drag on synthetic production widget. Host DPI={VisualTreeHelper.GetDpi(widget).DpiScaleX * 100:0}%.");
        }
        finally
        {
            widget.CloseWithoutActivation();
            if (foreground != IntPtr.Zero) Check(SetForegroundWindow(foreground), "Could not restore foreground after native widget checks.");
        }
    }

    private static void NativeKey(Window widget, ushort key, bool shift = false)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        var shifted = false;
        var pressed = false;
        try
        {
            RequireForeground(hwnd);
            if (shift) { SendNative(hwnd, KeyInput(0x10, false)); shifted = true; }
            SendNative(hwnd, KeyInput(key, false)); pressed = true; Pump();
            SendNative(hwnd, KeyInput(key, true)); pressed = false; Pump();
        }
        finally
        {
            // Release anything this fixture pressed even if foreground changed while held.
            // Cleanup sends only key-up; new gestures are refused after a foreground mismatch.
            if (pressed) ReleaseNative(KeyInput(key, true));
            if (shifted) ReleaseNative(KeyInput(0x10, true));
            Pump();
        }
    }

    private static void NativeMouseGesture(Window widget, Point start, Point? end = null)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        RequireForeground(hwnd);
        Check(GetCursorPos(out var original), "Could not save cursor before native widget gesture.");
        var operation = Task.Run(() =>
        {
            var pressed = false;
            try
            {
                RequireForeground(hwnd);
                Check(SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y)), "Could not place synthetic pointer.");
                Thread.Sleep(80);
                Check(WindowFromPoint(new NativePoint { X = (int)Math.Round(start.X), Y = (int)Math.Round(start.Y) }) == hwnd,
                    "Native widget mouse input aborted: the target point belongs to another HWND.");
                SendNative(hwnd, MouseInput(0x0002)); pressed = true;
                Thread.Sleep(80);
                if (end is { } finish)
                    for (var step = 1; step <= 8; step++)
                    {
                        RequireForeground(hwnd);
                        Check(SetCursorPos((int)Math.Round(start.X + (finish.X - start.X) * step / 8),
                            (int)Math.Round(start.Y + (finish.Y - start.Y) * step / 8)), "Could not move synthetic pointer.");
                        Thread.Sleep(25);
                    }
                SendNative(hwnd, MouseInput(0x0004)); pressed = false;
                Thread.Sleep(80);
            }
            finally
            {
                if (pressed) ReleaseNative(MouseInput(0x0004));
                PostMessage(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero); // Cancel stranded move/capture.
                SetCursorPos(original.X, original.Y);
            }
        });
        AccountUiChecks.PumpUntil(operation); Pump();
    }

    private static void RequireForeground(IntPtr hwnd) => Check(GetForegroundWindow() == hwnd,
        "Native widget input aborted: the foreground is no longer the synthetic widget.");
    private static void SendNative(IntPtr hwnd, NativeInput input)
    {
        RequireForeground(hwnd);
        Check(SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1, "Interactive desktop rejected native widget input.");
    }
    private static void ReleaseNative(NativeInput input) => SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    private static NativeInput KeyInput(ushort key, bool up) => new() { Type = 1, Data = new NativeInputData { Keyboard = new NativeKeyboard { VirtualKey = key, Flags = up ? 2u : 0u } } };
    private static NativeInput MouseInput(uint flags) => new() { Type = 0, Data = new NativeInputData { Mouse = new NativeMouse { Flags = flags } } };
    private static void SendKey(UIElement target, Key key, RoutedEvent routedEvent) => target.RaiseEvent(
        new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = routedEvent });
    private static void Move(UIElement from, FocusNavigationDirection direction, UIElement expected, string action)
    {
        Check(from.MoveFocus(new TraversalRequest(direction)), action + " traversal refused."); Pump();
        Check(expected.IsKeyboardFocused, action + " focused an unexpected widget element.");
    }
    private static bool SameBrush(Brush? left, Brush? right) => left is SolidColorBrush a && right is SolidColorBrush b && a.Color == b.Color && a.Opacity == b.Opacity;

    private static CodexAccountView[] Accounts(int count) => Enumerable.Range(0, count).Select(i =>
        WidgetFixture.Synthetic("widget-button-" + i, UiText.T("Account ", "계정 ") + (i + 1),
            new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
            [new("five", 28 + i * 10, CodexWindowClassifier.FiveHourMinutes, Now.AddMinutes(35), CodexWindowKind.FiveHour),
             new("week", 17 + i * 8, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(2), CodexWindowKind.Weekly)], null))).ToArray();

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputData
    {
        [FieldOffset(0)] public NativeKeyboard Keyboard;
        [FieldOffset(0)] public NativeMouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
}
