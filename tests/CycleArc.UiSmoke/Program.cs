using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
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
        if (args is [ClaudeStatusLineProcessChecks.PreviousStatusLineChildArgument])
            return ClaudeStatusLineProcessChecks.RunPreviousStatusLineChild();
        if (args is ["--update-package", var releaseDirectory])
        {
            PackageUpdateChecks.Run(releaseDirectory);
            ManagedApplyChecks.Run(releaseDirectory);
            return 0;
        }
        if (args is ["--desktop-instance-child", var key, var reportPath])
            return DesktopInstanceProcessChecks.RunChildGuarded(key, reportPath);
        if (args is ["--desktop-instance"])
        {
            try
            {
                DesktopInstanceProcessChecks.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
        if (args is ["--shutdown-child", var scenario]) return ShutdownChecks.RunChild(scenario);
        if (args is ["--shutdown"]) { ShutdownChecks.Run(); return 0; }
        if (args is ["--claude-uninstall-seed", var seedRoot, var seedConfig, var seedCallback, var seedPath])
        {
            ClaudeUninstallCleanupChecks.Seed(seedRoot, seedConfig, seedCallback, seedPath);
            return 0;
        }
        if (args is ["--claude-uninstall-installed", var installedRoot, var installedSeed, var installedInstallation])
        {
            ClaudeUninstallCleanupChecks.VerifyInstalled(installedRoot, installedSeed, installedInstallation);
            ClaudeUninstallCleanupChecks.VerifyCallbackRuns(installedRoot, installedSeed);
            return 0;
        }
        if (args is ["--claude-uninstall-removed", var removedRoot, var removedSeed, var removedInstallation])
        {
            ClaudeUninstallCleanupChecks.VerifyRemoved(removedRoot, removedSeed, removedInstallation);
            ClaudeUninstallCleanupChecks.VerifyRestoredCallbackRuns(removedRoot, removedSeed);
            return 0;
        }
        if (args is ["--claude-process", var executable])
        {
            ClaudeStatusLineProcessChecks.Run(executable);
            return 0;
        }
        if (args is ["--live-accounts", "read" or "login" or "relogin"])
            return LiveAccountChecks.RunAsync(args[1]).GetAwaiter().GetResult();
        if (args is ["--cursor-live-read"])
            return CursorLiveChecks.RunAsync().GetAwaiter().GetResult();
        // Process/IPC checks run from --desktop-instance immediately after compile.
        // Empty-args UiSmoke keeps WPF/version UI checks and does not repeat that wait.
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
            if (args is ["--desktop-version-ui"])
            {
                DesktopInstanceProcessChecks.RunUiChecks();
                return 0;
            }
            if (args is ["--updates"]) { UpdateUiChecks.Run(); return 0; }
            if (args.Length == 0) DesktopInstanceProcessChecks.RunUiChecks();
            if (args is ["--tray-icons", var trayDirectory])
            {
                TrayIconChecks.Run(trayDirectory);
                return 0;
            }
            if (args is ["--widget-accounts", var widgetAccountsDirectory])
            {
                WidgetMultiAccountChecks.Run(widgetAccountsDirectory);
                WidgetLayoutChecks.Run(widgetAccountsDirectory);
                return 0;
            }
            if (args is ["--widget-layout", var widgetLayoutDirectory])
            {
                WidgetLayoutChecks.Run(widgetLayoutDirectory);
                return 0;
            }
            if (args is ["--settings-window"] or ["--settings-window", _])
            {
                SettingsWindowChecks.Run(args.Length == 2 ? args[1] : null);
                return 0;
            }
            if (args is ["--widget-zoom"] or ["--widget-zoom", _])
            {
                WidgetZoomChecks.Run(args.Length == 2 ? args[1] : null);
                return 0;
            }
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
            if (args is ["--claude-live-screenshots", var claudeLiveDirectory])
            {
                DocumentationScreenshots.Export(claudeLiveDirectory, claudeLiveOnly: true);
                return 0;
            }
            if (args is ["--widget-recovery", var recoveryDirectory])
            {
                WidgetRecoveryChecks.Run(recoveryDirectory);
                return 0;
            }
            if (args is ["--usage-period-screenshots", var periodScreenshots])
            {
                DocumentationScreenshots.Export(periodScreenshots, usagePeriodOnly: true);
                return 0;
            }
            if (args is ["--screenshots", var directory])
            {
                DocumentationScreenshots.Export(directory);
                return 0;
            }
            if (args is ["--claude-desktop-screenshots", var desktopDirectory])
            {
                DocumentationScreenshots.Export(desktopDirectory, claudeOnly: true);
                return 0;
            }
            if (args is ["--usage-periods", var periodDirectory])
            {
                UsagePeriodUiChecks.Run(app, periodDirectory);
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
            if (args is ["--cursor-ui", var cursorDirectory])
            {
                CursorUiChecks.Run(cursorDirectory);
                return 0;
            }
            if (args is ["--flyout-activation"] or ["--flyout-activation", _])
            {
                FlyoutActivationChecks.Run(app, args.Length == 2 ? args[1] : null);
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
            ShutdownChecks.Run();
            UpdateUiChecks.Run();
            ClaudeStatusLineProcessChecks.Run();
            AccountUiChecks.Run();
            CodexWindowUiChecks.Run();
            CursorUiChecks.Run();
            UsagePeriodUiChecks.Run(app);
            MixedProviderUiChecks.Run();
            ToolTipUiChecks.Run();
            FlyoutActivationChecks.Run(app);
            CheckEnvironmentCallbacks(app);
            CheckWidgetRecovery();
            CheckWidgetRestart();
            WidgetRecoveryChecks.Run();
            WidgetMultiAccountChecks.Run();
            WidgetLayoutChecks.Run();
            WidgetZoomChecks.Run();
            SettingsWindowChecks.Run();
            TrayIconChecks.Run();
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
                WidgetFixture.BindSnapshot(widget, snapshot);
                foreach (var status in Enum.GetValues<CodexQuotaStatus>())
                {
                    WidgetFixture.BindSnapshot(widget, snapshot with { Status = status });
                    var notice = WidgetFixture.Module(widget).StatusText;
                    var attention = status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing);
                    if (notice.Visibility != (attention ? Visibility.Visible : Visibility.Collapsed)
                        || string.IsNullOrEmpty(notice.Text) == attention)
                        throw new InvalidOperationException($"Incorrect widget notice for {status}.");
                }
                WidgetFixture.BindSnapshot(widget, snapshot); // Recovery must remove the old failure text and its space.
                var recovered = WidgetFixture.Module(widget).StatusText;
                if (recovered.Visibility != Visibility.Collapsed || recovered.Text.Length != 0)
                    throw new InvalidOperationException("Widget notice remains after recovery.");
                CheckWidgetAccountBinding(widget, now);
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
                    // The window's own declared size, so this preview follows it.
                    var previewSize = new Size(window.Width, window.Height);
                    content.Measure(previewSize);
                    content.Arrange(new Rect(new Point(), previewSize));
                    scroller.ScrollToTop();
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(previewSize.Width),
                        (int)Math.Ceiling(previewSize.Height), 96, 96, PixelFormats.Pbgra32);
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
        var header = (FrameworkElement)widget.FindName("WidgetHeader");
        if (header.ActualHeight <= 0 || header.ActualWidth <= 0)
            throw new InvalidOperationException("Widget header did not render.");
        var module = WidgetFixture.Module(widget);
        System.Windows.Controls.TextBlock[] texts =
            [(System.Windows.Controls.TextBlock)widget.FindName("ProductTitle"), module.NameText,
             module.RingValueText, module.Periods[0].RemainingText, module.Periods[0].ResetText];
        foreach (var text in texts)
        {
            if (!text.UseLayoutRounding || !text.SnapsToDevicePixels
                || TextOptions.GetTextFormattingMode(text) != TextFormattingMode.Display)
                throw new InvalidOperationException("Widget text must use pixel-aligned display formatting.");
        }
        if (module.TranslatePoint(new Point(), content).Y
            < header.TranslatePoint(new Point(0, header.ActualHeight), content).Y - 0.01)
            throw new InvalidOperationException("The account module overlaps the single widget header.");
    }

    // Accounts, their order, their periods and the click-to-select contract, without a native window.
    private static void CheckWidgetAccountBinding(FloatingWidget widget, DateTimeOffset now)
    {
        var both = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now, null, null, null,
        [
            new("five", 85, CodexWindowClassifier.FiveHourMinutes, now.AddMinutes(35), CodexWindowKind.FiveHour),
            new("week", 23, CodexWindowClassifier.WeeklyMinutes, now.AddHours(12).AddMinutes(45), CodexWindowKind.Weekly)
        ], null);
        var weeklyOnly = both with { Windows = [both.Windows[1]] };
        CodexAccountView[] accounts =
        [
            WidgetFixture.Synthetic("w-main", "Main", both),
            WidgetFixture.Synthetic("w-kakao", "Kakao", weeklyOnly),
            WidgetFixture.Synthetic("w-claude", "Work Claude", both with { Provider = UsageProviderId.Claude })
        ];
        widget.BindAccounts(accounts, "w-kakao", UsagePeriodPreference.Auto, WidgetFixture.Desktop);
        if (widget.Modules.Count != 3) throw new InvalidOperationException("The widget dropped an account module.");
        if (widget.Modules.Select(m => m.ProfileId).ToArray() is not ["w-main", "w-kakao", "w-claude"])
            throw new InvalidOperationException("The widget reordered the managed account order.");
        if (widget.Modules[0].Periods.Count != 2 || widget.Modules[1].Periods.Count != 1
            || widget.Modules[2].Periods.Count != 2)
            throw new InvalidOperationException("The widget hid a provided period or invented a missing one.");
        if (widget.Modules[0].Periods[0].ResetText.Text == widget.Modules[0].Periods[1].ResetText.Text)
            throw new InvalidOperationException("Both periods share one reset countdown.");
        if (widget.LastLayout is not { Columns: 3, Rows: 1 })
            throw new InvalidOperationException($"Three accounts did not share one row: {widget.LastLayout}.");

        // Removing an account must drop its module rather than leave a stale one behind.
        widget.BindAccounts(accounts.Take(1).ToArray(), "w-main", UsagePeriodPreference.Auto, WidgetFixture.Desktop);
        if (widget.Modules.Count != 1 || widget.LastLayout is not { Columns: 1, Rows: 1 })
            throw new InvalidOperationException("A removed account still occupies the widget.");
        widget.BindAccounts(accounts, "w-kakao", UsagePeriodPreference.Auto, WidgetFixture.Desktop);

        // A narrow monitor wraps by module instead of shrinking the row.
        widget.BindAccounts(accounts, "w-kakao", UsagePeriodPreference.Auto, [new ScreenRect(0, 0, 560, 1040)]);
        if (widget.LastLayout is not { Columns: 2, Rows: 2 })
            throw new InvalidOperationException($"A narrow work area did not wrap: {widget.LastLayout}.");
        widget.BindAccounts(accounts, "w-kakao", UsagePeriodPreference.Auto, WidgetFixture.Desktop);

        var selections = new List<string>();
        var opened = 0;
        void OnSelected(string id) => selections.Add(id);
        void OnOpened() => opened++;
        widget.AccountSelected += OnSelected;
        widget.FlyoutRequested += OnOpened;
        try
        {
            var drag = typeof(FloatingWidget).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pressed = typeof(FloatingWidget).GetField("_pressedProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var finish = typeof(FloatingWidget).GetMethod("FinishDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // A click on a module selects that account and opens the existing detail popup.
            drag.SetValue(widget, new WidgetDragSession(10, 10, 10, 10));
            pressed.SetValue(widget, "w-claude");
            finish.Invoke(widget, [true]);
            if (selections is not ["w-claude"] || opened != 1)
                throw new InvalidOperationException("A module click did not select its account and open the detail.");

            // A finished drag moves the widget and must not open anything.
            var moved = new WidgetDragSession(10, 10, 10, 10);
            moved.Move(200, 200);
            drag.SetValue(widget, moved);
            pressed.SetValue(widget, "w-main");
            finish.Invoke(widget, [true]);
            if (selections.Count != 1 || opened != 1)
                throw new InvalidOperationException("A drag selected an account or opened the detail.");

            // The header's own buttons raise their own actions and never select or drag.
            var hidden = 0;
            var settings = 0;
            widget.CloseRequested += () => hidden++;
            widget.SettingsRequested += () => settings++;
            ((System.Windows.Controls.Button)widget.FindName("WidgetCloseButton"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ((System.Windows.Controls.Button)widget.FindName("WidgetSettingsButton"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (hidden != 1 || settings != 1 || selections.Count != 1 || opened != 1)
                throw new InvalidOperationException("A header button leaked into selection, drag or the detail popup.");
        }
        finally
        {
            widget.AccountSelected -= OnSelected;
            widget.FlyoutRequested -= OnOpened;
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
            // Inset from the secondary origin so the current arranged widget still fits.
            // Right-178 was the old single-account width and overflowed a module-sized panel.
            settings.WidgetPixelLeft = secondary.WorkingArea.Left + 80;
            settings.WidgetPixelTop = secondary.WorkingArea.Top + 80;
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
