using System.Security;

namespace CycleArc.Services;

/// <summary>The task this installation registered, as Task Scheduler reports it.</summary>
public sealed record StartupTaskState(string Command, string Arguments, bool Enabled);

/// <summary>The per-user scheduled tasks CycleArc may read, create and delete.</summary>
public interface IStartupTaskStore
{
    StartupTaskState? Read(string name);

    /// <summary>Creates or replaces the task; false when Task Scheduler refuses or is unavailable.</summary>
    bool Register(string name, string definitionXml);

    void Delete(string name);
}

/// <summary>The HKCU Run value CycleArc used before scheduled tasks, and its Startup apps switch.</summary>
public interface IStartupRunEntry
{
    string? Read();
    void Write(string command);
    void Delete();

    /// <summary>The raw StartupApproved\Run state Windows keeps for the value, if any.</summary>
    byte[]? Approval();
}

/// <summary>
/// Windows startup for an installed desktop. A logon-triggered scheduled task starts as soon as
/// the person signs in; Explorer starts HKCU Run values one by one after sign-in, which left
/// CycleArc behind every other startup app, minutes late. The Run value remains the fallback
/// when Task Scheduler refuses the task, and stays in charge when the person has switched
/// CycleArc off under Startup apps, so that choice is never silently undone.
/// </summary>
public static class WindowsStartupRegistration
{
    public const string RunValueName = "CycleArc";
    public const string Arguments = "--autorun";
    private const string TaskNamePrefix = "CycleArc Startup";

    /// <summary>One task per Windows account, so accounts sharing a PC never replace each other's.</summary>
    public static string TaskName(string userSid) => TaskNamePrefix + " " + userSid;

    public static string RunCommand(string launcher) => "\"" + launcher + "\" " + Arguments;

    public static void Apply(bool enabled, string launcher, string userSid, IStartupTaskStore tasks, IStartupRunEntry run)
    {
        var name = TaskName(userSid);
        if (!enabled)
        {
            tasks.Delete(name);
            run.Delete();
            return;
        }

        if (IsSwitchedOffInStartupApps(run.Approval()) && run.Read() is not null)
        {
            // Keep the entry the person switched off, updated to this launcher, and nothing else.
            tasks.Delete(name);
            run.Write(RunCommand(launcher));
            return;
        }

        // An existing task for this launcher is left as it is, including one the person
        // disabled in Task Scheduler; rewriting it on every start would undo that.
        if (tasks.Read(name) is { } existing && Owns(existing, launcher))
        {
            run.Delete();
            return;
        }

        if (tasks.Register(name, BuildDefinition(userSid, launcher)))
        {
            run.Delete();
            return;
        }

        run.Write(RunCommand(launcher));
    }

    public static bool IsEnabled(string launcher, string userSid, IStartupTaskStore tasks, IStartupRunEntry run) =>
        run.Read() is not null || tasks.Read(TaskName(userSid)) is { } task && Owns(task, launcher);

    /// <summary>
    /// Removal of one installation: only a task or Run value that starts a launcher inside that
    /// installation's root. Another installation's entry, or one that was edited, stays.
    /// </summary>
    public static void RemoveForUninstall(string installRoot, string userSid, IStartupTaskStore tasks, IStartupRunEntry run)
    {
        var launcher = Path.Combine(installRoot, "CycleArc.exe");
        var name = TaskName(userSid);
        if (tasks.Read(name) is { } task && Owns(task, launcher)) tasks.Delete(name);
        if (string.Equals(run.Read(), RunCommand(launcher), StringComparison.OrdinalIgnoreCase)) run.Delete();
    }

    public static bool Owns(StartupTaskState task, string launcher) =>
        string.Equals(task.Arguments.Trim(), Arguments, StringComparison.Ordinal)
        && SamePath(task.Command.Trim().Trim('"'), launcher);

    /// <summary>
    /// Windows records Startup apps choices as a byte array whose first byte is even when the
    /// entry is enabled (2, 6) and odd when it was switched off (3, 7).
    /// </summary>
    public static bool IsSwitchedOffInStartupApps(byte[]? approval) => approval is { Length: > 0 } && (approval[0] & 1) == 1;

    /// <summary>
    /// Runs at sign-in of this account only, with its ordinary rights, without the battery and
    /// idle conditions or the three-day time limit Task Scheduler applies by default, and at
    /// normal rather than the default below-normal priority.
    /// </summary>
    public static string BuildDefinition(string userSid, string launcher)
    {
        var sid = SecurityElement.Escape(userSid);
        var command = SecurityElement.Escape(launcher);
        var directory = SecurityElement.Escape(Path.GetDirectoryName(launcher) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>CycleArc</Author>
                <Description>Starts CycleArc in the notification area when you sign in to Windows. Turn it off in CycleArc Settings.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{Arguments}</Arguments>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
