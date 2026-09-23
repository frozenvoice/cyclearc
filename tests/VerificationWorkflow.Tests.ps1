#Requires -Version 7.0
<#
.SYNOPSIS
    Structural contract checks for the thin Windows workflow and shared verification gate.

This intentionally does not depend on a YAML parser. GitHub evaluates the workflow on a
clean runner; these checks catch accidental trigger, runner, duplication and artifact drift
before a push without installing tooling or contacting GitHub.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workflowPath = Join-Path $RepoRoot '.github/workflows/windows.yml'
$devRunPath = Join-Path $RepoRoot 'dev-run.ps1'
if (!(Test-Path -LiteralPath $workflowPath -PathType Leaf)) { throw "Missing $workflowPath" }
if (!(Test-Path -LiteralPath $devRunPath -PathType Leaf)) { throw "Missing $devRunPath" }
$workflow = [IO.File]::ReadAllText($workflowPath)
$devRun = [IO.File]::ReadAllText($devRunPath)

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "Workflow contract failed: $Message" }
}
function Get-Block([string]$Text, [string]$Start, [string]$End) {
    $pattern = '(?ms)^' + [regex]::Escape($Start) + '(?<body>.*?)(?=^' + [regex]::Escape($End) + '|$(?![\r\n]))'
    $match = [regex]::Match($Text, $pattern)
    if (!$match.Success) { throw "Could not locate workflow block '$Start'" }
    $match.Groups['body'].Value
}

$push = Get-Block $workflow '  push:' '  pull_request:'
$pullRequest = Get-Block $workflow '  pull_request:' '# A newer commit supersedes'
$jobs = Get-Block $workflow 'jobs:' '  # Disposable GitHub-hosted runner only.'
$managed = [regex]::Match($workflow, '(?ms)^  managed-setup-install:.*\z').Value
$build = [regex]::Match($workflow, '(?ms)^  build:.*?(?=^  # Disposable GitHub-hosted runner only.)').Value

Assert-Contract ($push -match '(?ms)^\s+branches:\s*\r?\n\s+-\s+main\s*$') 'push must be restricted to main'
Assert-Contract (!$push.Contains('branches-ignore:')) 'push must not use a broad branch trigger'
Assert-Contract ($pullRequest.Contains('paths-ignore:')) 'pull_request must retain documentation path filtering'
Assert-Contract ($push.Contains("      - '.github/**/*.md'") -and $pullRequest.Contains("      - '.github/**/*.md'")) 'push and pull_request path filters must include workflow documentation'
Assert-Contract ($workflow.Contains('group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}')) 'concurrency group must use workflow plus PR number/ref fallback'
Assert-Contract ($workflow.Contains('cancel-in-progress: ${{ github.event_name == ''pull_request'' }}')) 'only pull_request runs may be cancelled'
Assert-Contract ($build -match '(?m)^\s+runs-on:\s+windows-2022\s*$') 'source build must use the VS2022 runner image'
Assert-Contract ($managed -match '(?m)^\s+runs-on:\s+windows-latest\s*$') 'managed install must retain its disposable current-image runner'
Assert-Contract ($build -match 'actions/setup-dotnet@v5') 'source build must set up the .NET SDK'
Assert-Contract ($build -match '(?m)^\s+dotnet-version:\s+["'']8\.0\.x["'']\s*$') 'source build must keep the 8.0.x SDK channel'
Assert-Contract ($build -notmatch '(?mi)^\s+cache:\s*') 'NuGet cache must remain opt-in'
Assert-Contract ($workflow -notmatch '(?mi)actions/cache@') 'workflow must not add a separate binary/cache action'
Assert-Contract ($build.Contains('./dev-run.ps1 -NoLaunch')) 'CI must call the shared no-launch gate'
Assert-Contract ($build.Contains('-TestResultsDirectory TestResults')) 'CI must request TRX evidence from the shared gate'
Assert-Contract ($build.Contains('-PreviewDirectory artifacts/widget-previews')) 'CI must request preview evidence from the shared gate'
Assert-Contract ($build.Contains('path: publish/.dev-velopack/*')) 'CI must upload the shared gate package directory'
Assert-Contract ($build.Contains('name: CycleArc-win-x64')) 'CI must preserve the release artifact name'
$installerUpload = Get-Block $workflow '      - name: Upload Windows installer assets' '      - name: Upload failed test results'
Assert-Contract ($installerUpload -match '(?m)^\s+include-hidden-files:\s+true\s*$') 'installer upload must include files in the hidden .dev-velopack directory'
Assert-Contract ($installerUpload -match '(?m)^\s+if-no-files-found:\s+error\s*$') 'missing installer assets must fail their upload step'
Assert-Contract ($build.Contains('path: artifacts/widget-previews/*')) 'CI must upload optional preview evidence'
Assert-Contract ($managed.Contains('actions/download-artifact@v6')) 'managed install must consume the packaged artifact'
Assert-Contract ($managed -notmatch '(?mi)dev-run\.ps1') 'managed install must not rebuild source'
Assert-Contract ($workflow -notmatch '(?mi)vs_buildtools|vswhere.*install|Workload\.VCTools') 'CI must not download/install Visual Studio'
Assert-Contract ($build -notmatch '(?mi)^\s+run:\s+.*dotnet\s+(restore|build|test|publish)\b') 'workflow must not duplicate the shared dotnet gate commands'
Assert-Contract ($workflow -notmatch '(?mi)Compile test-only build flavours') 'test-only compile must live in the shared gate'

