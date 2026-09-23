#Requires -Version 7.0
<#
.SYNOPSIS
Build, test, publish and run the single-file CycleArc desktop app.
.PARAMETER Fast
Skip tests only after they have already been run for these changes.
.PARAMETER NoLaunch
Validate the staged artifact without stopping or replacing the installed local build.
.PARAMETER TestResultsDirectory
Write TRX evidence under TestResults or a subdirectory of artifacts (optional).
.PARAMETER PreviewDirectory
Render synthetic widget previews under TestResults or a subdirectory of artifacts (optional).
#>
[CmdletBinding()]
param(
    [switch]$Fast,
    [switch]$NoLaunch,
    [string]$TestResultsDirectory,
    [string]$PreviewDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
Set-Location -LiteralPath $RepoRoot
$localInstallScript = Join-Path $RepoRoot 'scripts/LocalInstall.ps1'
. $localInstallScript
. (Join-Path $RepoRoot 'scripts/SetupUiToolchain.ps1')
$StagingDir = Join-Path $RepoRoot 'publish/.dev-staging'
$CurrentLocalDir = Join-Path $RepoRoot 'publish/local'
# The development single-file build replaces itself here. Kept apart from the managed
# install, whose new-install default is %LOCALAPPDATA%\Programs\CycleArc: both would
# otherwise own a CycleArc.exe in the same directory.
$LocalDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc-dev'
$AllowedRoots = @($RepoRoot)

function Assert-DevRunPath([string]$Target) {
    Assert-InstallPath -Path $Target -AllowedRoots $AllowedRoots | Out-Null
}
function Resolve-DevRunPath([string]$Target) {
    if ([string]::IsNullOrWhiteSpace($Target)) { return $null }
    $full = if ([IO.Path]::IsPathRooted($Target)) {
        [IO.Path]::GetFullPath($Target)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $RepoRoot $Target))
    }
    $artifactRoot = Join-Path $RepoRoot 'artifacts'
    $testResultsRoot = Join-Path $RepoRoot 'TestResults'
    if ($full.TrimEnd([char[]]@('\', '/')).Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose an evidence subdirectory under artifacts, not the artifacts root.'
    }
    Assert-InstallPath -Path $full -AllowedRoots @($artifactRoot, $testResultsRoot) | Out-Null
    $full
}
function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "Running: dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE). See the command output above." }
}
foreach ($target in @($StagingDir, $CurrentLocalDir)) { Assert-DevRunPath $target }
$TestResultsPath = Resolve-DevRunPath $TestResultsDirectory
$PreviewPath = Resolve-DevRunPath $PreviewDirectory
$KnownExecutablePaths = @(
    foreach ($directory in @($CurrentLocalDir, $LocalDir)) {
        foreach ($name in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
            ConvertTo-InstallAbsolutePath (Join-Path $directory $name)
        }
    }
) | Select-Object -Unique

$script:DevRunStarted = [Diagnostics.Stopwatch]::StartNew()
$script:DevRunStage = 'preflight'

