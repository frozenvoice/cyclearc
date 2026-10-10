using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;
using Control = System.Windows.Controls.Control;

namespace CycleArc.UiSmoke;

// Reuse must never change what is shown: unchanged rows, avatars and tray icons keep their
// objects, while every changed account, selection, order, language and identity is redrawn.
internal static class UiReuseChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    public static void Run(App app)
    {
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            CheckAccountRows(theme => applyTheme.Invoke(null, [theme]), theme);
            CheckDetailRows(theme => applyTheme.Invoke(null, [theme]), theme);
            CheckAccountEditing(theme => applyTheme.Invoke(null, [theme]), theme);
            CheckWidgetAvatars();
            CheckTrayIcons();
            CheckAnimationClocks();
            CheckHiddenPopup(app);
        }
        CheckReopenStress(app);
        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine("PASS: popup rows, avatars and tray icons are reused only for identical output; a hidden popup is rebuilt before it shows, and at once for withdrawn accounts.");
        Console.WriteLine("PASS: popup detail/credit rows and account-management rows are kept for identical output and rewritten for quota, theme, language and busy changes; nickname editing keeps text, focus and caret through usage updates.");
    }

    // Detail and reset-credit rows of an open popup: identical output (including another
    // account's quota change) keeps the elements; any change of the selected account's
    // values, theme, language or busy state rebuilds them, and handlers never accumulate.
    private static void CheckDetailRows(Action<AppTheme> applyTheme, AppTheme theme)
    {
        var flyout = new FlyoutWindow();
        try
        {
            var prompts = 0;
            typeof(FlyoutWindow).GetProperty("ConfirmCreditForTest", PrivateInstance)!
                .SetValue(flyout, (Func<string, bool>)(_ => { prompts++; return false; }));
            flyout.RedeemAccountCredit = (_, _) => Task.FromResult(CreditRedemptionOutcome.Unavailable);
            var accounts = Accounts(3);
            accounts[0] = accounts[0] with { Snapshot = CreditSnapshot(40) };
            var id = accounts[0].Profile.Id;
            flyout.BindAccounts(accounts, id, false);
            var details = flyout.DetailRows.ToArray();
            var credits = CreditRows(flyout);
            Require(details.Length > 0 && credits.Length == 1 && CreditButton(credits[0]).IsEnabled,
                "The popup did not show detail rows and a usable reset credit.");

            for (var bind = 0; bind < 10; bind++) flyout.BindAccounts(Copies(accounts), id, false);
            accounts[1] = accounts[1] with { Snapshot = Snapshot(88) };
            flyout.BindAccounts(accounts, id, false);
            Require(flyout.DetailRows.SequenceEqual(details) && CreditRows(flyout).SequenceEqual(credits),
                "An unchanged selected account rebuilt its detail or credit rows.");
            CreditButton(credits[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(prompts == 1, "Repeated binds accumulated reset-credit handlers.");

            accounts[0] = accounts[0] with { Snapshot = CreditSnapshot(65) };
            flyout.BindAccounts(accounts, id, false);
            var expected = CodexDisplayFormatting.DetailSections(accounts[0].Snapshot,
                CodexRingPresentation.FromDetail(accounts[0].Snapshot, UsagePeriodPreference.Auto).Window);
            Require(!flyout.DetailRows.SequenceEqual(details) && flyout.DetailRows.Count == expected.Primary.Count + expected.Secondary.Count
                && flyout.DetailRows.SelectMany(AccountUiChecks.Descendants<TextBlock>).Any(text =>
                    expected.Primary.Concat(expected.Secondary).Any(row => row.Value == text.Text && text.Text.Length > 0)),
                "A quota change kept the previous detail values.");
            details = flyout.DetailRows.ToArray();
            credits = CreditRows(flyout);

            flyout.BindAccounts(accounts, id, true);
            Require(!CreditRows(flyout).SequenceEqual(credits) && !CreditButton(CreditRows(flyout)[0]).IsEnabled
                && flyout.DetailRows.SequenceEqual(details), "Refreshing did not disable reset use, or rebuilt detail rows.");
            flyout.BindAccounts(accounts, id, false);
            Require(CreditButton(CreditRows(flyout)[0]).IsEnabled, "Reset use stayed disabled after refresh.");
            credits = CreditRows(flyout);

            var language = UiText.Language;
            UiText.SetLanguage(language == UiLanguage.English ? UiLanguage.Korean : UiLanguage.English);
            try
            {
                flyout.BindAccounts(accounts, id, false);
                Require(!flyout.DetailRows.SequenceEqual(details) && !CreditRows(flyout).SequenceEqual(credits)
                    && (string)CreditButton(CreditRows(flyout)[0]).Content == UiText.T("Use reset", "리셋권 사용"),
                    "A language change kept detail or credit text.");
            }
            finally { UiText.SetLanguage(language); }
            flyout.BindAccounts(accounts, id, false);
            details = flyout.DetailRows.ToArray();

            applyTheme(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            try
            {
                flyout.BindAccounts(accounts, id, false);
                var label = (TextBlock)((Grid)flyout.DetailRows[0].Child).Children[0];
                Require(!flyout.DetailRows.SequenceEqual(details)
                    && ReferenceEquals(label.Foreground, Application.Current.Resources["MutedBrush"])
                    && ReferenceEquals(flyout.DetailRows[0].BorderBrush, Application.Current.Resources["LineBrush"]),
                    "Detail rows kept the previous theme's brushes.");
            }
            finally { applyTheme(theme); }
        }
        finally { flyout.Close(); }
    }

    // Account management: quota, selection, other accounts' renames and removal update rows in
    // place, so an open nickname editor keeps its text, focus and caret (and with them an IME
    // composition, which only lives while the same focused editor is untouched).
    private static void CheckAccountEditing(Action<AppTheme> applyTheme, AppTheme theme)
    {
        var window = new AccountsWindow { ShowActivated = true, Left = 40, Top = 40, Width = 700, Height = 800 };
        try
        {
            var renamed = new List<(string Id, string Label)>();
            var removed = new List<string>();
            window.RenameAccount = (id, label) => renamed.Add((id, label));
            window.RemoveAccount = id => { removed.Add(id); return true; };
            var accounts = Accounts(4);
            window.Bind(accounts, accounts[0].Profile.Id);
            window.Show();
            Pump();
            var rows = ManageRows(window);
            var editor = Editor(rows[1]);
            var summaries = rows.Select(row => row.Children.OfType<Button>().Single()).ToArray();
            var contents = summaries.Select(summary => summary.Content).ToArray();
            editor.Focus();
            editor.Text = "Partial 별명";
            editor.Select(2, 5);
            Require(editor.IsFocused, "The nickname editor could not take focus.");

            for (var bind = 0; bind < 10; bind++)
            {
                accounts[1] = accounts[1] with { Snapshot = Snapshot(20 + bind) };
                window.Bind(accounts.ToArray(), accounts[bind % 2].Profile.Id);
            }
            accounts[3] = accounts[3] with { Profile = accounts[3].Profile with { Label = "Delta saved" } };
            window.Bind(accounts.ToArray(), accounts[1].Profile.Id);
            Pump();
            var current = ManageRows(window);
            Require(current.SequenceEqual(rows) && ReferenceEquals(Editor(current[1]), editor)
                && editor.Text == "Partial 별명" && editor.SelectionStart == 2 && editor.SelectionLength == 5 && editor.IsFocused,
                "A usage update interrupted nickname editing.");
            Require(!ReferenceEquals(summaries[1].Content, contents[1]) && ReferenceEquals(summaries[2].Content, contents[2])
                && System.Windows.Automation.AutomationProperties.GetName(summaries[1]).Contains(
                    AccountSummary.QuotaLabel(accounts[1].Snapshot.Windows[0], UsageProviderId.Codex)),
                "The account summary was not updated in place for only the changed account.");
            Require(Editor(current[3]).Text == "Delta saved", "An untouched nickname editor kept the old saved name.");

            for (var bind = 0; bind < 5; bind++) window.Bind(Copies(accounts), accounts[1].Profile.Id);
            ActionButtons(current[1]).Single(button => (string)button.Content == UiText.T("Save name", "별명 저장"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ActionButtons(current[2]).Single(button => (string)button.Content == UiText.T("Remove", "제거"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(renamed.SequenceEqual([(accounts[1].Profile.Id, "Partial 별명")]) && removed.SequenceEqual([accounts[2].Profile.Id]),
                "Repeated binds accumulated or retargeted account actions.");

            var remaining = accounts.Where(account => account.Profile.Id != accounts[2].Profile.Id).ToArray();
            window.Bind(remaining, accounts[1].Profile.Id);
            Require(ManageRows(window).SequenceEqual([rows[0], rows[1], rows[3]]) && ReferenceEquals(Editor(rows[1]), editor)
                && editor.Text == "Partial 별명", "Removing another account rebuilt or reset the remaining rows.");

            applyTheme(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            try
            {
                var identity = rows[0].Children.OfType<TextBlock>().First();
                Require(ReferenceEquals(identity.Foreground, Application.Current.Resources["MutedBrush"]),
                    "A kept account-management row did not follow the live theme.");
            }
            finally { applyTheme(theme); }
        }
        finally { window.Close(); }
    }

    private static StackPanel[] ManageRows(AccountsWindow window) =>
        ((ItemsControl)window.FindName("AccountRows")).Items.Cast<StackPanel>().ToArray();

    private static TextBox Editor(StackPanel row) =>
        row.Children.OfType<DockPanel>().Single().Children.OfType<TextBox>().Single();

    private static Button[] ActionButtons(StackPanel row) =>
        row.Children.OfType<DockPanel>().Single().Children.OfType<Button>().ToArray();

    private static Border[] CreditRows(FlyoutWindow flyout) =>
        ((ItemsControl)flyout.FindName("CreditExpiryRows")).Items.Cast<Border>().ToArray();

    private static Button CreditButton(Border row) => ((Grid)row.Child).Children.OfType<Button>().Single();

    private static CodexQuotaSnapshot CreditSnapshot(double used) => Snapshot(used) with
    {
        ResetCreditsAvailable = 1,
        RedeemableCredits = [new CodexResetCredit("synthetic-credit", Now.AddDays(20))]
    };

    private static void CheckAccountRows(Action<AppTheme> applyTheme, AppTheme theme)
    {
        var flyout = new FlyoutWindow();
        try
        {
            var selections = new List<string>();
            flyout.AccountSelected += selections.Add;
            var accounts = Accounts(5);
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            var rows = Rows(flyout);
            var contents = rows.Select(row => row.Content).ToArray();
            var avatar = ((ContentControl)flyout.FindName("SelectedAvatarHost")).Content;
            Require(rows.Length == 5 && avatar is Border, "The popup did not show five rows and an avatar.");
            for (var bind = 0; bind < 10; bind++)
                flyout.BindAccounts(Copies(accounts), accounts[0].Profile.Id, false);
            Require(Rows(flyout).SequenceEqual(rows) && rows.Select(row => row.Content).SequenceEqual(contents)
                && ReferenceEquals(((ContentControl)flyout.FindName("SelectedAvatarHost")).Content, avatar),
                "An identical rebind replaced a row, its content or the selected avatar.");
            rows[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(selections.SequenceEqual([accounts[2].Profile.Id]), "A reused row raised its selection more or less than once.");

            // One account's quota changes: only its row content is rewritten, in the same row.
            var before = System.Windows.Automation.AutomationProperties.GetName(rows[2]);
            accounts[2] = accounts[2] with { Snapshot = Snapshot(77) };
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            Require(Rows(flyout).SequenceEqual(rows), "A quota change replaced a row.");
            Require(Changed(rows, contents).SequenceEqual([2]), "A quota change rewrote another account's row.");
            Require(System.Windows.Automation.AutomationProperties.GetName(rows[2]) != before
                && System.Windows.Automation.AutomationProperties.GetName(rows[2]).Contains(
                    AccountSummary.QuotaLabel(accounts[2].Snapshot.Windows[0], UsageProviderId.Codex)),
                "The rewritten row does not describe the new quota.");
            contents = rows.Select(row => row.Content).ToArray();

            // Selection moves in place: the two affected rows change their selection look only.
            flyout.BindAccounts(accounts, accounts[3].Profile.Id, false);
            Require(Rows(flyout).SequenceEqual(rows) && Changed(rows, contents).SequenceEqual([0, 3]),
                "A selection change rewrote the wrong rows.");
            Require(rows[0].ReadLocalValue(Control.BorderBrushProperty) == DependencyProperty.UnsetValue
                && rows[0].ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue
                && rows[3].ReadLocalValue(Control.BorderBrushProperty) != DependencyProperty.UnsetValue,
                "The previous selection kept its selected look.");
            Require(!ReferenceEquals(((ContentControl)flyout.FindName("SelectedAvatarHost")).Content, avatar),
                "The selected account changed but its avatar did not.");
            contents = rows.Select(row => row.Content).ToArray();

            // Renaming rewrites the row and its avatar; a new avatar replaces the shown one.
            var shownAvatar = ((ContentControl)flyout.FindName("SelectedAvatarHost")).Content;
            accounts[3] = accounts[3] with { Profile = accounts[3].Profile with { Label = "Zeta renamed" } };
            flyout.BindAccounts(accounts, accounts[3].Profile.Id, false);
            Require(Changed(rows, contents).SequenceEqual([3])
                && System.Windows.Automation.AutomationProperties.GetName(rows[3]).StartsWith("Zeta renamed", StringComparison.Ordinal)
                && !ReferenceEquals(((ContentControl)flyout.FindName("SelectedAvatarHost")).Content, shownAvatar),
                "A rename was not redrawn in its row and avatar.");

            // Order changes move the same rows; removal drops a row, and a returning account gets a new one.
            flyout.BindAccounts(Enumerable.Reverse(accounts).ToArray(), accounts[3].Profile.Id, false);
            Require(Rows(flyout).SequenceEqual(Enumerable.Reverse(rows)), "Reordering did not move the existing rows.");
            flyout.BindAccounts(accounts[..4], accounts[3].Profile.Id, false);
            Require(Rows(flyout).SequenceEqual(rows[..4]), "Removing an account did not remove only its row.");
            flyout.BindAccounts(accounts, accounts[3].Profile.Id, false);
            Require(Rows(flyout)[..4].SequenceEqual(rows[..4]) && !ReferenceEquals(Rows(flyout)[4], rows[4]),
                "A removed row was kept for a returning account.");
            flyout.BindAccounts(accounts[..1], accounts[0].Profile.Id, false);
            Require(Rows(flyout).Length == 0, "A single account still shows the account list.");
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            rows = Rows(flyout);
            contents = rows.Select(row => row.Content).ToArray();

            // Language is part of every row's text, so a language change rewrites every row.
            var language = UiText.Language;
            UiText.SetLanguage(language == UiLanguage.English ? UiLanguage.Korean : UiLanguage.English);
            try
            {
                flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
                Require(Changed(rows, contents).SequenceEqual([0, 1, 2, 3, 4]), "A language change kept old row text.");
            }
            finally { UiText.SetLanguage(language); }
            // Theme brushes are resource references, so kept rows follow a live theme change.
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            applyTheme(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            try
            {
                rows = Rows(flyout);
                contents = rows.Select(row => row.Content).ToArray();
                flyout.BindAccounts(Copies(accounts), accounts[0].Profile.Id, false);
                Require(Rows(flyout).SequenceEqual(rows) && Changed(rows, contents).Length == 0
                    && ReferenceEquals(rows[1].Background, Application.Current.Resources["CardBrush"])
                    && ReferenceEquals(rows[0].BorderBrush, Application.Current.Resources["AccentBrush"]),
                    "A kept row did not follow the live theme.");
            }
            finally { applyTheme(theme); }
            selections.Clear();
            Rows(flyout)[4].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(selections.SequenceEqual([accounts[4].Profile.Id]), "Repeated binds accumulated row selection handlers.");
        }
        finally { flyout.Close(); }
    }

    private static void CheckWidgetAvatars()
    {
        var widget = new FloatingWidget();
        try
        {
            var accounts = Accounts(3);
            widget.BindAccounts(accounts, accounts[0].Profile.Id);
            var avatars = WidgetAvatars(widget);
            widget.BindAccounts(Copies(accounts), accounts[1].Profile.Id);
            Require(WidgetAvatars(widget).SequenceEqual(avatars), "The widget rebuilt unchanged avatars.");
            accounts[1] = accounts[1] with { Profile = accounts[1].Profile with { Label = "Omega" } };
            widget.BindAccounts(accounts, accounts[1].Profile.Id);
            var next = WidgetAvatars(widget);
            Require(ReferenceEquals(next[0], avatars[0]) && !ReferenceEquals(next[1], avatars[1]) && ReferenceEquals(next[2], avatars[2])
                && ((TextBlock)((Border)next[1]).Child).Text == "OM", "The widget did not redraw only the renamed avatar.");
        }
        finally { widget.Close(); }
    }

    // A finished spinner must stop its repeating clock, not only detach it: a detached clock
    // keeps ticking every frame until a garbage collection happens to collect it.
    private static void CheckAnimationClocks()
    {
        var flyout = new FlyoutWindow { ShowActivated = false, Left = 40, Top = 40 };
        var widget = new FloatingWidget { ShowActivated = false, Left = 40, Top = 40 };
        try
        {
            flyout.Show();
            var spinner = typeof(FlyoutWindow).GetField("_spinnerClock", PrivateInstance)!;
            flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(false, true, "refreshing")); Pump();
            var running = (System.Windows.Media.Animation.AnimationClock?)spinner.GetValue(flyout);
            Require(running is { CurrentState: System.Windows.Media.Animation.ClockState.Active } && flyout.RefreshIndicator.IsAnimating,
                "The popup refresh spinner did not run while refreshing.");
            for (var bind = 0; bind < 3; bind++) flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(false, true, "refreshing"));
            Pump();
            Require(ReferenceEquals(spinner.GetValue(flyout), running) && running.CurrentState == System.Windows.Media.Animation.ClockState.Active,
                "Repeating the refreshing state replaced the running spinner clock.");
            flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(true, false, "")); Pump();
            Require(running.CurrentState == System.Windows.Media.Animation.ClockState.Stopped && spinner.GetValue(flyout) is null,
                "The popup refresh spinner kept its clock running after refresh.");
            flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(false, true, "refreshing")); Pump();
            running = (System.Windows.Media.Animation.AnimationClock?)spinner.GetValue(flyout);
            flyout.Hide(); Pump();
            Require(running!.CurrentState == System.Windows.Media.Animation.ClockState.Stopped,
                "Hiding the popup kept its refresh spinner clock running.");
            flyout.Show(); Pump();
            var resumed = (System.Windows.Media.Animation.AnimationClock?)spinner.GetValue(flyout);
            Require(resumed is { CurrentState: System.Windows.Media.Animation.ClockState.Active } && !ReferenceEquals(resumed, running),
                "Showing the still-refreshing popup did not resume exactly one spinner clock.");
            flyout.SetRefreshPresentation(new FlyoutRefreshPresentation(true, false, "")); Pump();
            Require(resumed.CurrentState == System.Windows.Media.Animation.ClockState.Stopped, "The resumed spinner clock kept running.");

            widget.Show();
            var status = typeof(WidgetAccountModuleView).GetField("_statusClock", PrivateInstance)!;
            var accounts = Accounts(2);
            accounts[0] = accounts[0] with { Snapshot = accounts[0].Snapshot with { Status = CodexQuotaStatus.Refreshing } };
            widget.BindAccounts(accounts, accounts[0].Profile.Id);
            Pump();
            widget.BindAccounts(accounts, accounts[0].Profile.Id);
            Pump();
            var active = (System.Windows.Media.Animation.AnimationClock?)status.GetValue(widget.Modules[0]);
            Require(widget.Modules[0].StatusActivityIcon.Visibility != Visibility.Visible
                || active is { CurrentState: System.Windows.Media.Animation.ClockState.Active },
                "The widget activity icon is shown without its rotation.");
            if (active is not null)
            {
                for (var bind = 0; bind < 3; bind++) widget.BindAccounts(Copies(accounts), accounts[0].Profile.Id);
                Pump();
                Require(ReferenceEquals(status.GetValue(widget.Modules[0]), active), "Rebinding the same refreshing state replaced its clock.");
                // A hidden widget is not rebound; its rotation stops and resumes with the window.
                widget.Hide(); Pump();
                Require(active.CurrentState == System.Windows.Media.Animation.ClockState.Stopped && status.GetValue(widget.Modules[0]) is null,
                    "A hidden widget kept its activity rotation running.");
                widget.Show(); Pump();
                active = (System.Windows.Media.Animation.AnimationClock?)status.GetValue(widget.Modules[0]);
                Require(active is { CurrentState: System.Windows.Media.Animation.ClockState.Active },
                    "Showing the widget while refreshing did not resume its rotation.");
            }
            accounts[0] = accounts[0] with { Snapshot = Snapshot(12) };
            widget.BindAccounts(accounts, accounts[0].Profile.Id);
            Pump();
            Require(status.GetValue(widget.Modules[0]) is null
                && (active is null || active.CurrentState == System.Windows.Media.Animation.ClockState.Stopped),
                "The widget activity rotation kept its clock running after refresh.");
        }
        finally { flyout.Close(); widget.Close(); }
    }

    // Lets WPF lay out, render and advance animation clocks for a few frames.
    private static void Pump(int milliseconds = 150)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // A frame timer can win over ApplicationIdle work when rendering is busy. Wait for
    // the queued idle work itself: the equal-priority FIFO barrier follows the App bind.
    private static void DrainDispatcherIdle()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void CheckTrayIcons()
    {
        var available = Snapshot(63);
        var view = TrayIconRenderer.Describe(available, TrayIconStyle.ProgressRing, 32);
        Require(view == TrayIconRenderer.Describe(available with { LastSuccessfulRefresh = Now.AddMinutes(-9) }, TrayIconStyle.ProgressRing, 32),
            "A time-only change would redraw the tray icon.");
        // Same digits, different band: the ring color must still change.
        var normal = TrayIconRenderer.Describe(Snapshot(69.99), TrayIconStyle.ProgressRing, 32);
        var caution = TrayIconRenderer.Describe(Snapshot(70), TrayIconStyle.ProgressRing, 32);
        Require(normal.Text == caution.Text && normal != caution, "A band edge with the same digits kept the old icon.");
        Require(view != TrayIconRenderer.Describe(available, TrayIconStyle.RemainingNumber, 32)
            && view != TrayIconRenderer.Describe(available, TrayIconStyle.ProgressRing, 24)
            && TrayIconRenderer.Describe(available, TrayIconStyle.RemainingNumber, 32)
                != TrayIconRenderer.Describe(available, TrayIconStyle.RemainingNumber, 32, lightTaskbar: true)
            && view != TrayIconRenderer.Describe(available, TrayIconStyle.ProgressRing, 32, claudeAwaitingUsage: true)
            && view != TrayIconRenderer.Describe(available with { Status = CodexQuotaStatus.Stale }, TrayIconStyle.ProgressRing, 32)
            && view != TrayIconRenderer.Describe(Snapshot(64), TrayIconStyle.ProgressRing, 32),
            "A style, size, taskbar, waiting, status or value change kept the old icon.");

        using var tray = new TrayController();
        var current = typeof(TrayController).GetField("_current", PrivateInstance)!;
        var notify = (System.Windows.Forms.NotifyIcon)typeof(TrayController).GetField("_icon", PrivateInstance)!.GetValue(tray)!;
        var accounts = Accounts(2);
        tray.Update(UsageAccountOverview.Create(accounts, accounts[0].Profile.Id), TrayIconStyle.ProgressRing);
        var icon = (System.Drawing.Icon)current.GetValue(tray)!;
        var text = notify.Text;
        accounts[0] = accounts[0] with { Profile = accounts[0].Profile with { Label = "Tooltip only" } };
        tray.Update(UsageAccountOverview.Create(accounts, accounts[0].Profile.Id), TrayIconStyle.ProgressRing);
        Require(ReferenceEquals(current.GetValue(tray), icon) && notify.Text != text && ReferenceEquals(notify.Icon, icon),
            "A tooltip-only change redrew the icon or left the old tooltip.");
        tray.Update(UsageAccountOverview.Create(accounts, accounts[1].Profile.Id), TrayIconStyle.ProgressRing);
        var next = (System.Drawing.Icon)current.GetValue(tray)!;
        Require(!ReferenceEquals(next, icon) && ReferenceEquals(notify.Icon, next) && Disposed(icon),
            "Selecting another value did not set a new icon and release the previous one.");
    }

    private static void CheckHiddenPopup(App app)
    {
        var names = new[] { "_settings", "_settingsStore", "_log", "_tray", "_codex", "_refresh", "_flyout", "_widgetController" };
        var fields = names.Select(name => typeof(App).GetField(name, PrivateInstance)!).ToArray();
        var original = fields.Select(field => field.GetValue(app)).ToArray();
        void Set(string name, object? value) => fields[Array.IndexOf(names, name)].SetValue(app, value);
        T Get<T>(string name) => (T)fields[Array.IndexOf(names, name)].GetValue(app)!;
        void Call(string method) => typeof(App).GetMethod(method, PrivateInstance)!.Invoke(app, null);
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-ui-reuse-" + Guid.NewGuid().ToString("N"));
        var provider = new MutableProvider();
        var profiles = Enumerable.Range(0, 3).Select(index => new CodexAccountProfile(
            (index + 1).ToString("D32"), Path.Combine(root, "home" + index), "Hidden " + (index + 1))).ToArray();
        var store = new CodexAccountStore(root);
        store.Save(new(1, profiles[0].Id, profiles));
        var manager = new CodexAccountManager(store, Path.Combine(root, "default-home"), [provider]);
        var tray = new TrayController();
        try
        {
            var settings = new AppSettings { UsageAlertsEnabled = false, FloatingWidgetEnabled = true, WidgetLeft = 20,
                WidgetTop = 20, FlyoutPositionConfigured = true, FlyoutLeft = 120, FlyoutTop = 80 };
            Set("_settings", settings); Set("_settingsStore", new SettingsStore(Path.Combine(root, "settings.json")));
            Set("_log", new AppLog(Path.Combine(root, "logs"))); Set("_tray", tray); Set("_codex", manager);
            Set("_refresh", manager.Refresh); Set("_flyout", null); Set("_widgetController", null);
            Call("ApplyWidget");
            Call("ToggleFlyout");
            var flyout = Get<FlyoutWindow>("_flyout");
            var widget = Get<FloatingWidgetController>("_widgetController").CurrentWindow!;
            Require(flyout.IsVisible && Rows(flyout).Length == 3, "The first popup show did not bind its accounts.");
            var ring = (TextBlock)flyout.FindName("CodexRingValueText");
            var shown = ring.Text;
            flyout.Hide();
            var rows = Rows(flyout);
            var contents = rows.Select(row => row.Content).ToArray();
            var trayText = ((System.Windows.Forms.NotifyIcon)typeof(TrayController).GetField("_icon", PrivateInstance)!.GetValue(tray)!).Text;

            // Hidden: quota changes reach the tray and widget, not the popup's views.
            provider.Services[profiles[0].Id].Snapshot = Snapshot(81);
            for (var refresh = 0; refresh < 3; refresh++) Call("RefreshSnapshot");
            Require(Rows(flyout).SequenceEqual(rows) && rows.Select(row => row.Content).SequenceEqual(contents) && ring.Text == shown,
                "A hidden popup rebuilt its rows or detail.");
            Require(((System.Windows.Forms.NotifyIcon)typeof(TrayController).GetField("_icon", PrivateInstance)!.GetValue(tray)!).Text != trayText,
                "The tray stopped following quota while the popup was hidden.");
            Require(WidgetFixture.RingValue(widget) == WidgetAccountModel.From(manager.Accounts[0], true).RingRemainingValueText
                && WidgetFixture.RingValue(widget) != WidgetAccountModel.From(manager.Accounts[0] with { Snapshot = Snapshot(31) }, true).RingRemainingValueText,
                "The widget stopped following quota while the popup was hidden.");

            // The first visible frame already has the new value: the bind happens before Show.
            string? firstFrame = null;
            DependencyPropertyChangedEventHandler capture = (_, _) => { if (flyout.IsVisible) firstFrame ??= ring.Text; };
            flyout.IsVisibleChanged += capture;
            Call("ToggleFlyout");
            flyout.IsVisibleChanged -= capture;
            var expected = CodexRingPresentation.FromDetail(Snapshot(81), UsagePeriodPreference.Auto).RemainingValueText;
            Require(firstFrame == expected && ring.Text == expected && Rows(flyout).SequenceEqual(rows)
                && Changed(rows, contents).SequenceEqual([0]), "Reopening did not show the latest value in its first frame.");

            // A minimized popup is still shown, so it keeps following quota and restores in place.
            flyout.WindowState = WindowState.Minimized;
            provider.Services[profiles[0].Id].Snapshot = Snapshot(83);
            Call("RefreshSnapshot");
            Require(ring.Text == CodexRingPresentation.FromDetail(Snapshot(83), UsagePeriodPreference.Auto).RemainingValueText,
                "A minimized popup stopped following quota.");
            Call("ShowMain");
            Require(flyout.IsVisible && flyout.WindowState == WindowState.Normal, "A minimized popup was not restored.");
            flyout.Hide();

            // A different selected account while hidden is bound once at idle, after visible
            // updates, so a later reopen only rebinds in place. Quota alone waits for the show.
            manager.Select(profiles[1].Id);
            Call("RefreshSnapshot");
            Require(flyout.SelectedProfileId == profiles[0].Id, "The hidden selection bind ran before visible updates.");
            var visibleUpdatesRan = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() =>
                {
                    Require(flyout.SelectedProfileId == profiles[0].Id,
                        "The hidden selection bind ran before higher-priority visible updates.");
                    visibleUpdatesRan = true;
                }));
            DrainDispatcherIdle();
            Require(visibleUpdatesRan && !flyout.IsVisible && flyout.SelectedProfileId == profiles[1].Id && Rows(flyout).SequenceEqual(rows),
                $"A hidden popup was not prepared once for the new selection. Selected={flyout.SelectedProfileId}; queued={typeof(App).GetField("_hiddenFlyoutSelectionBindQueued", PrivateInstance)!.GetValue(app)}.");

            // Multiple selections before idle must coalesce into the latest account.
            manager.Select(profiles[0].Id); Call("RefreshSnapshot");
            manager.Select(profiles[2].Id); Call("RefreshSnapshot");
            Require(flyout.SelectedProfileId == profiles[1].Id, "A coalesced hidden selection ran before idle.");
            DrainDispatcherIdle();
            Require(flyout.SelectedProfileId == profiles[2].Id && Rows(flyout).SequenceEqual(rows),
                "The coalesced hidden bind did not prepare the latest selection.");
            manager.Select(profiles[1].Id); Call("RefreshSnapshot"); DrainDispatcherIdle();
            contents = rows.Select(row => row.Content).ToArray();
            provider.Services[profiles[1].Id].Snapshot = Snapshot(61);
            Call("RefreshSnapshot"); DrainDispatcherIdle();
            Require(rows.Select(row => row.Content).SequenceEqual(contents), "A hidden quota change rebuilt popup rows.");
            manager.Select(profiles[0].Id);
            Call("RefreshSnapshot"); DrainDispatcherIdle();

            // Withdrawn data is dropped at once, even while hidden.
            manager.Remove(profiles[2].Id);
            Call("RefreshSnapshot");
            Require(Rows(flyout).Length == 2 && !Rows(flyout).Any(row => Equals(row.Tag, profiles[2].Id)),
                "A hidden popup kept a removed account.");
            provider.Services[profiles[1].Id].Email = "another@example.invalid";
            Call("RefreshSnapshot");
            Require(Rows(flyout).Single(row => Equals(row.Tag, profiles[1].Id)).ToolTip is string tip
                && tip.StartsWith("another@example.invalid", StringComparison.Ordinal),
                "A hidden popup kept another login's email.");
            manager.Select(profiles[1].Id);
            Call("ToggleFlyout");
            var protectedValue = ring.Text;
            flyout.Hide();
            provider.Services[profiles[1].Id].Snapshot = Snapshot(55) with { TechnicalDetail = "codex-identity-mismatch" };
            Call("RefreshSnapshot");
            Require(ring.Text != protectedValue && ring.Text != CodexRingPresentation.FromDetail(Snapshot(55), UsagePeriodPreference.Auto).RemainingValueText,
                "A hidden popup kept quota that identity protection hides.");
        }
        finally
        {
            // Complete callbacks against their own fixture before restoring another App state.
            DrainDispatcherIdle();
            Get<FlyoutWindow?>("_flyout")?.Close();
            Get<FloatingWidgetController?>("_widgetController")?.Dispose();
            tray.Dispose();
            for (var index = 0; index < fields.Length; index++) fields[index].SetValue(app, original[index]);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Repeated popup reopen and account switching through the App paths: every first frame is
    // the selected account, rows are never recreated, replaced tray icons are released and a
    // row click still selects exactly once.
    private static void CheckReopenStress(App app)
    {
        var names = new[] { "_settings", "_settingsStore", "_log", "_tray", "_codex", "_refresh", "_flyout", "_widgetController" };
        var fields = names.Select(name => typeof(App).GetField(name, PrivateInstance)!).ToArray();
        var original = fields.Select(field => field.GetValue(app)).ToArray();
        void Set(string name, object? value) => fields[Array.IndexOf(names, name)].SetValue(app, value);
        void Call(string method) => typeof(App).GetMethod(method, PrivateInstance)!.Invoke(app, null);
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-ui-stress-" + Guid.NewGuid().ToString("N"));
        var provider = new MutableProvider();
        var profiles = Enumerable.Range(0, 5).Select(index => new CodexAccountProfile(
            (index + 1).ToString("D32"), Path.Combine(root, "home" + index), "Stress " + (index + 1))).ToArray();
        var store = new CodexAccountStore(root);
        store.Save(new(1, profiles[0].Id, profiles));
        var manager = new CodexAccountManager(store, Path.Combine(root, "default-home"), [provider]);
        var tray = new TrayController();
        var current = typeof(TrayController).GetField("_current", PrivateInstance)!;
        try
        {
            Set("_settings", new AppSettings { UsageAlertsEnabled = false, FloatingWidgetEnabled = true, WidgetLeft = 20,
                WidgetTop = 20, FlyoutPositionConfigured = true, FlyoutLeft = 120, FlyoutTop = 80 });
            Set("_settingsStore", new SettingsStore(Path.Combine(root, "settings.json")));
            Set("_log", new AppLog(Path.Combine(root, "logs"))); Set("_tray", tray); Set("_codex", manager);
            Set("_refresh", manager.Refresh); Set("_flyout", null); Set("_widgetController", null);
            Call("ApplyWidget");
            Call("ToggleFlyout");
            var flyout = (FlyoutWindow)fields[Array.IndexOf(names, "_flyout")].GetValue(app)!;
            var ring = (TextBlock)flyout.FindName("CodexRingValueText");
            var rows = Rows(flyout);
            var icons = new List<System.Drawing.Icon>();
            void Next()
            {
                manager.Select(profiles[(Array.FindIndex(profiles, profile => profile.Id == manager.SelectedId) + 1) % profiles.Length].Id);
                Call("RefreshSnapshot");
                if (current.GetValue(tray) is System.Drawing.Icon icon && !icons.Contains(icon)) icons.Add(icon);
            }
            for (var cycle = 0; cycle < 40; cycle++)
            {
                if (!flyout.IsVisible) Call("ToggleFlyout");
                var expected = CodexRingPresentation.FromDetail(provider.Services[manager.SelectedId].Snapshot, UsagePeriodPreference.Auto).RemainingValueText;
                Require(flyout.IsVisible && flyout.SelectedProfileId == manager.SelectedId && ring.Text == expected,
                    $"Reopen {cycle} did not show the selected account.");
                Next();
                Require(flyout.SelectedProfileId == manager.SelectedId, $"Visible switch {cycle} did not follow the selection.");
                Call("ToggleFlyout");
                Next();
                DrainDispatcherIdle();
            }
            Require(Rows(flyout).SequenceEqual(rows), "Repeated reopen and switching recreated account rows.");
            Require(icons.Count > 1 && icons.Take(icons.Count - 1).All(icon => !ReferenceEquals(icon, current.GetValue(tray)) && Disposed(icon)),
                "Replaced tray icons were not released.");
            var clicks = 0;
            flyout.AccountSelected += _ => clicks++;
            Call("ToggleFlyout");
            var target = Rows(flyout).First(row => !Equals(row.Tag, manager.SelectedId));
            target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(clicks == 1 && manager.SelectedId == (string)target.Tag, "A row click after the stress selected more or less than once.");
        }
        finally
        {
            DrainDispatcherIdle();
            (fields[Array.IndexOf(names, "_flyout")].GetValue(app) as FlyoutWindow)?.Close();
            (fields[Array.IndexOf(names, "_widgetController")].GetValue(app) as FloatingWidgetController)?.Dispose();
            tray.Dispose();
            for (var index = 0; index < fields.Length; index++) fields[index].SetValue(app, original[index]);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static int[] Changed(Button[] rows, object[] contents) =>
        Enumerable.Range(0, rows.Length).Where(index => !ReferenceEquals(rows[index].Content, contents[index])).ToArray();

    private static Button[] Rows(FlyoutWindow flyout) =>
        ((ItemsControl)flyout.FindName("AccountOverview")).Items.Cast<Button>().ToArray();

    private static object[] WidgetAvatars(FloatingWidget widget)
    {
        var host = typeof(WidgetAccountModuleView).GetField("_avatarHost", PrivateInstance)!;
        return widget.Modules.Select(module => ((Border)host.GetValue(module)!).Child).ToArray<object>();
    }

    private static bool Disposed(System.Drawing.Icon icon)
    {
        try { _ = icon.Handle; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static CodexAccountView[] Accounts(int count) => Enumerable.Range(0, count).Select(index => new CodexAccountView(
        new CodexAccountProfile((index + 1).ToString("D32"), @"C:\synthetic\reuse" + index,
            new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }[index]),
        Snapshot(12 + index * 17), $"reuse{index}@example.invalid") { IsConnected = true }).ToArray();

    private static CodexAccountView[] Copies(IEnumerable<CodexAccountView> accounts) =>
        accounts.Select(account => account with { Snapshot = account.Snapshot with { } }).ToArray();

    private static CodexQuotaSnapshot Snapshot(double used) => new(CodexQuotaStatus.Available, "pro",
        Now.AddMinutes(-3), Now.AddMinutes(-1), null, null, 2,
        [new("codex", used, 10080, Now.AddDays(4), CodexWindowKind.Weekly)], null, [Now.AddDays(28)]);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MutableProvider : IUsageProvider
    {
        public Dictionary<string, MutableService> Services { get; } = new(StringComparer.Ordinal);
        public UsageProviderId Id => UsageProviderId.Codex;
        public IUsageAccountService Create(CodexAccountProfile profile) => Services[profile.Id] = new MutableService(profile.Id);
    }

    private sealed class MutableService(string id) : IUsageAccountService
    {
        private CodexQuotaSnapshot _snapshot = Snapshot(30 + id[^1] - '0');
        private string? _email = $"hidden{id[^1]}@example.invalid";
        public CodexQuotaSnapshot Snapshot { get => _snapshot; set { _snapshot = value; Changed?.Invoke(value); } }
        public string? Email { get => _email; set { _email = value; Changed?.Invoke(_snapshot); } }
        public string? IdentityFingerprint => null;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => false;
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed;
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) =>
            throw new InvalidOperationException("The reuse fixture must not request quota.");
    }
}
