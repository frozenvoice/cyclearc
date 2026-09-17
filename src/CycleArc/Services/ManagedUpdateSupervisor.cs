using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using CycleArc.Updates;

namespace CycleArc.Services;

/// <summary>
/// Runs a copy of the previous executable outside Velopack's installation. The helper
/// survives replacement, and restores the previous files if apply or desktop startup fails.
/// It never opens settings, account stores, credentials or Claude configuration.
/// </summary>
internal static class ManagedUpdateSupervisor
{
    internal const string Argument = "--apply-update";
    private static string RecoveryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CycleArc-update-recovery");
    private static string PipeName => DesktopInstancePipe.ForCurrentUserSession();
    private sealed record Job(UpdateRecoverySnapshot Snapshot, string PackageFileName, long PackageSize,
        string PackageSha256, string TargetVersion, string TargetExecutableSha256,
        int PreviousProcessId, long PreviousStartedUtcTicks, bool Korean);

    internal static void Prepare(string installationRoot, string packageFileName, long packageSize,
        string packageSha256, string targetVersion, string currentVersion)
    {
        var currentExecutable = Path.Combine(installationRoot, "current", "CycleArc.exe");
        if (!SamePath(Environment.ProcessPath, currentExecutable))
            throw new IOException("Only the running managed installation can apply this update.");
        CleanupCompleted();
        Directory.CreateDirectory(RecoveryRoot);
        RejectRedirected(RecoveryRoot);
        var directory = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = UpdateRecoverySnapshot.Create(installationRoot, Path.Combine(directory, "snapshot"), currentVersion);
        var packages = Path.Combine(installationRoot, "packages");
        UpdatePackageVerifier.VerifyAsync(packages, packageFileName, packageSize, packageSha256).GetAwaiter().GetResult();
        using var process = Process.GetCurrentProcess();
        var job = new Job(snapshot, packageFileName, packageSize, packageSha256, targetVersion,
            ReadPackageExecutableHash(Path.Combine(packages, packageFileName)),
            process.Id, process.StartTime.ToUniversalTime().Ticks, UiText.IsKorean);
        var jobPath = Path.Combine(directory, "job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job));
        using var helper = Start(Path.Combine(snapshot.SnapshotDirectory, "CycleArc.exe"), Argument, jobPath);
        // The app keeps running unless its recovery supervisor has validated the job.
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (helper.HasExited) throw new IOException("The recovery helper could not start.");
            if (File.Exists(Path.Combine(directory, "ready"))) return;
            Thread.Sleep(100);
        }
        helper.Kill();
        helper.WaitForExit(10000);
        throw new IOException("The recovery helper did not become ready.");
    }