function Get-DevRunElapsedStamp {
    $elapsed = $script:DevRunStarted.Elapsed
    '{0:00}:{1:00}.{2}' -f [int][math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds, [int][math]::Floor($elapsed.Milliseconds / 100.0)
}

function Write-DevRunStageFile([string]$Stage) {
    $path = [string]$env:CYCLEARC_DEV_RUN_STAGE_FILE
    if ([string]::IsNullOrWhiteSpace($path)) { return }
    try { [IO.File]::WriteAllText($path, $Stage + [Environment]::NewLine) } catch { }
}

# A marker only this script emits. Stages of this gate run nested scripts that print
# stage lines of the same '[mm:ss.d] name' shape from their own synthetic runs, so a
# parent watching the output cannot tell those apart from this gate's stages by format.
# The human-readable line above is unchanged; this one is what a parent matches.
function Write-DevRunStageEvent([string]$Stage, [string]$State) {
    Write-Host ('##dev-run## {0} {1} {2}' -f (Get-DevRunElapsedStamp), $State, $Stage)
}

function Set-DevRunStage([string]$Stage) {
    $script:DevRunStage = $Stage
    Write-DevRunStageFile $Stage
    Write-Host ('[{0}] {1}' -f (Get-DevRunElapsedStamp), $Stage)
    Write-DevRunStageEvent $Stage 'start'
}

function Complete-DevRunStage([string]$Stage) {
    Write-Host ('[{0}] {1} passed' -f (Get-DevRunElapsedStamp), $Stage)
    Write-DevRunStageEvent $Stage 'passed'
}

function Invoke-DevRunStep {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Set-DevRunStage $Stage
    try {
        & $Action
        Complete-DevRunStage $Stage
    }
    catch {
        Write-Host ("Failed at: {0}" -f $Stage)
        Write-Host ("Elapsed: {0}" -f $script:DevRunStarted.Elapsed.ToString('mm\:ss\.fff'))
        Write-DevRunStageEvent $Stage 'failed'
        throw
    }
}

Invoke-DevRunStep 'preflight' {
    # Identify desktop instances before any build cleanup. A process holding a file
    # under a build output would make the cleanup ambiguous, so stop before touching
    # that output and report the exact PID/path to the caller.
    $earlyDesktopProcesses = @(Get-CycleArcDesktopProcess -KnownExecutablePaths $KnownExecutablePaths)
    try {
        foreach ($processRecord in $earlyDesktopProcesses) {
            Write-Host "Preflight: CycleArc desktop PID $($processRecord.ProcessId) path $($processRecord.Path)"
            $buildOutput = @(@(
                (Join-Path $RepoRoot 'src/CycleArc/bin'),
                (Join-Path $RepoRoot 'src/CycleArc/obj'),
                (Join-Path $RepoRoot 'publish/.dev-staging'),
                (Join-Path $RepoRoot 'publish/win-x64')
            ) | Where-Object { $processRecord.Path -and (Test-InstallPathWithin $processRecord.Path $_) })
            if ($buildOutput.Count -gt 0) {
                throw "Preflight found a running CycleArc desktop process in build output (PID $($processRecord.ProcessId), path $($processRecord.Path)); stop it before running dev-run.ps1."
            }
        }
    }
    finally { foreach ($processRecord in $earlyDesktopProcesses) { $processRecord.Process.Dispose() } }
}

Invoke-DevRunStep 'workflow-contract' { & (Join-Path $RepoRoot 'tests/VerificationWorkflow.Tests.ps1') }
Invoke-DevRunStep 'setup-ui-toolchain' {
    try {
        Assert-SetupUiToolchain -RepoRoot $RepoRoot | Out-Null
    }
    catch {
        if ($env:GITHUB_ACTIONS -eq 'true') {
            throw ("The hosted runner does not satisfy the preinstalled VS2022 Native AOT toolchain contract; CI will not download or run a Visual Studio installer. " + $_.Exception.Message)
        }
        throw
    }
}
Invoke-DevRunStep 'release-guard' { & (Join-Path $RepoRoot 'tests/Release.Tests.ps1') }
Invoke-DevRunStep 'restore' {
    if (Test-Path -LiteralPath $StagingDir) {
        Assert-DevRunPath $StagingDir
        Remove-Item -LiteralPath $StagingDir -Recurse -Force
    }
    Invoke-Dotnet -Arguments @('restore', 'CycleArc.sln', '-p:Configuration=Release')
}
Invoke-DevRunStep 'tool-restore' { Invoke-Dotnet -Arguments @('tool', 'restore') }
Invoke-DevRunStep 'build' { Invoke-Dotnet -Arguments @('build', 'CycleArc.sln', '-c', 'Release', '--no-restore') }
# Process/IPC checks are load-sensitive and used to fail after the unit suite and
# the rest of UiSmoke. Run them immediately after compile; empty-args UiSmoke
# no longer repeats this same check.
Invoke-DevRunStep 'ui-smoke-desktop-instance' {
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build', '--', '--desktop-instance')
}
Invoke-DevRunStep 'local-install-regression' { & (Join-Path $RepoRoot 'tests/LocalInstall.Tests.ps1') }
Invoke-DevRunStep 'build-local-regression' { & (Join-Path $RepoRoot 'tests/BuildLocal.Tests.ps1') }
Invoke-DevRunStep 'unit-test' {
    if ($Fast) {
        Write-Host 'unit-test skipped (-Fast)'
        return
    }
    $testArguments = @('test', 'CycleArc.sln', '-c', 'Release', '--no-build')
    if ($TestResultsPath) {
        New-Item -ItemType Directory -Path $TestResultsPath -Force | Out-Null
        $testArguments += @('--logger', 'trx', '--results-directory', $TestResultsPath)
    }
    Invoke-Dotnet -Arguments $testArguments
}
Invoke-DevRunStep 'ui-smoke-full' {
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build')
}
if ($PreviewPath) {
    Invoke-DevRunStep 'widget-preview' {
        New-Item -ItemType Directory -Path $PreviewPath -Force | Out-Null
        Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build', '--', '--widget-accounts', $PreviewPath)
    }
}
Invoke-DevRunStep 'test-flavour-build' {
    $flavourOutput = Join-Path $RepoRoot 'artifacts/test-flavour-build'
    Assert-DevRunPath $flavourOutput
    if (Test-Path -LiteralPath $flavourOutput) {
        Remove-Item -LiteralPath $flavourOutput -Recurse -Force
    }
    Invoke-Dotnet -Arguments @(
        'build', 'src/CycleArc/CycleArc.csproj', '-c', 'Release',
        '-p:CycleArcTestBuild=CYCLEARC_TEST_E2E%3BCYCLEARC_TEST_FAIL_STARTUP',
        '-o', $flavourOutput
    )
}
Invoke-DevRunStep 'publish' {
    Invoke-Dotnet -Arguments @('publish', 'src/CycleArc/CycleArc.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $StagingDir)
    $files = @(Get-ChildItem -LiteralPath $StagingDir -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'CycleArc.exe') { throw 'Publish must contain exactly CycleArc.exe' }
    Write-Host 'Publish artifacts verified: CycleArc.exe only'
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build', '--', '--claude-process', (Join-Path $StagingDir 'CycleArc.exe'))
}
Invoke-DevRunStep 'package' {
    $versionMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Raw), '<Version>\s*([^<]+?)\s*</Version>')
    if (!$versionMatch.Success) { throw 'Directory.Build.props does not contain a package version' }
    $script:packageVersion = $versionMatch.Groups[1].Value.Trim()
    $script:packageOutput = Join-Path $RepoRoot 'publish/.dev-velopack'
    $packageArguments = @{ PublishedDir = $StagingDir; OutputDir = $script:packageOutput; Version = $script:packageVersion }
    $releaseNotes = Join-Path $RepoRoot "release-notes/$($script:packageVersion).md"
    if (Test-Path -LiteralPath $releaseNotes) { $packageArguments.ReleaseNotesPath = $releaseNotes }
    & (Join-Path $RepoRoot 'scripts/Package.ps1') @packageArguments
    Write-Host "Velopack artifacts verified: $($script:packageOutput)"
}
Invoke-DevRunStep 'package-verify' {
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build', '--', '--update-package', $script:packageOutput)
}
if ($NoLaunch) { Write-Host "Staged: $StagingDir"; exit 0 }

