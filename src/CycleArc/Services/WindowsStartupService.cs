using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CycleArc.Services;

public sealed class WindowsStartupService : IWindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void Apply(bool enabled)
    {
        // Portable uses shared preferences but must preserve the installed app's startup entry.
        if (InstalledApp.IsPortable) return;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            // Preserve startup consent and touch only previous entries owned by this installation.
            foreach (var (name, file) in new[]
            {
                (LegacyInstallation.StartupValueName, LegacyInstallation.ExecutableFileName),
                (LegacyInstallation.PreviousStartupValueName, LegacyInstallation.PreviousExecutableFileName)
            })
            {
                var command = key?.GetValue(name) as string;
                if (LegacyInstallation.OwnsStartupCommand(command, Environment.ProcessPath, file)
                    || OwnsMigratedStartup(command))
                    key!.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        WindowsStartupRegistration.Apply(enabled, Launcher, UserSid,
            new ScheduledStartupTasks(), new RegistryStartupRunEntry());
    }

    private static string Launcher => InstalledApp.LauncherPath ?? DesktopBootstrap.ExecutablePath;

    internal static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The Windows account has no SID.");
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

    public static bool IsEnabled() =>
        WindowsStartupRegistration.IsEnabled(Launcher, UserSid, new ScheduledStartupTasks(), new RegistryStartupRunEntry());

    /// <summary>
    /// Velopack's uninstall hook: removes this installation's own startup entry. Never throws,
    /// and never touches an entry that starts another installation.
    /// </summary>
    public static void RemoveForUninstall(string installRoot)
    {
        try
        {
            WindowsStartupRegistration.RemoveForUninstall(installRoot, UserSid,
                new ScheduledStartupTasks(), new RegistryStartupRunEntry());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            // Removal continues regardless.
        }
    }
}

/// <summary>The current user's CycleArc value under HKCU Run.</summary>
public sealed class RegistryStartupRunEntry : IStartupRunEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(WindowsStartupRegistration.RunValueName, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(WindowsStartupRegistration.RunValueName, command);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(WindowsStartupRegistration.RunValueName, throwOnMissingValue: false);
    }

    public byte[]? Approval()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(WindowsStartupRegistration.RunValueName) as byte[];
    }
}

/// <summary>Task Scheduler's own COM service, in the root folder the current user can write.</summary>
public sealed class ScheduledStartupTasks : IStartupTaskStore
{
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;
    private const int ExecAction = 0;
    private const int NotFound = unchecked((int)0x80070002);

    public StartupTaskState? Read(string name) => WithRoot(root =>
    {
        dynamic? task = null;
        try
        {
            task = root.GetTask(name);
            foreach (dynamic action in task.Definition.Actions)
            {
                if ((int)action.Type != ExecAction) continue;
                return new StartupTaskState((string)(action.Path ?? ""), (string)(action.Arguments ?? ""), (bool)task.Enabled);
            }
            return new StartupTaskState("", "", (bool)task.Enabled);
        }
        catch (Exception ex) when (ex.HResult == NotFound)
        {
            return null;
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
        }
    }, fallback: null);

    public bool Register(string name, string definitionXml) => WithRoot(root =>
    {
        try
        {
            var task = root.RegisterTask(name, definitionXml, CreateOrUpdate, null, null, InteractiveToken, null);
            Marshal.FinalReleaseComObject(task);
            return true;
        }
        catch (Exception ex) when (IsSchedulerFailure(ex))
        {
            return false;
        }
    }, fallback: false);

    public void Delete(string name) => WithRoot(root =>
    {
        try { root.DeleteTask(name, 0); }
        catch (Exception ex) when (ex.HResult == NotFound) { }
        return true;
    }, fallback: false);

    // Task Scheduler reports through HRESULTs that surface as COM, IO, access or binder
    // exceptions. Any of them means "not available", never a crash of the desktop.
    private static bool IsSchedulerFailure(Exception ex) =>
        ex is COMException or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static T WithRoot<T>(Func<dynamic, T> use, T fallback)
    {
        dynamic? service = null;
        dynamic? root = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return fallback;
            service = Activator.CreateInstance(type);
            if (service is null) return fallback;
            service.Connect();
            root = service.GetFolder("\\");
            return use(root);
        }
        catch (Exception ex) when (IsSchedulerFailure(ex))
        {
            return fallback;
        }
        finally
        {
            if (root is not null) Marshal.FinalReleaseComObject(root);
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }
}
