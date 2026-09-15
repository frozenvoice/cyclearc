#Requires -Version 7.0
<#
.SYNOPSIS
Build, test, publish and run the single-file CycleArc desktop app.
.PARAMETER Fast
Skip tests only after they have already been run for these changes.
.PARAMETER NoLaunch
Validate the staged artifact without stopping or replacing the installed local build.
#>
[CmdletBinding()]
param([switch]$Fast, [switch]$NoLaunch)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
Set-Location -LiteralPath $RepoRoot
$localInstallScript = Join-Path $RepoRoot 'scripts/LocalInstall.ps1'
. $localInstallScript
$Layout = Resolve-InstallLayout -RepoRoot $RepoRoot
$StagingDir = Join-Path $RepoRoot 'publish/.dev-staging'
$CurrentLocalDir = Join-Path $RepoRoot 'publish/local'
$LocalDir = $Layout.LocalDir
$BackupDir = $Layout.BackupDir
$InstallStagingRoot = Join-Path $Layout.InstallRoot 'publish/.dev-install-staging'
$AllowedRoots = @($RepoRoot, $Layout.InstallRoot | Select-Object -Unique)

function Assert-DevRunPath([string]$Target) {
    Assert-InstallPath -Path $Target -AllowedRoots $AllowedRoots | Out-Null
}
function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "Running: dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE). See the command output above." }
}
foreach ($target in @($StagingDir, $CurrentLocalDir, $LocalDir, $BackupDir)) { Assert-DevRunPath $target }
$KnownExecutablePaths = @(
    foreach ($directory in @($CurrentLocalDir, $LocalDir)) {
        foreach ($name in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
            ConvertTo-InstallAbsolutePath (Join-Path $directory $name)
        }
    }
) | Select-Object -Unique

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
& (Join-Path $RepoRoot 'tests/Release.Tests.ps1')
if (Test-Path -LiteralPath $StagingDir) {
    Assert-DevRunPath $StagingDir
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
}
Invoke-Dotnet -Arguments @('restore')
Invoke-Dotnet -Arguments @('build', 'CycleArc.sln', '-c', 'Release')
if (!$Fast) { Invoke-Dotnet -Arguments @('test', 'CycleArc.sln', '-c', 'Release', '--no-build') }
& (Join-Path $RepoRoot 'tests/LocalInstall.Tests.ps1')
Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build')
Invoke-Dotnet -Arguments @('publish', 'src/CycleArc/CycleArc.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $StagingDir)
$files = @(Get-ChildItem -LiteralPath $StagingDir -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'CycleArc.exe') { throw 'Publish must contain exactly CycleArc.exe' }
Write-Host 'Publish artifacts verified: CycleArc.exe only'
Invoke-Dotnet -Arguments @('run', '--project', 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj', '-c', 'Release', '--no-build', '--', '--claude-process', (Join-Path $StagingDir 'CycleArc.exe'))
if ($NoLaunch) { Write-Host "Staged: $StagingDir"; exit 0 }

Assert-InstallSameVolume -Paths @($InstallStagingRoot, $LocalDir, $BackupDir)
$lease = New-InstallLease -InstallRoot $Layout.InstallRoot -AllowedRoots @($Layout.InstallRoot)
try {
    Assert-DevRunPath $InstallStagingRoot
    New-Item -ItemType Directory -Path $InstallStagingRoot -Force | Out-Null
    $installStaging = Join-Path $InstallStagingRoot ([guid]::NewGuid().ToString('N'))
    Assert-DevRunPath $installStaging
    New-Item -ItemType Directory -Path $installStaging | Out-Null
    Copy-ValidatedExecutable -SourcePath (Join-Path $StagingDir 'CycleArc.exe') `
        -DestinationDirectory $installStaging -SourceRoots @($RepoRoot) -DestinationRoots @($Layout.InstallRoot) | Out-Null

    # Re-query after staging is validated so an app started during the build is
    # handled too. Keep headless Claude callbacks alive; only desktop instances
    # in the current session are returned by Get-CycleArcDesktopProcess.
    $desktopProcesses = @(Get-CycleArcDesktopProcess -KnownExecutablePaths $KnownExecutablePaths)
    try {
    foreach ($processRecord in $desktopProcesses) {
        Write-Host "Stopping existing CycleArc desktop PID $($processRecord.ProcessId) path $($processRecord.Path)"
        if (Stop-CycleArcDesktopProcess -ProcessRecord $processRecord) {
            Write-Host "Stopped existing CycleArc desktop PID $($processRecord.ProcessId)"
        }
    }
    }
    finally { foreach ($processRecord in $desktopProcesses) { $processRecord.Process.Dispose() } }
    Invoke-InstallRetry { Assert-InstallDesktopMutexAbsent } 40 250

    Install-StagedApp -StagingDir $installStaging -LocalDir $LocalDir -BackupDir $BackupDir -Validate ${function:Assert-DevRunPath}
}
finally {
    $lease.Dispose()
}
