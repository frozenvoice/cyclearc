namespace CycleArc.Models;

public sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public bool FirstRunCompleted { get; set; }
    public SubscriptionPreset PlanPreset { get; set; } = SubscriptionPreset.Pro100;
    public int WeeklyProQuota { get; set; } = 50;
    public int? DailyProQuota { get; set; }
    public int? SolProDailyQuota { get; set; }
    public int? CombinedDailyQuota { get; set; }
    public int? ReasoningQuota { get; set; }
    public DayOfWeek ResetWeekday { get; set; } = DayOfWeek.Monday;
    public TimeSpan ResetTime { get; set; } = new(0, 0, 0);
    public string ResetTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool ResetAnchorConfigured { get; set; }
    public DisplayMode DisplayMode { get; set; } = DisplayMode.TrayOnly;
    private UsagePeriodPreference _usagePeriod = UsagePeriodPreference.Auto;
    public UsagePeriodPreference UsagePeriod
    {
        get => _usagePeriod;
        set => _usagePeriod = value is UsagePeriodPreference.Auto or UsagePeriodPreference.FiveHour or UsagePeriodPreference.Weekly
            ? value : UsagePeriodPreference.Auto;
    }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AuthTransportKind AuthTransport { get; set; } = AuthTransportKind.BrowserCompanion;
    public string? CompanionExtensionId { get; set; }
    public string? ChromeExtensionId { get; set; }
    public string? EdgeExtensionId { get; set; }
    public bool CompanionConnectOptIn { get; set; }
    public bool AutoSync { get; set; }
    public int SyncIntervalMinutes { get; set; } = 15;
    public int BodyFetchDelayMilliseconds { get; set; } = 250;
    public bool StartWithWindows { get; set; }
    public bool FloatingWidgetEnabled { get; set; }
    // Deprecated JSON compatibility only; unsafe taskbar overlays are never created.
    public bool TaskbarStatusEnabled { get; set; }
    public string? CodexExePath { get; set; }
    public static IReadOnlyList<int> CodexRefreshIntervals { get; } = Array.AsReadOnly(new[] { 1, 2, 5, 10, 30, 60 });
    private int _codexRefreshIntervalMinutes = 5;
    public int CodexRefreshIntervalMinutes
    {
        get => _codexRefreshIntervalMinutes;
        set => _codexRefreshIntervalMinutes = CodexRefreshIntervals.Contains(value) ? value : 5;
    }
    public double WidgetLeft { get; set; } = 40;
    public double WidgetTop { get; set; } = 40;
    // Physical screen coordinates are stable across mixed-DPI process restarts.
    public int? WidgetPixelLeft { get; set; }
    public int? WidgetPixelTop { get; set; }
    public double WidgetOpacity { get; set; } = 0.92;
    public bool WidgetAlwaysOnTop { get; set; } = true;
    public bool WidgetClickThrough { get; set; }
    public bool SnapWindowsToScreenEdges { get; set; } = true;
    public Codex.HorizontalEdgeAnchor WidgetHorizontalAnchor { get; set; }
    public Codex.VerticalEdgeAnchor WidgetVerticalAnchor { get; set; }
    public Codex.HorizontalEdgeAnchor FlyoutHorizontalAnchor { get; set; }
    public Codex.VerticalEdgeAnchor FlyoutVerticalAnchor { get; set; }
    public UiLanguage UiLanguage { get; set; } = UiLanguage.English;
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.RemainingNumber;
    // Deprecated: retained only for settings JSON compatibility. Detail Flyout no longer auto-hides on focus loss.
    public bool FlyoutCloseOnDeactivate { get; set; } = true;
    public bool FlyoutPinned { get; set; }
    public int FlyoutZoomPercent { get; set; } = 100;
    // The widget scales independently of the detail flyout. Settings written before this
    // existed simply get the default, which is what an unscaled widget already was.
    public int WidgetZoomPercent { get; set; } = 100;
    public double FlyoutLeft { get; set; }
    public double FlyoutTop { get; set; }
    public int? FlyoutPixelLeft { get; set; }
    public int? FlyoutPixelTop { get; set; }
    public bool FlyoutPositionConfigured { get; set; }
    public bool NotifyAt20 { get; set; } = true;
    public bool NotifyAt10 { get; set; } = true;
    public bool NotifyExhausted { get; set; } = true;
    public bool NotifyReset { get; set; } = true;
    public bool NotifySyncError { get; set; } = true;
    public bool ImportHistoricalStatistics { get; set; }
    public string LastNotifiedPeriodKey { get; set; } = "";
    public int LastNotifiedRemainingBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedExhausted { get; set; }
    public string? LastResetNotifiedPeriod { get; set; }
    public string LastNotifiedDailyKey { get; set; } = "";
    public int LastNotifiedSolDailyBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedSolDailyExhausted { get; set; }
    public int LastNotifiedCombinedDailyBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedCombinedDailyExhausted { get; set; }
    public string LastSyncErrorToastCategory { get; set; } = "";
    public string LastSyncErrorToastDetail { get; set; } = "";
    public string LastSyncErrorToastAt { get; set; } = "";
    public string LastSyncFailureCategory { get; set; } = "";
    public string LastSyncFailureAt { get; set; } = "";
    public string LastNotifiedProRestrictionState { get; set; } = "";
    public string LastNotifiedProRestrictionKey { get; set; } = "";

    public static AppSettings CreateDefaults() => new();

    public void ClearWindowEdgeAnchors()
    {
        WidgetHorizontalAnchor = FlyoutHorizontalAnchor = Codex.HorizontalEdgeAnchor.None;
        WidgetVerticalAnchor = FlyoutVerticalAnchor = Codex.VerticalEdgeAnchor.None;
    }

    public static AppSettings CreateNewInstall(CultureInfo? uiCulture = null)
    {
        var settings = CreateDefaults();
        var culture = uiCulture ?? CultureInfo.CurrentUICulture;
        if (culture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase)
            || culture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            settings.UiLanguage = UiLanguage.Korean;
        }

        return settings;
    }

    public void ApplyPreset(SubscriptionPreset preset)
    {
        PlanPreset = preset;
        switch (preset)
        {
            case SubscriptionPreset.Pro100:
                // Official ChatGPT Help (2026): Pro $100 shares 50 weekly messages
                // across GPT-6 Pro and GPT-5.6 Sol Pro.
                WeeklyProQuota = 50;
                DailyProQuota = null;
                SolProDailyQuota = null;
                CombinedDailyQuota = null;
                break;
            case SubscriptionPreset.Pro200:
                // Official ChatGPT Help (2026): Pro $200 has 200 GPT-6 Pro weekly,
                // 170 Sol Pro daily, and 200 combined daily.
                WeeklyProQuota = 200;
                DailyProQuota = null;
                SolProDailyQuota = 170;
                CombinedDailyQuota = 200;
                break;
        }
    }
}
