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
    public void PublishFlags_SharedGateKeepsSingleFileContract()
    {
        var devRunText = File.ReadAllText(DevRunScriptPath);
        const string marker = "Invoke-Dotnet -Arguments @('publish'";
        var start = devRunText.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The shared gate must publish the development executable.");
        var end = devRunText.IndexOf('\n', start);
        var publish = devRunText[start..(end < 0 ? devRunText.Length : end)];
        foreach (var flag in new[]
        {
            "'-c', 'Release'", "'-r', 'win-x64'", "'--self-contained', 'true'",
            "'-p:PublishSingleFile=true'", "'-p:IncludeNativeLibrariesForSelfExtract=true'",
            "'-p:DebugType=None'", "'-p:DebugSymbols=false'", "'-o', $StagingDir"
        })
        {
            Assert.Contains(flag, publish, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RequiredArtifacts_WorkflowUploadsVerifiedSharedGatePackages()
    {
        var workflowText = File.ReadAllText(WorkflowPath);
        var devRunText = File.ReadAllText(DevRunScriptPath);
        var packageOutput = System.Text.RegularExpressions.Regex.Match(devRunText,
            @"\$script:packageOutput = Join-Path \$RepoRoot '(?<path>[^']+)'");
        Assert.True(packageOutput.Success, "The shared gate must declare its package output directory.");
        var workflowArtifacts = ExtractWorkflowRelativePaths(workflowText,
            packageOutput.Groups["path"].Value + "/");
        Assert.Equal(new[] { "*" }, workflowArtifacts);
        Assert.Contains("name: CycleArc-win-x64", workflowText, StringComparison.Ordinal);
        Assert.Contains("$files.Count -ne 1 -or $files[0].Name -ne 'CycleArc.exe'", devRunText, StringComparison.Ordinal);
        Assert.Contains("'--update-package', $script:packageOutput", devRunText, StringComparison.Ordinal);
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
            "workflow-contract",
            "setup-ui-toolchain",
            "release-guard",
            "restore",
            "tool-restore",
            "build",
            "ui-smoke-desktop-instance",
            "local-install-regression",
            "build-local-regression",
            "unit-test",
            "ui-smoke-full",
            "widget-preview",
            "test-flavour-build",
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
    public void Workflow_DelegatesFailFastOrderToSharedGate()
    {
        var workflow = File.ReadAllText(WorkflowPath);
        const string gate = "./dev-run.ps1 -NoLaunch";
        var gateStart = workflow.IndexOf(gate, StringComparison.Ordinal);
        Assert.True(gateStart >= 0);
        Assert.Equal(-1, workflow.IndexOf(gate, gateStart + gate.Length, StringComparison.Ordinal));
        Assert.True(workflow.IndexOf("name: Upload Windows installer assets", StringComparison.Ordinal) > gateStart);
        Assert.DoesNotMatch(@"\bdotnet\s+(restore|build|test|publish)\b", workflow);
        Assert.DoesNotContain("-Fast", workflow, StringComparison.Ordinal);
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
        // The development build's own directory, kept apart from the managed install root.
        Assert.Contains("'Programs/CycleArc-dev'", text);
        Assert.DoesNotContain("'Programs/CycleArc'", text);
    }
    [Fact]
    public void Script_MatchesRunningProcessByExactNameOnly()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "LocalInstall.ps1"));
        Assert.Contains("Get-Process -Name 'prometer'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name 'prometer-companion-host'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name \"prometer-companion-host\"", text, StringComparison.Ordinal);
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
