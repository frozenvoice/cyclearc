using System.Windows;
using System.Windows.Input;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

/// <summary>
/// F5 / Ctrl+R refresh in the popup and widget, Ctrl + wheel zoom steps, and the opt-in global
/// shortcut, which registers only when turned on and reports a combination already in use.
/// </summary>
internal static class ShortcutChecks
{
    public static void Run()
    {
        Check(WindowShortcuts.IsRefresh(Key.F5, ModifierKeys.None) && WindowShortcuts.IsRefresh(Key.R, ModifierKeys.Control)
            && !WindowShortcuts.IsRefresh(Key.F5, ModifierKeys.Shift) && !WindowShortcuts.IsRefresh(Key.R, ModifierKeys.None)
            && !WindowShortcuts.IsRefresh(Key.R, ModifierKeys.Control | ModifierKeys.Shift),
            "Refresh shortcuts accept the wrong keys.");
        Check(WindowShortcuts.WheelZoom(120, ModifierKeys.Control, 100) == FlyoutZoom.Adjust(100, increase: true)
            && WindowShortcuts.WheelZoom(-120, ModifierKeys.Control, 100) == FlyoutZoom.Adjust(100, increase: false)
            && WindowShortcuts.WheelZoom(-120, ModifierKeys.Control, FlyoutZoom.MinPercent) == FlyoutZoom.MinPercent
            && WindowShortcuts.WheelZoom(120, ModifierKeys.None, 100) is null
            && WindowShortcuts.WheelZoom(0, ModifierKeys.Control, 100) is null,
            "Ctrl + wheel does not step the zoom, or a plain wheel stops scrolling.");

        var flyout = new FlyoutWindow { ShowActivated = false, Left = -20000, Top = -20000 };
        var widget = new FloatingWidget { ShowActivated = false };
        var accounts = new AccountsWindow();
        try
        {
            // Status that changes without focus moving is announced politely to screen readers.
            Check(System.Windows.Automation.AutomationProperties.GetLiveSetting((FrameworkElement)flyout.FindName("StatusIndicator"))
                    == System.Windows.Automation.AutomationLiveSetting.Polite
                && System.Windows.Automation.AutomationProperties.GetLiveSetting((FrameworkElement)accounts.FindName("OperationStatus"))
                    == System.Windows.Automation.AutomationLiveSetting.Polite,
                "Popup status or account operation status is not a live region.");
            flyout.ApplyWindowSettings(new AppSettings());
            flyout.Show();
            var flyoutRefreshes = 0;
            flyout.SyncRequested += () => flyoutRefreshes++;
            Press(flyout, Key.F5);
            Check(flyoutRefreshes == 1, "F5 in the popup did not refresh.");
            flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(false, true, UiText.CodexRefreshing));
            Press(flyout, Key.F5);
            Check(flyoutRefreshes == 1, "F5 started a second refresh while one was running.");

            widget.Apply(new AppSettings());
            widget.Left = -20000; widget.Top = -20000;
            widget.Show();
            var widgetRefreshes = 0;
            widget.RefreshRequested += () => widgetRefreshes++;
            Press(widget, Key.F5);
            Check(widgetRefreshes == 1, "F5 in the widget did not refresh.");
            widget.SetRefreshing(true);
            Press(widget, Key.F5);
            Check(widgetRefreshes == 1, "F5 in the widget started a second refresh.");
        }
        finally { flyout.Close(); widget.Close(); accounts.Close(); }

        using var first = new GlobalHotkey();
        using var second = new GlobalHotkey();
        Check(first.Apply(false) && !first.IsRegistered, "A disabled global shortcut registered anyway.");
        if (first.Apply(true))
        {
            Check(first.IsRegistered && !second.Apply(true), "A shortcut already in use was reported as registered.");
            Check(first.Apply(false) && !first.IsRegistered && second.Apply(true), "Turning the shortcut off did not release it.");
            second.Apply(false);
            Console.WriteLine("PASS: shortcuts and live status; F5/Ctrl+R refresh without doubling, Ctrl + wheel zoom, global shortcut register/conflict/release.");
        }
        else
        {
            // Another program on this machine owns the combination: the failure path is what ran.
            Check(!first.IsRegistered, "A failed registration claims to be registered.");
            Console.WriteLine($"PASS: shortcuts and live status; F5/Ctrl+R refresh without doubling, Ctrl + wheel zoom. {GlobalHotkey.DisplayText} is owned by another program here, so only its conflict path ran.");
        }
    }

    // With no account, the popup's one action adds an account and the hint explains the hidden
    // tray icon area; with accounts, the same button manages them.
    public static void RunEmptyState()
    {
        var previous = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            {
                UiText.SetLanguage(language);
                var flyout = new FlyoutWindow();
                try
                {
                    flyout.BindAccounts([], "", false);
                    var button = (System.Windows.Controls.Button)flyout.FindName("ManageAccountsButton");
                    var hint = (System.Windows.Controls.TextBlock)flyout.FindName("AccountSelectionHint");
                    var requested = 0;
                    flyout.AccountsRequested += () => requested++;
                    Check((string)button.Content == UiText.T("Add an account", "계정 추가")
                        && ReferenceEquals(button.Style, flyout.FindResource("PrimaryButton"))
                        && hint.Visibility == Visibility.Visible && hint.Text.Contains("(^)", StringComparison.Ordinal),
                        "The empty popup does not lead with Add an account and the hidden-icons hint.");
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Check(requested == 1, "Add an account did not open account management.");
                }
                finally { flyout.Close(); }
            }
        }
        finally { UiText.SetLanguage(previous); }
        Console.WriteLine("PASS: empty popup leads with Add an account and the hidden tray icon hint in EN/KO.");
    }

    private static void Press(Window window, Key key) =>
        window.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        { RoutedEvent = UIElement.PreviewKeyDownEvent });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
