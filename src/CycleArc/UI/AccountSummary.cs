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
    public static Button Create(CodexAccountView account, bool selected, Action select)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("AccountButton"),
            Margin = new Thickness(0, 0, 0, 7), Tag = account.Profile.Id };
        if (selected) button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new DockPanel();
        var avatar = Avatar(account, 30);
        DockPanel.SetDock(avatar, Dock.Left);
        identity.Children.Add(avatar);
        var name = Text((selected ? "● " : "") + account.DisplayName, 12, "TextBrush", bold: true);
        name.Margin = new Thickness(9, 0, 0, 0);
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
        var status = Text(account.IsSigningIn ? UiText.T("Signing in…", "로그인 중…")
            : CycleArcPresentation.StatusLabel(account.Snapshot), 11, stale ? "StaleBrush" : "MutedBrush", bold: stale);
        status.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        content.Children.Add(header);
        var usable = CursorUsagePresentation.IsCursor(account.Snapshot)
            ? CodexDisplayFormatting.ShowsQuotaWindows(account.Snapshot) && account.Snapshot.Windows.Count > 0
            : CodexRingPresentation.From(account.Snapshot).IsAvailable;
        if (usable)
        {
            foreach (var window in account.Snapshot.Windows)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = Text(CodexDisplayFormatting.CompactWindowKindLabel(window, account.Profile.Provider), 11, "MutedBrush");
                if (CursorUsagePresentation.IsCursor(account.Profile.Provider))
                {
                    // Cursor's official name plus cadence is intentionally longer than the
                    // old Auto/API labels. Let that label reflow in the flexible column;
                    // Codex and Claude retain their single-line card layout.
                    label.TextWrapping = TextWrapping.Wrap;
                    label.TextTrimming = TextTrimming.None;
                    label.MaxWidth = 180;
                    label.HorizontalAlignment = HorizontalAlignment.Left;
                }
                row.Children.Add(label);
                var value = Text(CodexDisplayFormatting.QuotaSummaryText(window, account.Profile.Provider), 12, stale ? "StaleBrush" : "TextBrush");
                Grid.SetColumn(value, 1); row.Children.Add(value); content.Children.Add(row);
            }
        }
        else
        {
            var notice = Text(account.IsSigningIn ? UiText.T("Complete sign-in in your browser.", "브라우저에서 로그인을 완료하세요.")
                : CodexDisplayFormatting.StatusText(account.Snapshot), 11, "MutedBrush");
            notice.TextWrapping = TextWrapping.Wrap;
            notice.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(notice);
        }
        if (account.Profile.Provider == UsageProviderId.Claude && account.Snapshot.LastSuccessfulRefresh is not null)
        {
            var receipt = Text(ClaudeUsagePresentation.LastReceivedText(account.Snapshot), 11, "MutedBrush");
            receipt.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(receipt);
        }
        if (CursorUsagePresentation.IsCursor(account.Profile.Provider))
        {
            var updated = Text(CursorUsagePresentation.UpdatedText(account.Snapshot), 11,
                stale ? "StaleBrush" : "MutedBrush", bold: stale);
            updated.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(updated);
        }
        if (account.HasMatchingIdentity && !CodexIdentityPresentation.NeedsReconnection(account.Snapshot))
        {
            var duplicate = Text(UiText.T("Same reported login email as another profile", "다른 프로필과 같은 로그인 이메일으로 조회됨"), 11, "MutedBrush");
            duplicate.TextWrapping = TextWrapping.Wrap;
            duplicate.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(duplicate);
        }
        button.Content = content;
        button.ToolTip = (account.Email ?? account.DisplayName + " · " + account.ProviderName)
            + (account.Profile.Provider == UsageProviderId.Claude
                ? Environment.NewLine + CycleArcPresentation.Tooltip(account.Snapshot) : "");
        System.Windows.Automation.AutomationProperties.SetName(button,
            account.DisplayName + " · " + account.ProviderName + " · " + status.Text
                + (selected ? UiText.T(" · Selected", " · 선택됨") : ""));
        button.Click += (_, _) => select();
        return button;
    }

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
