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

    # Stop only exact CycleArc/legacy executable paths in this worktree and the
    # primary worktree installation.  The current linked-worktree installation
    # remains on disk for existing absolute-path Claude receivers.
    $knownExecutables = @{}
    foreach ($directory in @($CurrentLocalDir, $LocalDir)) {
        foreach ($name in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
            $knownExecutables[(ConvertTo-InstallAbsolutePath (Join-Path $directory $name))] = $true
        }
    }
    foreach ($process in @(Get-Process -Name 'prometer', 'CodexMeter', 'CycleArc' -ErrorAction SilentlyContinue)) {
        try { $processPath = ConvertTo-InstallAbsolutePath ([string]$process.Path) } catch { continue }
        if ($knownExecutables.ContainsKey($processPath)) {
            try { if (!$process.HasExited) { Stop-Process -InputObject $process -Force -ErrorAction Stop } }
            catch { if (!$process.HasExited) { throw } } # Already exited between enumeration and stop.
            if (!$process.WaitForExit(10000)) { throw 'The previous CycleArc process did not exit within 10 seconds' }
        }
    }

    Install-StagedApp -StagingDir $installStaging -LocalDir $LocalDir -BackupDir $BackupDir -Validate ${function:Assert-DevRunPath}
}
finally {
    $lease.Dispose()
}
