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
$StagingDir = Join-Path $RepoRoot 'publish/.dev-staging'
$LocalDir = Join-Path $RepoRoot 'publish/local'
$BackupDir = Join-Path $RepoRoot 'publish/local.previous'

function Assert-OwnedDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory outside repository: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        if ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse target' }
        if (Get-ChildItem -LiteralPath $absolute -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'Reparse descendant'
        }
    }
}
function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "Running: dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE). See the command output above." }
}
foreach ($target in @($StagingDir, $LocalDir, $BackupDir)) { Assert-OwnedDirectory $target }
& (Join-Path $RepoRoot 'tests/Release.Tests.ps1')
if (Test-Path -LiteralPath $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force }
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

# Stop only this workspace's existing installation, including its former executable name.
$knownExecutables = @((Join-Path $LocalDir 'prometer.exe'), (Join-Path $LocalDir 'CodexMeter.exe'), (Join-Path $LocalDir 'CycleArc.exe'))
foreach ($process in @(Get-Process -Name 'prometer', 'CodexMeter', 'CycleArc' -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $knownExecutables -contains $process.Path) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
}
. (Join-Path $RepoRoot 'scripts/LocalInstall.ps1')
Install-StagedApp -StagingDir $StagingDir -LocalDir $LocalDir -BackupDir $BackupDir -Validate ${function:Assert-OwnedDirectory}
