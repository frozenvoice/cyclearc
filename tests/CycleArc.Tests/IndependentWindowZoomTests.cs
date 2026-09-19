using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// The widget and the detail flyout scale independently. They share FlyoutZoom's arithmetic
/// and nothing else: separate stored values, separate change events, separate normalisation.
/// Which window actually receives Ctrl +/- is a WPF input concern and is covered by the
/// UiSmoke checks against real windows, not here.
/// </summary>
public sealed class IndependentWindowZoomTests
{
    [Fact]
    public void EachWindowHasItsOwnSetting()
    {
        var settings = new AppSettings();
        Assert.Equal(FlyoutZoom.DefaultPercent, settings.FlyoutZoomPercent);
        Assert.Equal(FlyoutZoom.DefaultPercent, settings.WidgetZoomPercent);

        settings.FlyoutZoomPercent = 130;
        settings.WidgetZoomPercent = 90;
        Assert.Equal(130, settings.FlyoutZoomPercent);
        Assert.Equal(90, settings.WidgetZoomPercent);
    }

    // Settings written before the widget could scale have no such property; the default is
    // the unscaled widget those settings already described.
    [Fact]
    public void OlderSettingsWithoutTheWidgetValueGetTheDefault()
    {
        var settings = SettingsMigration.FromJson(
            "{ \"Version\": 1, \"FlyoutZoomPercent\": 130, \"WidgetOpacity\": 0.9 }");
        Assert.Equal(FlyoutZoom.DefaultPercent, settings.WidgetZoomPercent);
        Assert.Equal(130, settings.FlyoutZoomPercent);
    }

    [Theory]
    [InlineData(0, FlyoutZoom.MinPercent)]
    [InlineData(-40, FlyoutZoom.MinPercent)]
    [InlineData(70, FlyoutZoom.MinPercent)]
    [InlineData(1000, FlyoutZoom.MaxPercent)]
    [InlineData(94, 90)]
    [InlineData(96, 100)]
    public void OutOfRangeValuesAreNormalisedPerWindow(int stored, int expected)
    {
        var migrated = SettingsMigration.FromJson(
            "{ \"FlyoutZoomPercent\": " + stored + ", \"WidgetZoomPercent\": " + stored + " }");
        Assert.Equal(expected, migrated.FlyoutZoomPercent);
        Assert.Equal(expected, migrated.WidgetZoomPercent);
    }

    // Normalising one must not read or write the other.
    [Fact]
    public void MigrationKeepsTheTwoValuesApart()
    {
        var migrated = SettingsMigration.FromJson(
            "{ \"FlyoutZoomPercent\": 130, \"WidgetZoomPercent\": 90 }");
        Assert.Equal(130, migrated.FlyoutZoomPercent);
        Assert.Equal(90, migrated.WidgetZoomPercent);
    }

    [Fact]
    public void AdjustStopsAtTheLimitsAndStepsByTen()
    {
        Assert.Equal(FlyoutZoom.MinPercent, FlyoutZoom.Adjust(FlyoutZoom.MinPercent, increase: false));
        Assert.Equal(FlyoutZoom.MaxPercent, FlyoutZoom.Adjust(FlyoutZoom.MaxPercent, increase: true));
        Assert.Equal(110, FlyoutZoom.Adjust(100, increase: true));
        Assert.Equal(90, FlyoutZoom.Adjust(100, increase: false));
        // Reachable from either limit, so a disabled button never traps a window at one end.
        Assert.Equal(FlyoutZoom.DefaultPercent, FlyoutZoom.Normalize(FlyoutZoom.DefaultPercent));
    }

    // An absent value is the default. An explicitly null one is not the same thing: like every
    // other number in this file it makes the whole file unreadable, so the store keeps the last
    // good copy instead of accepting a file it only partly understands. Pinned here because the
    // widget's size must not be the property that quietly changes that rule.
    [Fact]
    public void AnAbsentWidgetValueOpensUnscaledAndAnExplicitNullKeepsTheLastGoodFile()
    {
        Assert.Equal(FlyoutZoom.DefaultPercent,
            SettingsMigration.FromJson("{ \"FlyoutZoomPercent\": 130 }").WidgetZoomPercent);

        var folder = Path.Combine(Path.GetTempPath(), "CycleArc-zoom-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var store = new SettingsStore(path);
        try
        {
            Directory.CreateDirectory(folder);
            var good = store.Load();
            good.WidgetZoomPercent = 120;
            good.FlyoutZoomPercent = 130;
            store.Save(good);
            // Saved twice, so the backup the recovery path reads exists.
            store.Save(good);

            File.WriteAllText(path, "{ \"WidgetZoomPercent\": null, \"FlyoutZoomPercent\": 80 }");
            var recovered = store.Load();
            Assert.True(store.RecoveredFromBackup);
            Assert.Equal(120, recovered.WidgetZoomPercent);
            Assert.Equal(130, recovered.FlyoutZoomPercent);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // Through the real file, not only the in-memory object: one window's size must not be
    // dropped or overwritten by the other on the way to disk and back.
    [Fact]
    public void BothSizesSurviveARealSaveAndLoad()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CycleArc-zoom-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        try
        {
            Directory.CreateDirectory(folder);
            var settings = store.Load();
            settings.FlyoutZoomPercent = 130;
            settings.WidgetZoomPercent = 90;
            store.Save(settings);

            var reloaded = store.Load();
            Assert.Equal(130, reloaded.FlyoutZoomPercent);
            Assert.Equal(90, reloaded.WidgetZoomPercent);

            // Changing one and saving again leaves the other where it was.
            reloaded.WidgetZoomPercent = 120;
            store.Save(reloaded);
            var again = store.Load();
            Assert.Equal(130, again.FlyoutZoomPercent);
            Assert.Equal(120, again.WidgetZoomPercent);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // The hints name the window's own current size and the keyboard equivalent.
    [Fact]
    public void ZoomHintsCarryTheCurrentPercentage()
    {
        var previous = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            {
                UiText.SetLanguage(language);
                Assert.Contains("110", UiText.ZoomInHint(110), StringComparison.Ordinal);
                Assert.Contains("110", UiText.ZoomOutHint(110), StringComparison.Ordinal);
                Assert.NotEqual(UiText.ZoomInHint(110), UiText.ZoomOutHint(110));
                Assert.NotEmpty(UiText.ResetWidgetSize);
            }
        }
        finally { UiText.SetLanguage(previous); }
    }
}