    internal static int Run(string jobPath)
    {
        Job? job = null;
        string? directory = null;
        DesktopInstanceLease? lease = null;
        Process? started = null;
        try
        {
            directory = ValidateJobPath(jobPath);
            if (new FileInfo(jobPath).Length > 128 * 1024) throw new InvalidDataException("Invalid recovery job.");
            job = JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath))
                ?? throw new InvalidDataException("Invalid recovery job.");
            if (!SamePath(job.Snapshot.SnapshotDirectory, Path.Combine(directory, "snapshot"))
                || !SamePath(Environment.ProcessPath, Path.Combine(job.Snapshot.SnapshotDirectory, "CycleArc.exe"))
                || !Version.TryParse(job.TargetVersion, out _))
                throw new InvalidDataException("Invalid recovery identity.");
            job.Snapshot.Verify();
            DesktopBootstrap.ValidateExecutable(Environment.ProcessPath!);
            var executable = Path.Combine(job.Snapshot.InstallationRoot, "current", "CycleArc.exe");
            using (var previous = Process.GetProcessById(job.PreviousProcessId))
            {
                _ = previous.Handle;
                if (previous.SessionId != Process.GetCurrentProcess().SessionId
                    || previous.StartTime.ToUniversalTime().Ticks != job.PreviousStartedUtcTicks
                    || !SamePath(previous.MainModule?.FileName, executable))
                    throw new IOException("The previous desktop identity changed.");
                File.WriteAllText(Path.Combine(directory, "ready"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                // The app owns graceful shutdown. Never force it closed to start an update.
                if (!previous.WaitForExit(60000)) throw new IOException("The previous desktop is still closing.");
            }
            lease = AcquireLease();
            var result = UpdateRecoveryTransaction.Run(job.Snapshot,
                apply: () => Apply(job, directory),
                startAndVerify: () =>
                {
                    VerifyExecutable(executable, job.TargetVersion, job.TargetExecutableSha256);
                    lease?.Dispose();
                    lease = null;
                    started = Start(executable, "--show");
                    VerifyReady(started, executable);
                },
                stopFailed: () =>
                {
                    StopLaunched(started, executable);
                    lease ??= AcquireLease();
                },
                startAndVerifyPrevious: () =>
                {
                    VerifyExecutable(executable, job.Snapshot.Version, job.Snapshot.ExecutableSha256);
                    lease?.Dispose();
                    lease = null;
                    started?.Dispose();
                    started = Start(executable, "--show");
                    VerifyReady(started, executable);
                });
            lease?.Dispose();
            lease = null;
            File.WriteAllText(Path.Combine(directory, result.Succeeded || result.Restored ? "completed" : "failed"),
                result.Succeeded ? "updated" : result.Restored ? "restored" : "recovery-required");
            WriteFailureDetail(directory, result.Failure);
            if (!result.Succeeded) Report(job.Korean, result.Restored, directory);
            return result.Succeeded ? 0 : result.Restored ? 2 : 1;
        }
        catch (Exception failure)
        {
            lease?.Dispose();
            lease = null;
            if (directory is not null) WriteFailureDetail(directory, failure);
            if (job is not null && directory is not null) Report(job.Korean, false, directory);
            return 1;
        }
        finally { started?.Dispose(); lease?.Dispose(); }
    }

