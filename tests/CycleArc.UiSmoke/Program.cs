using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class Program
{
    // Pumping WPF for native-window tests must never run production startup/account access.
    private sealed class OfflineApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--claude-process", var executable])
        {
            ClaudeStatusLineProcessChecks.Run(executable);
            return 0;
        }
        if (args is ["--live-accounts", "read" or "login" or "relogin"])
            return LiveAccountChecks.RunAsync(args[1]).GetAwaiter().GetResult();
        // Load production WPF views/resources with startup overridden: no account access,
        // settings writes, tray registration or background refresh occurs.
        var app = new OfflineApp();
        try
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/CycleArc;component/UI/Themes.xaml", UriKind.Relative)
            });
            if (args is ["--widget-dpi", var dpiDirectory])
            {
                WidgetDpiChecks.Run(dpiDirectory);
                return 0;
            }
            if (args is ["--claude-usage-screenshots", var claudeUsageDirectory])
            {
                DocumentationScreenshots.Export(claudeUsageDirectory, claudeUsageOnly: true);
                return 0;
            }
            if (args is ["--widget-recovery", var recoveryDirectory])
            {
                WidgetRecoveryChecks.Run(recoveryDirectory);
                return 0;
            }
            if (args is ["--screenshots", var directory])
            {
                DocumentationScreenshots.Export(directory);
                return 0;
            }
            if (args is ["--codex-windows", var codexWindowsDirectory])
            {
                CodexWindowUiChecks.Run(codexWindowsDirectory);
                return 0;
            }
            if (args is ["--accounts", var accountsDirectory])
            {
                AccountUiChecks.Run(accountsDirectory);
                return 0;
            }
            if (args is ["--tooltips", var toolTipDirectory])
            {
                ToolTipUiChecks.Run(toolTipDirectory);
                return 0;
            }
            if (args is ["--claude-ui", var claudeDirectory])
            {
                MixedProviderUiChecks.Run(claudeDirectory);
                return 0;
            }
            ClaudeStatusLineProcessChecks.Run();
            AccountUiChecks.Run();
            CodexWindowUiChecks.Run();
            MixedProviderUiChecks.Run();
            ToolTipUiChecks.Run();
            CheckEnvironmentCallbacks(app);
            CheckWidgetRecovery();
            CheckWidgetRestart();
            WidgetRecoveryChecks.Run();
            WidgetDpiChecks.Run();
            CheckPositionReset();
            var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("App.ApplyTheme");
            var count = 0;
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                CheckRefreshSettings(app, $"{language}-{theme}");
                var flyout = new FlyoutWindow();
                var widget = new FloatingWidget();
                var now = DateTimeOffset.Now;
                var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-3), now,
                    null, null, 3, [new CodexQuotaWindow("codex", 28, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
                CheckCreditUse(flyout, snapshot);
                flyout.Bind(snapshot);
                CheckZoomShortcuts(flyout);
                widget.Bind(snapshot);
                var notice = (System.Windows.Controls.TextBlock)widget.FindName("HistoryValue");
                foreach (var status in Enum.GetValues<CodexQuotaStatus>())
                {
                    widget.Bind(snapshot with { Status = status });
                    var attention = status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing);
                    if (notice.Visibility != (attention ? Visibility.Visible : Visibility.Collapsed)
                        || string.IsNullOrEmpty(notice.Text) == attention)
                        throw new InvalidOperationException($"Incorrect widget notice for {status}.");
                }
                widget.Bind(snapshot); // Recovery must remove the old failure text and its space.
                if (notice.Visibility != Visibility.Collapsed || notice.Text.Length != 0)
                    throw new InvalidOperationException("Widget notice remains after recovery.");
                Window[] windows = [flyout, widget,
                    new SettingsWindow(AppSettings.CreateDefaults()), new AboutWindow("1.0.0", "synthetic")];
                foreach (var window in windows)
                {
                    try
                    {
                        foreach (var zoom in window is FlyoutWindow ? new[] { 80, 100, 150 } : new[] { 100 })
                        {
                            if (window is FlyoutWindow zoomWindow)
                            {
                                zoomWindow.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                                zoomWindow.Bind(snapshot);
                                if (zoomWindow.ZoomPercent != zoom)
                                    throw new InvalidOperationException("Refresh reset the popup zoom.");
                            }
                            var content = (FrameworkElement)window.Content;
                            content.Measure(new Size(double.IsFinite(window.Width) ? window.Width : 900, 900));
                            content.Arrange(new Rect(new Point(), content.DesiredSize));
                            content.UpdateLayout();
                            if (window is FloatingWidget compactWidget)
                                CheckWidgetTextLayout(compactWidget, content);
                            if (window is FlyoutWindow)
                            {
                                var rows = (System.Windows.Controls.ItemsControl)window.FindName("CodexRows");
                                foreach (System.Windows.Controls.Border row in rows.Items)
                                {
                                    var grid = (System.Windows.Controls.Grid)row.Child;
                                    var label = (FrameworkElement)grid.Children[0];
                                    var value = (FrameworkElement)grid.Children[1];
                                    var labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), grid).X;
                                    var valueLeft = value.TranslatePoint(new Point(), grid).X;
                                    if (valueLeft < labelRight || valueLeft + value.ActualWidth > grid.ActualWidth + 1)
                                        throw new InvalidOperationException("Quota row text overlaps or overflows.");
                                }
                            }
                            if (content.ActualWidth <= 0 || content.ActualHeight <= 0)
                                throw new InvalidOperationException($"Empty layout: {window.GetType().Name}");
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width),
                                (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(content);
                            count++;
                        }
                    }
                    finally { window.Close(); }
                }
            }
            Console.WriteLine($"PASS: {count} production WPF resource/layout renders across languages and themes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void CheckRefreshSettings(App app, string previewName)
    {
        var settings = new AppSettings();
        var cancel = new SettingsWindow(settings);
        var choice = (System.Windows.Controls.ComboBox)cancel.FindName("RefreshIntervalBox");
        if (choice.SelectedIndex != 2 || choice.Items.Count != 6)
            throw new InvalidOperationException("Refresh choices/default are incorrect.");
        choice.SelectedIndex = 0;
        ((System.Windows.Controls.Button)cancel.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (settings.CodexRefreshIntervalMinutes != 5) throw new InvalidOperationException("Cancel changed the refresh schedule.");
        foreach (var minutes in AppSettings.CodexRefreshIntervals)
        {
            var window = new SettingsWindow(settings);
            try
            {
                ((System.Windows.Controls.TabItem)window.FindName("ConnectionTab")).IsSelected = true;
                var box = (System.Windows.Controls.ComboBox)window.FindName("RefreshIntervalBox");
                box.SelectedIndex = AppSettings.CodexRefreshIntervals.ToList().IndexOf(minutes);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(580, 480));
                content.Arrange(new Rect(0, 0, 580, 480));
                content.UpdateLayout();
                var scroller = (System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)window.FindName("ConnectionTab")).Content;
                scroller.ScrollToEnd();
                content.UpdateLayout();
                var bounds = box.TransformToAncestor(scroller).TransformBounds(new Rect(box.RenderSize));
                if (bounds.Top < 0 || bounds.Bottom > scroller.ActualHeight || box.ActualWidth < 170 || box.ActualHeight < 30)
                    throw new InvalidOperationException("Refresh selector is hidden or clipped.");
                if (minutes == 5)
                {
                    content.Measure(new Size(640, 590));
                    content.Arrange(new Rect(0, 0, 640, 590));
                    scroller.ScrollToTop();
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(640, 590, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    System.IO.Directory.CreateDirectory("artifacts/refresh-settings");
                    using var stream = System.IO.File.Create($"artifacts/refresh-settings/{previewName}.png");
                    encoder.Save(stream);
                }
                var saved = false;
                window.Saved += value => saved = value.CodexRefreshIntervalMinutes == minutes;
                ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (!saved || settings.CodexRefreshIntervalMinutes != minutes)
                    throw new InvalidOperationException("Save lost the selected refresh interval.");
                typeof(App).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, settings);
                typeof(App).GetMethod("ApplyRefreshSchedule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(App).GetField("_codexTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
                if (!timer.IsEnabled || timer.Interval != TimeSpan.FromMinutes(minutes))
                    throw new InvalidOperationException("Schedule did not apply to the running timer.");
                timer.Stop();
            }
            finally { window.Close(); }
        }
    }
    private static void CheckWidgetTextLayout(FloatingWidget widget, FrameworkElement content)
    {
        var stack = (FrameworkElement)widget.FindName("WidgetStatusStack");
        var top = stack.TranslatePoint(new Point(), content).Y;
        var bottom = content.ActualHeight - top - stack.ActualHeight;
        if (Math.Abs(top - bottom) > 1)
            throw new InvalidOperationException($"Widget status stack is not vertically centered: {top}/{bottom}.");
        foreach (var name in new[] { "ProductTitle", "CodexLabel", "CodexValue" })
        {
            var text = (System.Windows.Controls.TextBlock)widget.FindName(name);
            if (!text.UseLayoutRounding || !text.SnapsToDevicePixels
                || TextOptions.GetTextFormattingMode(text) != TextFormattingMode.Display)
                throw new InvalidOperationException("Widget text must use pixel-aligned display formatting.");
        }
    }

    private static void CheckZoomShortcuts(FlyoutWindow flyout)
    {
        var changes = new List<int>();
        flyout.ZoomChanged += changes.Add;
        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
        if (changes.Count != 0 || flyout.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.None)
            || flyout.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt)
            || flyout.TryHandleZoomShortcut(Key.A, ModifierKeys.Control))
            throw new InvalidOperationException("Unexpected zoom shortcut handling.");
        foreach (var (key, modifiers, expected) in new[]
        {
            (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, 110),
            (Key.Add, ModifierKeys.Control, 120),
            (Key.OemMinus, ModifierKeys.Control, 110),
            (Key.Subtract, ModifierKeys.Control, 100),
            (Key.OemPlus, ModifierKeys.Control, 110),
            (Key.D0, ModifierKeys.Control, 100),
            (Key.NumPad0, ModifierKeys.Control, 100)
        })
        {
            if (!flyout.TryHandleZoomShortcut(key, modifiers) || flyout.ZoomPercent != expected)
                throw new InvalidOperationException($"Zoom shortcut failed: {key}.");
        }
        if (changes.Count != 6) throw new InvalidOperationException("Zoom saves must occur only on changes.");
        flyout.ZoomChanged -= changes.Add;
    }

    private static void CheckPositionReset()
    {
        var settings = new AppSettings { WidgetLeft = 777, WidgetTop = 888 };
        var window = new SettingsWindow(settings);
        try
        {
            var button = (System.Windows.Controls.Button)window.FindName("ResetWidgetPositionButton");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (!window.ResetWidgetPositionOnSave || settings.WidgetLeft != 777 || settings.WidgetTop != 888)
                throw new InvalidOperationException("Position reset must remain pending until Save.");
        }
        finally { window.Close(); }
    }

    private static void CheckWidgetRestart()
    {
        var settings = new AppSettings { WidgetLeft = 9000, WidgetTop = 9000,
            WidgetPixelLeft = 100, WidgetPixelTop = 100, WidgetOpacity = 0.3 };
        var secondary = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => !x.Primary);
        if (secondary is not null)
        {
            // First calibrate the actual target-monitor size; the next instance tests its bottom edge.
            settings.WidgetPixelLeft = secondary.WorkingArea.Right - 178;
            settings.WidgetPixelTop = secondary.WorkingArea.Bottom - 100;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var widget = new FloatingWidget { ShowActivated = false };
            try
            {
                var saved = 0;
                widget.Moved += (_, _) => saved++;
                widget.Apply(settings);
                widget.RecoverPosition();
                if (saved != 0) throw new InvalidOperationException("Startup overwrote widget position before layout/DPI initialization.");
                widget.Show();
                PumpDispatcher(widget.Dispatcher);
                // ContentRendered is dispatched at input priority; pump a bounded second pass.
                PumpDispatcher(widget.Dispatcher);
                if (widget.PixelPosition != (settings.WidgetPixelLeft!.Value, settings.WidgetPixelTop!.Value))
                    throw new InvalidOperationException($"Widget physical position changed on restart: {widget.PixelPosition}.");
                if (secondary is not null && attempt == 0)
                    settings.WidgetPixelTop = secondary.WorkingArea.Bottom
                        - (int)Math.Ceiling(widget.ActualHeight * VisualTreeHelper.GetDpi(widget).DpiScaleY) - 5;
            }
            finally { widget.Close(); }
        }
    }

    private static void CheckCreditUse(FlyoutWindow flyout, CodexQuotaSnapshot snapshot)
    {
        // Offline only: synthetic identity and injected handler. No App.OnStartup or Codex client.
        var credit = new CodexResetCredit("synthetic-ui-credit", DateTimeOffset.Now.AddDays(2));
        snapshot = snapshot with { ResetCreditsAvailable = 1, RedeemableCredits = [credit] };
        var confirmation = typeof(FlyoutWindow).GetProperty("ConfirmCreditForTest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var method = typeof(FlyoutWindow).GetMethod("UseCreditAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var row = CodexCreditCard.From(snapshot, DateTimeOffset.Now).Rows.Single();
        var calls = 0;
        flyout.RedeemCredit = id => { calls++; return Task.FromResult(CreditRedemptionOutcome.Reset); };
        confirmation.SetValue(flyout, (Func<string, bool>)(_ => false));
        flyout.Bind(snapshot);
        ((Task)method.Invoke(flyout, [row])!).GetAwaiter().GetResult();
        if (calls != 0) throw new InvalidOperationException("Cancelled confirmation consumed a credit.");

        var pending = new TaskCompletionSource<CreditRedemptionOutcome>();
        flyout.RedeemCredit = id =>
        {
            if (id != credit.Id) throw new InvalidOperationException("Wrong credit selected.");
            calls++;
            return pending.Task;
        };
        confirmation.SetValue(flyout, (Func<string, bool>)(_ => true));
        var first = (Task)method.Invoke(flyout, [row])!;
        var duplicate = (Task)method.Invoke(flyout, [row])!;
        flyout.Bind(snapshot); // Timer/refresh binding must retain busy state.
        var rows = (System.Windows.Controls.ItemsControl)flyout.FindName("CreditExpiryRows");
        var grid = (System.Windows.Controls.Grid)((System.Windows.Controls.Border)rows.Items[0]).Child;
        var button = (System.Windows.Controls.Button)grid.Children[2];
        if (button.IsEnabled || calls != 1 || !duplicate.IsCompleted)
            throw new InvalidOperationException("Credit use is not single-flight.");
        pending.SetResult(CreditRedemptionOutcome.Reset);
        PumpDispatcher(flyout.Dispatcher);
        first.GetAwaiter().GetResult();
        flyout.Bind(snapshot with { Status = CodexQuotaStatus.Stale });
        ((Task)method.Invoke(flyout, [row])!).GetAwaiter().GetResult();
        if (calls != 1) throw new InvalidOperationException("Stale data allowed credit use.");
        confirmation.SetValue(flyout, null);
        flyout.RedeemCredit = null;
    }
    private static void CheckWidgetRecovery()
    {
        var widget = new FloatingWidget { Left = 9000, Top = 9000 };
        try
        {
            var changes = 0;
            widget.Moved += (left, top) =>
            {
                changes++;
                if (left != 40 || top != 40) throw new InvalidOperationException("Incorrect recovered coordinates.");
            };
            widget.RecoverPosition([new ScreenRect(0, 0, 1920, 1040)]);
            widget.RecoverPosition([new ScreenRect(0, 0, 1920, 1040)]);
            if (widget.Left != 40 || widget.Top != 40 || changes != 1)
                throw new InvalidOperationException("Widget recovery must persist one position change.");
        }
        finally { widget.Close(); }
    }

    private static void CheckEnvironmentCallbacks(App app)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckDispatcherCallbacks(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dispatcher test timed out.");
        if (failure is not null) throw new InvalidOperationException("Dispatcher test failed.", failure);

        var settingsField = typeof(App).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settings = (AppSettings)settingsField.GetValue(app)!;
        var themeChanged = typeof(App).GetMethod("OnSystemThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            settings.Theme = theme;
            var sentinel = new SolidColorBrush(Colors.Magenta);
            app.Resources["TextBrush"] = sentinel;
            themeChanged.Invoke(app, null);
            var replaced = !ReferenceEquals(sentinel, app.Resources["TextBrush"]);
            if (replaced != (theme == AppTheme.System)) throw new InvalidOperationException("System change overrode a fixed theme.");
        }
    }

    private static void CheckDispatcherCallbacks()
    {
        // Pump a separate STA dispatcher so production App startup never runs.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var owner = Environment.CurrentManagedThreadId;
        var themeCalls = 0;
        var displayCalls = 0;
        var restoredCalls = 0;
        void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != owner)
                throw new InvalidOperationException("Desktop event escaped the WPF dispatcher.");
        }
        using var monitor = new DesktopEnvironmentMonitor(dispatcher,
            () => { CheckThread(); themeCalls++; }, () => { CheckThread(); displayCalls++; }, false,
            () => { CheckThread(); restoredCalls++; });
        Task.Run(() => { monitor.NotifyThemeChanged(); monitor.NotifyDisplayChanged(); monitor.NotifyDesktopRestored(); }).GetAwaiter().GetResult();
        if (themeCalls != 0 || displayCalls != 0 || restoredCalls != 0) throw new InvalidOperationException("Desktop event ran on a worker.");
        PumpDispatcher(dispatcher);
        if (themeCalls != 1 || displayCalls != 1 || restoredCalls != 1) throw new InvalidOperationException("Desktop event was lost.");
        monitor.NotifyThemeChanged();
        monitor.NotifyDisplayChanged();
        monitor.NotifyDesktopRestored();
        monitor.Dispose();
        monitor.NotifyThemeChanged();
        monitor.NotifyDesktopRestored();
        PumpDispatcher(dispatcher);
        if (themeCalls != 1 || displayCalls != 1 || restoredCalls != 1) throw new InvalidOperationException("Disposed callbacks executed.");

        dispatcher.InvokeShutdown();
    }

    private static void PumpDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        dispatcher.BeginInvoke(() => frame.Continue = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

}
