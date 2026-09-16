using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using CycleArc.Updates;

namespace CycleArc.UiSmoke;

/// <summary>
/// Runs the real Velopack updater against a disposable portable installation.
/// This deliberately does not invoke Setup.exe or the installed application.
/// </summary>
internal static class ManagedApplyChecks
{
    private const string PackageId = "CycleArc";
    private const string OldVersion = "0.5.9";
    private const string MainExecutable = "CycleArc.exe";
    private static readonly TimeSpan UpdaterTimeout = TimeSpan.FromSeconds(90);

    public static void Run(string releaseDirectory)
    {
        var releaseRoot = Path.GetFullPath(releaseDirectory);
        if (!Directory.Exists(releaseRoot))
            throw new DirectoryNotFoundException($"Release directory does not exist: {releaseRoot}");

        var testRoot = Path.Combine(Path.GetTempPath(), "cyclearc-managed-apply-" + Guid.NewGuid().ToString("N"));
        var installationRoot = Path.Combine(testRoot, "installation");
        var dataRoot = Path.Combine(testRoot, "ProMeter");
        Directory.CreateDirectory(installationRoot);
        Directory.CreateDirectory(dataRoot);

        try
        {
            RunAsync(releaseRoot, installationRoot, dataRoot).GetAwaiter().GetResult();
        }
        finally
        {
            // The timeout path must never leave an updater alive while its root is deleted.
            TryDeleteTree(testRoot);
        }
    }

