namespace CycleArc.Tests;

/// <summary>
/// Structural guards for dev-run.ps1. These check invariants in the script text
/// (ordering, required flags/files) rather than executing PowerShell, so they stay
/// meaningful without being coupled to exact wording.
/// </summary>
public class DevRunScriptGuardTests
{
    [Fact]
    public void DevRunScript_ExistsAtRepoRoot()
    {
        Assert.True(File.Exists(DevRunScriptPath), $"dev-run.ps1 not found at {DevRunScriptPath}");
    }

    [Fact]
    public void PublishFlags_MatchGitHubActionsWorkflow()
    {
        var workflowFlags = ExtractWorkflowPublishFlags(File.ReadAllText(WorkflowPath));
        var devRunText = File.ReadAllText(DevRunScriptPath);

        Assert.NotEmpty(workflowFlags);
        foreach (var flag in workflowFlags)
        {
            Assert.Contains(flag, devRunText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RequiredArtifacts_MatchGitHubActionsAssertion()
    {
        var workflowText = File.ReadAllText(WorkflowPath);
        var devRunText = File.ReadAllText(DevRunScriptPath);

        var workflowArtifacts = ExtractWorkflowRelativePaths(workflowText, "publish/win-x64/");
        Assert.NotEmpty(workflowArtifacts);

        foreach (var artifact in workflowArtifacts)
        {
            var backslash = artifact.Replace('/', '\\');
            Assert.True(
                devRunText.Contains(artifact, StringComparison.Ordinal) || devRunText.Contains(backslash, StringComparison.Ordinal),
                $"dev-run.ps1 does not validate required artifact '{artifact}'");
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData(" ")]
    public void ArtifactPaths_StopAtWhitespace(string separator)
    {
        var paths = ExtractWorkflowRelativePaths("path: publish/win-x64/CycleArc.exe" + separator + "next: value", "publish/win-x64/");
        Assert.Equal(new[] { "CycleArc.exe" }, paths);
    }

    [Fact]
    public void Script_PublishesToStagingBeforeTouchingLocal()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        var stagingIndex = text.IndexOf(".dev-staging", StringComparison.Ordinal);
        var stopProcessIndex = text.IndexOf("Stop-CycleArcDesktopProcess -ProcessRecord", StringComparison.Ordinal);

        Assert.True(stagingIndex >= 0, "dev-run.ps1 does not reference a staging directory.");
        Assert.True(stopProcessIndex >= 0, "dev-run.ps1 does not stop the running CycleArc process.");
        Assert.True(stagingIndex < stopProcessIndex, "dev-run.ps1 must publish to staging before it ever stops the running process.");
    }

    [Fact]
    public void Script_OnlyStopsRunningProcess_AfterPublishIsValidated()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        var validatedIndex = text.IndexOf("Publish artifacts verified", StringComparison.Ordinal);
        var stopProcessIndex = text.IndexOf("Stop-CycleArcDesktopProcess -ProcessRecord", StringComparison.Ordinal);

        Assert.True(validatedIndex >= 0, "dev-run.ps1 does not report publish artifact validation.");
        Assert.True(stopProcessIndex >= 0, "dev-run.ps1 does not stop the running CycleArc process.");
        Assert.True(validatedIndex < stopProcessIndex, "dev-run.ps1 must validate the publish output before stopping the running process.");
    }

    [Fact]
    public void NoLaunch_ExitsBeforeStoppingOrDeployingAnything()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        var noLaunchExitIndex = text.IndexOf("if ($NoLaunch)", StringComparison.Ordinal);
        var stopProcessIndex = text.IndexOf("Stop-CycleArcDesktopProcess -ProcessRecord", StringComparison.Ordinal);
        var moveToLocalIndex = text.IndexOf("Install-StagedApp -StagingDir", StringComparison.Ordinal);
        var mutexCheckIndex = text.IndexOf("Assert-InstallDesktopMutexAbsent", StringComparison.Ordinal);

        Assert.True(noLaunchExitIndex >= 0, "dev-run.ps1 does not branch on -NoLaunch.");
        Assert.True(stopProcessIndex >= 0 && moveToLocalIndex >= 0);
        Assert.True(noLaunchExitIndex < stopProcessIndex, "-NoLaunch must be checked before the process is stopped.");
        Assert.True(noLaunchExitIndex < moveToLocalIndex, "-NoLaunch must be checked before publish\\local is replaced.");
        Assert.True(stopProcessIndex < mutexCheckIndex && mutexCheckIndex < moveToLocalIndex,
            "Existing desktop instances and their mutex must be cleared before installation files move.");

        var noLaunchBlockEnd = text.IndexOf("exit 0", noLaunchExitIndex, StringComparison.Ordinal);
        Assert.True(noLaunchBlockEnd >= 0 && noLaunchBlockEnd < stopProcessIndex, "-NoLaunch must exit before any deploy/launch step runs.");
    }

    [Fact]
    public void Script_MatchesRunningProcessByExactNameOnly()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "LocalInstall.ps1"));
        Assert.Contains("Get-Process -Name 'prometer'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name 'prometer-companion-host'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name \"prometer-companion-host\"", text, StringComparison.Ordinal);
    }

    private static List<string> ExtractWorkflowPublishFlags(string workflowText)
    {
        var start = workflowText.IndexOf("dotnet publish", StringComparison.Ordinal);
        Assert.True(start >= 0, "windows.yml does not contain a 'dotnet publish' step.");
        var end = workflowText.IndexOf("- name:", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = workflowText.Length;
        }

        var block = workflowText[start..end];
        var tokens = block.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var flags = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].TrimEnd('\r');
            if (token == "-o")
            {
                // dev-run.ps1 intentionally publishes to a local staging directory,
                // not the CI output path - that's the one deliberate difference.
                i++;
                continue;
            }

            if (token.StartsWith('-') || token is "win-x64" or "true")
            {
                flags.Add(token);
            }
        }

        return flags.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractWorkflowRelativePaths(string workflowText, string prefix)
    {
        var results = new List<string>();
        var searchStart = 0;
        while (true)
        {
            var index = workflowText.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var end = index + prefix.Length;
            while (end < workflowText.Length && !char.IsWhiteSpace(workflowText[end])
                   && workflowText[end] is not (')' or '"' or '\''))
            {
                end++;
            }

            results.Add(workflowText[(index + prefix.Length)..end]);
            searchStart = end;
        }

        return results.Distinct(StringComparer.Ordinal).ToList();
    }

    private static readonly string RepoRoot = FindRepoRoot();
    private static string DevRunScriptPath => Path.Combine(RepoRoot, "dev-run.ps1");
    private static string WorkflowPath => Path.Combine(RepoRoot, ".github", "workflows", "windows.yml");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CycleArc.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (CycleArc.sln) from " + AppContext.BaseDirectory);
    }
}
