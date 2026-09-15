using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class UsagePeriodPreferenceTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.Parse("2026-09-20T00:00:00Z");

    [Fact]
    public void AutoPrefersKnownFiveHourIncludingZero()
    {
        var weekly = Window(CodexWindowKind.Weekly, 31);
        var fiveHour = Window(CodexWindowKind.FiveHour, 0);
        var snapshot = Snapshot(weekly, fiveHour);

        Assert.Same(fiveHour, snapshot.DisplayWindow());
        Assert.Same(fiveHour, snapshot.CompactWindow);
        Assert.Same(fiveHour, CodexRingPresentation.From(snapshot).Window);
        Assert.Equal(0, CodexRingPresentation.From(snapshot).UsedPercent);
    }

    [Fact]
    public void AutoUsesKnownWeeklyWhenFiveHourIsUnknown()
    {
        var fiveHour = Window(CodexWindowKind.FiveHour, null);
        var weekly = Window(CodexWindowKind.Weekly, 42);
        var snapshot = Snapshot(fiveHour, weekly);

        Assert.Same(weekly, snapshot.DisplayWindow());
        Assert.Contains("42%", CycleArcPresentation.CompactText(snapshot));
        Assert.Contains("weekly", CycleArcPresentation.Tooltip(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UsagePeriodPreference.FiveHour, CodexWindowKind.FiveHour)]
    [InlineData(UsagePeriodPreference.Weekly, CodexWindowKind.Weekly)]
    public void ExplicitPreferenceChoosesKnownRequestedWindow(UsagePeriodPreference preference, CodexWindowKind expected)
    {
        var fiveHour = Window(CodexWindowKind.FiveHour, 12);
        var weekly = Window(CodexWindowKind.Weekly, 88);

        Assert.Equal(expected, Snapshot(fiveHour, weekly).DisplayWindow(preference)?.Kind);
    }

    [Fact]
    public void ExplicitPreferenceFallsBackToOtherKnownStandardWindow()
    {
        var fiveHour = Window(CodexWindowKind.FiveHour, null);
        var weekly = Window(CodexWindowKind.Weekly, 63);

        Assert.Same(weekly, Snapshot(fiveHour, weekly).DisplayWindow(UsagePeriodPreference.FiveHour));
    }

    [Fact]
    public void OtherDurationKnownIsFallbackAfterStandardWindows()
    {
        var weekly = Window(CodexWindowKind.Weekly, null);
        var fiveHour = Window(CodexWindowKind.FiveHour, null);
        var longerOther = Window(CodexWindowKind.Other, 17, 1440);
        var shorterOther = Window(CodexWindowKind.Other, 23, 60);

        Assert.Same(longerOther, Snapshot(weekly, fiveHour, shorterOther, longerOther)
            .DisplayWindow(UsagePeriodPreference.Weekly));
    }

    [Fact]
    public void UnknownRequestedWindowIsPreservedWhenNoKnownWindowExists()
    {
        var fiveHour = Window(CodexWindowKind.FiveHour, double.NaN);
        var weekly = Window(CodexWindowKind.Weekly, null);
        var snapshot = Snapshot(fiveHour, weekly);

        Assert.Same(fiveHour, snapshot.DisplayWindow(UsagePeriodPreference.FiveHour));
        Assert.Same(weekly, snapshot.DisplayWindow(UsagePeriodPreference.Weekly));
        Assert.Same(fiveHour, snapshot.DisplayWindow());
        Assert.Null(CodexRingPresentation.From(snapshot, UsagePeriodPreference.FiveHour).UsedPercent);
    }

    [Fact]
    public void AvailableUnknownWindowIsUsedWhenRequestedWindowIsAbsent()
    {
        var other = Window(CodexWindowKind.Other, null, 900);

        Assert.Same(other, Snapshot(other).DisplayWindow(UsagePeriodPreference.FiveHour));
    }

    [Fact]
    public void UsagePeriodRoundTripsThroughSettingsStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyclearc-period-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            new SettingsStore(path).Save(new AppSettings { UsagePeriod = UsagePeriodPreference.Weekly });
            Assert.Equal(UsagePeriodPreference.Weekly, new SettingsStore(path).Load().UsagePeriod);
            new SettingsStore(path).Save(new AppSettings { UsagePeriod = UsagePeriodPreference.Auto });
            File.WriteAllText(path, "{broken");
            var recovered = new SettingsStore(path);
            Assert.Equal(UsagePeriodPreference.Weekly, recovered.Load().UsagePeriod);
            Assert.True(recovered.RecoveredFromBackup);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidUsagePeriodFallsBackToAutoAndPersistsValidValue()
    {
        Assert.Equal(UsagePeriodPreference.Auto, SettingsMigration.FromJson("{}").UsagePeriod);
        var settings = AppSettings.CreateDefaults();
        Assert.Equal(UsagePeriodPreference.Auto, settings.UsagePeriod);

        settings.UsagePeriod = (UsagePeriodPreference)99;
        Assert.Equal(UsagePeriodPreference.Auto, settings.UsagePeriod);

        settings.UsagePeriod = UsagePeriodPreference.Weekly;
        var loaded = SettingsMigration.FromJson(JsonSerializer.Serialize(settings));
        Assert.Equal(UsagePeriodPreference.Weekly, loaded.UsagePeriod);

        var invalid = SettingsMigration.FromJson("""{"usagePeriod":99}""");
        Assert.Equal(UsagePeriodPreference.Auto, invalid.UsagePeriod);
    }

    [Fact]
    public void OverviewPreferencePropagatesToTrayTooltipAndKeepsTwoArgumentConstructor()
    {
        var previous = UiText.Language;
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var snapshot = Snapshot(Window(CodexWindowKind.FiveHour, 12), Window(CodexWindowKind.Weekly, 88));
            var account = new CodexAccountView(new CodexAccountProfile("period", "", "Period"), snapshot);
            var overview = UsageAccountOverview.Create([account], account.Profile.Id, UsagePeriodPreference.FiveHour);
            Assert.Equal(UsagePeriodPreference.FiveHour, overview.Preference);
            Assert.Contains("5-hour", overview.Tooltip, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("12%", CycleArcPresentation.TrayTooltip(snapshot, preference: UsagePeriodPreference.FiveHour));

            var legacy = new UsageAccountOverview([account], account);
            Assert.Equal(UsagePeriodPreference.Auto, legacy.Preference);
        }
        finally
        {
            UiText.SetLanguage(previous);
        }
    }

    private static CodexQuotaSnapshot Snapshot(params CodexQuotaWindow[] windows) =>
        new(CodexQuotaStatus.Available, "pro", Reset, Reset, null, null, null, windows, null);

    private static CodexQuotaWindow Window(CodexWindowKind kind, double? used, int? duration = null) =>
        new(kind.ToString(), used, duration ?? kind switch
        {
            CodexWindowKind.FiveHour => CodexWindowClassifier.FiveHourMinutes,
            CodexWindowKind.Weekly => CodexWindowClassifier.WeeklyMinutes,
            _ => 900
        }, Reset, kind);
}