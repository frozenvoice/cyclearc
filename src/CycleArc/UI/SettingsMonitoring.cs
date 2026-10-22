using System.Globalization;
using System.Windows.Controls;
using CycleArc.Monitoring;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CycleArc.UI;

public partial class SettingsWindow
{
    private CheckBox _widgetMoney = null!, _widgetResets = null!, _codexBalance = null!,
        _claudeSpend = null!, _cursorSpend = null!, _codexExpiry = null!, _claudeExpiry = null!;
    private TextBox _balanceThreshold = null!, _claudeThreshold = null!, _cursorThreshold = null!, _expiryDays = null!;
    private FinancialAlertOptions EditedFinancialOptions => new()
    {
        CodexLowBalanceEnabled = _codexBalance.IsChecked == true,
        CodexLowBalanceThreshold = Number(_balanceThreshold, 10, 0, 1_000_000_000),
        ClaudeSpendEnabled = _claudeSpend.IsChecked == true,
        ClaudeSpendThresholdPercent = Number(_claudeThreshold, 85, 1, 100),
        CursorSpendEnabled = _cursorSpend.IsChecked == true,
        CursorSpendThresholdPercent = Number(_cursorThreshold, 85, 1, 100),
        CodexResetExpiryEnabled = _codexExpiry.IsChecked == true,
        ClaudeResetExpiryEnabled = _claudeExpiry.IsChecked == true,
        ResetExpiryLeadDays = (int)Number(_expiryDays, 3, 1, 30)
    };

    private bool MonitoringSettingsChanged => _widgetMoney is not null &&
        ((_widgetMoney.IsChecked == true) != _settings.WidgetFinancialSummaryEnabled
        || (_widgetResets.IsChecked == true) != _settings.WidgetResetSummaryEnabled
        || System.Text.Json.JsonSerializer.Serialize(EditedFinancialOptions)
            != System.Text.Json.JsonSerializer.Serialize(_settings.FinancialAlerts ?? new()));

    private void InitializeMonitoringSettings(AppSettings settings)
    {
        var options = settings.FinancialAlerts ?? new();
        var widget = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _widgetMoney = Toggle(widget, UiText.T("Credit / spending summary", "크레딧·지출 요약"), settings.WidgetFinancialSummaryEnabled);
        _widgetResets = Toggle(widget, UiText.T("Reset credit count", "리셋권 개수"), settings.WidgetResetSummaryEnabled);
        WidgetMonitoringSettingsHost.Children.Add(widget);
        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        _codexBalance = Toggle(content, UiText.T("Codex: low credit balance", "Codex: 크레딧 잔액 부족"), options.CodexLowBalanceEnabled);
        _balanceThreshold = Numeric(content, UiText.T("Alert at or below (credits)", "이하일 때 알림 (크레딧)"), options.CodexLowBalanceThreshold);
        _claudeSpend = Toggle(content, UiText.T("Claude: monthly spending cap", "Claude: 월 지출 상한"), options.ClaudeSpendEnabled);
        _claudeThreshold = Numeric(content, UiText.T("Monthly cap used (%)", "월 상한 사용 (%)"), options.ClaudeSpendThresholdPercent);
        _cursorSpend = Toggle(content, UiText.T("Cursor: on-demand cap", "Cursor: 온디맨드 상한"), options.CursorSpendEnabled);
        _cursorThreshold = Numeric(content, UiText.T("On-demand cap used (%)", "온디맨드 상한 사용 (%)"), options.CursorSpendThresholdPercent);
        _codexExpiry = Toggle(content, UiText.T("Codex: reset expiry", "Codex: 리셋권 만료"), options.CodexResetExpiryEnabled);
        _claudeExpiry = Toggle(content, UiText.T("Claude: reset expiry", "Claude: 리셋권 만료"), options.ClaudeResetExpiryEnabled);
        _expiryDays = Numeric(content, UiText.T("Days before expiry", "만료 전 알림 (일)"), options.ResetExpiryLeadDays);
        var hint = new TextBlock { Text = UiText.T("Confirmed current values only. Alerts re-arm after recovery or a new period. Missing amounts and unknown expiry dates do not alert.",
            "확인된 최신 수치에만 알립니다. 회복 또는 새 기간 이후 다시 알리며, 미제공 금액·만료일에는 알리지 않습니다."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(hint);
        var expander = new Expander { Header = UiText.T("Credit, spending & expiry alerts", "크레딧·지출·만료 알림"), Content = content, Margin = new Thickness(0, 10, 0, 0) };
        FinancialSettingsHost.Children.Add(expander);
    }

    private CheckBox Toggle(Panel host, string label, bool value)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0), FontSize = 12 };
        var box = new CheckBox { IsChecked = value, Style = (Style)FindResource("SettingsSwitch") };
        SetName(box, label); Grid.SetColumn(box, 1);
        box.Checked += (_, _) => UpdateApplyState(); box.Unchecked += (_, _) => UpdateApplyState();
        grid.Children.Add(text); grid.Children.Add(box); host.Children.Add(grid); return box;
    }

    private TextBox Numeric(Panel host, string label, decimal value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        var text = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), MinHeight = 30, Padding = new Thickness(5) };
        SetName(box, label); Grid.SetColumn(box, 1);
        box.TextChanged += (_, _) => { if (_expiryDays is not null) UpdateApplyState(); };
        grid.Children.Add(text); grid.Children.Add(box); host.Children.Add(grid); return box;
    }

    private static decimal Number(TextBox box, decimal fallback, decimal min, decimal max) =>
        decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : fallback;

    private void CommitMonitoringSettings()
    {
        _settings.WidgetFinancialSummaryEnabled = _widgetMoney.IsChecked == true;
        _settings.WidgetResetSummaryEnabled = _widgetResets.IsChecked == true;
        _settings.FinancialAlerts = EditedFinancialOptions;
    }
}
