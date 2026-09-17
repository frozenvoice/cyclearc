namespace CycleArc.Tests;

public class BuildLocalScriptGuardTests
{
    [Fact]
    public void Cmd_ForwardsToBuildLocalScriptFromItsOwnDirectory()
    {
        Assert.True(File.Exists(CmdPath), $"build-local.cmd not found at {CmdPath}");
        Assert.True(File.Exists(ScriptPath), $"Build-Local.ps1 not found at {ScriptPath}");
        var cmd = File.ReadAllText(CmdPath);
        Assert.Contains("scripts\\Build-Local.ps1", cmd, StringComparison.Ordinal);
        Assert.Contains("%~dp0", cmd, StringComparison.Ordinal);
        Assert.Contains("pause", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E:\\Dev\\GitHub\\cyclearc", cmd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_PackagesThenInstallsManagedSetupWithoutCopyingDevelopmentExe()
    {
        var text = File.ReadAllText(ScriptPath);
        Assert.Contains("dev-run.ps1", text, StringComparison.Ordinal);
        Assert.Contains("-NoLaunch", text, StringComparison.Ordinal);
        Assert.Contains("--silent", text, StringComparison.Ordinal);
        Assert.Contains("current/CycleArc.exe", text, StringComparison.Ordinal);
        Assert.Contains("Get-BuildLocalSha256", text, StringComparison.Ordinal);
        Assert.Contains("--desktop-shutdown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy-ValidatedExecutable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Install-StagedApp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("git pull", text, StringComparison.Ordinal);
        var noLaunch = text.IndexOf("$devRun, '-NoLaunch'", StringComparison.Ordinal);
        var setup = text.IndexOf("publish/.dev-velopack/CycleArc-Setup.exe", StringComparison.Ordinal);
        Assert.True(noLaunch >= 0 && setup > noLaunch,
            "dev-run.ps1 -NoLaunch must run before the produced CycleArc-Setup.exe is used.");
    }

    private static readonly string RepoRoot = FindRepoRoot();
    private static string CmdPath => Path.Combine(RepoRoot, "build-local.cmd");
    private static string ScriptPath => Path.Combine(RepoRoot, "scripts", "Build-Local.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CycleArc.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (CycleArc.sln).");
    }
}
