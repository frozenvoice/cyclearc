using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class WindowEdgeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyclearc-edge-settings-" + Guid.NewGuid());

    [Fact]
    public void OldSettingsEnableFeatureWithoutInferringAnchorsOrMovingWindows()
    {
        var settings = SettingsMigration.FromJson("""{"WidgetLeft":8,"WidgetTop":8,"FlyoutLeft":1472,"FlyoutTop":700,"FlyoutPositionConfigured":true}""");
        Assert.True(settings.SnapWindowsToScreenEdges);
        Assert.Equal(HorizontalEdgeAnchor.None, settings.WidgetHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.None, settings.WidgetVerticalAnchor);
        Assert.Equal(HorizontalEdgeAnchor.None, settings.FlyoutHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.None, settings.FlyoutVerticalAnchor);
        Assert.Equal(8, settings.WidgetLeft);
        Assert.Equal(700, settings.FlyoutTop);
    }

    [Fact]
    public void RestartAndBackupPreserveIndependentAnchorsPixelsAndZoom()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);
        var settings = AttachedSettings();
        store.Save(settings);
        settings.WidgetZoomPercent = 150;
        store.Save(settings);
        var loaded = new SettingsStore(path).Load();
        AssertAnchors(loaded);
        Assert.Equal(150, loaded.WidgetZoomPercent);
        Assert.Equal(80, loaded.FlyoutZoomPercent);
        Assert.Equal(-2000, loaded.WidgetPixelLeft);
        Assert.Equal(-1200, loaded.FlyoutPixelLeft);
        File.WriteAllText(path, "{");
        loaded = store.Load();
        Assert.True(store.RecoveredFromBackup);
        AssertAnchors(loaded);
        Assert.Equal(100, loaded.WidgetZoomPercent);
    }

    [Fact]
    public void UnrelatedSettingsEditPreservesBothWindowsAndDisableReenableDoesNotReattach()
    {
        var settings = AttachedSettings();
        SettingsApplication.Apply(settings, new SettingsEdit { SnapWindowsToScreenEdges = true, Theme = AppTheme.Light });
        AssertAnchors(settings);
        SettingsApplication.Apply(settings, new SettingsEdit { SnapWindowsToScreenEdges = false });
        AssertDetached(settings);
        Assert.Equal(-1500, settings.WidgetLeft);
        Assert.Equal(-1000, settings.FlyoutLeft);
        SettingsApplication.Apply(settings, new SettingsEdit { SnapWindowsToScreenEdges = true });
        AssertDetached(settings);
    }

    [Fact]
    public void LoadNormalizesUnknownAnchorsAndClearsDisabledStateRegardlessOfJsonOrder()
    {
        var settings = SettingsMigration.FromJson("""{"WidgetHorizontalAnchor":99,"WidgetVerticalAnchor":-1,"FlyoutHorizontalAnchor":2,"FlyoutVerticalAnchor":1}""");
        Assert.Equal(HorizontalEdgeAnchor.None, settings.WidgetHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.None, settings.WidgetVerticalAnchor);
        Assert.Equal(HorizontalEdgeAnchor.Right, settings.FlyoutHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.Top, settings.FlyoutVerticalAnchor);
        AssertDetached(SettingsMigration.FromJson("""{"SnapWindowsToScreenEdges":false,"WidgetHorizontalAnchor":2,"FlyoutVerticalAnchor":2}"""));
    }

    private static AppSettings AttachedSettings() => new()
    {
        WidgetHorizontalAnchor = HorizontalEdgeAnchor.Right, WidgetVerticalAnchor = VerticalEdgeAnchor.Bottom,
        FlyoutHorizontalAnchor = HorizontalEdgeAnchor.Left, FlyoutVerticalAnchor = VerticalEdgeAnchor.Top,
        WidgetLeft = -1500, WidgetTop = 500, WidgetPixelLeft = -2000, WidgetPixelTop = 750,
        FlyoutLeft = -1000, FlyoutTop = 40, FlyoutPixelLeft = -1200, FlyoutPixelTop = 60,
        FlyoutPositionConfigured = true, FlyoutZoomPercent = 80
    };

    private static void AssertAnchors(AppSettings settings)
    {
        Assert.Equal(HorizontalEdgeAnchor.Right, settings.WidgetHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.Bottom, settings.WidgetVerticalAnchor);
        Assert.Equal(HorizontalEdgeAnchor.Left, settings.FlyoutHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.Top, settings.FlyoutVerticalAnchor);
    }

    private static void AssertDetached(AppSettings settings)
    {
        Assert.Equal(HorizontalEdgeAnchor.None, settings.WidgetHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.None, settings.WidgetVerticalAnchor);
        Assert.Equal(HorizontalEdgeAnchor.None, settings.FlyoutHorizontalAnchor);
        Assert.Equal(VerticalEdgeAnchor.None, settings.FlyoutVerticalAnchor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
