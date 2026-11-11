using System.Xml.Linq;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// Windows startup moved from the HKCU Run value, which Explorer starts one by one after sign-in
/// and so started CycleArc minutes late, to a logon-triggered scheduled task. These cover the
/// migration, the fallback when Task Scheduler refuses, the person's own Startup apps and Task
/// Scheduler choices, opt-out and uninstall ownership.
/// </summary>
public sealed class WindowsStartupRegistrationTests
{
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private const string Launcher = @"C:\Users\Example\AppData\Local\Programs\CycleArc\CycleArc.exe";
    private static readonly string TaskName = WindowsStartupRegistration.TaskName(Sid);
    private static readonly string RunCommand = "\"" + Launcher + "\" --autorun";

    [Fact]
    public void OptInRegistersALogonTaskAndNoRunValue()
    {
        var tasks = new FakeTasks();
        var run = new FakeRun();

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, run);

        var task = Assert.Single(tasks.Tasks);
        Assert.Equal(TaskName, task.Key);
        Assert.True(WindowsStartupRegistration.Owns(tasks.Read(TaskName)!, Launcher));
        Assert.Null(run.Value);
    }

    [Fact]
    public void AnExistingRunValueMigratesToTheTask()
    {
        var tasks = new FakeTasks();
        var run = new FakeRun { Value = RunCommand, Approved = [2, 0, 0, 0] };

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, run);

        Assert.True(tasks.Tasks.ContainsKey(TaskName));
        Assert.Null(run.Value);
    }

    [Fact]
    public void WhenTaskSchedulerRefusesTheRunValueStillStartsCycleArc()
    {
        var tasks = new FakeTasks { Refuse = true };
        var run = new FakeRun();

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, run);

        Assert.Empty(tasks.Tasks);
        Assert.Equal(RunCommand, run.Value);
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)7)]
    public void SwitchedOffInStartupAppsStaysOff(byte state)
    {
        var tasks = new FakeTasks();
        tasks.Tasks[TaskName] = new StartupTaskState(Launcher, "--autorun", true);
        var run = new FakeRun { Value = "\"C:\\old\\CycleArc.exe\" --autorun", Approved = [state, 0, 0, 0] };

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, run);

        // A task would start CycleArc at sign-in behind the person's Startup apps choice.
        Assert.Empty(tasks.Tasks);
        Assert.Equal(RunCommand, run.Value);
        Assert.Equal(state, run.Approved![0]);
    }

    [Fact]
    public void ATaskTheUserDisabledIsNotReenabledOnEveryStart()
    {
        var tasks = new FakeTasks();
        tasks.Tasks[TaskName] = new StartupTaskState(Launcher, "--autorun", Enabled: false);
        var run = new FakeRun();

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, run);

        Assert.Equal(0, tasks.Registrations);
        Assert.False(tasks.Read(TaskName)!.Enabled);
    }

    [Fact]
    public void ATaskForAnotherLauncherIsRetargeted()
    {
        var tasks = new FakeTasks();
        tasks.Tasks[TaskName] = new StartupTaskState(@"C:\Users\Example\AppData\Local\CycleArc\CycleArc.exe", "--autorun", true);

        WindowsStartupRegistration.Apply(true, Launcher, Sid, tasks, new FakeRun());

        Assert.Equal(1, tasks.Registrations);
        Assert.True(WindowsStartupRegistration.Owns(tasks.Read(TaskName)!, Launcher));
    }

    [Fact]
    public void OptOutRemovesBothTheTaskAndTheRunValue()
    {
        var tasks = new FakeTasks();
        tasks.Tasks[TaskName] = new StartupTaskState(Launcher, "--autorun", true);
        var run = new FakeRun { Value = RunCommand };

        WindowsStartupRegistration.Apply(false, Launcher, Sid, tasks, run);

        Assert.Empty(tasks.Tasks);
        Assert.Null(run.Value);
    }

    [Fact]
    public void UninstallRemovesOnlyThisInstallationsEntries()
    {
        var root = Path.GetDirectoryName(Launcher)!;
        var tasks = new FakeTasks();
        tasks.Tasks[TaskName] = new StartupTaskState(Launcher, "--autorun", true);
        var run = new FakeRun { Value = RunCommand };
        WindowsStartupRegistration.RemoveForUninstall(root, Sid, tasks, run);
        Assert.Empty(tasks.Tasks);
        Assert.Null(run.Value);

        var otherTask = new StartupTaskState(@"D:\Elsewhere\CycleArc.exe", "--autorun", true);
        tasks.Tasks[TaskName] = otherTask;
        run.Value = "\"D:\\Elsewhere\\CycleArc.exe\" --autorun";
        WindowsStartupRegistration.RemoveForUninstall(root, Sid, tasks, run);
        Assert.Equal(otherTask, tasks.Read(TaskName));
        Assert.Equal("\"D:\\Elsewhere\\CycleArc.exe\" --autorun", run.Value);

        tasks.Tasks[TaskName] = new StartupTaskState(Launcher, "--autorun --show", true);
        WindowsStartupRegistration.RemoveForUninstall(root, Sid, tasks, run);
        Assert.True(tasks.Tasks.ContainsKey(TaskName));
    }

    [Fact]
    public void TheTaskStartsTheLauncherQuietlyAtThisAccountsSignIn()
    {
        var launcher = @"C:\Users\A & B\AppData\Local\Programs\CycleArc\CycleArc.exe";
        var xml = XDocument.Parse(WindowsStartupRegistration.BuildDefinition(Sid, launcher));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(params string[] path) => path.Aggregate((XElement)xml.Root!, (node, name) => node.Element(ns + name)!).Value;

        Assert.Equal(Sid, Value("Triggers", "LogonTrigger", "UserId"));
        Assert.Equal(Sid, Value("Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Value("Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", Value("Principals", "Principal", "RunLevel"));
        Assert.Equal(launcher, Value("Actions", "Exec", "Command"));
        Assert.Equal("--autorun", Value("Actions", "Exec", "Arguments"));
        Assert.Equal(Path.GetDirectoryName(launcher), Value("Actions", "Exec", "WorkingDirectory"));
        Assert.Equal("PT0S", Value("Settings", "ExecutionTimeLimit"));
        Assert.Equal("false", Value("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("5", Value("Settings", "Priority"));
        Assert.Null(xml.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Element(ns + "Delay"));
    }

    [Fact]
    public void EachWindowsAccountHasItsOwnTask()
    {
        Assert.NotEqual(WindowsStartupRegistration.TaskName(Sid), WindowsStartupRegistration.TaskName("S-1-5-21-1000-2000-3000-1002"));
        Assert.StartsWith("CycleArc Startup ", WindowsStartupRegistration.TaskName(Sid), StringComparison.Ordinal);
    }

    private sealed class FakeTasks : IStartupTaskStore
    {
        public Dictionary<string, StartupTaskState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Refuse { get; init; }
        public int Registrations { get; private set; }

        public StartupTaskState? Read(string name) => Tasks.GetValueOrDefault(name);

        public bool Register(string name, string definitionXml)
        {
            if (Refuse) return false;
            Registrations++;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var exec = XDocument.Parse(definitionXml).Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
            Tasks[name] = new StartupTaskState(exec.Element(ns + "Command")!.Value, exec.Element(ns + "Arguments")!.Value, true);
            return true;
        }

        public void Delete(string name) => Tasks.Remove(name);
    }

    private sealed class FakeRun : IStartupRunEntry
    {
        public string? Value { get; set; }
        public byte[]? Approved { get; init; }
        public string? Read() => Value;
        public void Write(string command) => Value = command;
        public void Delete() => Value = null;
        public byte[]? Approval() => Approved;
    }
}
