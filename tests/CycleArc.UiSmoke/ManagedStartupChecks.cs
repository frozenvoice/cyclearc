using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;
using Microsoft.Win32;

namespace CycleArc.UiSmoke;

/// <summary>Real managed startup UI, registration and exact-command execution on a fresh disposable profile.</summary>
internal static class ManagedStartupChecks
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CycleArc";
    private const string SentinelName = "CycleArc.StartupVerification.Unrelated";
    private static readonly string[] StartupNames = ["CycleArc", LegacyInstallation.StartupValueName, LegacyInstallation.PreviousStartupValueName];

    public static void Run(string assetDirectory)
    {
        Check(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CYCLEARC_DISPOSABLE_PROFILE") == "1",
            "Managed startup verification requires an explicitly disposable Windows profile.");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = AppPaths.RootPath;
        var managedRoots = new[] { Path.Combine(local, "CycleArc"), Path.Combine(local, "Programs", "CycleArc"), DesktopBootstrap.InstallDirectory };
        Check(!Directory.Exists(data) && managedRoots.All(root => !Directory.Exists(root)),
            "Managed startup verification refuses an existing CycleArc/ProMeter profile.");
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            Check(key is null, "Managed startup verification refuses a registered installation.");
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            Check(StartupNames.Append(SentinelName).All(name => key?.GetValue(name) is null),
                "Managed startup verification refuses existing CycleArc or legacy startup registrations.");
        Check(!PortableDistributionChecks.Request(DesktopInstanceCommand.Status).Succeeded,
            "Managed startup verification refuses a running desktop.");
        Check(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "scripts", "Verify-ManagedStartupRelease.ps1")),
            "Managed startup verification requires the exact source checkout's release-probe helper.");
        _ = PowerShellPath();
        var work = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "managed-startup"));
        Check(!Directory.Exists(work), "Managed startup staging must be empty.");
        var output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "managed-startup"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(output);
        var assets = Path.GetFullPath(assetDirectory);
        var setup = Path.Combine(assets, "CycleArc-Setup.exe");
        Check(File.Exists(setup), "The same-run Setup asset is missing.");
        var portable = Directory.GetFiles(assets, "CycleArc-*-win-x64-portable.zip");
        var full = Directory.GetFiles(assets, "CycleArc-*-full.nupkg");
        Check(portable.Length == 1 && full.Length == 1, "Expected exactly one same-run portable and full package.");
        var portableExe = Path.Combine(work, "portable-CycleArc.exe");
        var packageExe = Path.Combine(work, "packed-CycleArc.exe");
        Extract(portable[0], "CycleArc.exe", portableExe);
        Extract(full[0], "CycleArc.exe", packageExe, fullPackage: true);
        var expectedHash = PortableDistributionChecks.Hash(portableExe);
        var info = FileVersionInfo.GetVersionInfo(portableExe);
        var version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        var expectedProductVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Check(expectedHash == PortableDistributionChecks.Hash(packageExe), "Portable and full-package EXE hashes differ.");
        Check(info.ProductName == "CycleArc" && info.OriginalFilename == "CycleArc.dll"
            && info.ProductVersion == expectedProductVersion
            && FileVersionInfo.GetVersionInfo(packageExe).ProductVersion == expectedProductVersion,
            "Managed startup assets and verification harness have different products/versions/source revisions.");
        Check(Path.GetFileName(portable[0]) == $"CycleArc-{version}-win-x64-portable.zip"
            && Path.GetFileName(full[0]) == $"CycleArc-{version}-full.nupkg", "Managed startup asset filenames have the wrong version.");
        string? installRoot = null;
        Process? desktop = null;
        var sentinelCreated = false;
        var completed = false;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(SentinelName, "synthetic-unrelated-startup-command");
            sentinelCreated = true;
            var unrelated = SnapshotUnrelatedRun();
            RunSetup(setup, output, "install");
            installRoot = ResolveManagedRoot();
            var current = Path.Combine(installRoot, "current", "CycleArc.exe");
            var launcher = Path.Combine(installRoot, "CycleArc.exe");
            CheckInstalled(current, expectedHash, expectedProductVersion!);
            var first = LaunchAndAwait(launcher, ["--show"], work, current, expectedProductVersion!);
            desktop = first.Process;
            var settingsPath = Path.Combine(data, "settings.json");
            var firstSettings = new SettingsStore(settingsPath).Load();
            Check(firstSettings.FirstRunCompleted && !firstSettings.StartWithWindows && ReadRunCommand() is null,
                "The first managed desktop opted into startup without user consent.");
            Check(SnapshotUnrelatedRun() == unrelated, "The first managed desktop changed unrelated Run values.");
            Shutdown(desktop, first.Ready, installRoot, work);
            desktop.Dispose();
            desktop = null;

            // Synthetic data only, seeded while the desktop is closed. Startup opt-in/off
            // below is performed only through the installed production settings controls.
            var id = "10000000000000000000000000000002";
            var config = Directory.CreateDirectory(Path.Combine(work, "synthetic-claude")).FullName;
            var accounts = new CodexAccountStore(data);
            accounts.Save(new CodexAccountConfiguration(3, id,
                [new CodexAccountProfile(id, "", "Managed startup synthetic account") { Provider = UsageProviderId.Claude }]));
            new ClaudeConnectionStore(accounts, id).Save(new ClaudeConnectionBinding(2, id, config,
                Path.Combine(work, "missing-claude.exe"), false, new string('B', 64), DateTimeOffset.UtcNow));
            firstSettings.FloatingWidgetEnabled = true;
            firstSettings.UsageAlertsEnabled = false;
            firstSettings.UiLanguage = UiLanguage.English;
            firstSettings.CodexRefreshIntervalMinutes = 60;
            new SettingsStore(settingsPath).Save(firstSettings);
            var sentinel = Path.Combine(data, "managed-startup-preservation-sentinel.txt");
            File.WriteAllText(sentinel, "synthetic shared data preserved across autorun and repair");
            var sentinelHash = PortableDistributionChecks.Hash(sentinel);

            var normal = LaunchAndAwait(launcher, ["--show"], work, current, expectedProductVersion!);
            desktop = normal.Process;
            var activation = PortableDistributionChecks.Request(DesktopInstanceCommand.Activate, normal.Ready);
            Check(activation.Succeeded && activation.ProcessId == desktop.Id,
                "Could not activate the verified installed desktop for startup opt-in.");
            SetStartupThroughUi(desktop.Id, enabled: true);
            var registered = ReadRunCommand();
            var expectedCommand = "\"" + launcher + "\" --autorun";
            Check(registered == expectedCommand && ReadRunKind() == RegistryValueKind.String,
                "Production startup UI did not register the exact quoted managed root launcher command.");
            Check(new SettingsStore(settingsPath).Load().StartWithWindows && SnapshotUnrelatedRun() == unrelated,
                "Startup UI did not save consent or changed unrelated Run values.");
            Shutdown(desktop, normal.Ready, installRoot, work);
            desktop.Dispose();
            desktop = null;

            var parsed = ParseRegisteredCommand(registered!, launcher);
            var autorun = LaunchAndAwait(parsed.Executable, parsed.Arguments, work, current, expectedProductVersion!);
            desktop = autorun.Process;
            PortableDistributionChecks.AwaitWidget(desktop);
            Check(PortableDistributionChecks.HasTraySink(desktop.Id), "Registered startup command did not initialize its native tray sink.");
            Check(FindWindow(desktop.Id, "CycleArc") == 0 && FindWindow(desktop.Id, "CycleArc · Settings") == 0,
                "Registered --autorun command opened the detail/settings window.");
            Check(ReadRunCommand() == registered && SnapshotUnrelatedRun() == unrelated,
                "Registered startup command changed startup registration or unrelated Run values.");
            using (var duplicate = Start(parsed.Executable, parsed.Arguments, work, ProcessWindowStyle.Normal))
            {
                Check(duplicate.WaitForExit(15_000) && duplicate.ExitCode == 0, "Duplicate autorun launcher failed.");
                var same = PortableDistributionChecks.Request(DesktopInstanceCommand.Status);
                Check(same.Succeeded && same.ProcessId == desktop.Id && same.InstanceId == autorun.Ready.InstanceId,
                    "Duplicate autorun started a second desktop or replaced the first.");
                Check(FindWindow(desktop.Id, "CycleArc") == 0, "Duplicate --autorun unexpectedly activated the detail window.");
            }
            Shutdown(desktop, autorun.Ready, installRoot, work);
            desktop.Dispose();
            desktop = null;

            RunSetup(setup, output, "repair-opt-in");
            Check(ResolveManagedRoot() == installRoot, "Same-version repair moved the managed installation.");
            CheckInstalled(current, expectedHash, expectedProductVersion!);
            Check(new SettingsStore(settingsPath).Load().StartWithWindows && ReadRunCommand() == registered,
                "Same-version repair lost startup opt-in or its registered command.");
            var afterRepair = LaunchAndAwait(parsed.Executable, parsed.Arguments, work, current, expectedProductVersion!);
            desktop = afterRepair.Process;
            PortableDistributionChecks.AwaitWidget(desktop);
            Check(FindWindow(desktop.Id, "CycleArc") == 0, "Repaired autorun opened the detail window.");
            var optOutActivation = PortableDistributionChecks.Request(DesktopInstanceCommand.Activate, afterRepair.Ready);
            Check(optOutActivation.Succeeded && optOutActivation.ProcessId == desktop.Id,
                "Could not activate the installed desktop for startup opt-out.");
            SetStartupThroughUi(desktop.Id, enabled: false);
            Check(!new SettingsStore(settingsPath).Load().StartWithWindows && ReadRunCommand() is null,
                "Production startup UI failed to save opt-out/remove Run registration.");
            Check(SnapshotUnrelatedRun() == unrelated, "Startup opt-out changed unrelated Run values.");
            Shutdown(desktop, afterRepair.Ready, installRoot, work);
            desktop.Dispose();
            desktop = null;

            RunSetup(setup, output, "repair-opt-out");
            CheckInstalled(current, expectedHash, expectedProductVersion!);
            var optedOut = LaunchAndAwait(launcher, ["--show"], work, current, expectedProductVersion!);
            desktop = optedOut.Process;
            Check(!new SettingsStore(settingsPath).Load().StartWithWindows && ReadRunCommand() is null,
                "Repair/normal restart re-enabled opted-out Windows startup.");
            Check(accounts.ReadExisting()?.Profiles.Single().Id == id && PortableDistributionChecks.Hash(sentinel) == sentinelHash,
                "Managed startup/repair lost shared synthetic account data.");
            Check(SnapshotUnrelatedRun() == unrelated, "Managed startup/repair changed unrelated Run values.");
            Shutdown(desktop, optedOut.Ready, installRoot, work);
            desktop.Dispose();
            desktop = null;

            RunWindowed(Path.Combine(installRoot, "Update.exe"), ["--uninstall", "--silent"], work, 120_000);
            Check(!File.Exists(current) && Directory.Exists(data) && PortableDistributionChecks.Hash(sentinel) == sentinelHash,
                "Uninstall failed or removed shared user data.");
            Check(ReadRunCommand() is null && SnapshotUnrelatedRun() == unrelated,
                "Uninstall left startup enabled or changed unrelated Run values.");
            File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new
            {
                verificationKind = "registered-command-replay", windowsLogonExercised = false,
                version = expectedProductVersion, executableSha256 = expectedHash,
                setupSha256 = PortableDistributionChecks.Hash(setup), fullPackageSha256 = PortableDistributionChecks.Hash(full[0]),
                portableZipSha256 = PortableDistributionChecks.Hash(portable[0]), installedRoot = installRoot,
                registeredCommand = registered, registeredValueKind = "REG_SZ", freshInstallStartupOff = true,
                productionUiOptIn = true, registeredCommandExecuted = true, autorunQuiet = true,
                trayReady = true, nativeWidgetVisible = true, duplicateAutorunKeepsFirstInstance = true,
                productionUiOptOut = true, optInRepairPreserved = true, optOutRepairPreserved = true,
                sharedDataPreserved = true, unrelatedRunValuesPreserved = true, uninstallPreservedData = true,
                checkedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
            completed = true;
            Console.WriteLine("PASS: exact production managed installer: first-use startup off, real Settings opt-in/off, actual HKCU Run command replay, quiet native tray/widget, duplicate-instance prevention, repair and shared-data preservation. Windows logon was not exercised.");
        }
        finally
        {
            if (desktop is not null)
            {
                try { if (!desktop.HasExited) { desktop.Kill(true); desktop.WaitForExit(10_000); } }
                finally { desktop.Dispose(); }
            }
            if (sentinelCreated)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                key?.DeleteValue(SentinelName, false);
            }
            // Delete only fixture roots proven absent before this disposable-only test.
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (completed && Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    private static void Extract(string archivePath, string entryName, string destination, bool fullPackage = false)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(entries.Length == 1, $"Expected exactly one {entryName} in the release archive.");
        Check(fullPackage ? Regex.IsMatch(entries[0].FullName, @"^lib/[^/]+/CycleArc\.exe$")
            : archive.Entries.Count == 1 && entries[0].FullName == entryName,
            "Release archive has the wrong executable layout.");
        entries[0].ExtractToFile(destination);
    }

    private static string ResolveManagedRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var value = key?.GetValue("InstallLocation") as string;
        Check(!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value), "Managed uninstall entry has no valid installation root.");
        var root = Path.GetFullPath(value!.Trim());
        Check(File.Exists(Path.Combine(root, "current", "CycleArc.exe")) && File.Exists(Path.Combine(root, "Update.exe")),
            "The registered root is not a managed installation.");
        return root;
    }

    private static void CheckInstalled(string current, string hash, string version)
        => Check(PortableDistributionChecks.Hash(current) == hash && FileVersionInfo.GetVersionInfo(current).ProductVersion == version,
            "Installed/repaired EXE hash or version differs from the same-run package/portable EXE.");

    private static void RunSetup(string setup, string output, string stage)
        => RunWindowed(setup, ["--silent", "--log", Path.Combine(output, stage + ".log")], output, 600_000);

    private static void RunWindowed(string executable, string[] arguments, string workingDirectory, int milliseconds)
    {
        using var process = Start(executable, arguments, workingDirectory);
        if (!process.WaitForExit(milliseconds))
        {
            process.Kill(true);
            process.WaitForExit(5_000);
            throw new TimeoutException("Managed installer/updater exceeded its bounded wait.");
        }
        Check(process.ExitCode == 0, $"Managed installer/updater exited {process.ExitCode}.");
    }

    private static Process Start(string executable, string[] arguments, string work,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = windowStyle, WorkingDirectory = work
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        start.Environment["CODEX_HOME"] = Path.Combine(work, "missing-codex");
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(work, "synthetic-claude");
        return Process.Start(start) ?? throw new InvalidOperationException("Managed startup process did not start.");
    }

    private static (Process Process, DesktopInstanceResponse Ready) LaunchAndAwait(
        string launcher, string[] arguments, string work, string current, string version)
    {
        using var stub = Start(launcher, arguments, work, ProcessWindowStyle.Normal);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
        {
            var ready = PortableDistributionChecks.Request(DesktopInstanceCommand.Status);
            if (ready.Succeeded)
            {
                Check(ready.ExecutablePath.Equals(current, StringComparison.OrdinalIgnoreCase) && ready.Version == version,
                    "Managed startup reported another executable or version.");
                var process = Process.GetProcessById(ready.ProcessId);
                try
                {
                    RetainExitStatus(process);
                    Check(process.MainModule?.FileName?.Equals(current, StringComparison.OrdinalIgnoreCase) == true,
                        "Managed startup PID does not match the registered installation.");
                    return (process, ready);
                }
                catch { process.Dispose(); throw; }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("The managed root launcher did not start a ready desktop.");
    }

    private static void Shutdown(Process desktop, DesktopInstanceResponse ready, string installRoot, string work)
    {
        var result = PortableDistributionChecks.Request(DesktopInstanceCommand.Shutdown, ready);
        Check(result.Succeeded && result.ProcessId == desktop.Id && desktop.WaitForExit(25_000) && desktop.ExitCode == 0,
            "Managed desktop did not shut down cleanly.");
        RunWindowed(PowerShellPath(), ["-NoProfile", "-NonInteractive", "-File",
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "Verify-ManagedStartupRelease.ps1"),
            "-InstallRoot", installRoot, "-ProcessId", desktop.Id.ToString()], work, 45_000);
    }

    private static void RetainExitStatus(Process process)
    {
        // GetProcessById only attaches an ID; MainModule opens a temporary query
        // handle. Keep this Process object's handle while the desktop is alive so
        // ExitCode remains available after shutdown, even after the PID vanishes.
        Check(!process.SafeHandle.IsInvalid && !process.SafeHandle.IsClosed,
            "Could not retain the observed desktop's process handle for exit-status verification.");
    }

    private static string PowerShellPath()
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "pwsh.exe"))
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("PowerShell 7 executable is missing from the harness environment.");

    private static (string Executable, string[] Arguments) ParseRegisteredCommand(string command, string launcher)
    {
        // The verified production contract contains exactly one quoted path and one switch.
        // No shell interpretation, substitutions or unverified startup entries are executed.
        Check(command == "\"" + launcher + "\" --autorun", "Refusing an unexpected registered startup command.");
        var closingQuote = command.IndexOf('"', 1);
        return (command[1..closingQuote], [command[(closingQuote + 2)..]]);
    }

    private static string? ReadRunCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("CycleArc", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static RegistryValueKind ReadRunKind()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key!.GetValueKind("CycleArc");
    }

    private static string SnapshotUnrelatedRun()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (key is not null)
            foreach (var name in key.GetValueNames().Where(name => name != "CycleArc"))
                values[name] = key.GetValueKind(name) + ":" + JsonSerializer.Serialize(
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        return JsonSerializer.Serialize(values);
    }

    private static void SetStartupThroughUi(int processId, bool enabled)
    {
        var flyout = AwaitWindow(processId, "CycleArc");
        UiA(() =>
        {
            var root = AutomationElement.FromHandle(flyout);
            var button = Control(root, "SettingsButton");
            Check(button.Current.IsEnabled, "Production Settings button is disabled.");
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        });
        var settings = AwaitWindow(processId, "CycleArc · Settings");
        UiA(() =>
        {
            var root = AutomationElement.FromHandle(settings);
            ToggleAndSave(root, enabled);
        });
        Await(() => FindWindow(processId, "CycleArc · Settings") == 0, "Production Settings window did not close after Save.");
    }

    private static void ToggleAndSave(AutomationElement root, bool enabled)
    {
        Console.WriteLine($"[managed-startup-uia] Locate StartupBox; requested={enabled} apartment={Thread.CurrentThread.GetApartmentState()}");
        var startup = Control(root, "StartupBox");
        Console.WriteLine("[managed-startup-uia] Read startup enabled/state");
        Check(startup.Current.IsEnabled, "Managed startup control is disabled.");
        var toggle = (TogglePattern)startup.GetCurrentPattern(TogglePattern.Pattern);
        Check(toggle.Current.ToggleState == (enabled ? ToggleState.Off : ToggleState.On),
            "Production startup control does not show the expected prior consent.");
        toggle.Toggle();
        Console.WriteLine("[managed-startup-uia] Toggle returned");
        Check(toggle.Current.ToggleState == (enabled ? ToggleState.On : ToggleState.Off),
            "Production startup TogglePattern failed to change consent.");
        Console.WriteLine("[managed-startup-uia] Locate SaveButton");
        ((InvokePattern)Control(root, "SaveButton").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Console.WriteLine("[managed-startup-uia] Save InvokePattern returned");
    }

    private static AutomationElement Control(AutomationElement root, string name)
    {
        AutomationElement? result = null;
        Await(() => (result = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, name))) is not null,
            $"Production UIA control {name} is missing.");
        return result!;
    }

    internal static void RunUiContract()
    {
        // Use the same external-process MTA UIA route as the installed test. The
        // synthetic child uses OfflineApp, never production startup/subscriptions.
        foreach (var enabled in new[] { true, false })
        {
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("The UIA verifier has no executable path.");
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--managed-startup-ui-child");
            start.ArgumentList.Add(enabled ? "on" : "off");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Synthetic Settings UIA child did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                var handle = AwaitWindow(child.Id, "CycleArc · Settings");
                using var observed = Process.GetProcessById(child.Id);
                RetainExitStatus(observed);
                Check(observed.MainModule?.FileName == child.MainModule?.FileName,
                    "Synthetic child's observed PID does not match its executable.");
                UiA(() => ToggleAndSave(AutomationElement.FromHandle(handle), enabled));
                Check(child.WaitForExit(10_000), "Synthetic Settings UIA child did not close after Save.");
                Check(child.ExitCode == 0, "Synthetic Settings UIA child failed: " + errors.GetAwaiter().GetResult());
                Check(observed.WaitForExit(10_000) && observed.ExitCode == 0,
                    "Observed synthetic child's exit status was not available or unsuccessful.");
                Console.Write(output.GetAwaiter().GetResult());
            }
            finally
            {
                if (!child.HasExited) { child.Kill(true); child.WaitForExit(5_000); }
            }
        }
        Console.WriteLine("PASS: external MTA UIA production Settings startup toggle/save and attached-process exit status with OfflineApp children; no registry or shared-profile access.");
    }

    internal static int RunUiChild(bool enabled)
    {
        UiText.SetLanguage(UiLanguage.English);
        var settings = AppSettings.CreateDefaults();
        settings.StartWithWindows = !enabled;
        var window = new SettingsWindow(settings);
        var saved = false;
        window.Saved += value => saved = value.StartWithWindows == enabled;
        window.Closed += (_, _) => Application.Current.Shutdown(saved ? 0 : 1);
        return Application.Current.Run(window);
    }

    private static void UiA(Action operation)
    {
        // UI Automation clients must use an MTA thread separate from the WPF dispatcher.
        var task = Task.Run(operation);
        Check(task.Wait(TimeSpan.FromSeconds(10)), "Production UI Automation call exceeded its bounded wait.");
        task.GetAwaiter().GetResult();
    }

    private static nint AwaitWindow(int processId, string title)
    {
        nint window = 0;
        Await(() => (window = FindWindow(processId, title)) != 0, $"Production window '{title}' did not appear.");
        return window;
    }

    private static void Await(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException(failure);
    }

    private static nint FindWindow(int processId, string title)
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (pid == processId && text.ToString() == title && IsWindowVisible(window)) result = window;
            return true;
        }, 0);
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
