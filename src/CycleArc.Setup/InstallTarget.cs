using Microsoft.Win32;

namespace CycleArc.Setup;

/// <summary>Where this installer will put the application, and why.</summary>
internal sealed record InstallTarget(string Directory, bool IsExistingInstall, string Source);

internal static class InstallTargets
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CycleArc";

    /// <summary>
    /// The default for a brand new installation. Per-user, never Program Files: the engine
    /// writes here without elevation and the app updates itself in place.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CycleArc");

    /// <summary>
    /// The location Velopack used before the default moved under Programs. An installation
    /// already there keeps its place; changing the default must not move or duplicate it.
    /// </summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CycleArc");

    /// <summary>
    /// Resolves the directory to install into, in the same order the repository's PowerShell
    /// helpers use: the registered uninstall entry first, then a recognisable installation at
    /// either known root, and only then the new-install default.
    /// </summary>
    public static InstallTarget Resolve()
    {
        var registered = FromUninstallEntry();
        if (registered is not null && Looks(registered))
            return new InstallTarget(registered, true, "registered");
        // A registered path that no longer holds an installation is still the owner's choice
        // of location: reinstalling there repairs it rather than leaving two copies behind.
        if (registered is not null)
            return new InstallTarget(registered, false, "registered-empty");

        foreach (var candidate in new[] { DefaultRoot, LegacyRoot })
            if (Looks(candidate)) return new InstallTarget(candidate, true, "detected");

        return new InstallTarget(DefaultRoot, false, "default");
    }

    /// <summary>A Velopack-managed installation has both the current build and the updater.</summary>
    public static bool Looks(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            return File.Exists(Path.Combine(root, "current", "CycleArc.exe"))
                && File.Exists(Path.Combine(root, "Update.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The stable launcher, which is what a shortcut and autorun point at.</summary>
    public static string Launcher(string root) => Path.Combine(root, "CycleArc.exe");

    private static string? FromUninstallEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var location = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(location)) return null;
            return Path.GetFullPath(location.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
