using Microsoft.Win32;

namespace CycleArc.Services;

public sealed class WindowsStartupService : IWindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CycleArc";

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
        {
            return;
        }

        // Preserve startup consent and touch only previous entries owned by this installation.
        foreach (var (name, file) in new[]
        {
            (LegacyInstallation.StartupValueName, LegacyInstallation.ExecutableFileName),
            (LegacyInstallation.PreviousStartupValueName, LegacyInstallation.PreviousExecutableFileName)
        })
        {
            var command = key.GetValue(name) as string;
            if (LegacyInstallation.OwnsStartupCommand(command, Environment.ProcessPath, file)
                || OwnsMigratedStartup(command))
                key.DeleteValue(name, throwOnMissingValue: false);
        }

        if (enabled)
        {
            key.SetValue(ValueName, "\"" + DesktopBootstrap.ExecutablePath + "\" --autorun");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static bool OwnsMigratedStartup(string? command)
    {
        if (command is null) return false;
        foreach (var path in DesktopBootstrap.ReadMigrationPaths())
        {
            if (!string.Equals(command, "\"" + path + "\"", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "\"" + path + "\" --autorun", StringComparison.OrdinalIgnoreCase)) continue;
            try { DesktopBootstrap.ValidateExecutable(path); return true; }
            catch { /* Never remove an unverified startup entry. */ }
        }
        return false;
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }
}
