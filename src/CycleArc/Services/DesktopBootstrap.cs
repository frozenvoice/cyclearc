using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace CycleArc.Services;

/// <summary>One desktop location for downloads and development builds. Claude modes bypass this class.</summary>
public static class DesktopBootstrap
{
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CycleArc");
    public static string ExecutablePath => Path.Combine(InstallDirectory, "CycleArc.exe");
    private static string MigrationPath => Path.Combine(InstallDirectory, "tray-migration.json");
    private static string PipeName => DesktopInstancePipe.ForCurrentUserSession();

    public static int Run(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        try
        {
            var options = DesktopLaunchOptions.Parse(args);
            if (options.StatusOnly)
            {
                var status = Request(DesktopInstanceCommand.Status, TimeSpan.FromSeconds(3));
                Console.Out.WriteLine(JsonSerializer.Serialize(status));
                return status.Succeeded ? 0 : 1;
            }

            if (InstalledApp.IsManaged)
            {
                if (options.Replace)
                    throw new IOException("Use CycleArc Updates to update this installed application.");
                return RunManaged(options);
            }

            var source = Environment.ProcessPath ?? throw new IOException("CycleArc executable path is unavailable.");
            if (!options.Replace)
            {
                var lease = DesktopInstanceLease.TryAcquire();
                if (lease is null) return ActivateExisting(options.Autorun);
                if (!IsSingleFile())
                {
                    lease.Dispose();
                    throw new IOException("Use dev-run.ps1 to publish and run the desktop application.");
                }
                if (SamePath(source, ExecutablePath))
                {
                    using (lease)
                    {
                        var app = new App { InstanceLease = lease };
                        app.InitializeComponent();
                        return app.Run();
                    }
                }
                // The installation lease serializes candidates. Recheck the desktop
                // mutex after staging: a desktop started meanwhile takes precedence.
                lease.Dispose();
            }
            if (!IsSingleFile()) throw new IOException("Install the published, single-file CycleArc.exe. Run dev-run.ps1 to build it.");
            if (SamePath(source, ExecutablePath))
                throw new IOException("Select the downloaded CycleArc.exe to install a different version.");
            ValidateExecutable(source);
            var hash = Hash(source);
            var version = FileVersionInfo.GetVersionInfo(source).FileVersion;
            if (options.Replace && (!hash.Equals(options.ExpectedHash, StringComparison.OrdinalIgnoreCase)
                || version != options.ExpectedVersion))
                throw new IOException("The selected executable changed after validation. Select it again.");
            return Install(source, hash, options);
        }
        catch (ExistingDesktopException)
        {
            try { return ActivateExisting(args.Contains("--autorun", StringComparer.Ordinal)); }
            catch (Exception ex) { ReportFailure(ex.Message, quiet); return 1; }
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message, quiet);
            return 1;
        }
    }

    private static int RunManaged(DesktopLaunchOptions options)
    {
        // A first launch from Setup may hand off the previous standalone installation.
        // Ordinary launches still preserve the first desktop, including development builds.
        if (InstalledApp.IsFirstRun)
        {
            var status = Request(DesktopInstanceCommand.Status, TimeSpan.FromSeconds(2));
            if (status.Succeeded && SamePath(status.ExecutablePath, ExecutablePath))
            {
                using var previous = OpenVerifiedProcess(status);
                var ack = Request(DesktopInstanceCommand.Shutdown, TimeSpan.FromSeconds(3), status);
                if (!ack.Succeeded || ack.ProcessId != previous.Id || !previous.WaitForExit(22000))
                    throw new IOException("The previous CycleArc is still closing. Close it from the tray and reopen CycleArc.");
            }
        }
        using var lease = DesktopInstanceLease.TryAcquire();
        if (lease is null) return ActivateExisting(options.Autorun);
        var app = new App { InstanceLease = lease };
        app.InitializeComponent();
        return app.Run();
    }

    public static void InstallSelectedVersion(string path)
    {
        try
        {
            ValidateExecutable(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) < new Version(0, 5, 8))
                throw new IOException(UiText.T("Choose CycleArc 0.5.8 or later, which supports version installation.",
                    "버전 설치를 지원하는 CycleArc 0.5.8 이상을 선택하세요."));
            if (SamePath(path, ExecutablePath))
                throw new IOException(UiText.T("This is the running installation. Select a downloaded CycleArc.exe.",
                    "현재 설치된 실행 파일입니다. 다운로드한 CycleArc.exe를 선택하세요."));
            using var installer = Start(path, "--install", "--expected-sha256", Hash(path),
                "--expected-version", version.FileVersion!, "--show");
        }
        catch (Exception ex) { ReportFailure(ex.Message, false); }
    }

    private static int Install(string source, string hash, DesktopLaunchOptions options)
    {
        DesktopInstanceLease? barrier = null;
        Process? started = null;
        var oldPaths = new List<string> { source };
        string? oldExternal = null;
        string? oldExternalHash = null;
        var restoredDesktop = false;
        try
        {
            var transaction = new DesktopInstallTransaction(InstallDirectory,
                stop: () =>
                {
                    if (barrier is not null) return;
                    if (!options.Replace)
                    {
                        barrier = DesktopInstanceLease.TryAcquire() ?? throw new ExistingDesktopException();
                    }
                    else
                    {
                        var status = Request(DesktopInstanceCommand.Status, TimeSpan.FromSeconds(1));
                        if (status.Succeeded)
                        {
                            using var current = OpenVerifiedProcess(status);
                            oldPaths.Add(status.ExecutablePath);
                            if (!SamePath(status.ExecutablePath, ExecutablePath))
                            {
                                oldExternal = status.ExecutablePath;
                                oldExternalHash = Hash(oldExternal);
                            }
                            var ack = Request(DesktopInstanceCommand.Shutdown, TimeSpan.FromSeconds(3), status);
                            if (!ack.Succeeded || ack.ProcessId != current.Id || !current.WaitForExit(22000))
                                throw new IOException("The running CycleArc has not finished shutting down. Its installation was preserved.");
                        }
                        barrier = DesktopInstanceLease.TryAcquire();
                        if (barrier is null)
                        {
                            // Give a modern process that is still starting time to publish IPC.
                            status = ProbeUntil(TimeSpan.FromSeconds(8));
                            if (status.Succeeded)
                            {
                                using var current = OpenVerifiedProcess(status);
                                oldPaths.Add(status.ExecutablePath);
                                if (!SamePath(status.ExecutablePath, ExecutablePath))
                                {
                                    oldExternal = status.ExecutablePath;
                                    oldExternalHash = Hash(oldExternal);
                                }
                                var ack = Request(DesktopInstanceCommand.Shutdown, TimeSpan.FromSeconds(3), status);
                                if (!ack.Succeeded || ack.ProcessId != current.Id || !current.WaitForExit(22000))
                                    throw new IOException("CycleArc shutdown did not finish. The previous installation was preserved.");
                            }
                            else
                            {
                                var legacy = LegacyDesktopMigration.StopOlderDesktops();
                                oldPaths.AddRange(legacy.Select(item => item.ExecutablePath));
                                var external = legacy.FirstOrDefault(item => !SamePath(item.ExecutablePath, ExecutablePath));
                                oldExternal = external?.ExecutablePath;
                                oldExternalHash = external?.Sha256;
                            }
                            barrier = AcquireBarrier(TimeSpan.FromSeconds(5));
                        }
                    }
                    // Tray cleanup is best-effort and must never turn a completed
                    // shutdown into an unrecorded installer failure.
                    try { WriteMigrationPaths(oldPaths); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                },
                startAndVerify: (target, restoringPrevious, expected) =>
                {
                    barrier?.Dispose();
                    barrier = null;
                    started?.Dispose();
                    var launchPath = restoringPrevious && oldExternal is not null ? oldExternal : target;
                    if (restoringPrevious && oldExternal is not null)
                    {
                        started = StartAndVerifyLegacy(launchPath, oldExternalHash!);
                        restoredDesktop = true;
                        return;
                    }
                    if (restoringPrevious && IsLegacyVersion(target))
                    {
                        started = StartAndVerifyLegacy(target, expected!);
                        restoredDesktop = true;
                        return;
                    }
                    started = Start(target, options.Autorun ? "--autorun" : "--show");
                    var status = ProbeUntil(TimeSpan.FromSeconds(20));
                    if (!status.Succeeded || !SamePath(status.ExecutablePath, target)
                        || expected is null || !Hash(target).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The installed CycleArc did not become ready with the verified executable.");
                    using var running = OpenVerifiedProcess(status);
                    if (running.HasExited) throw new IOException("CycleArc exited during startup verification.");
                    restoredDesktop = restoringPrevious;
                },
                stopFailed: () =>
                {
                    // Only the exact launched child or a verified canonical desktop
                    // is eligible for cleanup. Never terminate a headless callback.
                    if (started is { HasExited: false })
                    {
                        var status = Request(DesktopInstanceCommand.Status, TimeSpan.FromSeconds(1));
                        var response = status.Succeeded && status.ProcessId == started.Id
                            ? Request(DesktopInstanceCommand.Shutdown, TimeSpan.FromSeconds(2), status)
                            : DesktopInstanceResponse.Failure("different-instance");
                        if (!response.Succeeded || response.ProcessId != started.Id || !started.WaitForExit(22000))
                        {
                            started.Kill();
                            if (!started.WaitForExit(10000)) throw new IOException("The failed CycleArc process could not be stopped.");
                        }
                    }
                    barrier ??= AcquireBarrier(TimeSpan.FromSeconds(5));
                });
            var result = transaction.Install(source, hash);
            if (result.Succeeded) return 0;
            if (result.Failure is ExistingDesktopException) throw result.Failure;
            if (!restoredDesktop && oldExternal is not null && oldExternalHash is not null
                && File.Exists(oldExternal) && barrier is not null)
            {
                barrier?.Dispose();
                barrier = null;
                using var restored = StartAndVerifyLegacy(oldExternal, oldExternalHash);
                restoredDesktop = true;
            }
            throw new IOException(result.RolledBack
                ? "Installation failed; the previous CycleArc version was restored. " + result.Failure?.Message
                : "Installation failed. " + result.Failure?.Message, result.Failure);
        }
        finally { started?.Dispose(); barrier?.Dispose(); }
    }

    private static DesktopInstanceLease AcquireBarrier(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            var lease = DesktopInstanceLease.TryAcquire();
            if (lease is not null) return lease;
            Thread.Sleep(100);
        } while (elapsed.Elapsed < timeout);
        throw new IOException("The running CycleArc still owns its desktop session. Installation files were preserved.");
    }

    private static int ActivateExisting(bool autorun)
    {
        var status = ProbeUntil(TimeSpan.FromSeconds(12));
        if (!status.Succeeded)
        {
            if (!autorun) ReportFailure("CycleArc is already running. This older version cannot receive activation requests. Exit it from the tray, or use dev-run.ps1 / About → Install another version to switch.", false);
            return autorun ? 0 : 1;
        }
        if (!autorun)
        {
            using var current = OpenVerifiedProcess(status);
            AllowSetForegroundWindow(current.Id);
            var activated = Request(DesktopInstanceCommand.Activate, TimeSpan.FromSeconds(3));
            if (!activated.Succeeded) throw new IOException("Could not activate the running CycleArc.");
        }
        return 0;
    }

    private static DesktopInstanceResponse ProbeUntil(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        DesktopInstanceResponse status;
        do
        {
            status = Request(DesktopInstanceCommand.Status, TimeSpan.FromMilliseconds(650));
            if (status.Succeeded) return status;
            Thread.Sleep(100);
        } while (elapsed.Elapsed < timeout);
        return status;
    }

    private static DesktopInstanceResponse Request(DesktopInstanceCommand command, TimeSpan timeout,
        DesktopInstanceResponse? expected = null)
        => DesktopInstanceClient.RequestAsync(PipeName, command, timeout, expectedInstance: expected).GetAwaiter().GetResult();

    private static Process OpenVerifiedProcess(DesktopInstanceResponse status)
    {
        var process = Process.GetProcessById(status.ProcessId);
        try
        {
            _ = process.Handle; // Retain the process identity across subsequent exit waits.
            if (process.SessionId != Process.GetCurrentProcess().SessionId
                || !SamePath(process.MainModule?.FileName, status.ExecutablePath))
                throw new IOException("The running CycleArc identity could not be verified.");
            ValidateExecutable(status.ExecutablePath);
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            // Detach the desktop from an installer's redirected stdout/stderr.
            // Otherwise dev-run's ReadToEnd can wait for the desktop to exit.
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("CycleArc could not be started.");
    }

    private static bool IsLegacyVersion(string executable)
        => Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion, out var version)
            && version < new Version(0, 5, 8);

    private static Process StartAndVerifyLegacy(string path, string expectedHash)
    {
        ValidateExecutable(path);
        if (Hash(path) != expectedHash) throw new IOException("The previous executable changed and cannot be restarted.");
        var process = Start(path, "--show");
        try
        {
            if (process.WaitForExit(2500) || !SamePath(process.MainModule?.FileName, path))
                throw new IOException("The previous CycleArc version could not restart.");
            using var unexpectedLease = DesktopInstanceLease.TryAcquire();
            if (unexpectedLease is not null) throw new IOException("The previous CycleArc did not acquire its desktop session.");
            return process;
        }
        catch
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); } }
            finally { process.Dispose(); }
            throw;
        }
    }

    internal static void ValidateExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.GetFileName(path).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Select an existing CycleArc.exe.");
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The CycleArc installation path must not contain redirected files or folders.");
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != "CycleArc" || info.OriginalFilename != "CycleArc.dll")
            throw new IOException("The selected executable is not a CycleArc build.");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SamePath(string? first, string? second) => first is not null && second is not null
        && Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

