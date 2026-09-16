using System.IO;
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
            .OnFirstRun(_ => IsFirstRun = true).Run();
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
}
