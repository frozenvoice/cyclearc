using System.Text.Json;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class SettingsApplicationTests
{
    [Fact]
    public void SavingOtherSettings_DoesNotConfirmResetAnchor()
    {
        var settings = AppSettings.CreateDefaults();
        Assert.False(settings.ResetAnchorConfigured);

        SettingsApplication.Apply(settings, BaseEdit(settings) with
        {
            Theme = AppTheme.Dark,
            AutoSync = true,
            StartWithWindows = true,
            SyncIntervalMinutes = 30,
            ResetWeekday = DayOfWeek.Wednesday,
            ResetTime = TimeSpan.FromHours(8),
            ResetAnchorConfigured = false
        });

        Assert.False(settings.ResetAnchorConfigured);
        Assert.Equal(DayOfWeek.Wednesday, settings.ResetWeekday);
        Assert.Equal(TimeSpan.FromHours(8), settings.ResetTime);
        Assert.True(settings.AutoSync);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public void CheckingResetAnchor_MarksUserConfigured()
    {
        var settings = AppSettings.CreateDefaults();
        SettingsApplication.Apply(settings, BaseEdit(settings) with { ResetAnchorConfigured = true });
        Assert.True(settings.ResetAnchorConfigured);

        var coverage = new CoverageInfo { NormalChats = true };
        var snapshot = new QuotaEngine().Build(
            [],
            settings,
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            coverage,
            new QuotaMetadataSet(),
            AppSyncStatus.UpToDate);
        Assert.Equal(ResetAnchorSource.UserConfigured, snapshot.ResetAnchorSource);
        Assert.Equal(CoverageConfidence.HighConfidence, coverage.CountConfidence);
    }

    [Fact]
    public void UncheckingResetAnchor_ReturnsToDefaultEstimated()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetAnchorConfigured = true;
        SettingsApplication.Apply(settings, BaseEdit(settings) with { ResetAnchorConfigured = false });
        Assert.False(settings.ResetAnchorConfigured);

        var coverage = new CoverageInfo { NormalChats = true };
        var snapshot = new QuotaEngine().Build(
            [],
            settings,
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            coverage,
            new QuotaMetadataSet(),
            AppSyncStatus.UpToDate);
        Assert.Equal(ResetAnchorSource.Default, snapshot.ResetAnchorSource);
        Assert.Equal(CoverageConfidence.Estimated, coverage.CountConfidence);
        Assert.True(snapshot.ResetEstimated);
    }

    [Fact]
    public void ExistingSettingsWithoutFlag_RemainUnconfirmed()
    {
        var json = """{"version":1,"autoSync":true,"startWithWindows":true}""";
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, ChatGptJson.Options);
        Assert.NotNull(loaded);
        Assert.False(loaded.ResetAnchorConfigured);
    }

    [Fact]
    public void Apply_DoesNotChangeDeprecatedFlyoutCloseOnDeactivate()
    {
        var settings = AppSettings.CreateDefaults();
        settings.FlyoutCloseOnDeactivate = false;
        SettingsApplication.Apply(settings, BaseEdit(settings) with { Theme = AppTheme.Dark });
        Assert.False(settings.FlyoutCloseOnDeactivate);

        settings.FlyoutCloseOnDeactivate = true;
        SettingsApplication.Apply(settings, BaseEdit(settings) with { AutoSync = true });
        Assert.True(settings.FlyoutCloseOnDeactivate);
    }

    [Fact]
    public void DeprecatedFlyoutCloseOnDeactivate_StillDeserializes()
    {
        var json = """{"version":2,"flyoutCloseOnDeactivate":false,"flyoutPinned":true}""";
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, ChatGptJson.Options);
        Assert.NotNull(loaded);
        Assert.False(loaded.FlyoutCloseOnDeactivate);
        Assert.True(loaded.FlyoutPinned);
    }

    private static SettingsEdit BaseEdit(AppSettings settings) => new()
    {
        PlanPreset = settings.PlanPreset,
        WeeklyProQuota = settings.WeeklyProQuota,
        DailyProQuota = settings.DailyProQuota,
        SolProDailyQuota = settings.SolProDailyQuota,
        CombinedDailyQuota = settings.CombinedDailyQuota,
        ReasoningQuota = settings.ReasoningQuota,
        ResetWeekday = settings.ResetWeekday,
        ResetTime = settings.ResetTime,
        ResetAnchorConfigured = settings.ResetAnchorConfigured,
        AuthTransport = settings.AuthTransport,
        CompanionExtensionId = settings.CompanionExtensionId,
        ChromeExtensionId = settings.ChromeExtensionId,
        EdgeExtensionId = settings.EdgeExtensionId,
        AutoSync = settings.AutoSync,
        SyncIntervalMinutes = settings.SyncIntervalMinutes,
        StartWithWindows = settings.StartWithWindows,
        FloatingWidgetEnabled = settings.FloatingWidgetEnabled,
        TaskbarStatusEnabled = settings.TaskbarStatusEnabled,
        CodexExePath = settings.CodexExePath,
        Theme = settings.Theme,
        TrayIconStyle = settings.TrayIconStyle,
        NotifyAt20 = settings.NotifyAt20,
        NotifyAt10 = settings.NotifyAt10,
        NotifyExhausted = settings.NotifyExhausted,
        NotifyReset = settings.NotifyReset,
        NotifySyncError = settings.NotifySyncError,
        ImportHistoricalStatistics = settings.ImportHistoricalStatistics,
        WidgetOpacity = settings.WidgetOpacity,
        WidgetAlwaysOnTop = settings.WidgetAlwaysOnTop,
        SnapWindowsToScreenEdges = settings.SnapWindowsToScreenEdges,
        WidgetClickThrough = settings.WidgetClickThrough
    };
}

public class StartupConsentTests
{
    [Fact]
    public void FirstRun_DoesNotWriteStartup()
    {
        var startup = new RecordingStartup();
        var settings = AppSettings.CreateDefaults();
        Assert.False(settings.FirstRunCompleted);
        Assert.False(settings.StartWithWindows);
        StartupConsent.ApplyIfPermitted(startup, settings);
        Assert.Empty(startup.Calls);
    }

    [Fact]
    public void CompletedOnboarding_AppliesExplicitChoice()
    {
        var startup = new RecordingStartup();
        var settings = AppSettings.CreateDefaults();
        settings.FirstRunCompleted = true;
        settings.StartWithWindows = true;
        StartupConsent.ApplyIfPermitted(startup, settings);
        Assert.Equal(new[] { true }, startup.Calls);

        settings.StartWithWindows = false;
        StartupConsent.ApplyIfPermitted(startup, settings);
        Assert.Equal(new[] { true, false }, startup.Calls);
    }

    [Fact]
    public void NewInstallDefaults_AreOptIn()
    {
        var settings = AppSettings.CreateDefaults();
        Assert.False(settings.AutoSync);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.TaskbarStatusEnabled);
        Assert.True(string.IsNullOrWhiteSpace(settings.CodexExePath));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
    }

    private sealed class RecordingStartup : IWindowsStartup
    {
        public List<bool> Calls { get; } = [];
        public void Apply(bool enabled) => Calls.Add(enabled);
    }
}
