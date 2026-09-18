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
    public void Script_ValidatesPublishedReceiverBeforeRequestingReplacement()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        var receiver = text.IndexOf("'--claude-process'", StringComparison.Ordinal);
        var replacement = text.IndexOf("'--replace'", StringComparison.Ordinal);
        Assert.True(receiver >= 0 && replacement > receiver);
        Assert.Contains("'--expected-sha256'", text);
        Assert.Contains("'--expected-version'", text);
        Assert.DoesNotContain("Stop-CycleArcDesktopProcess -ProcessRecord", text);
        Assert.DoesNotContain("Install-StagedApp -StagingDir", text);
    }

    [Fact]
    public void FailFastOrder_RunsDesktopInstanceBeforeExpensiveGates()
    {
        var stages = ExtractDevRunStages(File.ReadAllText(DevRunScriptPath));
        var expected = new[]
        {
            "preflight",
            "release-guard",
            "restore",
            "tool-restore",
            "build",
            "ui-smoke-desktop-instance",
            "local-install-regression",
            "build-local-regression",
            "unit-test",
            "ui-smoke-full",
            "publish",
            "package",
            "package-verify"
        };
        Assert.Equal(expected, stages);

        var desktop = stages.IndexOf("ui-smoke-desktop-instance");
        Assert.True(desktop > stages.IndexOf("build"));
        Assert.True(stages.IndexOf("unit-test") > desktop);
        Assert.True(stages.IndexOf("ui-smoke-full") > stages.IndexOf("unit-test"));
        Assert.True(stages.IndexOf("publish") > stages.IndexOf("ui-smoke-full"));
        Assert.True(stages.IndexOf("package-verify") > stages.IndexOf("package"));
    }

    [Fact]
    public void FailFastOrder_StopsOnFirstFailureAndLeavesDesktopUntilPackageVerify()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        Assert.Contains("$ErrorActionPreference = 'Stop'", text, StringComparison.Ordinal);
        Assert.Contains("Failed at:", text, StringComparison.Ordinal);
        var noLaunch = text.IndexOf("if ($NoLaunch)", StringComparison.Ordinal);
        var packageVerify = text.IndexOf("Invoke-DevRunStep 'package-verify'", StringComparison.Ordinal);
        var start = text.IndexOf("[Diagnostics.Process]::Start", StringComparison.Ordinal);
        Assert.True(packageVerify >= 0 && noLaunch > packageVerify && start > noLaunch);
    }

    [Fact]
    public void Workflow_MatchesFailFastOrder()
    {
        var workflow = File.ReadAllText(WorkflowPath);
        var desktop = workflow.IndexOf("--desktop-instance", StringComparison.Ordinal);
        var localInstall = workflow.IndexOf("tests/LocalInstall.Tests.ps1", StringComparison.Ordinal);
        var buildLocal = workflow.IndexOf("tests/BuildLocal.Tests.ps1", StringComparison.Ordinal);
        var unit = workflow.IndexOf("dotnet test CycleArc.sln", StringComparison.Ordinal);
        var fullSmoke = workflow.IndexOf("Validate desktop resources", StringComparison.Ordinal);
        var publish = workflow.IndexOf("Publish win-x64", StringComparison.Ordinal);
        Assert.True(desktop >= 0 && localInstall > desktop && buildLocal > localInstall);
        Assert.True(unit > buildLocal && fullSmoke > unit && publish > fullSmoke);
        Assert.Contains("Desktop instance process checks", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArgsUiSmoke_DoesNotRepeatDesktopInstanceProcessChecks()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "tests", "CycleArc.UiSmoke", "Program.cs"));
        var targeted = text.IndexOf("if (args is [\"--desktop-instance\"])", StringComparison.Ordinal);
        Assert.True(targeted >= 0);
        var run = text.IndexOf("DesktopInstanceProcessChecks.Run();", targeted, StringComparison.Ordinal);
        var emptyUi = text.IndexOf("if (args.Length == 0)", StringComparison.Ordinal);
        Assert.True(run >= 0 && emptyUi > run);
        Assert.DoesNotContain("DesktopInstanceProcessChecks.Run();", text[emptyUi..], StringComparison.Ordinal);
        Assert.Contains("RunUiChecks()", text[emptyUi..], StringComparison.Ordinal);
    }

    [Fact]
    public void NoLaunch_ExitsBeforeInstallerStarts()
    {
        var text = File.ReadAllText(DevRunScriptPath);
        var noLaunch = text.IndexOf("if ($NoLaunch)", StringComparison.Ordinal);
        var exit = text.IndexOf("exit 0", noLaunch, StringComparison.Ordinal);
        var start = text.IndexOf("[Diagnostics.Process]::Start", StringComparison.Ordinal);
        Assert.True(noLaunch >= 0 && exit > noLaunch && start > exit);
        Assert.DoesNotContain("Resolve-InstallLayout", text);
        Assert.Contains("'Programs/CycleArc'", text);
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

    private static List<string> ExtractDevRunStages(string text)
    {
        const string marker = "Invoke-DevRunStep '";
        var results = new List<string>();
        var searchStart = 0;
        while (true)
        {
            var index = text.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (index < 0) break;
            var nameStart = index + marker.Length;
            var nameEnd = text.IndexOf('\'', nameStart);
            Assert.True(nameEnd > nameStart, "Invoke-DevRunStep is missing a closing quote.");
            results.Add(text[nameStart..nameEnd]);
            searchStart = nameEnd + 1;
        }

        return results;
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