    /// <summary>
    /// Keeps why an update or its recovery failed beside the retained copy. Without it the
    /// notice tells a person that recovery is required and nothing tells anyone why, because
    /// this helper outlives the desktop that would otherwise have logged it.
    /// </summary>
    private static void WriteFailureDetail(string directory, Exception? failure)
    {
        if (failure is null) return;
        try
        {
            var text = failure.ToString();
            File.WriteAllText(Path.Combine(directory, "failure.txt"), text.Length > 16000 ? text[..16000] : text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Apply(Job job, string directory)
    {
        var root = job.Snapshot.InstallationRoot;
        var packages = Path.Combine(root, "packages");
        UpdatePackageVerifier.VerifyAsync(packages, job.PackageFileName, job.PackageSize, job.PackageSha256)
            .GetAwaiter().GetResult();
        var updater = Path.Combine(root, "Update.exe");
        RejectRedirected(updater);
        // No restart: readiness and recovery are owned by this external supervisor.
        using var child = Start(updater, "--silent", "--verbose", "--rootDir", root,
            "--packageDir", packages, "--log", Path.Combine(directory, "apply.log"),
            "apply", "--norestart", "--package", Path.Combine(packages, job.PackageFileName));
        if (!child.WaitForExit(120000))
        {
            child.Kill(entireProcessTree: true);
            if (!child.WaitForExit(10000)) throw new IOException("The updater did not stop.");
            throw new IOException("The updater timed out.");
        }
        if (child.ExitCode != 0) throw new IOException("The updater could not replace the application.");
    }

    private static void VerifyReady(Process child, string executable)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (child.HasExited) throw new IOException("CycleArc exited before becoming ready.");
            var status = Request(DesktopInstanceCommand.Status);
            if (status.Succeeded && status.ProcessId == child.Id && SamePath(status.ExecutablePath, executable)
                && SamePath(child.MainModule?.FileName, executable)) return;
            Thread.Sleep(100);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(25));
        throw new IOException("CycleArc did not become ready.");
    }

    private static void StopLaunched(Process? child, string executable)
    {
        if (child is null || child.HasExited) return;
        if (!SamePath(child.MainModule?.FileName, executable)) throw new IOException("The new process identity changed.");
        var status = Request(DesktopInstanceCommand.Status);
        if (status.Succeeded && status.ProcessId == child.Id && SamePath(status.ExecutablePath, executable))
        {
            Request(DesktopInstanceCommand.Shutdown, status);
            if (child.WaitForExit(22000)) return;
        }
        child.Kill();
        if (!child.WaitForExit(10000)) throw new IOException("The failed new desktop could not stop.");
    }

    private static DesktopInstanceResponse Request(DesktopInstanceCommand command, DesktopInstanceResponse? expected = null)
        => DesktopInstanceClient.RequestAsync(PipeName, command, TimeSpan.FromMilliseconds(700), expectedInstance: expected)
            .GetAwaiter().GetResult();

    private static DesktopInstanceLease AcquireLease()
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            var lease = DesktopInstanceLease.TryAcquire();
            if (lease is not null) return lease;
            Thread.Sleep(100);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(8));
        throw new IOException("Another CycleArc desktop owns this session.");
    }

    private static Process Start(string path, params string[] args)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("The update process could not start.");
    }

    private static string ReadPackageExecutableHash(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.FullName == "lib/app/CycleArc.exe").ToArray();
        if (entries is not [var entry] || entry.Length <= 0 || entry.Length > 1024L * 1024 * 1024)
            throw new InvalidDataException("The update package has no valid desktop executable.");
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyExecutable(string path, string version, string sha256)
    {
        DesktopBootstrap.ValidateExecutable(path);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase)
            || FileVersionInfo.GetVersionInfo(path).FileVersion != Version.Parse(version).ToString(3) + ".0")
            throw new IOException("The installed desktop did not match the verified release.");
    }

    private static string ValidateJobPath(string jobPath)
    {
        if (!Path.IsPathFullyQualified(jobPath) || Path.GetFileName(jobPath) != "job.json")
            throw new InvalidDataException("Invalid recovery job path.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(jobPath))!;
        if (!SamePath(Path.GetDirectoryName(directory), RecoveryRoot)
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new InvalidDataException("Invalid recovery job directory.");
        RejectRedirected(jobPath);
        return directory;
    }

    private static void RejectRedirected(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery paths must not be redirected.");
    }

    private static bool SamePath(string? a, string? b) => a is not null && b is not null
        && Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    internal static void CleanupCompleted()
    {
        try
        {
            if (!Directory.Exists(RecoveryRoot)) return;
            RejectRedirected(RecoveryRoot);
            foreach (var directory in Directory.EnumerateDirectories(RecoveryRoot).Take(32))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                    || !File.Exists(Path.Combine(directory, "completed"))) continue;
                try
                {
                    RejectRedirected(directory);
                    if (HelperStillRunning(directory)) continue;
                    // Do not follow a substituted junction during recursive cleanup.
                    ValidateTree(directory);
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) { } // The helper can still be exiting or showing recovery notice.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool HelperStillRunning(string directory)
    {
        var ready = Path.Combine(directory, "ready");
        if (!File.Exists(ready) || new FileInfo(ready).Length > 32
            || !int.TryParse(File.ReadAllText(ready), out var pid)) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && SamePath(process.MainModule?.FileName,
                Path.Combine(directory, "snapshot", "CycleArc.exe"));
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static void ValidateTree(string root, int depth = 0)
    {
        if (depth > 16) throw new IOException("Recovery directory is too deep.");
        var paths = Directory.EnumerateFileSystemEntries(root).Take(513).ToArray();
        if (paths.Length > 512) throw new IOException("Recovery directory has too many entries.");
        foreach (var path in paths)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected recovery file.");
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(path, depth + 1);
        }
    }

    private static void Report(bool korean, bool restored, string directory)
    {
        var message = restored
            ? korean ? "업데이트를 완료하지 못해 이전 버전으로 복구했습니다. 계정과 설정은 그대로 유지됩니다."
                : "The update could not finish. Your previous version was restored. Accounts and settings are preserved."
            : korean ? "업데이트를 완료하지 못했습니다. 계정과 설정, 이전 버전의 복구 사본은 보존했습니다. CycleArc를 다시 열거나 최신 설치 파일을 실행하세요.\n\n복구 사본: " + directory
                : "The update could not finish. Accounts, settings and the recovery copy are preserved. Reopen CycleArc or run the latest installer.\n\nRecovery copy: " + directory;
        MessageBox.Show(message, "CycleArc", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
