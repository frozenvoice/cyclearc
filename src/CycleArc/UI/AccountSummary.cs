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
    // The displayed output each row or avatar was last rendered from. An unchanged output
    // keeps the existing elements, so a rebind does not rebuild or re-lay out the row.
    private static readonly DependencyProperty RenderedProperty =
        DependencyProperty.RegisterAttached("Rendered", typeof(object), typeof(AccountSummary));

    /// <param name="compactNotice">One line with the full text in its tooltip, for the popup list
    /// that already shows the selected account's notice in the detail card above it.</param>
    public static Button Create(CodexAccountView account, bool selected, Action select, bool compactNotice = false)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("AccountButton"),
            Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 8, 10, 8), Tag = account.Profile.Id };
        Update(button, account, selected, compactNotice);
        button.Click += (_, _) => select();
        return button;
    }

    /// <summary>
    /// Rewrites a row created for the same account. Returns false, keeping every element,
    /// when the account, selection and current texts would display exactly the same row.
    /// </summary>
    public static bool Update(Button button, CodexAccountView account, bool selected, bool compactNotice = false)
    {
        var view = Describe(account, selected, compactNotice);
        if (Equals(button.GetValue(RenderedProperty), view)) return false;
        Render(button, view);
        button.SetValue(RenderedProperty, view);
        return true;
    }

    /// <summary>Makes the next Update render the row even when its output is unchanged.</summary>
    public static void Invalidate(Button button) => button.ClearValue(RenderedProperty);

    private sealed record QuotaRowView(string Label, string Value, bool Cursor, double? Remaining, string FillBrush, string Tooltip);

    private sealed record QuotaRowViews(QuotaRowView[] Items)
    {
        public bool Equals(QuotaRowViews? other) => other is not null && Items.AsSpan().SequenceEqual(other.Items);
        public override int GetHashCode() => Items.Length;
    }

    private sealed record RowView(bool Selected, AvatarView Avatar, string DisplayName, UsageProviderId Provider,
        string Status, bool Stale, bool ShowStatus, QuotaRowViews? Quotas, string? Notice, bool CompactNotice,
        string? Receipt, string? ReceiptTooltip, string? Updated, bool Duplicate, string Tooltip, string AutomationName);

    private static RowView Describe(CodexAccountView account, bool selected, bool compactNotice)
    {
        var stale = ClaudeUsagePresentation.IsStale(account.Snapshot)
            || (CursorUsagePresentation.IsCursor(account.Snapshot) && account.Snapshot.Status == CodexQuotaStatus.Stale);
        var unconnected = ClaudeUsagePresentation.IsUnconnected(account);
        var status = account.IsSigningIn ? UiText.T("Signing in…", "로그인 중…")
            : unconnected ? ClaudeUsagePresentation.NotConnectedLabel
            : CycleArcPresentation.StatusLabel(account.Snapshot);
        // Same rule as the widget status row: a healthy server sample needs no repeated
        // "Updated" text, while received samples, stale values and failures stay named.
        var needsStatus = account.IsSigningIn || WidgetAccountModel.From(account, selected).ShowStatusRow;
        var usable = CursorUsagePresentation.IsCursor(account.Snapshot)
            ? CodexDisplayFormatting.ShowsQuotaWindows(account.Snapshot) && account.Snapshot.Windows.Count > 0
            : CodexRingPresentation.From(account.Snapshot).IsAvailable;
        var provider = account.Profile.Provider;
        // Every limit the provider reported keeps its own row; none is folded away.
        var quotas = usable ? new QuotaRowViews(account.Snapshot.Windows.Select(window => QuotaRowFor(window, provider)).ToArray()) : null;
        var notice = usable ? null
            : account.IsSigningIn ? UiText.T("Complete sign-in in your browser.", "브라우저에서 로그인을 완료하세요.")
            : unconnected ? ClaudeUsagePresentation.NotConnectedText
            : CodexDisplayFormatting.StatusText(account.Snapshot);
        string? receipt = null, receiptTooltip = null;
        if (needsStatus && provider == UsageProviderId.Claude && account.Snapshot.LastSuccessfulRefresh is not null)
        {
            // The card uses the popup's short stamp; its tooltip keeps the full receipt date.
            receipt = ClaudeUsagePresentation.ReceiptLabel(account.Snapshot) + " "
                + CodexDisplayFormatting.ResetStamp(account.Snapshot.LastSuccessfulRefresh);
            receiptTooltip = ClaudeUsagePresentation.LastReceivedText(account.Snapshot);
        }
        var updated = needsStatus && CursorUsagePresentation.IsCursor(provider)
            ? CursorUsagePresentation.UpdatedText(account.Snapshot) : null;
        var tooltip = (account.Email ?? account.DisplayName + " · " + account.ProviderName)
            + (provider == UsageProviderId.Claude
                ? Environment.NewLine + CycleArcPresentation.Tooltip(account.Snapshot) : "");
        var quotaNames = usable
            ? account.Snapshot.Windows.Select(window => QuotaLabel(window, provider) + " "
                + CodexDisplayFormatting.QuotaSummaryText(window, provider))
            : [];
        var automationName = string.Join(" · ", new[] { account.DisplayName, account.ProviderName, status }.Concat(quotaNames))
            + (selected ? UiText.T(" · Selected", " · 선택됨") : "");
        return new(selected, AvatarViewFor(account, 24), account.DisplayName, provider, status, stale, needsStatus,
            quotas, notice, compactNotice, receipt, receiptTooltip, updated,
            account.HasMatchingIdentity && !CodexIdentityPresentation.NeedsReconnection(account.Snapshot),
            tooltip, automationName);
    }

    private static void Render(Button button, RowView view)
    {
        // The list keeps the account order; selection is shown in place, never by moving the row.
        if (view.Selected)
        {
            button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            button.SetResourceReference(Control.BackgroundProperty, "GhostBrush");
        }
        else
        {
            button.ClearValue(Control.BorderBrushProperty);
            button.ClearValue(Control.BackgroundProperty);
        }
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new DockPanel();
        var avatar = CreateAvatar(view.Avatar);
        DockPanel.SetDock(avatar, Dock.Left);
        identity.Children.Add(avatar);
        var name = Text(view.DisplayName, 12, "TextBrush", bold: true);
        name.Margin = new Thickness(8, 0, 0, 0);
        var nameAndProvider = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        nameAndProvider.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameAndProvider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameAndProvider.Children.Add(name);
        var provider = new UsageProviderBadge { Provider = view.Provider, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(provider, 1);
        nameAndProvider.Children.Add(provider);
        identity.Children.Add(nameAndProvider);
        header.Children.Add(identity);
        var status = Text(view.Status, 11, view.Stale ? "StaleBrush" : "MutedBrush", bold: view.Stale);
        status.Margin = new Thickness(12, 0, 0, 0);
        status.Visibility = view.ShowStatus ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        content.Children.Add(header);
        if (view.Quotas is { } quotas)
        {
            foreach (var quota in quotas.Items)
                content.Children.Add(QuotaRow(quota));
        }
        else
        {
            var notice = Text(view.Notice ?? "", 11, "MutedBrush");
            if (view.CompactNotice)
            {
                notice.TextWrapping = TextWrapping.NoWrap;
                notice.TextTrimming = TextTrimming.CharacterEllipsis;
                notice.ToolTip = notice.Text;
            }
            else notice.TextWrapping = TextWrapping.Wrap;
            notice.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(notice);
        }
        if (view.Receipt is not null)
        {
            var receipt = Text(view.Receipt, 11, "MutedBrush");
            receipt.ToolTip = view.ReceiptTooltip;
            receipt.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(receipt);
        }
        if (view.Updated is not null)
        {
            var updated = Text(view.Updated, 11, view.Stale ? "StaleBrush" : "MutedBrush", bold: view.Stale);
            updated.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(updated);
        }
        if (view.Duplicate)
        {
            var duplicate = Text(UiText.T("Same reported login email as another profile", "다른 프로필과 같은 로그인 이메일로 조회됨"), 11, "MutedBrush");
            duplicate.TextWrapping = TextWrapping.Wrap;
            duplicate.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(duplicate);
        }
        button.Content = content;
        button.ToolTip = view.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(button, view.AutomationName);
    }

    private static QuotaRowView QuotaRowFor(CodexQuotaWindow window, UsageProviderId provider)
    {
        var remaining = IsValidPercent(window.UsedPercent) ? window.RemainingPercent : null;
        return new(QuotaLabel(window, provider), QuotaValue(window, provider), CursorUsagePresentation.IsCursor(provider),
            remaining, UsageRingBands.ArcBrushKey(UsageRingBands.From(window.UsedPercent)),
            QuotaLabel(window, provider) + " · " + CodexDisplayFormatting.QuotaSummaryText(window, provider));
    }

    // One limit: name, what is left at popup precision, and a bar filled with that remainder.
    // The bar color is the limit's usage band from the unrounded value, as on the rings.
    private static Grid QuotaRow(QuotaRowView view)
    {
        var row = new Grid { Tag = "AccountQuotaRow", Margin = new Thickness(0, 6, 0, 0) };
        // Shared across the whole account list (the ItemsControl is the size scope), so every
        // bar starts and ends at the same x and equal remainders draw equal lengths.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64, SharedSizeGroup = "AccountQuotaLabel" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "AccountQuotaValue" });
        var label = Text(view.Label, 11, "MutedBrush");
        label.Margin = new Thickness(0, 0, 10, 0);
        if (view.Cursor)
        {
            label.TextTrimming = TextTrimming.None;
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = 150;
        }
        row.Children.Add(label);
        var value = Text(view.Value, 12, "TextBrush", bold: true);
        value.Margin = new Thickness(10, 0, 0, 0);
        value.TextTrimming = TextTrimming.None;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        var bar = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Tag = "AccountQuotaBar" };
        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        bar.Children.Add(track);
        if (view.Remaining is { } left)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - left, GridUnitType.Star) });
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(2.5), Tag = "AccountQuotaBarFill" };
            fill.SetResourceReference(Border.BackgroundProperty, view.FillBrush);
            bar.Children.Add(fill);
        }
        else
        {
            // Unknown usage and amount-only allowances get no invented fill.
            bar.Visibility = Visibility.Hidden;
        }
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        row.ToolTip = view.Tooltip;
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

    private static readonly string[] AvatarColors = ["#167048", "#956000", "#3864B5", "#7952A5", "#A84268", "#227575"];

    private sealed record AvatarView(string Initials, string Color, double Size, string Tooltip);

    private static AvatarView AvatarViewFor(CodexAccountView account, double size)
    {
        // Local presentation only: the official account protocol provides no web profile image.
        var source = account.DisplayName.Split('@', 2)[0].Trim();
        var elements = StringInfo.GetTextElementEnumerator(source);
        var initials = "";
        for (var i = 0; i < 2 && elements.MoveNext(); i++) initials += elements.GetTextElement();
        if (initials.Length == 0) initials = "C";
        uint hash = 2166136261;
        foreach (var character in account.Profile.Id) hash = unchecked((hash ^ character) * 16777619);
        return new(initials.ToUpperInvariant(), AvatarColors[hash % AvatarColors.Length], size,
            UiText.T($"Icon made from the name in {UiText.ProductName}", $"{UiText.ProductName}에서 이름으로 만든 아이콘"));
    }

    // Shared with the floating widget's account modules so both draw the same identity.
    internal static Border Avatar(CodexAccountView account, double size) => CreateAvatar(AvatarViewFor(account, size));

    /// <summary>
    /// The avatar for one host: its current avatar when that already draws this account at this
    /// size and language, otherwise a new one. Never an element shown by another parent.
    /// </summary>
    internal static Border AvatarFor(CodexAccountView account, double size, object? current)
    {
        var view = AvatarViewFor(account, size);
        return current is Border existing && Equals(existing.GetValue(RenderedProperty), view) ? existing : CreateAvatar(view);
    }

    private static Border CreateAvatar(AvatarView view)
    {
        var avatar = new Border
        {
            Tag = "AccountAvatar", Width = view.Size, Height = view.Size, CornerRadius = new CornerRadius(view.Size / 2),
            Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(view.Color)),
            ToolTip = view.Tooltip,
            Child = new TextBlock { Text = view.Initials, FontSize = Math.Max(9, view.Size * 0.37), FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }
        };
        avatar.SetValue(RenderedProperty, view);
        return avatar;
    }

    private static TextBlock Text(string text, double size, string brush, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
}
