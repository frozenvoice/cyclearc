using System.Security.Cryptography;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class DesktopInstallTransactionTests
{
    [Fact]
    public void StagesAndVerifiesBeforeStoppingThenBacksUpExistingExecutable()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var events = new List<string>();

        var result = fixture.Create(
            stop: () =>
            {
                events.Add("stop");
                Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
            },
            startAndVerify: (target, _, _) =>
            {
                events.Add("start");
                Assert.Equal(fixture.TargetPath, target);
                Assert.Equal("new executable", File.ReadAllText(target));
            },
            stopFailed: () => events.Add("stop-failed"))
            .Install(source, InstallFixture.HashOf("new executable"));

        Assert.True(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Null(result.Failure);
        Assert.Equal(InstallFixture.HashOf("new executable"), result.SourceHash);
        Assert.Equal(new[] { "stop", "start" }, events);
        Assert.Equal("new executable", File.ReadAllText(fixture.TargetPath));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("old executable", File.ReadAllText(result.BackupPath));
        Assert.True(File.Exists(result.BackupPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void HashMismatchDoesNotStopOrMutateDestination()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var callbackCount = 0;

        var result = fixture.Create(
            stop: () => callbackCount++,
            startAndVerify: (_, _, _) => callbackCount++,
            stopFailed: () => callbackCount++)
            .Install(source, InstallFixture.HashOf("different executable"));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.Equal(0, callbackCount);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void RejectsUsingInstalledExecutableAsSourceBeforeCallbacks()
    {
        using var fixture = new InstallFixture();
        fixture.WriteTarget("old executable");
        var callbackCount = 0;
        var transaction = fixture.Create(
            stop: () => callbackCount++,
            startAndVerify: (_, _, _) => callbackCount++,
            stopFailed: () => callbackCount++);

        Assert.Throws<ArgumentException>(() => transaction.Install(
            fixture.TargetPath,
            InstallFixture.HashOf("old executable")));
        Assert.Equal(0, callbackCount);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
    }

    [Fact]
    public void StartFailureRestoresOldExecutableAndReportsInitialFailure()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var events = new List<string>();
        var initialFailure = new InvalidOperationException("new version failed health check");
        var starts = 0;

        var result = fixture.Create(
            stop: () => events.Add("stop"),
            startAndVerify: (target, restoringPrevious, _) =>
            {
                starts++;
                events.Add(starts == 1 ? "start-new" : "start-old");
                Assert.Equal(starts > 1, restoringPrevious);
                if (starts == 1)
                    throw initialFailure;
                Assert.Equal("old executable", File.ReadAllText(target));
            },
            stopFailed: () => events.Add("stop-failed"))
            .Install(source, InstallFixture.HashOf("new executable"));

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Same(initialFailure, result.Failure);
        Assert.Equal(new[] { "stop", "start-new", "stop-failed", "start-old" }, events);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
        Assert.NotNull(result.BackupPath);
        Assert.False(File.Exists(result.BackupPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void StartFailureKeepsInitialFailureWhenRollbackRestartFails()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var initialFailure = new InvalidOperationException("new version failed health check");
        var restartFailure = new InvalidOperationException("old version did not restart");
        var starts = 0;

        var result = fixture.Create(
            stop: () => { },
            startAndVerify: (_, restoringPrevious, _) =>
            {
                starts++;
                Assert.Equal(starts > 1, restoringPrevious);
                throw starts == 1 ? initialFailure : restartFailure;
            },
            stopFailed: () => { })
            .Install(source, InstallFixture.HashOf("new executable"));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Same(initialFailure, result.Failure);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void StopFailedFailurePreservesSwappedFilesAndDoesNotRestart()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var initialFailure = new InvalidOperationException("new version failed health check");
        var events = new List<string>();
        var starts = 0;

        var result = fixture.Create(
            stop: () => events.Add("stop"),
            startAndVerify: (_, restoringPrevious, _) =>
            {
                starts++;
                Assert.False(restoringPrevious);
                throw initialFailure;
            },
            stopFailed: () =>
            {
                events.Add("stop-failed");
                throw new IOException("failed process still owns the desktop mutex");
            })
            .Install(source, InstallFixture.HashOf("new executable"));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Same(initialFailure, result.Failure);
        Assert.Equal(new[] { "stop", "stop-failed" }, events);
        Assert.Equal(1, starts);
        Assert.Equal("new executable", File.ReadAllText(fixture.TargetPath));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("old executable", File.ReadAllText(result.BackupPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void RecoversDurableSwappedJournalBeforeStartingNextCandidate()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "next executable");
        fixture.WriteTarget("new executable");
        var transactionId = new string('a', 32);
        var stagingId = new string('b', 32);
        var backup = Path.Combine(fixture.DirectoryPath,
            DesktopInstallTransaction.BackupFilePrefix + transactionId + ".exe");
        var staging = Path.Combine(fixture.DirectoryPath,
            DesktopInstallTransaction.StagingFilePrefix + stagingId + ".exe");
        File.WriteAllText(backup, "old executable");
        var journal = Path.Combine(fixture.DirectoryPath, DesktopInstallTransaction.JournalFileName);
        File.WriteAllText(journal, $"{{\"TargetPath\":\"{fixture.TargetPath.Replace("\\", "\\\\")}\",\"BackupPath\":\"{backup.Replace("\\", "\\\\")}\",\"StagingPath\":\"{staging.Replace("\\", "\\\\")}\",\"TargetExisted\":true,\"Phase\":\"Swapped\"}}");
        var events = new List<string>();

        var result = fixture.Create(
            stop: () => events.Add("stop"),
            startAndVerify: (target, restoringPrevious, expectedHash) =>
            {
                events.Add(restoringPrevious ? "start-recovered-old" : "start-next");
                var expectedContents = restoringPrevious ? "old executable" : "next executable";
                Assert.Equal(expectedContents, File.ReadAllText(target));
                Assert.Equal(InstallFixture.HashOf(expectedContents), expectedHash);
            },
            stopFailed: () => events.Add("stop-failed"))
            .Install(source, InstallFixture.HashOf("next executable"));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "stop", "start-recovered-old", "stop", "start-next" }, events);
        Assert.Equal("next executable", File.ReadAllText(fixture.TargetPath));
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void InvalidJournalFailsClosedWithoutTouchingExecutable()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "next executable");
        fixture.WriteTarget("old executable");
        var journal = Path.Combine(fixture.DirectoryPath, DesktopInstallTransaction.JournalFileName);
        File.WriteAllText(journal, "{\"TargetPath\":\"C:\\\\outside\\CycleArc.exe\",\"BackupPath\":\"bad\",\"StagingPath\":\"bad\",\"TargetExisted\":true,\"Phase\":\"Swapped\"}");
        var callbackCount = 0;

        var result = fixture.Create(
            stop: () => callbackCount++,
            startAndVerify: (_, _, _) => callbackCount++,
            stopFailed: () => callbackCount++)
            .Install(source, InstallFixture.HashOf("next executable"));

        Assert.False(result.Succeeded);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.Equal(0, callbackCount);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void ReparsePointSourceIsRejectedBeforeStoppingApp()
    {
        using var fixture = new InstallFixture();
        var realSource = fixture.Write("candidate.exe", "next executable");
        var linkedSource = Path.Combine(fixture.DirectoryPath, "linked-candidate.exe");
        try
        {
            File.CreateSymbolicLink(linkedSource, realSource);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        fixture.WriteTarget("old executable");
        var callbackCount = 0;
        var result = fixture.Create(
            stop: () => callbackCount++,
            startAndVerify: (_, _, _) => callbackCount++,
            stopFailed: () => callbackCount++)
            .Install(linkedSource, InstallFixture.HashOf("next executable"));

        Assert.False(result.Succeeded);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(0, callbackCount);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
    }

    [Fact]
    public void LockTimeoutLeavesFilesUntouchedAndDoesNotStopApp()
    {
        using var fixture = new InstallFixture();
        var source = fixture.Write("candidate.exe", "new executable");
        fixture.WriteTarget("old executable");
        var lockPath = Path.Combine(fixture.DirectoryPath, DesktopInstallTransaction.LockFileName);
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var callbackCount = 0;

        var result = fixture.Create(
            stop: () => callbackCount++,
            startAndVerify: (_, _, _) => callbackCount++,
            stopFailed: () => callbackCount++,
            leaseTimeout: TimeSpan.FromMilliseconds(50),
            retryDelay: TimeSpan.FromMilliseconds(5))
            .Install(source, InstallFixture.HashOf("new executable"));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.IsType<TimeoutException>(result.Failure);
        Assert.Equal(0, callbackCount);
        Assert.Equal("old executable", File.ReadAllText(fixture.TargetPath));
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public void CreatesMissingCanonicalDirectoryAfterValidatingItsAncestors()
    {
        using var fixture = new InstallFixture(createDirectory: false);
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "CycleArcInstallSource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "candidate.exe");
        File.WriteAllText(source, "new executable");
        try
        {
            var result = fixture.Create(
                stop: () => { },
                startAndVerify: (target, restoringPrevious, expectedHash) =>
                {
                    Assert.False(restoringPrevious);
                    Assert.Equal(InstallFixture.HashOf("new executable"), expectedHash);
                    Assert.Equal("new executable", File.ReadAllText(target));
                },
                stopFailed: () => { })
                .Install(source, InstallFixture.HashOf("new executable"));

            Assert.True(result.Succeeded);
            Assert.True(Directory.Exists(fixture.DirectoryPath));
            Assert.Equal("new executable", File.ReadAllText(fixture.TargetPath));
            Assert.Null(result.BackupPath);
        }
        finally
        {
            try { Directory.Delete(sourceDirectory, recursive: true); }
            catch { }
        }
    }

    private sealed class InstallFixture : IDisposable
    {
        public InstallFixture(bool createDirectory = true)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "CycleArcInstallTests-" + Guid.NewGuid().ToString("N"));
            if (createDirectory) Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string TargetPath => Path.Combine(DirectoryPath, DesktopInstallTransaction.TargetFileName);

        public string BackupPath => Path.Combine(DirectoryPath, DesktopInstallTransaction.BackupFileName);

        public string Write(string name, string contents)
        {
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void WriteTarget(string contents) => File.WriteAllText(TargetPath, contents);

        public DesktopInstallTransaction Create(
            Action stop,
            Action<string, bool, string> startAndVerify,
            Action stopFailed,
            TimeSpan? leaseTimeout = null,
            TimeSpan? retryDelay = null) =>
            new(DirectoryPath, stop, startAndVerify, stopFailed,
                leaseTimeout: leaseTimeout,
                retryDelay: retryDelay);

        public IEnumerable<string> StagingFiles() =>
            Directory.EnumerateFiles(DirectoryPath, DesktopInstallTransaction.StagingFilePrefix + "*.exe");

        public static string HashOf(string contents) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch { }
        }
    }
}
