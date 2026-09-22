namespace CycleArc.Services;

public sealed record SettingsEdit
{
    public SubscriptionPreset PlanPreset { get; set; }
    public int WeeklyProQuota { get; set; }
    public int? DailyProQuota { get; set; }
    public int? SolProDailyQuota { get; set; }
    public int? CombinedDailyQuota { get; set; }
    public int? ReasoningQuota { get; set; }
    public DayOfWeek ResetWeekday { get; set; }
    public TimeSpan ResetTime { get; set; }
    public bool ResetAnchorConfigured { get; set; }
    public AuthTransportKind AuthTransport { get; set; }
    public string? CompanionExtensionId { get; set; }
    public string? ChromeExtensionId { get; set; }
    public string? EdgeExtensionId { get; set; }
    public bool AutoSync { get; set; }
    public int SyncIntervalMinutes { get; set; }
    public bool StartWithWindows { get; set; }
    public bool FloatingWidgetEnabled { get; set; }
    public bool TaskbarStatusEnabled { get; set; }
    public string? CodexExePath { get; set; }
    public AppTheme Theme { get; set; }
    public TrayIconStyle TrayIconStyle { get; set; }
    public UiLanguage UiLanguage { get; set; }
    public bool NotifyAt20 { get; set; }
    public bool NotifyAt10 { get; set; }
    public bool NotifyExhausted { get; set; }
    public bool NotifyReset { get; set; }
    public bool NotifySyncError { get; set; }
    public bool ImportHistoricalStatistics { get; set; }
    public double WidgetOpacity { get; set; } = 0.92;
    public bool WidgetAlwaysOnTop { get; set; } = true;
    public bool WidgetClickThrough { get; set; }
    public bool SnapWindowsToScreenEdges { get; set; } = true;
}

public static class SettingsApplication
{
    public static void Apply(AppSettings target, SettingsEdit edit)
    {
        target.PlanPreset = edit.PlanPreset;
        target.WeeklyProQuota = edit.WeeklyProQuota;
        target.DailyProQuota = edit.DailyProQuota;
        target.SolProDailyQuota = edit.SolProDailyQuota;
        target.CombinedDailyQuota = edit.CombinedDailyQuota;
        target.ReasoningQuota = edit.ReasoningQuota;
        target.ResetWeekday = edit.ResetWeekday;
        target.ResetTime = edit.ResetTime;
        target.ResetAnchorConfigured = edit.ResetAnchorConfigured;
        target.AuthTransport = edit.AuthTransport;
        target.CompanionExtensionId = edit.CompanionExtensionId;
        target.ChromeExtensionId = edit.ChromeExtensionId;
        target.EdgeExtensionId = edit.EdgeExtensionId;
        target.AutoSync = edit.AutoSync;
        target.SyncIntervalMinutes = Math.Clamp(edit.SyncIntervalMinutes, 5, 180);
        target.StartWithWindows = edit.StartWithWindows;
        target.FloatingWidgetEnabled = edit.FloatingWidgetEnabled;
        target.TaskbarStatusEnabled = edit.TaskbarStatusEnabled;
        target.CodexExePath = string.IsNullOrWhiteSpace(edit.CodexExePath) ? null : edit.CodexExePath.Trim();
        target.DisplayMode = edit.FloatingWidgetEnabled ? DisplayMode.TrayAndWidget : DisplayMode.TrayOnly;
        target.Theme = edit.Theme;
        target.TrayIconStyle = edit.TrayIconStyle;
        target.UiLanguage = edit.UiLanguage;
        target.NotifyAt20 = edit.NotifyAt20;
        target.NotifyAt10 = edit.NotifyAt10;
        target.NotifyExhausted = edit.NotifyExhausted;
        target.NotifyReset = edit.NotifyReset;
        target.NotifySyncError = edit.NotifySyncError;
        target.ImportHistoricalStatistics = edit.ImportHistoricalStatistics;
        target.WidgetOpacity = Math.Clamp(edit.WidgetOpacity, 0.3, 1);
        target.WidgetAlwaysOnTop = edit.WidgetAlwaysOnTop;
        target.WidgetClickThrough = edit.WidgetClickThrough;
        target.SnapWindowsToScreenEdges = edit.SnapWindowsToScreenEdges;
        if (!target.SnapWindowsToScreenEdges) target.ClearWindowEdgeAnchors();
    }
}
