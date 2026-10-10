using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using Microsoft.Win32;

namespace CycleArc.UiSmoke;

/// <summary>Runs only on a fresh disposable Windows profile, using the exact production ZIP.</summary>
internal static class PortableDistributionChecks
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CycleArc";
    private static readonly string[] NativeHosts = [
        @"Software\Google\Chrome\NativeMessagingHosts\com.prometer.bridge",
        @"Software\Microsoft\Edge\NativeMessagingHosts\com.prometer.bridge",
        @"Software\Naver\Naver Whale\NativeMessagingHosts\com.prometer.bridge"];

    public static void Run(string assetDirectory)
    {
        Check(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CYCLEARC_DISPOSABLE_PROFILE") == "1",
            "Portable production verification requires an explicitly disposable Windows profile.");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = AppPaths.RootPath;
        var managedRoots = new[] { Path.Combine(local, "CycleArc"), Path.Combine(local, "Programs", "CycleArc"), DesktopBootstrap.InstallDirectory };
        Check(!Directory.Exists(data) && managedRoots.All(root => !Directory.Exists(root)),
            "Portable production verification refuses an existing CycleArc/ProMeter profile.");
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            Check(key is null, "Portable verification refuses a registered CycleArc installation.");
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            Check(key?.GetValue("CycleArc") is null, "Portable verification refuses an existing CycleArc startup entry.");
        foreach (var path in NativeHosts)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            Check(key is null, "Portable verification refuses existing legacy native-host registrations.");
        }
        using (var lease = DesktopInstanceLease.TryAcquire("Local\\ProMeter.SingleInstance.PortableProbe"))
            Check(lease is not null, "Portable probe already running.");
        var preexisting = Request(DesktopInstanceCommand.Status);
        Check(!preexisting.Succeeded, "Portable verification refuses a running desktop.");
        var archives = Directory.GetFiles(Path.GetFullPath(assetDirectory), "CycleArc-*-win-x64-portable.zip");
        Check(archives.Length == 1, "Expected exactly one versioned portable ZIP.");
        var work = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "portable-distribution"));
        Check(!Directory.Exists(work), "The portable verification staging directory must be empty.");
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "portable-distribution");
        Directory.CreateDirectory(output);
        var extraction = Path.Combine(work, "portable");
        Directory.CreateDirectory(extraction);
        Process? desktop = null;
        var seeded = false;
        try
        {
            using (var archive = ZipFile.OpenRead(archives[0]))
            {
                Check(archive.Entries.Count == 1 && archive.Entries[0].FullName == "CycleArc.exe",
                    "Portable ZIP must contain only the root self-contained executable.");
                archive.Entries[0].ExtractToFile(Path.Combine(extraction, "CycleArc.exe"));
            }
            var executable = Path.Combine(extraction, "CycleArc.exe");
            var info = FileVersionInfo.GetVersionInfo(executable);
            var hash = Hash(executable);
            var id = "10000000000000000000000000000001";
            var store = new CodexAccountStore(data);
            var config = Directory.CreateDirectory(Path.Combine(work, "synthetic-claude")).FullName;
            var profile = new CodexAccountProfile(id, "", "Portable synthetic account") { Provider = UsageProviderId.Claude };
            store.Save(new CodexAccountConfiguration(3, id, [profile]));
            new ClaudeConnectionStore(store, id).Save(new ClaudeConnectionBinding(2, id, config,
                Path.Combine(work, "missing-claude.exe"), false, new string('A', 64), DateTimeOffset.UtcNow));
            var settings = AppSettings.CreateDefaults();
            settings.FirstRunCompleted = true;
            settings.StartWithWindows = true; // Portable must preserve the managed preference and entry.
            settings.FloatingWidgetEnabled = true;
            settings.CodexRefreshIntervalMinutes = 60;
            settings.UsageAlertsEnabled = false;
            settings.UiLanguage = UiLanguage.English;
            new SettingsStore(Path.Combine(data, "settings.json")).Save(settings);
            var sentinel = Path.Combine(data, "portable-preservation-sentinel.txt");
            File.WriteAllText(sentinel, "synthetic account/settings data outside the portable directory");
            var sentinelHash = Hash(sentinel);
            seeded = true;
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue("CycleArc", "\"C:\\synthetic-installed\\CycleArc.exe\" --autorun");
            foreach (var path in NativeHosts)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("", Path.Combine(data, LegacyInstallation.NativeHostManifestFileName));
            }
            var registry = SnapshotRegistry();
            desktop = Start(executable, work);
            var ready = AwaitReady(desktop, executable, info.ProductVersion!);
            var widget = AwaitWidget(desktop);
            Check(GetDpiForWindow(widget) > 0, "Widget has no valid native DPI.");
            Check(HasTraySink(desktop.Id), "The portable desktop did not create its native tray notification sink.");
            using (var second = Start(executable, work))
            {
                Check(second.WaitForExit(15_000) && second.ExitCode == 0, "Second portable launch failed to activate the first instance.");
                var stillRunning = Request(DesktopInstanceCommand.Status);
                Check(stillRunning.Succeeded && stillRunning.ProcessId == desktop.Id && stillRunning.InstanceId == ready.InstanceId,
                    "Second portable launch replaced the running desktop or changed its identity.");
            }
            Check(registry == SnapshotRegistry(), "Portable startup changed startup, uninstall or browser native-host registry entries.");
            Check(managedRoots.All(root => !Directory.Exists(root)), "Portable startup created a managed/development installation.");
            Check(Directory.GetFileSystemEntries(extraction).SequenceEqual([executable]), "Portable startup polluted its extracted folder.");
            Check(Hash(executable) == hash && Hash(sentinel) == sentinelHash, "Portable startup changed its executable or shared data sentinel.");
            Shutdown(desktop, ready);
            desktop.Dispose();
            desktop = null;
            var savedSettings = new SettingsStore(Path.Combine(data, "settings.json")).Load();
            Check(savedSettings.StartWithWindows && savedSettings.FloatingWidgetEnabled,
                "Portable shutdown changed the shared startup/widget preferences.");
            Check(store.ReadExisting()?.Profiles.Single().Id == id, "Portable startup lost the shared synthetic account.");
            desktop = Start(executable, work);
            var restarted = AwaitReady(desktop, executable, info.ProductVersion!);
            AwaitWidget(desktop);
            Check(restarted.InstanceId != ready.InstanceId, "Portable relaunch did not create a new desktop identity.");
            Shutdown(desktop, restarted);
            desktop.Dispose();
            desktop = null;
            Check(registry == SnapshotRegistry() && Hash(sentinel) == sentinelHash && Hash(executable) == hash,
                "Portable relaunch changed protected registry, executable or shared data.");
            File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new
            {
                archive = Path.GetFileName(archives[0]), version = info.ProductVersion, executableSha256 = hash,
                executablePath = executable, standalone = true, selfContained = true, trayReady = true,
                widgetNativeVisible = true, secondLaunchActivatesFirst = true, sharedDataPreserved = true,
                registryPreserved = true, managedInstallCreated = false, manualRelaunch = true,
                checkedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: exact production portable ZIP: standalone self-contained launch, native tray/widget, shared data, second-launch activation, restart, registry/managed-install isolation.");
        }
        finally
        {
            if (desktop is not null)
            {
                try { if (!desktop.HasExited) { desktop.Kill(true); desktop.WaitForExit(10_000); } }
                finally { desktop.Dispose(); }
            }
            if (seeded)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                key?.DeleteValue("CycleArc", false);
                foreach (var path in NativeHosts) Registry.CurrentUser.DeleteSubKey(path, false);
            }
            // Both paths were checked absent and created by this disposable-only check.
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    private static Process Start(string executable, string work)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
        start.ArgumentList.Add("--quiet");
        // Exercise self-contained startup without any installed dotnet or provider CLI on PATH.
        start.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        start.Environment["DOTNET_ROOT"] = Path.Combine(work, "missing-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(work, "missing-dotnet");
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(work, "native-extraction");
        start.Environment["CODEX_HOME"] = Path.Combine(work, "missing-codex");
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(work, "synthetic-claude");
        return Process.Start(start) ?? throw new InvalidOperationException("Portable executable did not start.");
    }

    internal static DesktopInstanceResponse Request(DesktopInstanceCommand command, DesktopInstanceResponse? expected = null)
        => DesktopInstanceClient.RequestAsync(DesktopInstancePipe.ForCurrentUserSession(), command,
            TimeSpan.FromSeconds(2), expectedInstance: expected).GetAwaiter().GetResult();

    private static DesktopInstanceResponse AwaitReady(Process process, string executable, string version)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            Check(!process.HasExited, "Portable executable exited before desktop readiness.");
            var result = Request(DesktopInstanceCommand.Status);
            if (result.Succeeded)
            {
                Check(result.ProcessId == process.Id && result.ExecutablePath.Equals(executable, StringComparison.OrdinalIgnoreCase)
                    && result.Version == version, "Portable status reported another executable, process or version.");
                return result;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Portable desktop did not report readiness.");
    }

    private static void Shutdown(Process process, DesktopInstanceResponse ready)
    {
        var result = Request(DesktopInstanceCommand.Shutdown, ready);
        Check(result.Succeeded && result.ProcessId == process.Id && process.WaitForExit(25_000) && process.ExitCode == 0,
            "Portable desktop did not shut down cleanly.");
    }

    internal static nint AwaitWidget(Process process)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            nint widget = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                var title = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                if (pid == process.Id && title.ToString() == "CycleArc widget" && IsWindowVisible(window)) widget = window;
                return true;
            }, 0);
            if (widget != 0) return widget;
            Check(!process.HasExited, "Portable exited while waiting for its widget.");
            Thread.Sleep(100);
        }
        throw new TimeoutException("Portable widget native window was not visible.");
    }

    internal static bool HasTraySink(int processId)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (pid == processId && name.ToString().StartsWith("WindowsForms10.Window", StringComparison.Ordinal)) found = true;
            return true;
        }, 0);
        return found;
    }

    private static string SnapshotRegistry()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { RunKey, UninstallKey }.Concat(NativeHosts))
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            values[path] = key is null ? "absent" : "present";
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
                values[path + "|" + name] = key.GetValueKind(name) + ":" + JsonSerializer.Serialize(
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        return JsonSerializer.Serialize(values);
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