foreach ($stage in @(
    'preflight', 'workflow-contract', 'setup-ui-toolchain', 'release-guard', 'restore',
    'tool-restore', 'build', 'ui-smoke-desktop-instance', 'local-install-regression',
    'build-local-regression', 'unit-test', 'ui-smoke-full', 'widget-preview',
    'test-flavour-build', 'publish', 'package', 'package-verify'
)) {
    Assert-Contract ($devRun.Contains("Invoke-DevRunStep '$stage'")) "dev-run is missing stage '$stage'"
}
Assert-Contract ($devRun.Contains('[string]$TestResultsDirectory') -and $devRun.Contains('[string]$PreviewDirectory')) 'dev-run evidence parameters are missing'
Assert-Contract ($devRun.Contains('Assert-SetupUiToolchain -RepoRoot $RepoRoot')) 'the shared gate must check the preinstalled AOT toolchain'
Assert-Contract ($devRun.Contains('''--logger'', ''trx''') -and $devRun.Contains('''--results-directory'', $TestResultsPath')) 'TRX output must be optional and shared'
Assert-Contract ($devRun.Contains('''--widget-accounts'', $PreviewPath')) 'preview output must be optional and shared'
Assert-Contract ($devRun.Contains('CycleArcTestBuild=CYCLEARC_TEST_E2E%3BCYCLEARC_TEST_FAIL_STARTUP')) 'test-only build flavours must be in the shared gate'
$releaseRestore = "Invoke-Dotnet -Arguments @('restore', 'CycleArc.sln', '-p:Configuration=Release')"
$releaseBuild = "Invoke-Dotnet -Arguments @('build', 'CycleArc.sln', '-c', 'Release', '--no-restore')"
Assert-Contract ($devRun.Contains($releaseRestore)) 'the shared Release build must have a matching solution restore'
Assert-Contract ($devRun.Contains($releaseBuild)) 'the shared solution build must reuse its matching Release restore'
$testFlavourStage = [regex]::Match($devRun, "(?s)Invoke-DevRunStep 'test-flavour-build'.*?(?=Invoke-DevRunStep 'publish')").Value
$publishStage = [regex]::Match($devRun, "(?s)Invoke-DevRunStep 'publish'.*?(?=Invoke-DevRunStep 'package')").Value
Assert-Contract ($testFlavourStage -and $testFlavourStage -notmatch '--no-restore') 'the distinct test-flavour build must keep its own restore evaluation'
Assert-Contract ($publishStage -and $publishStage -notmatch '--no-restore') 'the win-x64 self-contained publish must keep its RID-specific restore evaluation'
$toolchainIndex = $devRun.IndexOf("Invoke-DevRunStep 'setup-ui-toolchain'")
$restoreIndex = $devRun.IndexOf("Invoke-DevRunStep 'restore'")
$buildIndex = $devRun.IndexOf("Invoke-DevRunStep 'build'")
Assert-Contract ($toolchainIndex -ge 0 -and $toolchainIndex -lt $restoreIndex) 'toolchain check must precede restore'
Assert-Contract ($restoreIndex -ge 0 -and $restoreIndex -lt $buildIndex) 'the matching Release restore must precede the no-restore build'
$flavourIndex = $devRun.IndexOf("Invoke-DevRunStep 'test-flavour-build'")
$publishIndex = $devRun.IndexOf("Invoke-DevRunStep 'publish'")
Assert-Contract ($flavourIndex -ge 0 -and $flavourIndex -lt $publishIndex) 'test-only build must precede publish'
Write-Host 'PASS: Windows workflow uses one shared gate, a VS2022 source runner, PR-only cancellation, and explicit evidence paths.'
# Execute only the evidence path resolver, never the build gate, against real path guards.
# A caller must not be able to target source files or delete the checkout via an option.
. (Join-Path $RepoRoot 'scripts/LocalInstall.ps1')
$gateParseErrors = $null
$gateAst = [Management.Automation.Language.Parser]::ParseFile($devRunPath, [ref]$null, [ref]$gateParseErrors)
Assert-Contract (!$gateParseErrors) 'the common gate must parse as PowerShell'
$pathFunction = $gateAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-DevRunPath'
}, $true)
Assert-Contract ($null -ne $pathFunction) 'the evidence path resolver must exist'
. ([scriptblock]::Create($pathFunction.Extent.Text))
foreach ($invalidEvidencePath in @('.', 'src', '.git', '..', 'artifacts')) {
    $rejected = $false
    try { Resolve-DevRunPath $invalidEvidencePath | Out-Null }
    catch { $rejected = $true }
    Assert-Contract $rejected "evidence must not target $invalidEvidencePath"
}
foreach ($validEvidencePath in @('TestResults', 'TestResults/ci', 'artifacts/verification/previews')) {
    $resolvedEvidencePath = Resolve-DevRunPath $validEvidencePath
    Assert-Contract ([IO.Path]::IsPathFullyQualified($resolvedEvidencePath)) 'evidence paths must be fully resolved'
}
Assert-Contract ($devRun -notmatch 'Remove-Item -LiteralPath \$(TestResultsPath|PreviewPath)') 'evidence options must not recursively remove caller-selected directories'
Write-Host 'PASS: evidence paths reject repository/source/outside roots and never delete caller-selected directories.'
