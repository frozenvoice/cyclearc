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
            CheckWidgetAvatars();
            CheckTrayIcons();
            CheckHiddenPopup(app);
        }
        UiText.SetLanguage(UiLanguage.English);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        Console.WriteLine("PASS: popup rows, avatars and tray icons are reused only for identical output; a hidden popup is rebuilt before it shows, and at once for withdrawn accounts.");
    }

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
            Get<FlyoutWindow?>("_flyout")?.Close();
            Get<FloatingWidgetController?>("_widgetController")?.Dispose();
            tray.Dispose();
            for (var index = 0; index < fields.Length; index++) fields[index].SetValue(app, original[index]);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
