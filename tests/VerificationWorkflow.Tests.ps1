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

function Assert-ManualWorkflow([string]$Text, [string]$Name) {
    $events = [regex]::Match($Text, '(?ms)^on:\s*\r?\n(?<body>.*?)(?=^[^\s#]|\z)')
    Assert-Contract $events.Success "$Name must declare its workflow events"
    $eventNames = @([regex]::Matches($events.Groups['body'].Value, '(?m)^  (?<name>[a-zA-Z][\w-]*):') |
        ForEach-Object { $_.Groups['name'].Value })
    Assert-Contract ($eventNames.Count -eq 1 -and $eventNames[0] -ceq 'workflow_dispatch') "$Name must be manual-only, with no push, PR, schedule or indirect event"
}

function Assert-BuildLocalSetupIsolation([string]$Text) {
    $entryPoint = [regex]::Match($Text, '(?ms)^  build-local-entry-point:.*?(?=^  [a-zA-Z][\w-]*:|\z)').Value
    $setupUi = [regex]::Match($Text, '(?ms)^  setup-ui:.*?(?=^  [a-zA-Z][\w-]*:|\z)').Value
    Assert-Contract (!$entryPoint.Contains('Verify-SetupUi.ps1')) 'fresh-user Setup UI must not run after the real CMD installation in the same job'
    Assert-Contract ($entryPoint.Contains('./scripts/Verify-BuildLocalEntryPoint.ps1 -ConfirmDisposableEnvironment')) 'build-local must retain the real guarded CMD verification'
    Assert-Contract ($setupUi -match '(?m)^\s+runs-on:\s+windows-2022\s*$') 'build-local Setup UI must use its own disposable hosted runner'
    Assert-Contract ($setupUi -match '(?m)^\s+needs:\s+build-local-entry-point\s*$') 'build-local Setup UI must wait for successful CMD verification'
    Assert-Contract ($entryPoint.Contains('setup-artifact-id: ${{ steps.upload-setup.outputs.artifact-id }}')) 'CMD job must expose the exact uploaded installer artifact ID'
    Assert-Contract ($entryPoint.Contains('setup-sha256: ${{ steps.record-setup.outputs.sha256 }}')) 'CMD job must expose the packaged installer hash'
    $upload = [regex]::Match($entryPoint, '(?ms)^      - name: Upload verified build-local installer.*?(?=^      - name:|\z)').Value
    Assert-Contract ($upload.Contains('id: upload-setup') -and $upload.Contains('actions/upload-artifact@v6')) 'CMD job must upload its own installer'
    Assert-Contract ($upload.Contains('path: publish/.dev-velopack/CycleArc-Setup.exe') -and $upload.Contains('include-hidden-files: true') -and $upload.Contains('if-no-files-found: error')) 'CMD job must require the existing exact installer under the hidden package directory'
    $download = [regex]::Match($setupUi, '(?ms)^      - name: Download verified build-local installer.*?(?=^      - name:|\z)').Value
    Assert-Contract ($download.Contains('actions/download-artifact@v6') -and $download.Contains('artifact-ids: ${{ needs.build-local-entry-point.outputs.setup-artifact-id }}') -and $download.Contains('path: publish/build-local-setup') -and $download.Contains('merge-multiple: true')) 'fresh Setup UI must download the exact producer artifact by ID into the verified installer path'
    Assert-Contract ($download -notmatch '(?m)^\s+(name|pattern|run-id|repository):') 'Setup UI must not select an installer from another name, run or repository'
    Assert-Contract ($entryPoint.Contains('Get-FileHash -LiteralPath $setup -Algorithm SHA256') -and $entryPoint.Contains('"sha256=$hash" >> $env:GITHUB_OUTPUT')) 'producer hash must come from the uploaded installer'
    Assert-Contract ($setupUi.Contains('EXPECTED_SETUP_SHA256: ${{ needs.build-local-entry-point.outputs.setup-sha256 }}') -and $setupUi.Contains('Get-FileHash -LiteralPath $setup -Algorithm SHA256') -and $setupUi.Contains('if ($actual -cne $env:EXPECTED_SETUP_SHA256)')) 'fresh Setup UI must enforce the producer installer SHA-256'
    Assert-Contract ($setupUi.Contains("'publish/build-local-setup/CycleArc-Setup.exe'") -and $setupUi.Contains('./scripts/Verify-SetupUi.ps1 -SetupPath $setup -OutputDirectory $artifacts -ConfirmDisposableEnvironment')) 'fresh runner must verify the downloaded installer through the guarded UI script'
    Assert-Contract ($setupUi -notmatch '(?mi)dev-run\.ps1|dotnet\s+(build|publish)|Remove-Item|Verify-BuildLocalEntryPoint\.ps1') 'fresh UI job must not rebuild or clean an installation to evade its fresh-user requirement'
    Assert-Contract ($Text -notmatch '(?mi)^\s+continue-on-error:\s*true\s*$') 'build-local must not mask a failed verification'
    Assert-Contract ($setupUi.Contains('name: setup-ui-${{ github.run_id }}-${{ github.run_attempt }}') -and $setupUi.Contains('if: always()')) 'fresh UI runner must upload its diagnostic evidence on failure'
}