# The same executable owns installation for both downloads and development.
# It stages/hash-checks before graceful shutdown and rolls back failed startup.
$stagedExecutable = Join-Path $StagingDir 'CycleArc.exe'
$expectedHash = (Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash
$expectedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedExecutable).FileVersion
$installStart = [Diagnostics.ProcessStartInfo]::new($stagedExecutable)
$installStart.UseShellExecute = $false
$installStart.CreateNoWindow = $true
$installStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$installStart.RedirectStandardOutput = $true
$installStart.RedirectStandardError = $true
foreach ($argument in @('--replace', '--expected-sha256', $expectedHash, '--expected-version', $expectedVersion, '--show', '--quiet')) {
    $installStart.ArgumentList.Add($argument)
}
$installer = [Diagnostics.Process]::Start($installStart)
try {
    $outputTask = $installer.StandardOutput.ReadToEndAsync()
    $errorTask = $installer.StandardError.ReadToEndAsync()
    if (!$installer.WaitForExit(180000)) { throw "CycleArc installer did not finish in 180 seconds (PID $($installer.Id)). Inspect this process before retrying." }
    $output = $outputTask.GetAwaiter().GetResult()
    $errorText = $errorTask.GetAwaiter().GetResult()
    if ($output) { Write-Host $output.Trim() }
    if ($installer.ExitCode -ne 0) { throw "CycleArc installation failed (exit $($installer.ExitCode)): $errorText" }
}
finally { $installer.Dispose() }
$installedExecutable = Join-Path $LocalDir 'CycleArc.exe'
if ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Installed CycleArc hash does not match the validated artifact.'
}
Write-Host "Running verified CycleArc $expectedVersion at $installedExecutable (SHA256 $expectedHash)"
