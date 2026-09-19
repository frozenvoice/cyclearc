using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// The widget and the detail flyout scale independently, and a click on the widget does not
/// hand the keyboard to the flyout.
///
/// The reported symptom - "Ctrl +/- at the widget resizes the detail window" - was not a shared
/// zoom value. Two separate causes produced it: the widget had no zoom of its own, and every
/// widget click (header and empty chrome included) raised FlyoutRequested, so the detail window
/// opened and activated and took the keyboard with it. A test that only called the zoom helpers
/// would have proved neither, so the focus half runs against real shown windows and the real
/// click gesture.
/// </summary>
internal static class WidgetZoomChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ScreenRect> Wide = WidgetFixture.Desktop;

    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousLanguage = UiText.Language;
        var count = 0;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var suffix = $"{language}-{theme}".ToLowerInvariant();
                count += SeparateScales(directory, suffix);
                count += ZoomedHeaderFits(directory, suffix);
            }

            UiText.SetLanguage(UiLanguage.English);
            applyTheme.Invoke(null, [AppTheme.Dark]);
            count += FocusStaysWhereTheClickWas(directory);
            count += SizeSurvivesRecreation();
        }
        finally
        {
            UiText.SetLanguage(previousLanguage);
            applyTheme.Invoke(null, [AppTheme.Dark]);
        }

        Console.WriteLine($"PASS: {count} independent window zoom checks; separate widget/flyout scales, "
            + "buttons matching the keyboard, limits, header fit at 80/100/150% for 1/3/5 accounts, "
            + "real-window focus separation between a header click and an account click, and a "
            + "saved size surviving reload and recreation without being written back.");
    }

    /// <summary>
    /// Neither window can move the other's scale, by button or by shortcut, and each really
    /// resizes rather than only repainting.
    /// </summary>
    private static int SeparateScales(string? directory, string suffix)
    {
        var checks = 0;
        var accounts = MixedHeights().Take(3).ToArray();
        var widget = new FloatingWidget { ShowActivated = false };
        var flyout = new FlyoutWindow { ShowActivated = false };
        try
        {
            var widgetChanges = new List<int>();
            var flyoutChanges = new List<int>();
            widget.ZoomChanged += percent => widgetChanges.Add(percent);
            flyout.ZoomChanged += percent => flyoutChanges.Add(percent);
            var opened = 0;
            widget.FlyoutRequested += () => opened++;

            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, Wide, Now);
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            Layout(widget);

            // Two different saved sizes, restored the way the application restores them.
            widget.SetZoom(90, notify: false);
            flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 130, WidgetZoomPercent = 90 });
            Check(widget.ZoomPercent == 90 && flyout.ZoomPercent == 130,
                $"Restored sizes were not kept ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            // A shortcut handled by one window moves that window only.
            Check(widget.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control), "The widget refused Ctrl +.");
            Check(widget.ZoomPercent == 100 && flyout.ZoomPercent == 130,
                $"Zooming the widget moved the flyout ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            Check(flyout.TryHandleZoomShortcut(Key.OemMinus, ModifierKeys.Control), "The flyout refused Ctrl -.");
            Check(flyout.ZoomPercent == 120 && widget.ZoomPercent == 100,
                $"Zooming the flyout moved the widget ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            // Each window reported only its own change.
            Check(widgetChanges is [100] && flyoutChanges is [120],
                $"Change events crossed windows (widget {string.Join(",", widgetChanges)}, "
                + $"flyout {string.Join(",", flyoutChanges)}).");
            checks++;

            // Numeric keypad, and Ctrl 0 resetting one window without touching the other.
            Check(widget.TryHandleZoomShortcut(Key.Add, ModifierKeys.Control) && widget.ZoomPercent == 110,
                "The widget ignored the numeric-keypad plus.");
            Check(widget.TryHandleZoomShortcut(Key.Subtract, ModifierKeys.Control) && widget.ZoomPercent == 100,
                "The widget ignored the numeric-keypad minus.");
            Check(flyout.TryHandleZoomShortcut(Key.NumPad0, ModifierKeys.Control)
                && flyout.ZoomPercent == FlyoutZoom.DefaultPercent && widget.ZoomPercent == 100,
                "Ctrl 0 on the flyout did not reset it alone.");
            checks++;

            // Shift is how '+' is typed on most layouts; the key is still OemPlus.
            Check(widget.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift)
                && widget.ZoomPercent == 110, "Ctrl+Shift+'=' did not zoom the widget in.");
            checks++;

            // Keystrokes that are not this shortcut are declined by both windows and change nothing.
            foreach (var (key, modifiers, what) in new[]
            {
                (Key.OemPlus, ModifierKeys.None, "plus without Ctrl"),
                (Key.OemMinus, ModifierKeys.Shift, "minus with Shift alone"),
                (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt plus"),
                (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Windows, "Ctrl+Win plus"),
                (Key.D0, ModifierKeys.None, "zero without Ctrl"),
                (Key.A, ModifierKeys.Control, "Ctrl A"),
            })
            {
                var before = (widget.ZoomPercent, flyout.ZoomPercent);
                Check(!widget.TryHandleZoomShortcut(key, modifiers), $"The widget treated {what} as zoom.");
                Check(!flyout.TryHandleZoomShortcut(key, modifiers), $"The flyout treated {what} as zoom.");
                Check((widget.ZoomPercent, flyout.ZoomPercent) == before, $"{what} changed a size.");
                checks++;
            }

            // One keystroke is one step of ten, not two.
            widget.SetZoom(100, notify: false);
            widgetChanges.Clear();
            widget.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control);
            Check(widget.ZoomPercent == 110 && widgetChanges is [110],
                $"One Ctrl + produced {widget.ZoomPercent} and {widgetChanges.Count} event(s).");
            checks++;

            // Each window stops at its own limits, and Ctrl 0 still escapes from either end.
            foreach (var (name, get, zoom, shortcut) in Windows(widget, flyout))
            {
                zoom(FlyoutZoom.MaxPercent);
                shortcut(Key.OemPlus);
                Check(get() == FlyoutZoom.MaxPercent, $"The {name} went past its maximum.");
                shortcut(Key.D0);
                Check(get() == FlyoutZoom.DefaultPercent, $"Ctrl 0 could not return the {name} from its maximum.");
                zoom(FlyoutZoom.MinPercent);
                shortcut(Key.OemMinus);
                Check(get() == FlyoutZoom.MinPercent, $"The {name} went past its minimum.");
                shortcut(Key.D0);
                Check(get() == FlyoutZoom.DefaultPercent, $"Ctrl 0 could not return the {name} from its minimum.");
                checks++;
            }

            // The buttons take the same path as the keyboard and reflect the same limits.
            foreach (var (name, get, zoom, host) in new (string, Func<int>, Action<int>, Window)[]
            {
                ("widget", () => widget.ZoomPercent, percent => widget.SetZoom(percent, notify: false), widget),
                ("flyout", () => flyout.ZoomPercent, percent =>
                    flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = percent }), flyout),
            })
            {
                var prefix = name == "widget" ? "Widget" : "Flyout";
                var zoomIn = (Button)host.FindName(prefix + "ZoomInButton");
                var zoomOut = (Button)host.FindName(prefix + "ZoomOutButton");

                zoom(100);
                zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(get() == 110, $"The {name} zoom-in button produced {get()}.");
                zoomOut.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(get() == 100, $"The {name} zoom-out button produced {get()}.");
                checks++;

                // Button and keyboard must land on the same value from the same start.
                zoom(100);
                zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var byButton = get();
                zoom(100);
                Check(name == "widget"
                    ? widget.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control)
                    : flyout.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control), $"The {name} refused Ctrl +.");
                Check(get() == byButton, $"The {name} button and keyboard disagree ({byButton} vs {get()}).");
                checks++;

                zoom(FlyoutZoom.MaxPercent);
                Check(!zoomIn.IsEnabled && zoomOut.IsEnabled, $"The {name} zoom-in button stayed enabled at the maximum.");
                // A disabled button takes no pointer input; raising Click would bypass that.
                Check(!zoomIn.IsHitTestVisible || !zoomIn.IsEnabled,
                    $"The disabled {name} zoom-in button still accepts pointer input.");
                zoom(FlyoutZoom.MinPercent);
                Check(zoomIn.IsEnabled && !zoomOut.IsEnabled, $"The {name} zoom-out button stayed enabled at the minimum.");
                Check(!zoomOut.IsHitTestVisible || !zoomOut.IsEnabled,
                    $"The disabled {name} zoom-out button still accepts pointer input.");
                checks++;

                // The hint names this window's own current size.
                zoom(120);
                Check(zoomIn.ToolTip is string hint && hint.Contains("120", StringComparison.Ordinal),
                    $"The {name} zoom-in hint does not name its current size.");
                checks++;
            }

            // Zooming is a display change: it never opens the detail window.
            Check(opened == 0, $"Zooming opened the detail window {opened} time(s).");
            checks++;

            // The zoom buttons are chrome, so they neither drag the window nor select an account.
            foreach (var name in new[] { "WidgetZoomInButton", "WidgetZoomOutButton" })
            {
                Check(!FloatingWidget.ShouldBeginWindowDrag((DependencyObject)widget.FindName(name)),
                    $"{name} is classified as a window-drag source.");
                checks++;
            }

            // A LayoutTransform, not a render-only one. The panel's own ActualWidth is the
            // untransformed layout size and stays put by design, so the measurement that
            // matters is the size it asks its parent for - which is what sizes the HWND.
            var sizes = new List<(int Percent, double Desired, double Arranged)>();
            foreach (var percent in new[] { FlyoutZoom.MinPercent, 100, FlyoutZoom.MaxPercent })
            {
                widget.SetZoom(percent, notify: false);
                Layout(widget);
                sizes.Add((percent, ((FrameworkElement)widget.Content).DesiredSize.Width, ArrangedWidth(widget)));
            }
            Check(sizes[2].Desired > sizes[1].Desired + 1 && sizes[1].Desired > sizes[0].Desired + 1,
                "Scaling did not change the size the widget asks for ("
                + string.Join(" / ", sizes.Select(s => $"{s.Percent}%={s.Desired:n0}")) + ").");
            checks++;

            // The HWND size follows it. The grid wraps in unscaled child DIP while the panel
            // measures scaled, so a size taken from the grid alone would clip above 100%.
            foreach (var (percent, desired, arranged) in sizes)
            {
                Check(arranged + 0.5 >= desired,
                    $"The arranged widget size {arranged:n0} clips the {desired:n0} it needs at {percent}%.");
                checks++;
            }

            if (directory is not null)
            {
                foreach (var percent in new[] { FlyoutZoom.MinPercent, 100, FlyoutZoom.MaxPercent })
                {
                    widget.SetZoom(percent, notify: false);
                    Layout(widget);
                    WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-zoom-{percent}-{suffix}.png"));
                }
            }
        }
        finally
        {
            widget.Close();
            flyout.Close();
        }

        return checks;
    }

    /// <summary>
    /// The header still fits, keeps its order and keeps its title at every size, including the
    /// narrow single-account widget where two more buttons have the least room.
    /// </summary>
    private static int ZoomedHeaderFits(string? directory, string suffix)
    {
        var checks = 0;
        string[] order =
        [
            "WidgetZoomOutButton", "WidgetZoomInButton", "WidgetRefreshButton",
            "WidgetSettingsButton", "WidgetCloseButton",
        ];
        foreach (var accountCount in new[] { 1, 3, 5 })
        foreach (var percent in new[] { FlyoutZoom.MinPercent, 100, FlyoutZoom.MaxPercent })
        {
            var accounts = MixedHeights().Take(accountCount).ToArray();
            var widget = new FloatingWidget { ShowActivated = false };
            try
            {
                var where = $"{accountCount} account(s) at {percent}%";
                widget.SetZoom(percent, notify: false);
                widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, Wide, Now);
                Layout(widget);

                WidgetMultiAccountChecks.CheckAlignment(widget, $"{suffix}: {where}");

                var header = (FrameworkElement)widget.FindName("WidgetHeader");
                var boxes = order.Select(name =>
                {
                    var button = (Button)widget.FindName(name);
                    return (name, button, box: button.TransformToAncestor(header)
                        .TransformBounds(new Rect(button.RenderSize)));
                }).ToArray();

                foreach (var (name, button, box) in boxes)
                {
                    Check(button.Visibility == Visibility.Visible, $"{name} is not shown with {where}.");
                    Check(box.Width > 0 && box.Height > 0, $"{name} collapsed to nothing with {where}.");
                    Check(box.Left >= -0.1 && box.Top >= -0.1
                        && box.Right <= header.ActualWidth + 0.1 && box.Bottom <= header.ActualHeight + 0.1,
                        $"{name} is clipped with {where}.");
                    checks++;
                }

                for (var i = 1; i < boxes.Length; i++)
                {
                    Check(boxes[i - 1].box.Right <= boxes[i].box.Left + 0.1,
                        $"Header order is wrong: {boxes[i - 1].name} is not left of {boxes[i].name} with {where}.");
                    checks++;
                }

                // The new buttons must not have squeezed the product name out of the header.
                var title = (FrameworkElement)widget.FindName("ProductTitle");
                Check(title.ActualWidth > 0 && title.ActualWidth + 0.5 >= title.DesiredSize.Width,
                    $"The product title is clipped or collapsed with {where}.");
                checks++;

                // Scrolling still reaches the last account at every size.
                var scroller = (ScrollViewer)widget.FindName("ModuleScroller");
                Check(scroller.ScrollableHeight < 0.5 || scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                    $"Content overflows without a scrollbar with {where}.");
                checks++;

                if (directory is not null && accountCount == 5 && percent == FlyoutZoom.MaxPercent)
                    WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-zoom-five-max-{suffix}.png"));
                if (directory is not null && accountCount == 1 && percent == FlyoutZoom.MinPercent)
                    WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"widget-zoom-one-min-{suffix}.png"));
            }
            finally { widget.Close(); }
        }

        return checks;
    }

    /// <summary>
    /// Real shown windows and the real click gesture. A click on an account opens and activates
    /// the detail window as before; a click on the header or empty chrome focuses the widget and
    /// leaves the detail window alone, so the shortcut typed next reaches the widget.
    /// </summary>
    private static int FocusStaysWhereTheClickWas(string? directory)
    {
        var checks = 0;
        var accounts = MixedHeights().Take(3).ToArray();
        var widget = new FloatingWidget { ShowActivated = false, ShowInTaskbar = false, Left = 40, Top = 40 };
        var flyout = new FlyoutWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var opened = 0;
            var selected = new List<string>();
            widget.AccountSelected += id => selected.Add(id);
            // Mirrors what App.ShowMain does with the request: show the window and activate it.
            widget.FlyoutRequested += () =>
            {
                opened++;
                flyout.Show();
                flyout.Activate();
            };
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, Wide, Now);
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);

            widget.Show();
            Pump();
            Check(!widget.IsActive && GetActiveWindow() != Handle(widget),
                "Showing the widget activated it; it must never take the keyboard on its own.");
            checks++;

            widget.SetZoom(90, notify: false);
            flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 130, WidgetZoomPercent = 90 });

            // A press on an account module: the detail window opens and takes the keyboard,
            // which is the behaviour that was always wanted.
            var module = widget.Modules[1];
            SetActiveWindow(Handle(widget));
            Click(widget, module.ProfileId);
            Pump();
            Check(opened == 1 && selected is [var picked] && picked == module.ProfileId,
                $"An account click raised {opened} open request(s) and {selected.Count} selection(s).");
            Check(flyout.IsVisible && flyout.IsActive && GetActiveWindow() == Handle(flyout),
                "An account click did not open and activate the detail window.");
            checks++;

            // The detail window now owns the keyboard, so its shortcut moves it and not the widget.
            Check(flyout.TryHandleZoomShortcut(Key.OemMinus, ModifierKeys.Control), "The flyout refused Ctrl -.");
            Check(flyout.ZoomPercent == 120 && widget.ZoomPercent == 90,
                $"The flyout shortcut changed the widget ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            // The reported symptom, reproduced from the state that produced it: with the detail
            // window active, a click on the widget header must bring the keyboard back to the
            // widget and must not reopen or re-activate the detail window.
            var beforeHeaderClick = flyout.ZoomPercent;
            SetActiveWindow(Handle(widget));
            Click(widget, null);
            Pump();
            Check(opened == 1, $"A header click raised {opened - 1} extra open request(s).");
            Check(selected.Count == 1, "A header click selected an account.");
            Check(widget.IsActive && GetActiveWindow() == Handle(widget),
                "A header click did not leave the keyboard on the widget.");
            Check(!flyout.IsActive && GetActiveWindow() != Handle(flyout),
                "A header click handed the keyboard back to the detail window.");
            checks++;

            // With the widget focused, its own PreviewKeyDown handler is the one that runs, and
            // the detail window is untouched.
            var focused = Keyboard.FocusedElement as DependencyObject;
            Check(focused is null || ReferenceEquals(focused, widget) || IsInside(focused, widget),
                $"Keyboard focus sits outside the widget ({focused?.GetType().Name ?? "none"}).");
            Check(widget.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control), "The widget refused Ctrl +.");
            Check(widget.ZoomPercent == 100 && flyout.ZoomPercent == beforeHeaderClick,
                $"The widget shortcut changed the flyout ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            // Both sizes are live at once - this is the state the screenshots below record.
            Check(widget.IsVisible && flyout.IsVisible && widget.ZoomPercent != flyout.ZoomPercent,
                $"The two windows are not showing different sizes ({widget.ZoomPercent}/{flyout.ZoomPercent}).");
            checks++;

            if (directory is not null)
            {
                Layout(widget);
                WidgetFixture.RenderWidget(widget, Path.Combine(directory, $"zoom-both-widget-{widget.ZoomPercent}.png"));
                // The flyout carries its own LayoutTransform, and clamps it to the work area,
                // so the capture uses the scale actually applied rather than the stored percent.
                var applied = ((System.Windows.Media.ScaleTransform)flyout.FindName("FlyoutScale")).ScaleX;
                AccountUiChecks.RenderCurrent((FrameworkElement)flyout.Content,
                    Path.Combine(directory, $"zoom-both-flyout-{flyout.ZoomPercent}.png"), applied);
            }

            // A zoom button is a click inside the widget too: it must not move the keyboard away.
            var zoomIn = (Button)widget.FindName("WidgetZoomInButton");
            zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Check(opened == 1, "A zoom button opened the detail window.");
            Check(widget.IsActive && GetActiveWindow() == Handle(widget),
                "A zoom button moved the keyboard off the widget.");
            checks++;

            // A drag that moved is a move, not a click: it opens nothing and selects nothing.
            var moves = 0;
            widget.Moved += (_, _) => moves++;
            Drag(widget, module.ProfileId, 60);
            Pump();
            Check(opened == 1 && selected.Count == 1 && moves == 1,
                $"A drag produced {opened - 1} open request(s), {selected.Count - 1} selection(s) "
                + $"and {moves} move(s).");
            checks++;

            // Rebinding and relayout must still never activate the widget, and must not
            // undo the size the person chose.
            var chosen = widget.ZoomPercent;
            SetActiveWindow(Handle(flyout));
            Pump();
            widget.BindAccounts(accounts, accounts[0].Profile.Id, UsagePeriodPreference.Auto, Wide, Now.AddMinutes(1));
            widget.SetRefreshing(true);
            widget.SetRefreshing(false);
            Pump();
            Check(!widget.IsActive && GetActiveWindow() != Handle(widget),
                "Rebinding or a refresh pulled the keyboard to the widget.");
            Check(widget.ZoomPercent == chosen,
                $"Rebinding changed the widget size from {chosen} to {widget.ZoomPercent}.");
            checks++;
        }
        finally
        {
            widget.Close();
            flyout.Close();
        }

        return checks;
    }

    /// <summary>
    /// The saved widget size is restored through the controller, survives a recreation, and
    /// restoring it is not itself reported as a change - which would write the value back and,
    /// on a clamped or normalised value, quietly rewrite what the person chose.
    /// </summary>
    private static int SizeSurvivesRecreation()
    {
        var checks = 0;
        var folder = Path.Combine(Path.GetTempPath(), "CycleArc-zoom-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        var accounts = MixedHeights().Take(2).ToArray();
        var overview = UsageAccountOverview.Create(accounts, accounts[0].Profile.Id);
        var reported = new List<int>();
        FloatingWidgetController? controller = null;
        try
        {
            Directory.CreateDirectory(folder);
            controller = new FloatingWidgetController(window =>
            {
                window.ZoomChanged += percent =>
                {
                    reported.Add(percent);
                    var current = store.Load();
                    current.WidgetZoomPercent = percent;
                    store.Save(current);
                };
            });

            // Two different sizes saved together, the way the application saves them.
            var settings = store.Load();
            settings.FloatingWidgetEnabled = true;
            settings.WidgetZoomPercent = 90;
            settings.FlyoutZoomPercent = 130;
            store.Save(settings);

            var loaded = store.Load();
            Check(loaded.WidgetZoomPercent == 90 && loaded.FlyoutZoomPercent == 130,
                $"A saved pair did not survive a reload ({loaded.WidgetZoomPercent}/{loaded.FlyoutZoomPercent}).");
            checks++;

            controller.Update(loaded, overview);
            Pump();
            Check(controller.CurrentWindow?.ZoomPercent == 90,
                $"The widget opened at {controller.CurrentWindow?.ZoomPercent} instead of the saved 90.");
            Check(reported.Count == 0, "Restoring a saved size was reported as a change.");
            checks++;

            // A change made in the window reaches the file, and only its own value.
            controller.CurrentWindow!.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control);
            Pump();
            Check(reported is [100], $"One Ctrl + reported {reported.Count} change(s).");
            Check(store.Load() is { WidgetZoomPercent: 100, FlyoutZoomPercent: 130 },
                "The new widget size was not saved, or it overwrote the flyout's.");
            checks++;

            // A recreated window comes back at it, and still reports nothing for the restore.
            reported.Clear();
            controller.Recreate(store.Load(), overview);
            Pump();
            Check(controller.CurrentWindow?.ZoomPercent == 100,
                $"A recreated widget opened at {controller.CurrentWindow?.ZoomPercent} instead of 100.");
            Check(reported.Count == 0, "Recreation reported a size change.");
            checks++;

            // Settings written before the widget could scale have no such property. They open
            // unscaled rather than at whatever the flyout was set to.
            var older = SettingsMigration.FromJson(
                "{ \"Version\": 1, \"FloatingWidgetEnabled\": true, \"FlyoutZoomPercent\": 130 }");
            reported.Clear();
            controller.Recreate(older, overview);
            Pump();
            Check(controller.CurrentWindow?.ZoomPercent == FlyoutZoom.DefaultPercent,
                $"Older settings opened the widget at {controller.CurrentWindow?.ZoomPercent}.");
            Check(reported.Count == 0, "Opening older settings reported a size change.");
            checks++;

            // A value outside the range is clamped for display without being written back.
            reported.Clear();
            controller.Recreate(new AppSettings { FloatingWidgetEnabled = true, WidgetZoomPercent = 400 }, overview);
            Pump();
            Check(controller.CurrentWindow?.ZoomPercent == FlyoutZoom.MaxPercent,
                $"An out-of-range saved size opened at {controller.CurrentWindow?.ZoomPercent}.");
            Check(reported.Count == 0, "Clamping a stored size reported it as a change.");
            checks++;
        }
        finally
        {
            controller?.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        return checks;
    }

    /// <summary>
    /// Completes the production click gesture without moving or capturing the user's pointer.
    /// A null profile id is a press on the header or empty chrome.
    /// </summary>
    private static void Click(FloatingWidget widget, string? profileId)
    {
        typeof(FloatingWidget).GetField("_pressedProfileId", PrivateInstance)!.SetValue(widget, profileId);
        typeof(FloatingWidget).GetField("_drag", PrivateInstance)!.SetValue(widget,
            new WidgetDragSession(widget.Left, widget.Top, 0, 0));
        typeof(FloatingWidget).GetMethod("FinishDrag", PrivateInstance)!.Invoke(widget, [true]);
    }

    private static void Drag(FloatingWidget widget, string? profileId, double distance)
    {
        typeof(FloatingWidget).GetField("_pressedProfileId", PrivateInstance)!.SetValue(widget, profileId);
        var session = new WidgetDragSession(widget.Left, widget.Top, 0, 0);
        session.Move(distance, distance);
        typeof(FloatingWidget).GetField("_drag", PrivateInstance)!.SetValue(widget, session);
        typeof(FloatingWidget).GetMethod("FinishDrag", PrivateInstance)!.Invoke(widget, [true]);
    }

    private static (string Name, Func<int> Get, Action<int> Set, Action<Key> Shortcut)[] Windows(
        FloatingWidget widget, FlyoutWindow flyout) =>
    [
        ("widget", () => widget.ZoomPercent, percent => widget.SetZoom(percent, notify: false),
            key => widget.TryHandleZoomShortcut(key, ModifierKeys.Control)),
        ("flyout", () => flyout.ZoomPercent,
            percent => flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = percent }),
            key => flyout.TryHandleZoomShortcut(key, ModifierKeys.Control)),
    ];

    private static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    /// The width the widget gives its HWND, read from the production method rather than
    /// recomputed here, so a coordinate-space mistake in it cannot pass unnoticed.
    private static double ArrangedWidth(FloatingWidget widget) =>
        (((double Width, double Height))typeof(FloatingWidget)
            .GetMethod("ArrangedSize", PrivateInstance)!.Invoke(widget, null)!).Width;

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    private static void Layout(FloatingWidget widget)
    {
        var content = (FrameworkElement)widget.Content;
        content.InvalidateMeasure();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static CodexQuotaSnapshot Both(double fiveHourUsed, double weeklyUsed) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
        [
            new("five", fiveHourUsed, CodexWindowClassifier.FiveHourMinutes, Now.AddMinutes(35), CodexWindowKind.FiveHour),
            new("week", weeklyUsed, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(2).AddHours(4), CodexWindowKind.Weekly)
        ], null);

    private static CodexQuotaSnapshot WeeklyOnly(double used) =>
        Both(0, used) with
        {
            Windows = [new("week", used, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(1).AddHours(9), CodexWindowKind.Weekly)]
        };

    private static CodexAccountView Codex(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label), snapshot);

    private static CodexAccountView Claude(string id, string label, CodexQuotaSnapshot snapshot) =>
        new(new CodexAccountProfile(id, "", label) { Provider = UsageProviderId.Claude },
            snapshot with { Provider = UsageProviderId.Claude, TechnicalDetail = ClaudeUsagePresentation.LiveDetail })
        { IsConnected = true };

    private static CodexAccountView[] MixedHeights() =>
    [
        Codex("zoom-a", UiText.T("Main", "메인"), WeeklyOnly(41)),
        Claude("zoom-b", UiText.T("Work", "작업용"), Both(62, 18) with { Status = CodexQuotaStatus.Stale }),
        Claude("zoom-c", UiText.T("Personal Claude", "개인 Claude"), Both(85, 23)),
        Codex("zoom-d", UiText.T("Kakao", "카카오"), Both(97, 44) with { Status = CodexQuotaStatus.Stale }),
        Claude("zoom-e", UiText.T("Lab", "실험용"), Both(55, 30) with { TechnicalDetail = "claude-live-auth-required" })
    ];

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
