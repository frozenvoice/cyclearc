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

    [Fact]
    public void BackupReaderDoesNotBlockPrimaryCommit()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 120 });
        store.Save(new AppSettings { FlyoutZoomPercent = 130 });
        var backup = File.ReadAllText(store.BackupPath);
        using (var reader = new FileStream(store.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Save(new AppSettings { FlyoutZoomPercent = 150, WidgetLeft = -200.25 });
            var restarted = new SettingsStore(PathName);
            Assert.Equal(150, restarted.Load().FlyoutZoomPercent);
            Assert.Equal(-200.25, restarted.Load().WidgetLeft);
            Assert.True(store.BackupRotationDeferred);
            Assert.True(restarted.BackupRotationDeferred);
            Assert.Equal(130, ReadSettings(store.RecoveryBackupPath).FlyoutZoomPercent);
            Assert.Equal(backup, File.ReadAllText(store.BackupPath));

            store.Save(new AppSettings { FlyoutZoomPercent = 140 });
            store.Save(new AppSettings { FlyoutZoomPercent = 130 });
            Assert.Equal(130, restarted.Load().FlyoutZoomPercent);
            Assert.Equal(140, ReadSettings(store.RecoveryBackupPath).FlyoutZoomPercent);
            Assert.Equal(backup, File.ReadAllText(store.BackupPath));
            Assert.Equal(2, Directory.GetFiles(_directory, "settings.json.bak*").Length);
            AssertNoTemporaryFiles();
        }

        // Rotation happens after this commit, so .bak must hold the immediately
        // preceding primary rather than the older pending backup.
        var afterRestart = new SettingsStore(PathName);
        afterRestart.Save(new AppSettings { FlyoutZoomPercent = 120 });
        Assert.False(afterRestart.BackupRotationDeferred);
        Assert.False(File.Exists(store.RecoveryBackupPath));
        Assert.Equal(130, ReadSettings(store.BackupPath).FlyoutZoomPercent);
        Assert.Equal(120, afterRestart.Load().FlyoutZoomPercent);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void WindowsLockedCanonicalBackupProducesExactRemoveReplacedError()
    {
        var store = CreateTwoVersions();
        var primary = File.ReadAllText(PathName);
        var backup = File.ReadAllText(store.BackupPath);
        var replacement = PathName + ".native-probe.tmp";
        File.Copy(PathName, replacement);
        using (var reader = LockForReading(store.BackupPath))
        {
            var error = Assert.Throws<IOException>(() =>
                File.Replace(replacement, PathName, store.BackupPath, ignoreMetadataErrors: true));
            Assert.Equal(unchecked((int)0x80070497), error.HResult);
            Assert.Equal(primary, File.ReadAllText(PathName));
            Assert.Equal(backup, File.ReadAllText(store.BackupPath));
            Assert.True(File.Exists(replacement));
        }
        File.Delete(replacement);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartRecoversPendingBackupBeforeOlderCanonicalAndPreservesBothOnRepair(bool missingPrimary)
    {
        var store = CreateDeferredBackup();
        var canonical = File.ReadAllText(store.BackupPath);
        var pending = File.ReadAllText(store.RecoveryBackupPath);
        if (missingPrimary) File.Delete(PathName);
        else File.WriteAllText(PathName, "{");

        var restarted = new SettingsStore(PathName);
        var recovered = restarted.Load();
        Assert.Equal(130, recovered.FlyoutZoomPercent);
        Assert.True(restarted.RecoveredFromBackup);
        Assert.True(restarted.BackupRotationDeferred);
        restarted.Save(recovered);
        Assert.Equal(canonical, File.ReadAllText(store.BackupPath));
        Assert.Equal(pending, File.ReadAllText(store.RecoveryBackupPath));
        Assert.Equal(130, new SettingsStore(PathName).Load().FlyoutZoomPercent);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CorruptPendingBackupFallsBackToCanonicalAndRepairPreservesBoth()
    {
        var store = CreateDeferredBackup();
        var canonical = File.ReadAllText(store.BackupPath);
        File.WriteAllText(store.RecoveryBackupPath, "{");
        File.WriteAllText(PathName, "{");
        var restarted = new SettingsStore(PathName);
        var recovered = restarted.Load();
        Assert.Equal(120, recovered.FlyoutZoomPercent);
        Assert.True(restarted.RecoveredFromBackup);
        restarted.Save(recovered);
        Assert.Equal(canonical, File.ReadAllText(store.BackupPath));
        Assert.Equal("{", File.ReadAllText(store.RecoveryBackupPath));
        Assert.Equal(120, ReadSettings(PathName).FlyoutZoomPercent);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void LockedPrimaryWithPendingBackupLeavesAllVersionsUntouched()
    {
        var store = CreateDeferredBackup();
        var before = ReadVersions(store);
        using (var reader = LockForReading(PathName))
            Assert.ThrowsAny<IOException>(() => store.Save(new AppSettings { FlyoutZoomPercent = 140 }));
        AssertVersionsUnchanged(store, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedPendingBackupCannotBeBypassedAndLeavesAllVersionsUntouched(bool lockCanonical)
    {
        var store = CreateDeferredBackup();
        var before = ReadVersions(store);
        using (var canonical = lockCanonical ? LockForReading(store.BackupPath) : null)
        using (var reader = LockForReading(store.RecoveryBackupPath))
            Assert.ThrowsAny<IOException>(() => store.Save(new AppSettings { FlyoutZoomPercent = 140 }));
        AssertVersionsUnchanged(store, before);
    }

    [Fact]
    public void UnusablePendingDestinationDoesNotCommitOrLoseCanonicalBackup()
    {
        var store = CreateTwoVersions();
        var primary = File.ReadAllText(PathName);
        var backup = File.ReadAllText(store.BackupPath);
        Directory.CreateDirectory(store.RecoveryBackupPath);
        using (var reader = LockForReading(store.BackupPath))
        {
            var error = Record.Exception(() => store.Save(new AppSettings { FlyoutZoomPercent = 140 }));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
        }
        Assert.Equal(primary, File.ReadAllText(PathName));
        Assert.Equal(backup, File.ReadAllText(store.BackupPath));
        Assert.True(Directory.Exists(store.RecoveryBackupPath));
        Assert.Empty(Directory.GetFileSystemEntries(store.RecoveryBackupPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void SerializationFailureWithPendingBackupLeavesAllVersionsAndCleansTemporary()
    {
        var store = CreateDeferredBackup();
        var before = ReadVersions(store);
        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings { WidgetLeft = double.NaN }));
        AssertVersionsUnchanged(store, before);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesCommitAndLoadWholeVersionsWithoutLeakingTemporaryFiles()
    {
        var initial = CreateTwoVersions();
        using (var canonical = LockForReading(initial.BackupPath))
        {
            initial.Save(Version(1));
            var writerOne = new SettingsStore(PathName);
            var writerTwo = new SettingsStore(PathName);
            var reader = new SettingsStore(PathName);
            var writers = new[] { writerOne, writerTwo };
            var tasks = writers.Select((writer, writerIndex) => Task.Run(() =>
            {
                for (var i = 1; i <= 30; i++)
                {
                    writer.Save(Version(2 + writerIndex * 30 + i));
                    AssertWholeVersion(writer.Load());
                }
            })).Append(Task.Run(() =>
            {
                for (var i = 0; i < 60; i++) AssertWholeVersion(reader.Load());
            }));
            await Task.WhenAll(tasks);
            AssertWholeVersion(ReadSettings(PathName));
            AssertWholeVersion(ReadSettings(initial.RecoveryBackupPath));
            Assert.Equal(120, ReadSettings(initial.BackupPath).FlyoutZoomPercent);
            Assert.Equal(2, Directory.GetFiles(_directory, "settings.json.bak*").Length);
        }
        new SettingsStore(PathName).Save(Version(100));
        AssertWholeVersion(ReadSettings(initial.BackupPath));
        Assert.False(File.Exists(initial.RecoveryBackupPath));
        AssertNoTemporaryFiles();
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

    private SettingsStore CreateTwoVersions()
    {
        var store = new SettingsStore(PathName);
        store.Save(new AppSettings { FlyoutZoomPercent = 120 });
        store.Save(new AppSettings { FlyoutZoomPercent = 130 });
        return store;
    }

    private SettingsStore CreateDeferredBackup()
    {
        var store = CreateTwoVersions();
        using (var reader = LockForReading(store.BackupPath))
            store.Save(new AppSettings { FlyoutZoomPercent = 150 });
        Assert.True(store.BackupRotationDeferred);
        Assert.Equal(130, ReadSettings(store.RecoveryBackupPath).FlyoutZoomPercent);
        return store;
    }

    private static FileStream LockForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static AppSettings ReadSettings(string path) => SettingsMigration.FromJson(File.ReadAllText(path));

    private (string Primary, string Canonical, string Pending) ReadVersions(SettingsStore store) =>
        (File.ReadAllText(PathName), File.ReadAllText(store.BackupPath), File.ReadAllText(store.RecoveryBackupPath));

    private void AssertVersionsUnchanged(SettingsStore store, (string Primary, string Canonical, string Pending) before)
    {
        Assert.Equal(before, ReadVersions(store));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

    private static AppSettings Version(int version) => new()
    {
        WidgetLeft = version,
        WidgetTop = -version,
        FlyoutZoomPercent = version % 2 == 0 ? 120 : 130,
        UiLanguage = version % 2 == 0 ? UiLanguage.English : UiLanguage.Korean
    };

    private static void AssertWholeVersion(AppSettings settings)
    {
        Assert.InRange(settings.WidgetLeft, 1, 100);
        Assert.Equal(Math.Truncate(settings.WidgetLeft), settings.WidgetLeft);
        Assert.Equal(-settings.WidgetLeft, settings.WidgetTop);
        var version = Version((int)settings.WidgetLeft);
        Assert.Equal(version.FlyoutZoomPercent, settings.FlyoutZoomPercent);
        Assert.Equal(version.UiLanguage, settings.UiLanguage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