Assert-ManualWorkflow $workflow 'Windows full verification'
foreach ($forbiddenEvent in @('push', 'pull_request', 'schedule', 'workflow_run', 'workflow_call', 'repository_dispatch')) {
    $rejected = $false
    try { Assert-ManualWorkflow ($workflow.Replace('  workflow_dispatch:', "  workflow_dispatch:`n  ${forbiddenEvent}:")) 'synthetic trigger' }
    catch { $rejected = $true }
    Assert-Contract $rejected "manual trigger guard must reject $forbiddenEvent"
}
$jobNames = @([regex]::Matches((Get-Block $workflow 'jobs:' '__end_of_jobs__'), '(?m)^  (?<name>[a-zA-Z][\w-]*):') |
    ForEach-Object { $_.Groups['name'].Value })
Assert-Contract (($jobNames -join ',') -ceq 'build,managed-setup-install,setup-shortcut-choices') 'full verification must retain exactly its build and two independent install jobs'
$managed = [regex]::Match($workflow, '(?ms)^  managed-setup-install:.*?(?=^  [a-zA-Z][\w-]*:|\z)').Value
$shortcutChoices = [regex]::Match($workflow, '(?ms)^  setup-shortcut-choices:.*?(?=^  [a-zA-Z][\w-]*:|\z)').Value
$build = [regex]::Match($workflow, '(?ms)^  build:.*?(?=^  # Disposable GitHub-hosted runner only.)').Value

Assert-Contract ($workflow.Contains('group: ${{ github.workflow }}-${{ github.ref }}')) 'manual verification concurrency must use workflow and selected ref'
Assert-Contract ($workflow.Contains('cancel-in-progress: false')) 'another request must not cancel the selected full run'
Assert-Contract ($workflow -notmatch '(?mi)^\s+continue-on-error:\s*true\s*$') 'full verification must not convert validation failures to success'
Assert-Contract ($build -match '(?m)^\s+runs-on:\s+windows-2022\s*$') 'source build must use the VS2022 runner image'
Assert-Contract ($managed -match '(?m)^\s+runs-on:\s+windows-latest\s*$') 'managed install must retain its disposable current-image runner'
Assert-Contract ($build -match 'actions/setup-dotnet@v5') 'source build must set up the .NET SDK'
Assert-Contract ($build -match '(?m)^\s+dotnet-version:\s+["'']10\.0\.x["'']\s*$') 'source build must install the stable 10.0.x SDK channel selected by global.json'
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
Assert-Contract ($managed -match '(?m)^\s+needs:\s+build\s*$') 'managed install must depend on successful build'
Assert-Contract ($managed.Contains('name: CycleArc-win-x64')) 'managed install must download the exact packaged artifact name'
Assert-Contract ($managed.Contains("Get-FileHash -LiteralPath (Join-Path `$extract 'CycleArc.exe') -Algorithm SHA256") -and $managed.Contains('Get-FileHash -LiteralPath $current -Algorithm SHA256')) 'managed install must compare the installed executable with the packed executable'
Assert-Contract ($managed.Contains('if ($actual -cne $expected)') -and $managed.Contains('if ($repaired -cne $expected)')) 'installation and same-version repair must both enforce the packed hash'
Assert-Contract ($managed.Contains('Wait-InstallDesktopReleased') -and $managed.Contains('Same-version Setup.exe exited')) 'repair must retain desktop release and installer exit checks'
Assert-Contract ($managed.Contains('name: setup-install-logs') -and $managed.Contains('if: always()')) 'managed install must collect diagnostic evidence on failures as well'
Assert-Contract ($managed -notmatch '(?mi)dev-run\.ps1') 'managed install must not rebuild source'
Assert-Contract (!$managed.Contains('Verify-SetupUi.ps1')) 'fresh-user shortcut checks must not reuse the managed install runner'
Assert-Contract ($shortcutChoices -match '(?m)^\s+needs:\s+build\s*$') 'shortcut choices must consume the verified build'
Assert-Contract ($shortcutChoices -match '(?m)^\s+runs-on:\s+windows-latest\s*$') 'shortcut choices must run on a separate disposable Windows runner'
Assert-Contract ($shortcutChoices.Contains('actions/download-artifact@v6') -and $shortcutChoices.Contains('name: CycleArc-win-x64')) 'shortcut choices must download the existing installer artifact'
Assert-Contract ($shortcutChoices.Contains('./scripts/Verify-SetupUi.ps1') -and $shortcutChoices.Contains('-ConfirmDisposableEnvironment')) 'shortcut choices must run the guarded installer UI verification'
Assert-Contract ($shortcutChoices.Contains('name: setup-shortcut-choices') -and $shortcutChoices.Contains('if: always()')) 'shortcut choices must retain evidence collection'
Assert-Contract ($shortcutChoices -notmatch '(?mi)dev-run\.ps1') 'shortcut choices must not rebuild source'
Assert-Contract ($workflow -notmatch '(?mi)vs_buildtools|vswhere.*install|Workload\.VCTools') 'CI must not download/install Visual Studio'
Assert-Contract ($build -notmatch '(?mi)^\s+run:\s+.*dotnet\s+(restore|build|test|publish)\b') 'workflow must not duplicate the shared dotnet gate commands'
Assert-Contract ($workflow -notmatch '(?mi)Compile test-only build flavours') 'test-only compile must live in the shared gate'

