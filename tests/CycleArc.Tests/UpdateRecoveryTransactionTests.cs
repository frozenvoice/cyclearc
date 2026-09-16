using CycleArc.Updates;

namespace CycleArc.Tests;

public sealed class UpdateRecoveryTransactionTests
{
    [Fact]
    public void UpdaterRemovingCurrentThenThrowingRestoresPreviousFiles()
    {
        using var fixture = Fixture.Create();
        var calls = new List<string>();

        var result = UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () =>
            {
                calls.Add("apply");
                Directory.Delete(fixture.Current, recursive: true);
                throw new IOException("synthetic updater failure after removing current");
            },
            startAndVerify: () => throw new InvalidOperationException("new desktop must not start"),
            stopFailed: () => calls.Add("stop"),
            startAndVerifyPrevious: () =>
            {
                calls.Add("start-previous");
                AssertCurrent(fixture, "old-app", "old-version");
            });

        Assert.False(result.Succeeded);
        Assert.True(result.Restored);
        Assert.NotNull(result.Failure);
        Assert.Equal(["apply", "stop", "start-previous"], calls);
        AssertCurrent(fixture, "old-app", "old-version");
        fixture.Snapshot.Verify();
    }

    [Fact]
    public void NewDesktopReadinessFailureRestoresAfterSuccessfulReplacement()
    {
        using var fixture = Fixture.Create();
        var calls = new List<string>();

        var result = UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () =>
            {
                calls.Add("apply");
                ReplaceCurrent(fixture, "new-app", "new-version");
            },
            startAndVerify: () =>
            {
                calls.Add("start-new");
                throw new InvalidOperationException("new desktop never became ready");
            },
            stopFailed: () => calls.Add("stop"),
            startAndVerifyPrevious: () =>
            {
                calls.Add("start-previous");
                AssertCurrent(fixture, "old-app", "old-version");
            });

        Assert.False(result.Succeeded);
        Assert.True(result.Restored);
        Assert.Equal(["apply", "start-new", "stop", "start-previous"], calls);
        AssertCurrent(fixture, "old-app", "old-version");
        Assert.Equal("old-update", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal("old-stub", File.ReadAllText(fixture.Stub));
        fixture.Snapshot.Verify();
    }

    [Fact]
    public void UnstoppableFailedProcessLeavesCurrentUntouchedAndBackupRetained()
    {
        using var fixture = Fixture.Create();
        var stopCalled = false;

        var result = UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () => { },
            startAndVerify: () => throw new InvalidOperationException("synthetic failed process"),
            stopFailed: () =>
            {
                stopCalled = true;
                throw new IOException("failed process could not be stopped");
            },
            startAndVerifyPrevious: () => throw new InvalidOperationException("restore must not be attempted"));

        Assert.False(result.Succeeded);
        Assert.False(result.Restored);
        Assert.True(stopCalled);
        AssertCurrent(fixture, "old-app", "old-version");
        Assert.Equal("old-update", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal("old-stub", File.ReadAllText(fixture.Stub));
        fixture.Snapshot.Verify();
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void RestoredDesktopReadinessFailureLeavesPreviousFilesAndBackup()
    {
        using var fixture = Fixture.Create();
        var previousStartAttempts = 0;

        var result = UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () => ReplaceCurrent(fixture, "new-app", "new-version"),
            startAndVerify: () => throw new InvalidOperationException("new desktop readiness failure"),
            stopFailed: () => { },
            startAndVerifyPrevious: () =>
            {
                previousStartAttempts++;
                throw new InvalidOperationException("restored desktop readiness failure");
            });

        Assert.False(result.Succeeded);
        Assert.False(result.Restored);
        Assert.Equal(1, previousStartAttempts);
        AssertCurrent(fixture, "old-app", "old-version");
        Assert.Equal("old-update", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal("old-stub", File.ReadAllText(fixture.Stub));
        fixture.Snapshot.Verify();
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void TamperedBackupPreventsApplyBeforeAnyCallbackRuns()
    {
        using var fixture = Fixture.Create();
        File.AppendAllText(Path.Combine(fixture.Snapshot.SnapshotDirectory, "CycleArc.exe"), "tampered");
        var applyCalled = false;

        Assert.Throws<InvalidDataException>(() => UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () => applyCalled = true,
            startAndVerify: () => throw new InvalidOperationException(),
            stopFailed: () => throw new InvalidOperationException(),
            startAndVerifyPrevious: () => throw new InvalidOperationException()));

        Assert.False(applyCalled);
        AssertCurrent(fixture, "old-app", "old-version");
    }

    [Fact]
    public void SuccessfulApplyRetainsExternalRecoverySnapshotForCleanup()
    {
        using var fixture = Fixture.Create();
        var stopCalled = false;
        var previousStartCalled = false;

        var result = UpdateRecoveryTransaction.Run(
            fixture.Snapshot,
            apply: () => ReplaceCurrent(fixture, "new-app", "new-version"),
            startAndVerify: () => AssertCurrent(fixture, "new-app", "new-version"),
            stopFailed: () => stopCalled = true,
            startAndVerifyPrevious: () => previousStartCalled = true);

        Assert.True(result.Succeeded);
        Assert.False(result.Restored);
        Assert.Null(result.Failure);
        Assert.False(stopCalled);
        Assert.False(previousStartCalled);
        AssertCurrent(fixture, "new-app", "new-version");
        Assert.Equal("new-update", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal("new-stub", File.ReadAllText(fixture.Stub));
        Assert.True(File.Exists(Path.Combine(fixture.Snapshot.SnapshotDirectory, "CycleArc.exe")));
        fixture.Snapshot.Verify();
    }

    private static void ReplaceCurrent(Fixture fixture, string executable, string version)
    {
        Directory.Delete(fixture.Current, recursive: true);
        Directory.CreateDirectory(fixture.Current);
        File.WriteAllText(Path.Combine(fixture.Current, "CycleArc.exe"), executable);
        File.WriteAllText(Path.Combine(fixture.Current, "sq.version"), version);
        File.WriteAllText(fixture.UpdateExe, "new-update");
        File.WriteAllText(fixture.Stub, "new-stub");
    }

    private static void AssertCurrent(Fixture fixture, string executable, string version)
    {
        Assert.Equal(executable, File.ReadAllText(Path.Combine(fixture.Current, "CycleArc.exe")));
        Assert.Equal(version, File.ReadAllText(Path.Combine(fixture.Current, "sq.version")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _root = new("cyclearc-recovery-transaction-");
        private readonly TemporaryDirectory _snapshots = new("cyclearc-recovery-backup-");

        private Fixture()
        {
            Root = _root.FullName;
            Current = Directory.CreateDirectory(Path.Combine(Root, "current")).FullName;
            UpdateExe = Path.Combine(Root, "Update.exe");
            Stub = Path.Combine(Root, "CycleArc.exe");
            File.WriteAllText(Path.Combine(Current, "CycleArc.exe"), "old-app");
            File.WriteAllText(Path.Combine(Current, "sq.version"), "old-version");
            File.WriteAllText(UpdateExe, "old-update");
            File.WriteAllText(Stub, "old-stub");
            Snapshot = UpdateRecoverySnapshot.Create(Root, Path.Combine(_snapshots.FullName, "snapshot"), "0.5.9");
        }

        public string Root { get; }
        public string Current { get; }
        public string UpdateExe { get; }
        public string Stub { get; }
        public UpdateRecoverySnapshot Snapshot { get; }

        public static Fixture Create() => new();

        public void Dispose()
        {
            _root.Dispose();
            _snapshots.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory;

        public TemporaryDirectory(string prefix)
        {
            _directory = Directory.CreateTempSubdirectory(prefix);
        }

        public string FullName => _directory.FullName;

        public void Dispose()
        {
            if (Directory.Exists(FullName))
                Directory.Delete(FullName, recursive: true);
        }
    }
}
