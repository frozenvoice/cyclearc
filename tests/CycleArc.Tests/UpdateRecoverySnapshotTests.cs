using System.Security.Cryptography;
using CycleArc.Updates;

namespace CycleArc.Tests;

public sealed class UpdateRecoverySnapshotTests
{
    [Fact]
    public void CreateCapturesCurrentTreeAndOptionalRootBootFiles()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        var files = PrepareInstallation(install.FullName, includeRootBootFiles: true);

        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-1"),
            "1.2.3");

        snapshot.Verify();
        Assert.Equal(files.Count, snapshot.Files.Length - 2);
        Assert.Contains(snapshot.Files, file => file.RelativePath == "CycleArc.exe");
        Assert.Contains(snapshot.Files, file => file.RelativePath == "sq.version");
        Assert.Contains(snapshot.Files, file => file.RelativePath == Path.Combine(".root", "Squirrel.exe"));
        Assert.Contains(snapshot.Files, file => file.RelativePath == Path.Combine(".root", "CycleArc_ExecutionStub.exe"));
        Assert.Equal(snapshot.Files.Single(file => file.RelativePath == "CycleArc.exe").Sha256,
            snapshot.ExecutableSha256);
    }

    [Fact]
    public void RestoreWaitsOutATransientLockOnTheReplacedInstallation()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: true);
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName, Path.Combine(snapshots.FullName, "snapshot-lock"), "1.2.3");
        var current = Path.Combine(install.FullName, "current");
        File.WriteAllText(Path.Combine(current, "CycleArc.exe"), "replaced-by-a-failed-update");

        // An update has just rewritten these files, so a scanner or the image of the process
        // that was started and quit can still hold them while recovery starts. Releasing the
        // handle shortly afterwards must let recovery finish rather than abandon the install.
        var released = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var handle = new FileStream(Path.Combine(current, "CycleArc.exe"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            released.Set();
            Thread.Sleep(TimeSpan.FromSeconds(1));
        });
        released.Wait(TimeSpan.FromSeconds(5));

        snapshot.Restore();

        holder.Wait(TimeSpan.FromSeconds(30));
        snapshot.Verify();
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(current, "CycleArc.exe")));
    }

    [Fact]
    public void RestoreRebuildsMissingCurrentAndRootBootFilesWithoutTouchingExternalData()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: true);
        File.WriteAllText(Path.Combine(external.FullName, "settings.json"), "keep-me");
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-2"),
            "1.2.3");

        Directory.Delete(Path.Combine(install.FullName, "current"), recursive: true);
        File.WriteAllText(Path.Combine(install.FullName, "Update.exe"), "new-update");
        File.WriteAllText(Path.Combine(install.FullName, "CycleArc.exe"), "new-stub");

        snapshot.Restore();

        Assert.Equal("old-app", File.ReadAllText(Path.Combine(install.FullName, "current", "CycleArc.exe")));
        Assert.Equal("old-version", File.ReadAllText(Path.Combine(install.FullName, "current", "sq.version")));
        Assert.Equal("old-update", File.ReadAllText(Path.Combine(install.FullName, "Update.exe")));
        Assert.Equal("old-stub", File.ReadAllText(Path.Combine(install.FullName, "CycleArc.exe")));
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(external.FullName, "settings.json")));
        snapshot.Verify();
    }

    [Fact]
    public void TamperedSnapshotOrUnrecordedAdditionIsRejectedBeforeRestore()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-3"),
            "1.2.3");
        var snapshotPath = Path.Combine(snapshot.SnapshotDirectory, "CycleArc.exe");
        File.AppendAllText(snapshotPath, "tampered");

        Assert.Throws<InvalidDataException>(() => snapshot.Verify());
        Assert.Throws<InvalidDataException>(() => snapshot.Restore());
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(install.FullName, "current", "CycleArc.exe")));
    }

    [Fact]
    public void AddedSnapshotFileAndUnsafeRecordPathAreRejected()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-4"),
            "1.2.3");
        File.WriteAllText(Path.Combine(snapshot.SnapshotDirectory, "unexpected.bin"), "extra");
        Assert.Throws<InvalidDataException>(() => snapshot.Verify());

        var unsafeSnapshot = snapshot with
        {
            Files = [.. snapshot.Files, new UpdateRecoveryFile("..\\outside.bin", 1, new string('A', 64))]
        };
        Assert.Throws<InvalidDataException>(() => unsafeSnapshot.Verify());
    }

    [Fact]
    public void InstallationAndSnapshotRootsCannotOverlap()
    {
        using var install = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);

        Assert.Throws<InvalidDataException>(() => UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(install.FullName, "recovery"),
            "1.2.3"));
        Assert.Throws<InvalidDataException>(() => UpdateRecoverySnapshot.Create(
            install.FullName,
            install.FullName,
            "1.2.3"));
    }

    [Fact]
    public void ReparsePointInsideCurrentIsRejectedWhenSupportedByHost()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var target = Path.Combine(install.FullName, "outside.txt");
        File.WriteAllText(target, "outside");
        var link = Path.Combine(install.FullName, "current", "redirected.txt");
        try { File.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        Assert.Throws<InvalidDataException>(() => UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-5"),
            "1.2.3"));
    }

    [Fact]
    public void FailedActivationRestoresOldCurrentAndLeavesExternalSnapshot()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-6"),
            "1.2.3");

        var current = Path.Combine(install.FullName, "current");
        Assert.Throws<IOException>(() => snapshot.Restore(() => Directory.CreateDirectory(current)));

        Assert.Equal("old-app", File.ReadAllText(Path.Combine(current, "CycleArc.exe")));
        Assert.True(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "CycleArc.exe")));
        snapshot.Verify();
    }

    [Fact]
    public void RootRestoreFailureRollsBackCurrentAndBothRootFiles()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: true);
        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-root-rollback"),
            "1.2.3");

        File.WriteAllText(Path.Combine(install.FullName, "current", "CycleArc.exe"), "new-app");
        File.WriteAllText(Path.Combine(install.FullName, "Update.exe"), "new-update");
        File.WriteAllText(Path.Combine(install.FullName, "CycleArc.exe"), "new-stub");

        Assert.Throws<IOException>(() => snapshot.Restore(
            beforeActivate: null,
            afterFirstRootFile: () => throw new IOException("simulated second root target failure")));

        Assert.Equal("new-app", File.ReadAllText(Path.Combine(install.FullName, "current", "CycleArc.exe")));
        Assert.Equal("new-update", File.ReadAllText(Path.Combine(install.FullName, "Update.exe")));
        Assert.Equal("new-stub", File.ReadAllText(Path.Combine(install.FullName, "CycleArc.exe")));
        Assert.True(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "CycleArc.exe")));
        snapshot.Verify();
    }

    [Fact]
    public void CurrentFilesNamedLikeRootBootBackupsArePreserved()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: true);
        File.WriteAllText(Path.Combine(install.FullName, "current", "Squirrel.exe"), "current-squirrel");
        File.WriteAllText(Path.Combine(install.FullName, "current", "CycleArc_ExecutionStub.exe"), "current-stub");

        var snapshot = UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-root-name-collision"),
            "1.2.3");
        Directory.Delete(Path.Combine(install.FullName, "current"), recursive: true);

        snapshot.Restore();

        Assert.Equal("current-squirrel", File.ReadAllText(Path.Combine(install.FullName, "current", "Squirrel.exe")));
        Assert.Equal("current-stub", File.ReadAllText(Path.Combine(install.FullName, "current", "CycleArc_ExecutionStub.exe")));
        Assert.Equal("old-update", File.ReadAllText(Path.Combine(install.FullName, "Update.exe")));
        Assert.Equal("old-stub", File.ReadAllText(Path.Combine(install.FullName, "CycleArc.exe")));
    }

    [Fact]
    public void DeepTreeIsRejectedBeforeFileHashing()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var directory = Path.Combine(install.FullName, "current");
        for (var index = 0; index < 40; index++)
        {
            directory = Path.Combine(directory, $"d{index}");
            Directory.CreateDirectory(directory);
        }

        Assert.Throws<InvalidDataException>(() => UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-too-deep"),
            "1.2.3"));
    }

    [Fact]
    public void FileCountLimitIsCheckedBeforeHashing()
    {
        using var install = new TemporaryDirectory();
        using var snapshots = new TemporaryDirectory();
        PrepareInstallation(install.FullName, includeRootBootFiles: false);
        var current = Path.Combine(install.FullName, "current");
        for (var index = 0; index < 254; index++)
            File.WriteAllText(Path.Combine(current, $"extra-{index}.bin"), "x");

        Assert.Throws<InvalidDataException>(() => UpdateRecoverySnapshot.Create(
            install.FullName,
            Path.Combine(snapshots.FullName, "snapshot-too-many-files"),
            "1.2.3"));
    }

    private static Dictionary<string, string> PrepareInstallation(string root, bool includeRootBootFiles)
    {
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CycleArc.exe"] = "old-app",
            ["sq.version"] = "old-version",
            [Path.Combine("lib", "CycleArc.Core.dll")] = "old-core",
        };
        foreach (var pair in files)
        {
            var path = Path.Combine(current, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value);
        }

        if (includeRootBootFiles)
        {
            File.WriteAllText(Path.Combine(root, "Update.exe"), "old-update");
            File.WriteAllText(Path.Combine(root, "CycleArc.exe"), "old-stub");
        }
        return files;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("cyclearc-recovery-");
        public string FullName => _directory.FullName;

        public void Dispose()
        {
            if (_directory.Exists)
                _directory.Delete(recursive: true);
        }
    }
}