    private static async Task RunAsync(string releaseRoot, string installationRoot, string dataRoot)
    {
        var packagePath = FindFullPackage(releaseRoot);
        var packageFileName = Path.GetFileName(packagePath);
        var packagesDirectory = Directory.CreateDirectory(Path.Combine(installationRoot, "packages")).FullName;
        var currentDirectory = Directory.CreateDirectory(Path.Combine(installationRoot, "current")).FullName;
        var logsDirectory = Directory.CreateDirectory(Path.Combine(installationRoot, "logs")).FullName;
        var installedPackage = Path.Combine(packagesDirectory, packageFileName);
        File.Copy(packagePath, installedPackage, overwrite: false);

        var packageApp = ReadAppFiles(packagePath);
        var targetVersion = ReadVersion(packageApp["sq.version"]);
        if (string.Equals(targetVersion, OldVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release package must be newer than the synthetic 0.5.9 installation.");
        if (!packageApp.ContainsKey(MainExecutable)
            || !packageApp.ContainsKey("Squirrel.exe")
            || !packageApp.ContainsKey("CycleArc_ExecutionStub.exe")
            || !packageApp.ContainsKey("sq.version"))
            throw new InvalidOperationException("The full package is missing the files required for a managed apply smoke test.");

        var expectedApplicationHash = ComputeSha256(packageApp[MainExecutable]);
        WriteSyntheticInstallation(currentDirectory, installationRoot, packageApp);
        var dataSentinel = WriteDataSentinels(dataRoot);

        File.WriteAllBytes(Path.Combine(installationRoot, ".portable"), []);
        File.Copy(Path.Combine(currentDirectory, "Squirrel.exe"), Path.Combine(installationRoot, "Update.exe"));
        File.Copy(Path.Combine(currentDirectory, "CycleArc_ExecutionStub.exe"), Path.Combine(installationRoot, "CycleArc.exe"));

        var snapshot = UpdateRecoverySnapshot.Create(installationRoot,
            Path.Combine(Path.GetDirectoryName(installationRoot)!, "recovery"), OldVersion);

        var updatePath = Path.Combine(installationRoot, "Update.exe");
        var logPath = Path.Combine(logsDirectory, "apply.log");
        var exitCode = await RunUpdaterAsync(updatePath, installationRoot, packagesDirectory, logPath, installedPackage);
        if (exitCode != 0)
        {
            var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "<no updater log>";
            throw new InvalidOperationException($"Velopack Update.exe apply failed with exit code {exitCode}.\n{log}");
        }

        var updatedVersion = ReadVersion(File.ReadAllBytes(Path.Combine(currentDirectory, "sq.version")));
        if (!string.Equals(updatedVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Updater installed version {updatedVersion}, expected {targetVersion}.");
        if (File.Exists(Path.Combine(currentDirectory, "old-version-marker.txt")))
            throw new InvalidOperationException("The old current directory marker survived the package replacement.");

        var actualApplicationHash = ComputeSha256(await File.ReadAllBytesAsync(Path.Combine(currentDirectory, MainExecutable)));
        if (!string.Equals(actualApplicationHash, expectedApplicationHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed application does not match the full package payload.");
        if (!File.Exists(updatePath) || !File.Exists(Path.Combine(installationRoot, "CycleArc.exe")))
            throw new InvalidOperationException("The portable updater root lost its stable launcher files.");
        AssertSentinelsUnchanged(dataRoot, dataSentinel);

        // Exercise restoring a full production executable after the real updater has
        // replaced current. An external helper has its old binary open during recovery.
        using (var helperFile = new FileStream(Path.Combine(snapshot.SnapshotDirectory, MainExecutable),
            FileMode.Open, FileAccess.Read, FileShare.Read))
            snapshot.Restore();
        if (ReadVersion(File.ReadAllBytes(Path.Combine(currentDirectory, "sq.version"))) != OldVersion
            || !File.Exists(Path.Combine(currentDirectory, "old-version-marker.txt"))
            || ComputeSha256(Path.Combine(currentDirectory, MainExecutable)) != expectedApplicationHash)
            throw new InvalidOperationException("The previous production installation was not restored.");
        snapshot.Verify();
        AssertSentinelsUnchanged(dataRoot, dataSentinel);

        Console.WriteLine($"PASS: real Velopack Update.exe applied {targetVersion} in an isolated portable root, then restored the previous production files from an open external snapshot; external ProMeter data is unchanged.");
    }

    private static string FindFullPackage(string releaseRoot)
    {
        var packages = Directory.GetFiles(releaseRoot, $"{PackageId}-*-full.nupkg", SearchOption.TopDirectoryOnly);
        if (packages.Length != 1)
            throw new InvalidOperationException($"Expected exactly one {PackageId}-*-full.nupkg in {releaseRoot}, found {packages.Length}.");
        return Path.GetFullPath(packages[0]);
    }

    private static Dictionary<string, byte[]> ReadAppFiles(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        const string prefix = "lib/app/";
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            var relativeName = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (relativeName.Length == 0 || Path.IsPathRooted(relativeName) || relativeName.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe app entry in package: {entry.FullName}");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files.Add(relativeName, memory.ToArray());
        }
        return files;
    }

    private static void WriteSyntheticInstallation(string currentDirectory, string installationRoot, IReadOnlyDictionary<string, byte[]> appFiles)
    {
        foreach (var pair in appFiles)
        {
            var destination = Path.GetFullPath(Path.Combine(currentDirectory, pair.Key));
            EnsureChildPath(currentDirectory, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var bytes = pair.Key.Equals("sq.version", StringComparison.OrdinalIgnoreCase)
                ? ReplaceVersion(pair.Value, OldVersion)
                : pair.Value;
            File.WriteAllBytes(destination, bytes);
        }
        File.WriteAllText(Path.Combine(currentDirectory, "old-version-marker.txt"), "synthetic 0.5.9 marker", Encoding.UTF8);

        // The root stable launcher is copied below after current is prepared. This helper
        // is intentionally limited to files extracted from the trusted local package.
        _ = installationRoot;
    }

    private static byte[] ReplaceVersion(byte[] sqVersion, string version)
    {
        var text = Encoding.UTF8.GetString(sqVersion);
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var versionElement = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "version")
            ?? throw new InvalidOperationException("sq.version has no version element.");
        versionElement.Value = version;
        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }

    private static string ReadVersion(byte[] sqVersion)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(sqVersion));
        return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "version")?.Value
            ?? throw new InvalidOperationException("sq.version has no version element.");
    }

    private static Dictionary<string, string> WriteDataSentinels(string dataRoot)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings.json"] = "{\"UiLanguage\":1,\"WidgetLeft\":-240,\"FirstRunCompleted\":true}",
            ["codex-accounts.json"] = "{\"Version\":2,\"SelectedProfileId\":\"synthetic\",\"Profiles\":[]}",
        };
        foreach (var pair in values)
            File.WriteAllText(Path.Combine(dataRoot, pair.Key), pair.Value, Encoding.UTF8);
        return values;
    }

    private static void AssertSentinelsUnchanged(string dataRoot, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var pair in expected)
        {
            var path = Path.Combine(dataRoot, pair.Key);
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != pair.Value)
                throw new InvalidOperationException($"The external synthetic ProMeter sentinel changed: {pair.Key}");
        }
    }

    private static async Task<int> RunUpdaterAsync(string updatePath, string root, string packagesDirectory, string logPath, string packagePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updatePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--rootDir");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("--packageDir");
        startInfo.ArgumentList.Add(packagesDirectory);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("apply");
        startInfo.ArgumentList.Add("--norestart");
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(packagePath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Could not start the isolated Velopack updater.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(UpdaterTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync();
            throw new TimeoutException($"Velopack updater exceeded {UpdaterTimeout.TotalSeconds:0} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
            File.AppendAllText(logPath, $"\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}\n", Encoding.UTF8);
        return process.ExitCode;
    }

    private static string EnsureChildPath(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Package extraction escaped its target root: {candidate}");
        return fullCandidate;
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the original updater log when cleanup is blocked by Windows file
            // handles; the smoke test has already reported the apply result by this point.
        }
    }
}