#pragma warning disable IL3000 // Empty assembly location deliberately identifies a published single-file bundle.
    private static bool IsSingleFile() => string.IsNullOrEmpty(typeof(Program).Assembly.Location);
#pragma warning restore IL3000

    private static void WriteMigrationPaths(IEnumerable<string> paths)
    {
        var saved = ReadMigrationPaths();
        var merged = saved.Concat(paths).Where(path => !SamePath(path, ExecutablePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        if (File.Exists(MigrationPath) && (File.GetAttributes(MigrationPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Tray migration record is redirected.");
        File.WriteAllText(MigrationPath, JsonSerializer.Serialize(merged));
    }

    internal static string[] ReadMigrationPaths()
    {
        try
        {
            if (!File.Exists(MigrationPath) || new FileInfo(MigrationPath).Length > 32768
                || (File.GetAttributes(MigrationPath) & FileAttributes.ReparsePoint) != 0) return [];
            return (JsonSerializer.Deserialize<string?[]>(File.ReadAllText(MigrationPath)) ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Take(64).Select(path => Path.GetFullPath(path!)).ToArray();
        }
        catch { return []; }
    }

    private static void ReportFailure(string message, bool quiet)
    {
        Console.Error.WriteLine(message);
        if (!quiet) MessageBox.Show(message, "CycleArc", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    private sealed class ExistingDesktopException : Exception { }
}
