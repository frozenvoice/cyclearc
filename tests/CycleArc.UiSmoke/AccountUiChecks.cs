using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class AccountUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        var widgetIdentityCount = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            CheckCodexIdentityRecovery(directory, language, theme);
            widgetIdentityCount += CheckWidgetAccountIdentity(directory, language, theme);
            foreach (var size in new[] { 0, 1, 3, 8 })
            {
                var accounts = Fixtures(size);
                var visibleAccounts = accounts.Where(UsageAccountOverview.CanDisplay).ToArray();
                var visibleCount = visibleAccounts.Length;
                var id = accounts.FirstOrDefault()?.Profile.Id ?? "";
                var flyout = new FlyoutWindow();
                var window = new AccountsWindow();
                var widget = new FloatingWidget();
                try
                {
                    var selected = "";
                    var refreshes = 0;
                    flyout.AccountSelected += value => selected = value;
                    flyout.SyncRequested += () => refreshes++;
                    flyout.BindAccounts(accounts, id, false);
                    var overview = (ItemsControl)flyout.FindName("AccountOverview");
                    if (overview.Items.Count != (visibleCount > 1 ? visibleCount : 0)) throw new InvalidOperationException("Account overview includes an unconnected account or omits a usable account.");
                    if (size > 1)
                    {
                        ((Button)overview.Items[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (selected != accounts[1].Profile.Id || refreshes != 0) throw new InvalidOperationException("Account inspection started refresh or chose wrong account.");
                        var expected = CodexRingPresentation.From(accounts[1].Snapshot).CenterValueText;
                        flyout.BindAccounts(accounts, selected, false);
                        if (((TextBlock)flyout.FindName("SelectedAccountText")).Text != accounts[1].DisplayName)
                            throw new InvalidOperationException("Selected identity is not visible.");
                        flyout.BindAccounts(Enumerable.Reverse(accounts).ToArray(), selected, false);
                        if (!overview.Items.Cast<Button>().Select(button => button.Tag as string)
                            .SequenceEqual(Enumerable.Reverse(visibleAccounts).Select(account => account.Profile.Id)))
                            throw new InvalidOperationException("Usage popup did not follow saved account order.");
                    }
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        flyout.BindAccounts(accounts, id, false);
                        Render(flyout, 440 * zoom / 100d, null, directory is not null && size == 3 && zoom == 100
                            ? Path.Combine(directory, $"accounts-{language}-{theme}.png") : null);
                        CheckSummaryRows(flyout);
                        CheckProviderLabels(flyout, visibleCount == 0 ? 0 : (visibleCount > 1 ? visibleCount : 0) + 1);
                        flyout.BindAccounts(accounts, id, true);
                        if (((Button)flyout.FindName("RefreshAllButton")).IsEnabled)
                            throw new InvalidOperationException("Batch refresh enabled early.");
                        count++;
                    }
                    window.Bind(accounts, id);
                    Render(window, 700, 800, directory is not null && size == 3
                        ? Path.Combine(directory, $"manage-{language}-{theme}.png") : null);
                    if (((ItemsControl)window.FindName("AccountRows")).Items.Count != size)
                        throw new InvalidOperationException("Account management list lost accounts.");
                    CheckProviderLabels(window, size);
                    if (size > 0) CheckRenameSurvivesDisplayTick(window, accounts, id);
                    CheckGuidanceAndOrder(window, accounts, directory, language, theme);
                    widget.BindAccounts(visibleAccounts, id, UsagePeriodPreference.Auto, WidgetFixture.Desktop);
                    WidgetFixture.RenderWidget(widget, null);
                    CheckProviderLabels(widget, visibleCount);
                    if (((TextBlock)widget.FindName("ProductTitle")).Text != "CycleArc")
                        throw new InvalidOperationException("Widget product title is incorrect.");
                    if (widget.Modules.Count != visibleCount
                        || !widget.Modules.Select(m => m.ProfileId).SequenceEqual(visibleAccounts.Select(a => a.Profile.Id)))
                        throw new InvalidOperationException("Widget does not show every displayable account in order.");
                    if (widget.Modules.Any(m => m.NameText.Visibility != Visibility.Visible || m.NameText.Text.Length == 0))
                        throw new InvalidOperationException("Widget does not identify an account it shows.");
                    if (visibleCount > 0 && widget.Modules.Count(m => m.Model!.IsSelected) != 1)
                        throw new InvalidOperationException("Widget lost or duplicated the shared selection.");
                    if (((TextBlock)widget.FindName("EmptyStateText")).Visibility
                        != (visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed))
                        throw new InvalidOperationException("Widget empty state disagrees with the account list.");
                    count += 2;
                }
                finally { flyout.Close(); window.Close(); widget.Close(); }
            }
            CheckLongNames();
        }
        CheckLoginCancellation();
        CheckCreditAccountCapture();
        CheckLocalIcons();
        Console.WriteLine($"PASS: {widgetIdentityCount} widget account identity renders; single account, nickname/email fallback, Codex/Claude, quota states, long email and removal.");
        Console.WriteLine($"PASS: {count} multi-account WPF renders; CycleArc branding, Codex badges/contrast/long names, guidance/compact scrolling, local icons, ordering, rename continuity, refresh, login cancellation and credit-account routing.");
    }

    private static int CheckWidgetAccountIdentity(string? directory, UiLanguage language, AppTheme theme)
    {
        var account = Fixtures(1)[0];
        const string email = "personal@example.invalid";
        var nickname = UiText.T("Personal", "개인 계정");
        var unnamed = account with { Profile = account.Profile with { Label = "" }, Email = email };
        var claude = unnamed with
        {
            Profile = unnamed.Profile with { Id = "claude-profile", Provider = UsageProviderId.Claude },
            Snapshot = unnamed.Snapshot with { Provider = UsageProviderId.Claude }
        };
        var longEmail = new string('a', 64) + "@example.invalid";
        (string Name, CodexAccountView Account, string Expected)[] cases =
        [
            ("email", unnamed, email),
            ("nickname", unnamed with { Profile = unnamed.Profile with { Label = nickname } }, nickname),
            ("blank-label", unnamed with { Profile = unnamed.Profile with { Label = "  " } }, email),
            ("refreshing", unnamed with { Snapshot = unnamed.Snapshot with { Status = CodexQuotaStatus.Refreshing } }, email),
            ("stale", unnamed with { Snapshot = unnamed.Snapshot with { Status = CodexQuotaStatus.Stale } }, email),
            ("signed-out", unnamed with { Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut), Email = null },
                UiText.T("Existing Codex", "기존 Codex")),
            ("claude-email", claude, email),
            ("claude-profile", claude with { Email = null }, "Claude · claude"),
            ("long-email", unnamed with { Email = longEmail }, longEmail)
        ];
        var widget = new FloatingWidget();
        try
        {
            foreach (var item in cases)
            {
                WidgetFixture.BindOne(widget, item.Account);
                var module = WidgetFixture.Module(widget);
                var name = module.NameText;
                if (name.Visibility != Visibility.Visible || name.Text != item.Expected)
                    throw new InvalidOperationException($"Single-account widget identity missing ({language}/{theme}/{item.Name}).");
                var content = (FrameworkElement)widget.Content;
                WidgetFixture.RenderWidget(widget, directory is not null
                    ? Path.Combine(directory, $"widget-{item.Name}-{language}-{theme}.png") : null);
                var badge = module.Badge;
                var nameBounds = name.TransformToAncestor(content).TransformBounds(new Rect(name.RenderSize));
                var badgeBounds = badge.TransformToAncestor(content).TransformBounds(new Rect(badge.RenderSize));
                if (name.ActualWidth <= 0 || name.ActualHeight <= 0
                    || nameBounds.Right > badgeBounds.Left + 1
                    || nameBounds.Right > content.ActualWidth + 1
                    || nameBounds.Bottom > content.ActualHeight + 1)
                    throw new InvalidOperationException("Widget account identity overlaps or is clipped.");
                // A long nickname is trimmed inside its fixed module and never widens the widget.
                if (Math.Abs(module.ActualWidth - WidgetGridLayout.ModuleWidth) > 0.51)
                    throw new InvalidOperationException($"A long identity resized the module ({item.Name}).");
                if (name.TextTrimming != TextTrimming.CharacterEllipsis
                    || module.ToolTip is not string tooltip
                    || !tooltip.StartsWith(item.Expected + Environment.NewLine, StringComparison.Ordinal)
                    || name.ToolTip as string != item.Expected)
                    throw new InvalidOperationException("Widget must retain the full identity in its tooltip.");
            }
            WidgetFixture.BindOne(widget, null);
            if (widget.Modules.Count != 0
                || ((TextBlock)widget.FindName("EmptyStateText")).Visibility != Visibility.Visible
                || widget.ToolTip is string clearedTooltip && clearedTooltip.Contains(longEmail, StringComparison.Ordinal))
                throw new InvalidOperationException("Widget retained a removed account's identity.");
            WidgetFixture.RenderWidget(widget, null);
        }
        finally { widget.Close(); }
        return cases.Length + 1;
    }

    private static void CheckProviderLabels(Window window, int expected)
    {
        if (!window.Title.StartsWith("CycleArc", StringComparison.Ordinal))
            throw new InvalidOperationException("Window title does not identify CycleArc.");
        if (Descendants<TextBlock>((FrameworkElement)window.Content).Any(text => text.Text.Contains("Codex Codex", StringComparison.Ordinal)))
            throw new InvalidOperationException("Provider name is repeated within a usage label.");
        var badges = Descendants<UsageProviderBadge>((FrameworkElement)window.Content).Where(badge => badge.Visibility == Visibility.Visible).ToArray();
        if (badges.Length != expected) throw new InvalidOperationException("Usage provider is missing from an account surface.");
        foreach (var badge in badges)
        {
            var label = (TextBlock)badge.Child;
            if (badge.Visibility != Visibility.Visible || label.Text != "Codex" || label.ActualWidth < label.DesiredSize.Width - 1)
                throw new InvalidOperationException("Codex provider label is missing or clipped.");
            var parent = (FrameworkElement)VisualTreeHelper.GetParent(badge);
            var left = badge.TranslatePoint(new Point(), parent).X;
            if (left < -1 || left + badge.ActualWidth > parent.ActualWidth + 1)
                throw new InvalidOperationException("Provider badge overflows its identity row.");
            static double Luminance(Color color)
            {
                static double Linear(byte value) { var c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }
            var foreground = Luminance(((SolidColorBrush)label.Foreground).Color);
            var background = Luminance(((SolidColorBrush)badge.Background).Color);
            if ((Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05) < 4.5)
                throw new InvalidOperationException("Provider label lacks contrast in the current theme.");
        }
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void CheckLongNames()
    {
        var accounts = Fixtures(3);
        accounts[0] = accounts[0] with { Profile = accounts[0].Profile with
            { Label = UiText.T(new string('W', 90), string.Concat(Enumerable.Repeat("아주 긴 별명 ", 20))) } };
        var flyout = new FlyoutWindow();
        var widget = new FloatingWidget();
        try
        {
            foreach (var status in Enum.GetValues<CodexQuotaStatus>())
            {
                accounts[0] = accounts[0] with { Snapshot = accounts[0].Snapshot with { Status = status } };
                flyout.BindAccounts(accounts, accounts[0].Profile.Id, status == CodexQuotaStatus.Refreshing);
                Render(flyout, 440, null, null);
                CheckSummaryRows(flyout);
                CheckProviderLabels(flyout, UsageAccountOverview.Create(accounts, accounts[0].Profile.Id).Accounts.Count + 1);
                var header = (Grid)flyout.FindName("SelectedAccountHeader");
                var name = (TextBlock)flyout.FindName("SelectedAccountText");
                var badge = (UsageProviderBadge)flyout.FindName("SelectedProviderBadge");
                if (name.TranslatePoint(new Point(name.ActualWidth, 0), header).X
                    > badge.TranslatePoint(new Point(), header).X + 1)
                    throw new InvalidOperationException("Long selected-account name overlaps its provider.");
                WidgetFixture.BindOne(widget, accounts[0]);
                WidgetFixture.RenderWidget(widget, null);
                CheckProviderLabels(widget, 1);
            }
        }
        finally { flyout.Close(); widget.Close(); }
    }

    private static CodexAccountView[] Fixtures(int count)
    {
        var now = DateTimeOffset.Now;
        return Enumerable.Range(0, count).Select(i => new CodexAccountView(
            new CodexAccountProfile(i == 0 ? "default" : i.ToString("D32"), @"C:\synthetic\account" + i,
                UiText.T(i == 0 ? "Personal" : i == 1 ? "Work" : "Account " + (i + 1),
                    i == 0 ? "개인 계정" : i == 1 ? "업무 계정" : "추가 계정 " + (i + 1)), i > 0),
            new CodexQuotaSnapshot(i == 2 ? CodexQuotaStatus.Stale : i == 3 ? CodexQuotaStatus.SignedOut : CodexQuotaStatus.Available,
                "pro", now.AddMinutes(-3), now.AddMinutes(-1), null, null, 2,
                [new("codex", 18 + i * 9, 10080, now.AddDays(4), CodexWindowKind.Weekly)], null,
                [now.AddDays(28), now.AddDays(54)]), $"account{i}@example.invalid")).ToArray();
    }

    private static void CheckSummaryRows(FlyoutWindow window)
    {
        foreach (Button button in ((ItemsControl)window.FindName("AccountOverview")).Items)
        foreach (var row in ((StackPanel)button.Content).Children.OfType<Grid>())
        {
            var first = (FrameworkElement)row.Children[0];
            var second = (FrameworkElement)row.Children[1];
            var right = first.TranslatePoint(new Point(first.ActualWidth, 0), row).X;
            var left = second.TranslatePoint(new Point(), row).X;
            if (right > left + 1 || left + second.ActualWidth > row.ActualWidth + 1)
                throw new InvalidOperationException("Account name/status or quota row overlaps.");
        }
    }

    private static void CheckGuidanceAndOrder(AccountsWindow window, CodexAccountView[] accounts,
        string? directory, UiLanguage language, AppTheme theme)
    {
        foreach (var name in new[] { "NewLoginHint", "ExistingHint", "ChooseHomeHint", "ProfileHelp", "ActionsHelp", "SelectionHint" })
            if (string.IsNullOrWhiteSpace(((TextBlock)window.FindName(name)).Text))
                throw new InvalidOperationException("Connection guidance is missing.");
        if (((TextBlock)window.FindName("EmptyAccountsHint")).Visibility != (accounts.Length == 0 ? Visibility.Visible : Visibility.Collapsed))
            throw new InvalidOperationException("First-use guidance is not visible.");
        var rows = (ItemsControl)window.FindName("AccountRows");
        var movedId = "";
        var movedDirection = 0;
        window.MoveAccount = (id, direction) => { movedId = id; movedDirection = direction; return true; };
        for (var i = 0; i < rows.Items.Count; i++)
        {
            var order = ((StackPanel)rows.Items[i]).Children.OfType<Grid>().Single().Children.OfType<StackPanel>().Single();
            var up = order.Children.OfType<Button>().Single(b => b.Tag as string == "MoveAccountUp");
            var down = order.Children.OfType<Button>().Single(b => b.Tag as string == "MoveAccountDown");
            if (up.IsEnabled != (i > 0) || down.IsEnabled != (i < accounts.Length - 1))
                throw new InvalidOperationException("Account order boundary buttons are incorrect.");
            if (i == 1)
            {
                up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (movedId != accounts[i].Profile.Id || movedDirection != -1)
                    throw new InvalidOperationException("Reordering targeted the wrong account.");
            }
        }
        var advanced = (Expander)window.FindName("AdvancedConnection");
        var help = (Expander)window.FindName("AccountHelp");
        var connection = (Expander)window.FindName("ConnectionOptions");
        if (connection.IsExpanded != (accounts.Length == 0))
            throw new InvalidOperationException("First-use connection guide has the wrong initial state.");
        if (advanced.IsExpanded || help.IsExpanded) throw new InvalidOperationException("Detailed guidance should start collapsed.");
        connection.IsExpanded = advanced.IsExpanded = help.IsExpanded = true;
        Render(window, 470, 400, null);
        var scroll = (ScrollViewer)window.FindName("AccountsScroll");
        if (scroll.ViewportHeight <= 30 || scroll.ScrollableHeight <= 0)
            throw new InvalidOperationException("Guidance expansion hid the compact window's scrolling content.");
        scroll.ScrollToBottom();
        ((FrameworkElement)window.Content).UpdateLayout();
        var target = accounts.Length > 0 ? (FrameworkElement)rows.Items[^1] : (FrameworkElement)window.FindName("EmptyAccountsHint");
        var bottom = target.TransformToAncestor(scroll).Transform(new Point(0, target.ActualHeight)).Y;
        if (bottom > scroll.ViewportHeight + 2 || bottom < 0)
            throw new InvalidOperationException("Last account cannot be reached after opening guidance.");
        scroll.ScrollToTop();
        Render(window, 700, 800, null);
        if (directory is not null && accounts.Length == 3)
            Render(window, 700, 800, Path.Combine(directory, $"guide-{language}-{theme}.png"));
    }

    private static void CheckLocalIcons()
    {
        var accounts = Fixtures(2);
        var window = new AccountsWindow();
        Border Icon()
        {
            var row = (StackPanel)((ItemsControl)window.FindName("AccountRows")).Items[0];
            var content = (StackPanel)row.Children.OfType<Button>().Single().Content;
            return ((DockPanel)content.Children.OfType<Grid>().First().Children[0]).Children.OfType<Border>().Single();
        }
        try
        {
            Color? color = null;
            foreach (var pair in new[] { ("frozenvoice", "FR"), ("D", "D"), ("개인 계정", "개인"), ("👩‍💻work", "👩‍💻W") })
            {
                accounts[0] = accounts[0] with { Profile = accounts[0].Profile with { Label = pair.Item1 } };
                window.Bind(accounts, accounts[0].Profile.Id);
                var icon = Icon();
                if (((TextBlock)icon.Child).Text != pair.Item2)
                    throw new InvalidOperationException("Local avatar initials are incorrect.");
                var background = ((SolidColorBrush)icon.Background).Color;
                if (color is not null && color != background)
                    throw new InvalidOperationException("Renaming changed the account's identifying color.");
                color = background;
                static double Linear(byte value) { var c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
                if (1.05 / (luminance + 0.05) < 4.5) throw new InvalidOperationException("Account icon text lacks contrast.");
                // Supply a new collection, as the manager does, before the next changed profile.
                accounts = accounts.ToArray();
            }
        }
        finally { window.Close(); }
    }


    private static void CheckCodexIdentityRecovery(string? directory, UiLanguage language, AppTheme theme)
    {
        foreach (var detail in new[] { "codex-identity-mismatch", "codex-identity-conflict", "codex-identity-binding-unavailable" })
        {
            var account = Fixtures(1)[0] with
            {
                Profile = Fixtures(1)[0].Profile with { IsManaged = false },
                Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail),
                Email = null,
                HasMatchingIdentity = detail == "codex-identity-conflict"
            };
            var window = new AccountsWindow();
            var flyout = new FlyoutWindow();
            var widget = new FloatingWidget();
            var release = new TaskCompletionSource<CodexLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? requestedId = null;
            var calls = 0;
            CancellationToken requestedToken = default;
            window.SignIn = (id, _, token) => { requestedId = id; requestedToken = token; calls++; return release.Task; };
            try
            {
                window.Bind([account], account.Profile.Id);
                if (((Expander)window.FindName("ConnectionOptions")).IsExpanded)
                    throw new InvalidOperationException("Existing connection repair must show the profile before new-account setup.");
                flyout.BindAccounts([account], account.Profile.Id, false);
                WidgetFixture.BindOne(widget, account);
                var stem = detail == "codex-identity-conflict" && directory is not null
                    ? Path.Combine(directory, $"codex-reconnect-{language}-{theme}") : null;
                Render(window, 700, 800, stem is null ? null : stem + "-manage.png");
                Render(flyout, 440, null, stem is null ? null : stem + "-flyout.png");
                WidgetFixture.RenderWidget(widget, null);
                if (WidgetFixture.Module(widget).Periods.Count != 0)
                    throw new InvalidOperationException("An unverified Codex identity must not show quota in the widget.");
                var texts = Descendants<TextBlock>((FrameworkElement)flyout.Content).Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToArray();
                if (!texts.Contains(CodexDisplayFormatting.StatusText(account.Snapshot))
                    || CodexRingPresentation.From(account.Snapshot).IsAvailable
                    || CodexDisplayFormatting.Rows(account.Snapshot).Count != 0)
                    throw new InvalidOperationException("An unverified Codex identity must show reconnection guidance without quota.");
                var row = (StackPanel)((ItemsControl)window.FindName("AccountRows")).Items[0];
                var reconnect = row.Children.OfType<DockPanel>().Single().Children.OfType<Button>()
                    .Single(b => b.Tag as string == "ReconnectCodex");
                reconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                reconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (calls != 1 || requestedId != account.Profile.Id
                    || ((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Linked Codex recovery must target the existing profile and remain single-flight.");
                ((Button)window.FindName("CancelOperationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!requestedToken.IsCancellationRequested) throw new InvalidOperationException("Codex recovery cannot be cancelled.");
                release.TrySetResult(new(CodexQuotaStatus.Cancelled));
                PumpUntil(window.ActiveOperation);
                if (((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Codex recovery did not release progress after cancellation.");
            }
            finally
            {
                release.TrySetResult(new(CodexQuotaStatus.Cancelled));
                window.Close(); flyout.Close(); widget.Close();
            }
        }
    }

    private static void CheckLoginCancellation()
    {
        var window = new AccountsWindow();
        var release = new TaskCompletionSource<CodexLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        window.SignIn = (_, _, ct) => { calls++; token = ct; return release.Task; };
        try
        {
            var add = (Button)window.FindName("AddAccountButton");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (calls != 1 || add.IsEnabled || ((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Login must visibly remain single-flight.");
            ((Button)window.FindName("CancelOperationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!token.IsCancellationRequested || add.IsEnabled) throw new InvalidOperationException("Cancel did not wait for login cleanup.");
            release.TrySetResult(new(CodexQuotaStatus.Cancelled));
            PumpUntil(window.ActiveOperation);
            if (!add.IsEnabled || ((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Login busy state was not released.");
        }
        finally { release.TrySetResult(new(CodexQuotaStatus.Cancelled)); window.Close(); }
    }

    private static void CheckRenameSurvivesDisplayTick(AccountsWindow window, CodexAccountView[] accounts, string selected)
    {
        TextBox Editor()
        {
            var row = (StackPanel)((ItemsControl)window.FindName("AccountRows")).Items[0];
            return row.Children.OfType<DockPanel>().Single().Children.OfType<TextBox>().Single();
        }
        var editor = Editor();
        editor.Text = "In-progress name";
        editor.Select(3, 4);
        // The local age/countdown timer supplies new view records with unchanged quota state.
        window.Bind(accounts.Select(account => account with { }).ToArray(), selected);
        if (!ReferenceEquals(editor, Editor()) || editor.Text != "In-progress name"
            || editor.SelectionStart != 3 || editor.SelectionLength != 4)
            throw new InvalidOperationException("Display-only update interrupted account-name editing.");
    }

    private static void CheckCreditAccountCapture()
    {
        var accounts = Fixtures(2);
        var credit = new CodexResetCredit("synthetic", DateTimeOffset.Now.AddDays(2));
        accounts[0] = accounts[0] with { Snapshot = accounts[0].Snapshot with { RedeemableCredits = [credit], ResetCreditsAvailable = 1 } };
        var flyout = new FlyoutWindow();
        try
        {
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            var captured = "";
            flyout.RedeemAccountCredit = (profile, _) => { captured = profile; return Task.FromResult(CreditRedemptionOutcome.Reset); };
            var confirmation = typeof(FlyoutWindow).GetProperty("ConfirmCreditForTest", BindingFlags.NonPublic | BindingFlags.Instance)!;
            confirmation.SetValue(flyout, (Func<string, bool>)(prompt =>
            {
                if (!prompt.Contains(accounts[0].DisplayName)) throw new InvalidOperationException("Credit confirmation omits account.");
                flyout.BindAccounts(accounts, accounts[1].Profile.Id, false); // simulate a nested-dispatcher account change
                return true;
            }));
            var use = typeof(FlyoutWindow).GetMethod("UseCreditAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var row = CodexCreditCard.From(accounts[0].Snapshot, DateTimeOffset.Now).Rows.Single();
            PumpUntil((Task)use.Invoke(flyout, [row])!);
            if (captured != accounts[0].Profile.Id) throw new InvalidOperationException("Credit redemption changed account during confirmation.");
        }
        finally { flyout.Close(); }
    }

    internal static void PumpUntil(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Offline UI operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    internal static void Render(Window window, double width, double? height, string? path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (path is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
