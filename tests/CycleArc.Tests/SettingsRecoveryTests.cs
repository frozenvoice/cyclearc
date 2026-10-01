using CycleArc.Services;

namespace CycleArc.Tests;

public class SettingsRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cyclearc-settings-" + Guid.NewGuid());
    private string PathName => Path.Combine(_directory, "settings.json");

    [Fact]
    public void UsageCardExpansionSurvivesSaveEditRestartAndBackup()
    {
        var id = Guid.NewGuid().ToString("N");
        var store = new SettingsStore(PathName);
        var settings = new AppSettings { UsageCardExpandedAccounts = new() { [id] = true } };
        store.Save(settings);
        SettingsApplication.Apply(settings, new SettingsEdit());
        store.Save(settings);
        Assert.True(new SettingsStore(PathName).Load().UsageCardExpandedAccounts[id]);
        File.WriteAllText(PathName, "broken");
        Assert.True(store.Load().UsageCardExpandedAccounts[id]);
        Assert.Empty(SettingsMigration.FromJson("{}").UsageCardExpandedAccounts);
        Assert.Empty(SettingsMigration.FromJson("{\"UsageCardExpandedAccounts\":null}").UsageCardExpandedAccounts);
    }

    [Fact]
    public void SaveKeepsPreviousNormalVersionAndPreservesPreferences()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 120, WidgetLeft = -200, UiLanguage = UiLanguage.Korean });
        store.Save(new AppSettings { FlyoutZoomPercent = 140, WidgetLeft = -300, UiLanguage = UiLanguage.English });
        Assert.Equal(140, store.Load().FlyoutZoomPercent);
        var previous = new SettingsStore(store.BackupPath).Load();
        Assert.Equal(120, previous.FlyoutZoomPercent);
        Assert.Equal(-200, previous.WidgetLeft);
        Assert.Equal(UiLanguage.Korean, previous.UiLanguage);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"FlyoutZoomPercent\":\"bad\"}")]
    public void DamagedPrimaryRecoversWithoutOverwritingGoodBackup(string invalid)
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 130 });
        store.Save(new AppSettings { FlyoutZoomPercent = 150 });
        File.WriteAllText(PathName, invalid);
        var recovered = store.Load();
        Assert.True(store.RecoveredFromBackup);
        Assert.Equal(130, recovered.FlyoutZoomPercent);
        var backup = File.ReadAllText(store.BackupPath);
        store.Save(recovered);
        Assert.Equal(backup, File.ReadAllText(store.BackupPath));
        Assert.Equal(130, new SettingsStore(PathName).Load().FlyoutZoomPercent);
    }

    [Fact]
    public void MissingPrimaryCanRecoverFromBackup()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 120 });
        store.Save(new AppSettings { FlyoutZoomPercent = 140 });
        File.Delete(PathName);
        Assert.Equal(120, store.Load().FlyoutZoomPercent);
        Assert.True(store.RecoveredFromBackup);
    }

    [Fact]
    public void InterruptedTemporaryWriteDoesNotAffectPrimary()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 140 });
        File.WriteAllText(PathName + ".interrupted.tmp", "{");
        Assert.Equal(140, new SettingsStore(PathName).Load().FlyoutZoomPercent);
    }

    [Fact]
    public void FailedReplacementLeavesPrimaryAndBackupUntouched()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 120 });
        store.Save(new AppSettings { FlyoutZoomPercent = 130 });
        var primary = File.ReadAllText(PathName);
        var backup = File.ReadAllText(store.BackupPath);
        using (var locked = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => store.Save(new AppSettings { FlyoutZoomPercent = 150 }));
        Assert.Equal(primary, File.ReadAllText(PathName));
        Assert.Equal(backup, File.ReadAllText(store.BackupPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
