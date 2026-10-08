using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;
using ShapePath = System.Windows.Shapes.Path;

namespace CycleArc.UiSmoke;

internal static class FlyoutPinChecks
{
    private static readonly Queue<string> KeyboardTrace = new();
    internal static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ApplyTheme(language, theme);
                var flyout = CreateFlyout();
                var pin = (ToggleButton)flyout.FindName("PinButton");
                try
                {
                    flyout.Show(); flyout.Activate(); Pump();
                    Console.WriteLine($"PIN host WPF DPI={VisualTreeHelper.GetDpi(flyout).DpiScaleX * 100:0}/{VisualTreeHelper.GetDpi(flyout).DpiScaleY * 100:0}%; {language}/{theme}; host high contrast={SystemParameters.HighContrast}.");
                    CheckActivation(flyout);
                    CheckPresentationModes(flyout, language, theme, directory);
                    CheckReopen(flyout);
                }
                finally { pin.Resources.Remove(SystemParameters.HighContrastKey); flyout.Close(); }
                foreach (var zoom in new[] { 80, 100, 150 })
                foreach (var dpi in new[] { 100, 125, 150, 175, 200 })
                    CheckInjectedLayout(zoom, dpi);
            }
            CheckPersistence();
        }
        finally { ApplyTheme(previousLanguage, AppTheme.Dark); }
        Console.WriteLine("PASS: direct pin EN/KO Dark/Light, real Toggle/keyboard class handlers and UIA Toggle pattern, WPF Tab/ShiftTab traversal, single notification, isolated save/restart, local normal/high-contrast fallback/override/restore, injected DPI and zoom; synthetic accounts, no native input.");
    }

    private static FlyoutWindow CreateFlyout()
    {
        var flyout = new FlyoutWindow { ShowActivated = false, Left = 40, Top = 40 };
        flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = false, FlyoutZoomPercent = 100 });
        var accounts = FlyoutHeaderChecks.Accounts();
        flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
        return flyout;
    }

    private static void CheckActivation(FlyoutWindow flyout)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        var settings = (Button)flyout.FindName("SettingsButton");
        var close = (Button)flyout.FindName("CloseButton");
        var events = new List<bool>();
        var selections = 0; var refreshes = 0; var moves = 0;
        flyout.PinChanged += events.Add;
        flyout.AccountSelected += _ => selections++;
        flyout.SyncRequested += () => refreshes++;
        flyout.PositionChanged += (_, _) => moves++;
        var position = flyout.PixelPosition; var anchors = flyout.EdgeAnchors; var selected = flyout.SelectedProfileId;
        try
        {
            CheckState(flyout, false);
            Require(settings.Focus(), "Settings refused focus."); Pump();
            MoveFocus(settings, FocusNavigationDirection.Next, pin, "Tab Settings -> Pin");
            MoveFocus(pin, FocusNavigationDirection.Next, close, "Tab Pin -> Close");
            MoveFocus(close, FocusNavigationDirection.Previous, pin, "ShiftTab Close -> Pin");
            MoveFocus(pin, FocusNavigationDirection.Previous, settings, "ShiftTab Pin -> Settings");
            Require(pin.Focus(), "Pin refused keyboard focus."); Pump();
            var before = Bounds(pin, (Visual)flyout.FindName("FlyoutHeaderGrid"));
            // Invoke the actual virtual class activation; raising Click alone bypasses OnToggle.
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, null);
            Pump(); CheckEvent(flyout, events, 1, true, "ButtonBase.OnClick");
            SendKey(pin, Key.Enter, Keyboard.KeyDownEvent);
            CheckEvent(flyout, events, 2, false, "Enter class handler");
            SendKey(pin, Key.Enter, Keyboard.KeyUpEvent);
            Require(events.Count == 2, "Enter key-up emitted a second pin notification.");
            SendKey(pin, Key.Space, Keyboard.KeyDownEvent);
            Require(pin.IsPressed && events.Count == 2 && !flyout.Pinned, "Space key-down must press without toggling on Release mode.");
            SendKey(pin, Key.Space, Keyboard.KeyUpEvent);
            CheckEvent(flyout, events, 3, true, "Space class handler");
            ToggleProvider(pin).Toggle(); Pump();
            CheckEvent(flyout, events, 4, false, "UIA Toggle provider");
            Require(pin.IsKeyboardFocused, "Activation lost keyboard focus on Pin.");
            Require(before == Bounds(pin, (Visual)flyout.FindName("FlyoutHeaderGrid")), "Pin checked state changed its header bounds.");
            flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = true, FlyoutZoomPercent = 100 }); Pump();
            CheckState(flyout, true);
            Require(events.Count == 4, "Settings restore emitted a pin save notification.");
            flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = false, FlyoutZoomPercent = 100 }); Pump();
            CheckState(flyout, false);
            Require(events.Count == 4, "Unpinned restore emitted a save notification.");
            pin.IsEnabled = false; Pump();
            var rejected = false;
            try { ToggleProvider(pin).Toggle(); }
            catch (ElementNotEnabledException) { rejected = true; }
            Require(rejected && events.Count == 4 && !flyout.Pinned, "Disabled UIA Toggle was accepted or changed pin state.");
            pin.IsEnabled = true; Pump();
            Require(selections == 0 && refreshes == 0 && moves == 0 && selected == flyout.SelectedProfileId
                && position == flyout.PixelPosition && anchors == flyout.EdgeAnchors, "Pin changed selection, refresh, drag, position or anchors.");
        }
        finally { pin.IsEnabled = true; flyout.PinChanged -= events.Add; }
    }

    private static void CheckEvent(FlyoutWindow flyout, List<bool> events, int count, bool pinned, string action)
    {
        CheckState(flyout, pinned);
        Require(events.Count == count && events[^1] == pinned, $"{action} must emit exactly one authoritative PinChanged event.");
    }

    private static IToggleProvider ToggleProvider(ToggleButton pin)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(pin);
        Require(peer is ToggleButtonAutomationPeer, "Pin must expose a real ToggleButtonAutomationPeer.");
        return (IToggleProvider)peer!.GetPattern(PatternInterface.Toggle)!;
    }

    private static void CheckState(FlyoutWindow flyout, bool pinned)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        var peer = UIElementAutomationPeer.CreatePeerForElement(pin)!;
        var name = pinned ? UiText.Unpin : UiText.Pin;
        var help = pinned
            ? UiText.T("Pinned. Keep the detail popup above other windows. Activate to unpin.", "고정됨. 상세 팝업을 다른 창 위에 유지합니다. 누르면 고정을 해제합니다.")
            : UiText.T("Unpinned. Activate to keep the detail popup above other windows.", "고정 해제됨. 누르면 상세 팝업을 다른 창 위에 유지합니다.");
        Require(flyout.Pinned == pinned && flyout.Topmost == pinned && pin.IsChecked == pinned && !pin.IsThreeState,
            $"Authoritative pin, Topmost and two-state toggle disagree: expected={pinned}, Pinned={flyout.Pinned}, Topmost={flyout.Topmost}, IsChecked={pin.IsChecked}, IsThreeState={pin.IsThreeState}; {InputState(pin)}; recent keyboard trace: {string.Join(" | ", KeyboardTrace)}.");
        Require(ToggleProvider(pin).ToggleState == (pinned ? ToggleState.On : ToggleState.Off), "UIA Toggle state is stale.");
        Require(peer.GetName() == name && peer.GetHelpText() == help && Equals(pin.ToolTip, name),
            $"Pin accessibility mismatch: name={peer.GetName()}, help={peer.GetHelpText()}, tooltip={pin.ToolTip}.");
        Require(!flyout.IsVisible || peer.IsContentElement(), "Shown Pin must be in the real UIA content view.");
        var icon = (ShapePath)flyout.FindName("PinIcon");
        Require(icon.Width == 14 && icon.Height == 14 && icon.Data is not null && SameBrush(icon.Stroke, pin.Foreground), "Pin vector does not inherit the button foreground.");
        Require(((RotateTransform)flyout.FindName("PinRotate")).Angle == (pinned ? 0 : -35), "Pin vector rotation does not reflect state.");
        Require(SameBrush(icon.Fill, pinned ? pin.Foreground : Brushes.Transparent), "Pin vector fill does not reflect checked/foreground state.");
    }

    private static void MoveFocus(Control from, FocusNavigationDirection direction, Control expected, string action)
    {
        Require(from.MoveFocus(new TraversalRequest(direction)), action + " traversal refused."); Pump();
        Require(expected.IsKeyboardFocused, action + " focused another control.");
    }

    private static void CheckPresentationModes(FlyoutWindow flyout, UiLanguage language, AppTheme theme, string? directory)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        var fallback = new ResourceDictionary();
        pin.Resources.MergedDictionaries.Add(fallback);
        try
        {
            foreach (var enabled in new[] { true, false })
            {
                fallback[SystemParameters.HighContrastKey] = enabled;
                pin.Resources.Remove(SystemParameters.HighContrastKey); Pump();
                Require(Equals(pin.Tag, enabled), "Pin did not resolve the fixture-local merged high-contrast fallback.");
                CheckVisualStates(flyout, enabled, null);
            }
            foreach (var enabled in new[] { false, true, false })
            {
                pin.Resources[SystemParameters.HighContrastKey] = enabled; Pump();
                Require(Equals(pin.Tag, enabled), "Pin did not resolve the fixture-local high-contrast override.");
                CheckVisualStates(flyout, enabled, directory is null ? null : Path.Combine(directory, $"pin-{language}-{theme}-{(enabled ? "hc" : "normal")}"));
            }
            ApplyTheme(language, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark); Pump();
            CheckVisual(flyout, false);
            ApplyTheme(language, theme); Pump(); CheckVisual(flyout, false);
        }
        finally
        {
            pin.IsEnabled = true;
            pin.Resources.Remove(SystemParameters.HighContrastKey);
            pin.Resources.MergedDictionaries.Remove(fallback); Pump();
            Require(Equals(pin.Tag, pin.FindResource(SystemParameters.HighContrastKey)), "Pin high-contrast resource did not restore to its host fallback.");
        }
    }

    private static void CheckVisualStates(FlyoutWindow flyout, bool highContrast, string? prefix)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        var settings = (Button)flyout.FindName("SettingsButton");
        foreach (var pinned in new[] { false, true })
        {
            flyout.ApplyWindowSettings(new AppSettings { FlyoutPinned = pinned, FlyoutZoomPercent = 100 });
            Require(settings.Focus(), "Could not remove Pin keyboard focus."); Pump(); CheckState(flyout, pinned); CheckVisual(flyout, highContrast);
            if (prefix is not null) CaptureHeader(flyout, prefix + (pinned ? "-on-rest.png" : "-off-rest.png"));
            Require(pin.Focus(), "Pin refused keyboard focus."); Pump(); CheckVisual(flyout, highContrast);
            if (prefix is not null) CaptureHeader(flyout, prefix + (pinned ? "-on-focus.png" : "-off-focus.png"));
            SendKey(pin, Key.Space, Keyboard.KeyDownEvent);
            Require(pin.IsPressed, $"Space did not enter the real pressed state; {InputState(pin)}; {string.Join(" | ", KeyboardTrace)}."); CheckVisual(flyout, highContrast);
            SendKey(pin, Key.Space, Keyboard.KeyUpEvent); CheckState(flyout, !pinned); CheckVisual(flyout, highContrast);
            pin.IsEnabled = false; Pump(); CheckVisual(flyout, highContrast);
            pin.IsEnabled = true; Pump();
        }
    }

    private static void CheckVisual(FlyoutWindow flyout, bool highContrast)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        pin.ApplyTemplate();
        var chrome = (Border)pin.Template.FindName("HeaderIconChrome", pin);
        var highlighted = pin.IsMouseOver || pin.IsKeyboardFocused || pin.IsPressed;
        Brush foreground = highContrast
            ? (Brush)pin.FindResource(!pin.IsEnabled ? SystemColors.GrayTextBrushKey : highlighted || pin.IsChecked == true ? SystemColors.HighlightTextBrushKey : SystemColors.WindowTextBrushKey)
            : (Brush)pin.FindResource(!pin.IsEnabled ? "DisabledBrush" : pin.IsPressed ? "OnAccentBrush" : pin.IsChecked == true ? "AccentBrush" : "TextBrush");
        Brush background = highContrast ? (Brush)pin.FindResource(SystemColors.WindowBrushKey) : Brushes.Transparent;
        if (pin.IsEnabled)
        {
            if (highContrast && (highlighted || pin.IsChecked == true)) background = (Brush)pin.FindResource(SystemColors.HighlightBrushKey);
            else if (pin.IsPressed) background = (Brush)pin.FindResource("AccentBrush");
            else if (pin.IsKeyboardFocused || pin.IsMouseOver || pin.IsChecked == true) background = (Brush)pin.FindResource("GhostBrush");
        }
        Brush border = Brushes.Transparent;
        if (pin.IsEnabled)
        {
            if (highContrast && pin.IsKeyboardFocused) border = (Brush)pin.FindResource(SystemColors.HighlightTextBrushKey);
            else if (highContrast && pin.IsChecked == true) border = (Brush)pin.FindResource(SystemColors.HighlightBrushKey);
            else if (pin.IsKeyboardFocused) border = (Brush)pin.FindResource("TextBrush");
            else if (pin.IsChecked == true) border = (Brush)pin.FindResource("AccentBrush");
        }
        Require(SameBrush(pin.Foreground, foreground) && SameBrush(chrome.Background, background) && SameBrush(chrome.BorderBrush, border)
            && chrome.BorderThickness == new Thickness(1) && pin.Opacity == (pin.IsEnabled || highContrast ? 1 : .45),
            $"Pin chrome mismatch HC={highContrast}, checked={pin.IsChecked}, focus={pin.IsKeyboardFocused}, hover={pin.IsMouseOver}, pressed={pin.IsPressed}, enabled={pin.IsEnabled}; foreground={pin.Foreground}/{foreground}, background={chrome.Background}/{background}, border={chrome.BorderBrush}/{border}, opacity={pin.Opacity}.");
        CheckState(flyout, flyout.Pinned);
        if (pin.IsEnabled) CheckVectorContrast(flyout, pin, chrome, highContrast);
    }

    private static void CheckVectorContrast(FlyoutWindow flyout, ToggleButton pin, Border chrome, bool highContrast)
    {
        var icon = (ShapePath)flyout.FindName("PinIcon");
        var stroke = icon.Stroke as SolidColorBrush;
        var background = chrome.Background as SolidColorBrush;
        var ground = pin.FindResource("BgBrush") as SolidColorBrush;
        Require(stroke is not null && background is not null && ground is not null,
            "Pin contrast requires the actual solid vector, chrome and popup ground brushes.");
        var groundColor = ground!.Color;
        Require(groundColor.A == 255 && ground!.Opacity == 1, "Popup ground must be opaque for the pin contrast calculation.");
        var visibleBackground = Composite(background!, (groundColor.R / 255d, groundColor.G / 255d, groundColor.B / 255d));
        var visibleStroke = Composite(stroke!, visibleBackground);
        var strokeLuminance = RelativeLuminance(visibleStroke);
        var backgroundLuminance = RelativeLuminance(visibleBackground);
        var contrast = (Math.Max(strokeLuminance, backgroundLuminance) + .05) / (Math.Min(strokeLuminance, backgroundLuminance) + .05);
        Require(contrast >= 3,
            $"Enabled pin vector contrast is {contrast:F3}:1, below 3:1; HC={highContrast}, checked={pin.IsChecked}, focus={pin.IsKeyboardFocused}, hover={pin.IsMouseOver}, pressed={pin.IsPressed}; stroke={stroke}, chrome={background}, ground={ground}.");
    }

    private static (double R, double G, double B) Composite(SolidColorBrush foreground, (double R, double G, double B) background)
    {
        var alpha = foreground.Color.A / 255d * foreground.Opacity;
        return (foreground.Color.R / 255d * alpha + background.R * (1 - alpha),
            foreground.Color.G / 255d * alpha + background.G * (1 - alpha),
            foreground.Color.B / 255d * alpha + background.B * (1 - alpha));
    }

    private static double RelativeLuminance((double R, double G, double B) color)
    {
        static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static void CheckInjectedLayout(int zoom, int dpiPercent)
    {
        var flyout = new FlyoutWindow { ShowActivated = false };
        try
        {
            var dpi = new DpiScale(dpiPercent / 100d, dpiPercent / 100d);
            VisualTreeHelper.SetRootDpi(flyout, dpi);
            VisualTreeHelper.SetRootDpi((Visual)flyout.Content, dpi);
            flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom, FlyoutPinned = false });
            var accounts = FlyoutHeaderChecks.Accounts(); flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            var content = (FrameworkElement)flyout.Content;
            Layout(flyout);
            var pin = (ToggleButton)flyout.FindName("PinButton");
            var header = (Grid)flyout.FindName("FlyoutHeaderGrid");
            var icon = (ShapePath)flyout.FindName("PinIcon");
            var before = Bounds(pin, header);
            Require(pin.ActualWidth >= 28 && pin.ActualHeight >= 28, $"Pin hit target shrank at zoom={zoom}, DPI={dpiPercent}.");
            var ink = Bounds(icon, pin);
            Require(new Rect(pin.RenderSize).Contains(ink), $"Rotated pin ink escaped hit target at zoom={zoom}, DPI={dpiPercent}: {ink}/{pin.RenderSize}.");
            CheckState(flyout, false);
            ToggleProvider(pin).Toggle(); Pump(); content.UpdateLayout(); CheckState(flyout, true);
            Require(before == Bounds(pin, header), $"Pin bounds changed on toggle at zoom={zoom}, DPI={dpiPercent}: {before}/{Bounds(pin, header)}.");
        }
        finally { flyout.Close(); }
    }

    private static void CheckReopen(FlyoutWindow flyout)
    {
        var pin = (ToggleButton)flyout.FindName("PinButton");
        var position = flyout.PixelPosition; var selected = flyout.SelectedProfileId; var pinned = flyout.Pinned;
        var events = 0; flyout.PinChanged += _ => events++;
        ((Button)flyout.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(!flyout.IsVisible && flyout.Pinned == pinned, "Direct Close changed pin state.");
        flyout.Show(); Pump(); CheckState(flyout, pinned);
        Require(events == 0 && flyout.PixelPosition == position && flyout.SelectedProfileId == selected, "Close/reopen changed pin, selection or position.");
        Require(pin.IsEnabled, "Reopened direct Pin stayed disabled.");
    }

    private static void CheckPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CycleArc.UiSmoke-pin-settings");
        Require(!Directory.Exists(directory), "Refusing to overwrite an existing pin-settings fixture directory.");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var settings = new AppSettings { FlyoutPinned = true, FlyoutPositionConfigured = true, FlyoutLeft = 40, FlyoutTop = 40, FlyoutZoomPercent = 120, WidgetZoomPercent = 90 };
            store.Save(settings);
            var saves = 0;
            var flyout = new FlyoutWindow();
            try
            {
                flyout.PinChanged += pinned => { settings.FlyoutPinned = pinned; store.Save(settings); saves++; };
                flyout.ApplyWindowSettings(store.Load());
                Require(saves == 0, "Initial restore wrote pin settings.");
                ToggleProvider((ToggleButton)flyout.FindName("PinButton")).Toggle();
                Require(saves == 1 && !store.Load().FlyoutPinned, "UIA pin action did not persist exactly once.");
            }
            finally { flyout.Close(); }
            var loaded = store.Load();
            var restarted = new FlyoutWindow();
            try
            {
                restarted.PinChanged += _ => saves++;
                restarted.ApplyWindowSettings(loaded); Layout(restarted); CheckState(restarted, false);
                Require(saves == 1 && loaded.FlyoutPositionConfigured && loaded.FlyoutLeft == 40 && loaded.FlyoutTop == 40
                    && loaded.FlyoutZoomPercent == 120 && loaded.WidgetZoomPercent == 90, "Restart changed unrelated placement/zoom settings or saved again.");
            }
            finally { restarted.Close(); }
        }
        finally
        {
            // Only the two explicitly owned fixture files are removed; refuse unknown contents.
            File.Delete(Path.Combine(directory, "settings.json"));
            File.Delete(Path.Combine(directory, "settings.json.bak"));
            Directory.Delete(directory);
        }
    }

    private static void Layout(FlyoutWindow flyout)
    {
        var content = (FrameworkElement)flyout.Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize)); content.UpdateLayout(); Pump();
    }

    private static Rect Bounds(FrameworkElement element, Visual relativeTo) => element.TransformToAncestor(relativeTo).TransformBounds(new Rect(element.RenderSize));
    private static bool SameBrush(Brush? left, Brush? right) => left is SolidColorBrush a && right is SolidColorBrush b && a.Color == b.Color && a.Opacity == b.Opacity;

    private static void SendKey(UIElement target, Key key, RoutedEvent routedEvent)
    {
        TraceKeyboard($"before {routedEvent.Name}/{key}: {InputState(target)}");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        TraceKeyboard($"routed {routedEvent.Name}/{key} handled={args.Handled}: {InputState(target)}");
        Pump();
        TraceKeyboard($"pumped {routedEvent.Name}/{key}: {InputState(target)}");
    }

    private static void TraceKeyboard(string state)
    {
        KeyboardTrace.Enqueue(state);
        while (KeyboardTrace.Count > 8) KeyboardTrace.Dequeue();
    }

    private static string InputState(UIElement target)
    {
        static string Element(object? element) => element is FrameworkElement control ? $"{control.GetType().Name}:{control.Name}" : element?.GetType().Name ?? "null";
        var pin = target as ToggleButton;
        var window = Window.GetWindow(target) as FlyoutWindow;
        return $"focus={Element(Keyboard.FocusedElement)}, capture={Element(Mouse.Captured)}, targetFocus={target.IsKeyboardFocused}, targetCapture={target.IsMouseCaptured}, left={Mouse.LeftButton}, pressed={pin?.IsPressed}, checked={pin?.IsChecked}, pinned={window?.Pinned}, topmost={window?.Topmost}, active={window?.IsActive}, visible={window?.IsVisible}";
    }

    private static void CaptureHeader(FlyoutWindow flyout, string path)
    {
        var header = (Grid)flyout.FindName("FlyoutHeaderGrid");
        var scale = VisualTreeHelper.GetDpi(header).DpiScaleX * ((ScaleTransform)flyout.FindName("FlyoutScale")).ScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(header.ActualWidth * scale), (int)Math.Ceiling(header.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(header);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
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

    // Opt-in only: these guards and verified restoration also protect the direct Close fixture.
    internal static void RunHeaderNative(string directory) => RunNative(directory, pinToggle: false);
    internal static void RunPinNative(string directory) => RunNative(directory, pinToggle: true);

    private static void RunNative(string directory, bool pinToggle)
    {
        Directory.CreateDirectory(directory);
        Require(GetCursorPos(out var originalPointer), "Could not save the pointer position.");
        var originalForeground = GetForegroundWindow();
        var previousLanguage = UiText.Language;
        var action = pinToggle ? "Pin" : "Close";
        var lines = new List<string> { $"Opt-in native direct-{action} checks; synthetic production windows only; restoration results recorded below." };
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
                    var target = (ButtonBase)flyout.FindName(pinToggle ? "PinButton" : "CloseButton");
                    var targetPoint = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                    Require(GetForegroundWindow() == hwnd, "Refusing native click because the foreground is not the synthetic popup.");
                    var dpi = VisualTreeHelper.GetDpi(flyout);
                    NativeHeaderClick(target, hwnd);
                    if (pinToggle)
                    {
                        CheckState(flyout, !pinned);
                        Require(pinEvents == 1 && flyout.IsVisible && widget.IsVisible && flyout.RefreshIndicator.IsAnimating,
                            "Native direct Pin did not toggle exactly once while preserving both surfaces and refresh.");
                        Require(GetForegroundWindow() == hwnd, "Refusing second native Pin click because synthetic foreground ownership was lost.");
                        NativeHeaderClick(target, hwnd); CheckState(flyout, pinned);
                        CheckVisual(flyout, Equals(target.Tag, true));
                        Require(pinEvents == 2 && flyout.IsVisible && widget.IsVisible && flyout.RefreshIndicator.IsAnimating,
                            "Native direct Pin did not return to its original state exactly once.");
                    }
                    else
                    {
                        Require(!flyout.IsVisible && widget.IsVisible && flyout.Pinned == pinned && !flyout.RefreshIndicator.IsAnimating && pinEvents == 0,
                            "Native direct Close changed another surface or pin/clock state.");
                        flyout.Show(); Pump();
                        Require(flyout.Pinned == pinned && flyout.RefreshIndicator.IsAnimating,
                            "Native close/reopen did not restore the ongoing refresh and pin state.");
                    }
                    Require(flyout.SelectedProfileId == selected && flyout.PixelPosition == position && flyout.EdgeAnchors == anchors
                        && selectionEvents == 0 && refreshes == 0 && moves == 0,
                        $"Native direct {action} changed selection, placement, anchors, refresh or drag state.");
                    flyout.SetRefreshPresentation(new(true, false, "")); Pump();
                    lines.Add($"PASS {language}/{theme}/{screen.DeviceName}: actual WPF DPI={dpi.DpiScaleX * 100:0}/{dpi.DpiScaleY * 100:0}%, app zoom={zoom}%, pinned={pinned}; {action} click physical={targetPoint.X:0},{targetPoint.Y:0}; widget survives, selection/position/anchors retained; {(pinToggle ? "pin round-trip single notifications, refresh remains active" : "popup only hidden, pin retained, refresh stopped/resumed")}.");
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
            File.WriteAllLines(Path.Combine(directory, pinToggle ? "native-pin-results.txt" : "native-header-results.txt"), lines);
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
        Require(SetCursorPos(target.X, target.Y), "Could not place the pointer on the synthetic direct-header target.");
        Require(GetCursorPos(out var pointer) && pointer.X == target.X && pointer.Y == target.Y
            && WindowFromPoint(pointer) == expectedHwnd && GetForegroundWindow() == expectedHwnd,
            "Refusing direct-header input because its pointer target or foreground is not the synthetic popup.");
        var down = new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0002 } } };
        var up = new NativeInput { Type = 0, Data = new NativeInputData { Mouse = new NativeMouseInput { Flags = 0x0004 } } };
        var written = SendInput(2, [down, up], Marshal.SizeOf<NativeInput>());
        if (written != 2)
        {
            if (written == 1 && GetForegroundWindow() == expectedHwnd
                && (GetCapture() == expectedHwnd || GetCursorPos(out var current) && WindowFromPoint(current) == expectedHwnd))
                Require(SendInput(1, [up], Marshal.SizeOf<NativeInput>()) == 1, "Synthetic direct-header partial click release was rejected.");
            throw new InvalidOperationException($"Interactive desktop wrote {written}/2 direct-header mouse inputs; guarded synthetic-only release attempted when still owned.");
        }
        AccountUiChecks.PumpUntil(Task.Delay(120)); Pump();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
