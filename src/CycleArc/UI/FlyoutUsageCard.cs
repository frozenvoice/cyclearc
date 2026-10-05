using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.UI;

public partial class FlyoutWindow
{
    private Dictionary<string, bool> _usageCardExpanded = new(StringComparer.Ordinal);
    private bool _usageCreditsCompact;
    public event Action<string, bool>? UsageCardExpansionChanged;

    private void ApplyUsageCardSettings(AppSettings settings)
    {
        _usageCardExpanded = new(settings.UsageCardExpandedAccounts ?? [], StringComparer.Ordinal);
        ApplyUsageCardExpansion();
    }

    private void BindUsageCard(CodexQuotaSnapshot snapshot)
    {
        var card = UsageCreditPresentation.Create(snapshot);
        _usageCreditsCompact = snapshot.Status == CodexQuotaStatus.Available && card.Notice.Length == 0
            && !UsageCreditPresentation.Hidden(snapshot) && (snapshot.Provider switch
            {
                UsageProviderId.Claude => snapshot.ExtraUsage is null,
                UsageProviderId.Cursor => !snapshot.Windows.Any(window => window.LimitId == "cursor-on-demand"),
                _ => snapshot.UsageCredits is null && snapshot.UsageCreditsFailure is null or "credits-not-provided"
            });
        UsageCreditsCard.Visibility = Visibility.Visible;
        UsageCreditsCard.Tag = SelectedProfileId;
        AutomationProperties.SetAutomationId(UsageCreditsCard, "usage-credits-" + SelectedProfileId);
        UsageCreditsTitle.Text = card.Title;
        UsageCreditsSummary.Text = card.Summary;
        UsageCreditsSummary.ToolTip = card.Tooltip;
        UsageCreditsCard.ToolTip = card.Tooltip;
        AutomationProperties.SetName(UsageCreditsCard, card.Title + ": " + card.Summary);
        AutomationProperties.SetHelpText(UsageCreditsCard, card.Tooltip);
        UsageCreditsNotice.Text = card.Notice;
        UsageCreditsWarning.Visibility = string.IsNullOrEmpty(card.Notice) ? Visibility.Collapsed : Visibility.Visible;
        UsageCreditsWarning.ToolTip = card.Notice;
        UsageCreditsNotice.Visibility = string.IsNullOrEmpty(card.Notice) ? Visibility.Collapsed : Visibility.Visible;
        UsageCreditsRows.Items.Clear();
        foreach (var item in card.Rows)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5), ToolTip = item.Tooltip };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = item.Label, FontSize = 12, Margin = new Thickness(0, 0, 12, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var value = new TextBlock
            {
                Text = item.Value, FontSize = 12, TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Value
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(value, 1);
            row.Children.Add(label);
            row.Children.Add(value);
            UsageCreditsRows.Items.Add(row);
        }
        ApplyUsageCardExpansion();
    }

    private void OnUsageCardExpandClick(object sender, RoutedEventArgs e)
    {
        var id = SelectedProfileId ?? "";
        var expanded = !_usageCardExpanded.GetValueOrDefault(id);
        if (expanded) _usageCardExpanded[id] = true;
        else _usageCardExpanded.Remove(id);
        ApplyUsageCardExpansion();
        if (id.Length > 0) UsageCardExpansionChanged?.Invoke(id, expanded);
    }

    private void ApplyUsageCardExpansion()
    {
        var expanded = _usageCardExpanded.GetValueOrDefault(SelectedProfileId ?? "");
        ApplyAuxiliaryCardAppearance(UsageCreditsCard, UsageCreditsTitle, _usageCreditsCompact && !expanded);
        UsageCreditsDetails.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UsageCreditsChevron.Data = Geometry.Parse(expanded ? "M1,7 L7,1 L13,7" : "M1,1 L7,7 L13,1");
        var action = expanded ? UiText.T("Collapse", "접기") : UiText.T("Expand", "펼치기");
        UsageCreditsExpandButton.ToolTip = UsageCreditsTitle.Text + " · " + action;
        AutomationProperties.SetName(UsageCreditsExpandButton, UsageCreditsTitle.Text + " · " + action);
        AutomationProperties.SetHelpText(UsageCreditsExpandButton, UsageCreditsSummary.Text);
    }

    private static void ApplyAuxiliaryCardAppearance(Border card, TextBlock title, bool compact)
    {
        card.BorderThickness = new Thickness(compact ? 0 : 1);
        card.Padding = new Thickness(12, compact ? 1 : 8, 12, compact ? 1 : 8);
        card.Margin = new Thickness(0, compact ? 6 : 10, 0, 0);
        title.FontSize = compact ? 12 : 13;
        title.FontWeight = compact ? FontWeights.Normal : FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, compact ? "MutedBrush" : "TextBrush");
        if (compact) card.Background = System.Windows.Media.Brushes.Transparent;
        else card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
    }
}
