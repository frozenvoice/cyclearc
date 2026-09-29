using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;

namespace CycleArc.UI;

internal static class AccountSummary
{
    /// <param name="compactNotice">One line with the full text in its tooltip, for the popup list
    /// that already shows the selected account's notice in the detail card above it.</param>
    public static Button Create(CodexAccountView account, bool selected, Action select, bool compactNotice = false)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("AccountButton"),
            Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 8, 10, 8), Tag = account.Profile.Id };
        // The list keeps the account order; selection is shown in place, never by moving the row.
        if (selected)
        {
            button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            button.SetResourceReference(Control.BackgroundProperty, "GhostBrush");
        }
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new DockPanel();
        var avatar = Avatar(account, 24);
        DockPanel.SetDock(avatar, Dock.Left);
        identity.Children.Add(avatar);
        var name = Text(account.DisplayName, 12, "TextBrush", bold: true);
        name.Margin = new Thickness(8, 0, 0, 0);
        var nameAndProvider = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        nameAndProvider.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameAndProvider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameAndProvider.Children.Add(name);
        var provider = new UsageProviderBadge { Provider = account.Profile.Provider, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(provider, 1);
        nameAndProvider.Children.Add(provider);
        identity.Children.Add(nameAndProvider);
        header.Children.Add(identity);
        var stale = ClaudeUsagePresentation.IsStale(account.Snapshot)
            || (CursorUsagePresentation.IsCursor(account.Snapshot) && account.Snapshot.Status == CodexQuotaStatus.Stale);
        var unconnected = ClaudeUsagePresentation.IsUnconnected(account);
        var status = Text(account.IsSigningIn ? UiText.T("Signing in…", "로그인 중…")
            : unconnected ? ClaudeUsagePresentation.NotConnectedLabel
            : CycleArcPresentation.StatusLabel(account.Snapshot), 11, stale ? "StaleBrush" : "MutedBrush", bold: stale);
        status.Margin = new Thickness(12, 0, 0, 0);
        // Same rule as the widget status row: a healthy server sample needs no repeated
        // "Updated" text, while received samples, stale values and failures stay named.
        var needsStatus = account.IsSigningIn || WidgetAccountModel.From(account, selected).ShowStatusRow;
        status.Visibility = needsStatus ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        content.Children.Add(header);
        var usable = CursorUsagePresentation.IsCursor(account.Snapshot)
            ? CodexDisplayFormatting.ShowsQuotaWindows(account.Snapshot) && account.Snapshot.Windows.Count > 0
            : CodexRingPresentation.From(account.Snapshot).IsAvailable;
        if (usable)
        {
            // Every limit the provider reported keeps its own row; none is folded away.
            foreach (var window in account.Snapshot.Windows)
                content.Children.Add(QuotaRow(window, account.Profile.Provider));
        }
        else
        {
            var notice = Text(account.IsSigningIn ? UiText.T("Complete sign-in in your browser.", "브라우저에서 로그인을 완료하세요.")
                : unconnected ? ClaudeUsagePresentation.NotConnectedText
                : CodexDisplayFormatting.StatusText(account.Snapshot), 11, "MutedBrush");
            if (compactNotice)
            {
                notice.TextWrapping = TextWrapping.NoWrap;
                notice.TextTrimming = TextTrimming.CharacterEllipsis;
                notice.ToolTip = notice.Text;
            }
            else notice.TextWrapping = TextWrapping.Wrap;
            notice.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(notice);
        }
        if (needsStatus && account.Profile.Provider == UsageProviderId.Claude && account.Snapshot.LastSuccessfulRefresh is not null)
        {
            // The card uses the popup's short stamp; its tooltip keeps the full receipt date.
            var receipt = Text(ClaudeUsagePresentation.ReceiptLabel(account.Snapshot) + " "
                + CodexDisplayFormatting.ResetStamp(account.Snapshot.LastSuccessfulRefresh), 11, "MutedBrush");
            receipt.ToolTip = ClaudeUsagePresentation.LastReceivedText(account.Snapshot);
            receipt.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(receipt);
        }
        if (needsStatus && CursorUsagePresentation.IsCursor(account.Profile.Provider))
        {
            var updated = Text(CursorUsagePresentation.UpdatedText(account.Snapshot), 11,
                stale ? "StaleBrush" : "MutedBrush", bold: stale);
            updated.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(updated);
        }
        if (account.HasMatchingIdentity && !CodexIdentityPresentation.NeedsReconnection(account.Snapshot))
        {
            var duplicate = Text(UiText.T("Same reported login email as another profile", "다른 프로필과 같은 로그인 이메일로 조회됨"), 11, "MutedBrush");
            duplicate.TextWrapping = TextWrapping.Wrap;
            duplicate.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(duplicate);
        }
        button.Content = content;
        button.ToolTip = (account.Email ?? account.DisplayName + " · " + account.ProviderName)
            + (account.Profile.Provider == UsageProviderId.Claude
                ? Environment.NewLine + CycleArcPresentation.Tooltip(account.Snapshot) : "");
        var quotas = usable
            ? account.Snapshot.Windows.Select(window => QuotaLabel(window, account.Profile.Provider) + " "
                + CodexDisplayFormatting.QuotaSummaryText(window, account.Profile.Provider))
            : [];
        System.Windows.Automation.AutomationProperties.SetName(button,
            string.Join(" · ", new[] { account.DisplayName, account.ProviderName, status.Text }.Concat(quotas))
                + (selected ? UiText.T(" · Selected", " · 선택됨") : ""));
        button.Click += (_, _) => select();
        return button;
    }

    // One limit: name, what is left at popup precision, and a bar filled with that remainder.
    // The bar color is the limit's usage band from the unrounded value, as on the rings.
    private static Grid QuotaRow(CodexQuotaWindow window, UsageProviderId provider)
    {
        var row = new Grid { Tag = "AccountQuotaRow", Margin = new Thickness(0, 6, 0, 0) };
        // Shared across the whole account list (the ItemsControl is the size scope), so every
        // bar starts and ends at the same x and equal remainders draw equal lengths.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64, SharedSizeGroup = "AccountQuotaLabel" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "AccountQuotaValue" });
        var cursor = CursorUsagePresentation.IsCursor(provider);
        var label = Text(QuotaLabel(window, provider), 11, "MutedBrush");
        label.Margin = new Thickness(0, 0, 10, 0);
        if (cursor)
        {
            label.TextTrimming = TextTrimming.None;
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = 150;
        }
        row.Children.Add(label);
        var value = Text(QuotaValue(window, provider), 12, "TextBrush", bold: true);
        value.Margin = new Thickness(10, 0, 0, 0);
        value.TextTrimming = TextTrimming.None;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        var remaining = IsValidPercent(window.UsedPercent) ? window.RemainingPercent : null;
        var bar = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Tag = "AccountQuotaBar" };
        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        bar.Children.Add(track);
        if (remaining is { } left)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - left, GridUnitType.Star) });
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(2.5), Tag = "AccountQuotaBarFill" };
            fill.SetResourceReference(Border.BackgroundProperty,
                UsageRingBands.ArcBrushKey(UsageRingBands.From(window.UsedPercent)));
            bar.Children.Add(fill);
        }
        else
        {
            // Unknown usage and amount-only allowances get no invented fill.
            bar.Visibility = Visibility.Hidden;
        }
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        row.ToolTip = QuotaLabel(window, provider) + " · " + CodexDisplayFormatting.QuotaSummaryText(window, provider);
        return row;
    }

    internal static string QuotaLabel(CodexQuotaWindow window, UsageProviderId provider) =>
        CursorUsagePresentation.IsCursor(provider)
            ? CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)
            : CodexDisplayFormatting.DurationLabel(window.WindowDurationMinutes);

    // Popup precision (up to two decimals). Off, unlimited and money keep their own words.
    internal static string QuotaValue(CodexQuotaWindow window, UsageProviderId provider)
    {
        if (!CursorUsagePresentation.IsCursor(provider))
            return UiText.WidgetLeft(UsagePercentFormatting.DetailRemaining(window));
        if (window.IsEnabled == false || window.IsUnlimited) return CursorUsagePresentation.DetailRemainingText(window);
        return UiText.WidgetLeft(CursorUsagePresentation.DetailRemainingText(window));
    }

    private static bool IsValidPercent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);

    // Shared with the floating widget's account modules so both draw the same identity.
    internal static Border Avatar(CodexAccountView account, double size)
    {
        // Local presentation only: the official account protocol provides no web profile image.
        var source = account.DisplayName.Split('@', 2)[0].Trim();
        var elements = StringInfo.GetTextElementEnumerator(source);
        var initials = "";
        for (var i = 0; i < 2 && elements.MoveNext(); i++) initials += elements.GetTextElement();
        if (initials.Length == 0) initials = "C";
        uint hash = 2166136261;
        foreach (var character in account.Profile.Id) hash = unchecked((hash ^ character) * 16777619);
        var colors = new[] { "#167048", "#956000", "#3864B5", "#7952A5", "#A84268", "#227575" };
        return new Border
        {
            Tag = "AccountAvatar", Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[hash % colors.Length])),
            ToolTip = UiText.T($"Icon made from the name in {UiText.ProductName}", $"{UiText.ProductName}에서 이름으로 만든 아이콘"),
            Child = new TextBlock { Text = initials.ToUpperInvariant(), FontSize = Math.Max(9, size * 0.37), FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private static TextBlock Text(string text, double size, string brush, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
}
