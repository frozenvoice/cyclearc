using System.IO;
using CycleArc.Services;
using CycleArc.Updates;
using Velopack;
using Velopack.Locators;

namespace CycleArc.UiSmoke;

/// <summary>Exercises the production adapter with real Velopack assets in an isolated installation.</summary>
internal static class PackageUpdateChecks
{
    public static void Run(string releaseDirectory)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "cyclearc-update-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { VerifyAsync(Path.GetFullPath(releaseDirectory), root).GetAwaiter().GetResult(); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyAsync(string releaseDirectory, string root)
    {
        var installation = Directory.CreateDirectory(Path.Combine(root, "installation")).FullName;
        var packages = Directory.CreateDirectory(Path.Combine(installation, "packages")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(installation, "current")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(root, "ProMeter")).FullName;
        var settings = Path.Combine(data, "settings.json");
        var accounts = Path.Combine(data, "codex-accounts.json");
        const string preferences = "{\"UiLanguage\":1,\"WidgetLeft\":-240,\"FirstRunCompleted\":true}";
        const string profiles = "{\"Version\":2,\"SelectedProfileId\":\"synthetic\",\"Profiles\":[]}";
        await File.WriteAllTextAsync(settings, preferences);
        await File.WriteAllTextAsync(accounts, profiles);
        var locator = new TestVelopackLocator("CycleArc", "0.5.9", packages, current, installation,
            Path.Combine(installation, "Update.exe"), "win");
        var manager = new UpdateManager(releaseDirectory,
            new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = 0 }, locator);
        var client = new VelopackUpdateClient(manager, packages);
        var coordinator = new AppUpdateCoordinator(client, maxAttempts: 1);
        await coordinator.CheckAsync();
        if (coordinator.State != AppUpdateState.Available || coordinator.Release is not { } release)
            throw new InvalidOperationException("The actual stable package was not discovered from the packaged feed.");
        var advertised = (await manager.CheckForUpdatesAsync())!.TargetFullRelease;
        var cached = Path.Combine(packages, advertised.FileName);
        await File.WriteAllTextAsync(cached, "damaged cached full package");
        await coordinator.DownloadAsync();
        if (coordinator.State != AppUpdateState.Ready)
            throw new InvalidOperationException("The adapter failed to replace a damaged cached package with the verified full package.");
        await UpdatePackageVerifier.VerifyAsync(packages, advertised.FileName, advertised.Size, advertised.SHA256);
        // Preserve length and corrupt just one byte after Ready: apply must re-hash and
        // fail before spawning Update.exe or asking the application to quit.
        using (var file = new FileStream(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var first = file.ReadByte();
            file.Position = 0;
            file.WriteByte((byte)(first ^ 0xff));
        }
        if (coordinator.ApplyOnExit() || coordinator.State != AppUpdateState.Failed)
            throw new InvalidOperationException("A package modified after verification was allowed to apply.");
        await coordinator.DownloadAsync();
        if (coordinator.State != AppUpdateState.Ready)
            throw new InvalidOperationException("A failed apply could not recover by explicitly downloading again.");
        if (await File.ReadAllTextAsync(settings) != preferences || await File.ReadAllTextAsync(accounts) != profiles)
            throw new InvalidOperationException("Updating touched the separate synthetic user data.");
        Console.WriteLine($"PASS: real Velopack feed/full package {release.Version}, damaged cache recovery, tamper-before-apply rejection, retry and data preservation.");
    }
}
