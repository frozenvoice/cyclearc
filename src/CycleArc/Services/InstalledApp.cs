using System.IO;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using Velopack;
using Velopack.Locators;

namespace CycleArc.Services;

/// <summary>Velopack owns packaged installs; loose development executables keep the legacy bootstrap.</summary>
public static class InstalledApp
{
    public static bool IsManaged { get; private set; }
    public static bool IsFirstRun { get; private set; }
    public static bool SupportsUpdates { get; private set; }
    public static string? LauncherPath { get; private set; }
    public static string? CallbackPath { get; private set; }

    public static void Initialize(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => IsFirstRun = true)
            // Velopack 1.2.0 stops the app, runs this hook from the installed executable, ignores
            // its result and then deletes the installation root. Uninstall can be neither cancelled
            // nor retried from here, so the cleanup is time-boxed and never throws. Velopack calls
            // Environment.Exit right after the hook: no WPF, login, model request or network use.
            .OnBeforeUninstallFastCallback(_ => RemoveOwnedClaudeCallbacks())
            .Run();
        var locator = VelopackLocator.Current;
        IsManaged = locator.AppId == "CycleArc" && locator.CurrentlyInstalledVersion is not null
            && !locator.IsPortable && locator.RootAppDir is not null && locator.AppContentDir is not null;
        if (!IsManaged) return;
        SupportsUpdates = locator.Channel == "win" && !locator.CurrentlyInstalledVersion!.IsPrerelease;
        LauncherPath = Path.Combine(locator.RootAppDir!, "CycleArc.exe");
        // The root stub launches asynchronously via Update.exe. Claude needs the real
        // process's stdin/stdout and exit code, so use the stable 'current' path instead.
        CallbackPath = Path.Combine(locator.RootAppDir!, "current", "CycleArc.exe");
    }

    private static void RemoveOwnedClaudeCallbacks()
    {
        try
        {
            // Read the existing data location only. Removal never creates, moves or deletes it.
            if (!Directory.Exists(AppPaths.RootPath)) return;
            // An unresolvable installation root cleans nothing and is recorded as incomplete,
            // rather than silently reported as a successful cleanup.
            ClaudeUninstallCleanup.RunAsync(new CodexAccountStore(AppPaths.RootPath),
                VelopackLocator.Current.RootAppDir ?? "").GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Removal continues regardless. The receipt records an incomplete cleanup.
        }
    }
}