foreach ($stage in @(
    'preflight', 'workflow-contract', 'sdk-regression', 'setup-ui-toolchain', 'release-guard', 'restore',
    'tool-restore', 'build', 'ui-smoke-desktop-instance', 'local-install-regression',
    'build-local-regression', 'installed-update-regression', 'unit-test', 'ui-smoke-full', 'widget-preview',
    'test-flavour-build', 'test-flavour-publish', 'publish', 'package', 'package-verify'
)) {
    Assert-Contract ($devRun.Contains("Invoke-DevRunStep '$stage'")) "dev-run is missing stage '$stage'"
}
Assert-Contract ($devRun.Contains('[string]$TestResultsDirectory') -and $devRun.Contains('[string]$PreviewDirectory')) 'dev-run evidence parameters are missing'
Assert-Contract ($devRun.Contains('Assert-SetupUiToolchain -RepoRoot $RepoRoot')) 'the shared gate must check the preinstalled AOT toolchain'
Assert-Contract ($devRun.Contains('Assert-CycleArcDotnetSdk -RepoRoot $RepoRoot')) 'the shared gate must verify the selected SDK before build cleanup'
foreach ($sourceWorkflow in @('windows-e2e.yml', 'windows-build-local.yml')) {
    $sourceText = Get-Content -LiteralPath (Join-Path $RepoRoot ".github/workflows/$sourceWorkflow") -Raw
    Assert-ManualWorkflow $sourceText $sourceWorkflow
    Assert-Contract ($sourceText -notmatch '(?mi)^\s+uses:\s+.*windows\.yml') "$sourceWorkflow must not indirectly invoke full verification"
    Assert-Contract ($sourceText -match '(?m)^\s+runs-on:\s+windows-2022\s*$') "$sourceWorkflow must use the VS2022 AOT image"
    Assert-Contract ($sourceText -match '(?m)^\s+dotnet-version:\s+["'']10\.0\.x["'']\s*$') "$sourceWorkflow must install the 10.0.x SDK channel"
}
$buildLocalWorkflow = Get-Content -LiteralPath (Join-Path $RepoRoot '.github/workflows/windows-build-local.yml') -Raw
Assert-BuildLocalSetupIsolation $buildLocalWorkflow
foreach ($mutation in @(
    @{ Name = 'shared installed user'; Text = $buildLocalWorkflow.Replace('      - name: Drive build-local.cmd end to end', "      - name: Invalid shared-user UI`n        run: ./scripts/Verify-SetupUi.ps1`n`n      - name: Drive build-local.cmd end to end") },
    @{ Name = 'different artifact'; Text = $buildLocalWorkflow.Replace('artifact-ids: ${{ needs.build-local-entry-point.outputs.setup-artifact-id }}', 'name: unrelated-installer') },
    @{ Name = 'hash mismatch bypass'; Text = $buildLocalWorkflow.Replace('if ($actual -cne $env:EXPECTED_SETUP_SHA256)', 'if ($false)') },
    @{ Name = 'unconfirmed install'; Text = $buildLocalWorkflow.Replace('./scripts/Verify-SetupUi.ps1 -SetupPath $setup -OutputDirectory $artifacts -ConfirmDisposableEnvironment', './scripts/Verify-SetupUi.ps1 -SetupPath $setup -OutputDirectory $artifacts') },
    @{ Name = 'installation cleanup bypass'; Text = $buildLocalWorkflow.Replace('./scripts/Verify-SetupUi.ps1 -SetupPath', "Remove-Item -LiteralPath `$installation -Recurse`n          ./scripts/Verify-SetupUi.ps1 -SetupPath") }
)) {
    $rejected = $false
    try { Assert-BuildLocalSetupIsolation $mutation.Text }
    catch { $rejected = $true }
    Assert-Contract $rejected "build-local isolation guard must reject $($mutation.Name)"
}
Write-Host 'PASS: build-local CMD and fresh-user Setup UI use separate hosted jobs and the same installer artifact ID and SHA-256; unsafe wiring mutations rejected.'
$runBlocks = [regex]::Matches($buildLocalWorkflow, '(?m)^        run: \|\r?\n(?<body>(?:^          [^\r\n]*\r?\n|^\r?\n)+)')
$runCount = [regex]::Matches($buildLocalWorkflow, '(?m)^        run:').Count
Assert-Contract ($runCount -eq ($runBlocks.Count + 1) -and $buildLocalWorkflow.Contains('        run: ./tests/BuildLocal.Tests.ps1')) 'every build-local inline PowerShell run block must be covered by the syntax check'
foreach ($runBlock in $runBlocks) {
    $scriptText = [regex]::Replace($runBlock.Groups['body'].Value, '(?m)^          ', '')
    $scriptErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$null, [ref]$scriptErrors)
    Assert-Contract (!$scriptErrors) "build-local workflow PowerShell must parse: $(@($scriptErrors | ForEach-Object { $_.Message }) -join '; ')"
}
Write-Host "PASS: all $($runBlocks.Count) multiline build-local workflow PowerShell blocks parse."
$installedWorkflow = Get-Content -LiteralPath (Join-Path $RepoRoot '.github/workflows/windows-e2e.yml') -Raw
Assert-Contract ($installedWorkflow.Contains('50c73b4f070ccb8e6192c0dbbfc1b6476aa89153')) 'installed migration check must default to the known .NET 8 version 0.9.1 source commit'
Assert-Contract ($installedWorkflow.Contains('dotnet-version: "8.0.x"') -and $installedWorkflow.Contains("version = '8.0.100'")) 'historical baseline must explicitly select its own .NET 8 SDK'
Assert-Contract ($installedWorkflow.Contains('$verifyArguments.BaselineRepoRoot')) 'installed migration check must pass the separately built .NET 8 baseline to the real installed-app verification'
Assert-Contract ($devRun.Contains('''--logger'', ''trx''') -and $devRun.Contains('''--results-directory'', $TestResultsPath')) 'TRX output must be optional and shared'
Assert-Contract ($devRun.Contains('''--widget-accounts'', $PreviewPath')) 'preview output must be optional and shared'
Assert-Contract ($devRun.Contains('CycleArcTestBuild=CYCLEARC_TEST_E2E%3BCYCLEARC_TEST_FAIL_STARTUP')) 'test-only build flavours must be in the shared gate'
Assert-Contract ($devRun.Contains("'--published-native-dependencies'")) 'single-file native dependencies must be exercised from an isolated test-flavour executable'
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
Write-Host 'PASS: Windows workflows remain manual-only; full verification retains its shared gate, VS2022 source runner, exact artifacts, install/repair hashes and evidence.'
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
