#Requires -Version 7.0
<#
.SYNOPSIS
    Drive the real build-local.cmd entry point end to end on a disposable Windows
    machine: no injected DevRun/RunSetup/StartLauncher scriptblocks, the real
    dev-run.ps1 gate, the real CycleArc-Setup.exe, and the real managed install.

.DESCRIPTION
    This is destructive verification, not ordinary use of the finished script. It
    installs CycleArc for the current Windows user, replaces that installation with
    a second build carrying the same version number, deliberately breaks the build
    to check failure reporting, and starts and stops the desktop. It cannot isolate
    %LOCALAPPDATA%\CycleArc, the HKCU uninstall entry, shortcuts, the single-instance
    mutex or desktop IPC, so run it only on a throwaway runner or VM.

    It never uninstalls in order to install, never force-stops CycleArc by name and
    never touches %LOCALAPPDATA%\ProMeter.

.PARAMETER ConfirmDisposableEnvironment
    Required. Confirms this machine may be left with a real CycleArc installation.

.PARAMETER WorkRoot
    Directory for the captured console logs. Defaults to artifacts/build-local-entry.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$ConfirmDisposableEnvironment,
    [string]$WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsWindows) { throw 'build-local.cmd is the Windows entry point; run this on Windows.' }
if (!$ConfirmDisposableEnvironment) { throw 'Pass -ConfirmDisposableEnvironment. This replaces a real CycleArc installation.' }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/LocalInstall.ps1')
. (Join-Path $repoRoot 'scripts/Build-Local.ps1') -LoadOnly

if (!$WorkRoot) { $WorkRoot = Join-Path $repoRoot 'artifacts/build-local-entry' }
$WorkRoot = ConvertTo-InstallAbsolutePath $WorkRoot
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

$entryPoint = Join-Path $repoRoot 'build-local.cmd'
$stagingExe = Join-Path $repoRoot 'publish/.dev-staging/CycleArc.exe'
$failureMarker = Join-Path $repoRoot 'artifacts/build-local/last-failure.txt'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CycleArc'
$installedExe = Join-Path $installRoot 'current/CycleArc.exe'
$developmentExe = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc/CycleArc.exe'
# A source file this script owns: the A/B builds differ by its contents only, so
# no tracked file is edited and the version number never moves.
$markerSource = Join-Path $repoRoot 'src/CycleArc/LocalBuildMarker.cs'

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "=== $Text ==="
}

# Write-Host, never the pipeline: these helpers are called from functions whose
# return value is the result object.
function Show-LogTail {
    param([Parameter(Mandatory)][string]$Path, [int]$Lines = 60)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) { return }
    Write-Host "----- $Path (last $Lines lines) -----"
    foreach ($line in @(Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue)) {
        Write-Host $line
    }
}

function Show-BuildLocalTranscript {
    param([int]$Lines = 120)
    $directory = Join-Path $repoRoot 'artifacts/build-local'
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return }
    $transcript = @(Get-ChildItem -LiteralPath $directory -Filter 'build-local-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if ($transcript.Count -eq 0) { return }
    Show-LogTail -Path $transcript[0].FullName -Lines $Lines
}

function Set-MarkerSource([string]$Body) {
    [IO.File]::WriteAllText($markerSource, $Body, [Text.UTF8Encoding]::new($false))
}

function New-MarkerSource([string]$Value) {
    Set-MarkerSource (@(
        'namespace CycleArc;',
        '',
        '/// <summary>Distinguishes the A and B builds of the build-local entry point check.</summary>',
        'internal static class LocalBuildMarker',
        '{',
        "    internal const string Value = `"$Value`";",
        '}',
        ''
    ) -join "`r`n")
}

function Remove-MarkerSource {
    if (Test-Path -LiteralPath $markerSource -PathType Leaf) { Remove-Item -LiteralPath $markerSource -Force }
}

# The double-click path exactly: cmd.exe runs build-local.cmd, stdin is closed so
# its failure "pause" returns, and the exit code comes back through CMD.
function Invoke-BuildLocalEntryPoint {
    param(
        [Parameter(Mandatory)][string]$Label,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 3600
    )
    $stdout = Join-Path $WorkRoot "$Label.out.log"
    $stderr = Join-Path $WorkRoot "$Label.err.log"
    $stdin = Join-Path $WorkRoot 'closed-stdin.txt'
    if (!(Test-Path -LiteralPath $stdin -PathType Leaf)) { Set-Content -LiteralPath $stdin -Value '' -NoNewline }
    Write-Host ("Running: cmd /c build-local.cmd {0}" -f ($Arguments -join ' '))
    $started = Get-Date
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList (@('/c', 'build-local.cmd') + $Arguments) `
        -WorkingDirectory $repoRoot -NoNewWindow -PassThru `
        -RedirectStandardInput $stdin -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "build-local.cmd ($Label) did not finish within $TimeoutSeconds seconds."
        }
        $exitCode = [int]$process.ExitCode
    }
    finally { $process.Dispose() }
    $elapsed = (Get-Date) - $started
    Write-Host ("build-local.cmd {0} exited {1} after {2:n1} minutes" -f $Label, $exitCode, $elapsed.TotalMinutes)
    foreach ($log in @($stdout, $stderr)) { Show-LogTail -Path $log -Lines 60 }
    # dev-run.ps1 runs as a grandchild, so its own output is in the transcript
    # rather than in the console this script captured.
    Show-BuildLocalTranscript
    [pscustomobject]@{ ExitCode = $exitCode; StandardOutput = $stdout; StandardError = $stderr }
}

function Get-ManagedDesktopStatus {
    param([int]$TimeoutSeconds = 30)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $status = Invoke-DesktopStatus -Executable $installedExe -LogDirectory $WorkRoot
        if ($status -and [bool]$status.Succeeded) { return $status }
        Start-Sleep -Milliseconds 500
    }
    $null
}

function Assert-RunningManagedBuild {
    param(
        [Parameter(Mandatory)][string]$ExpectedHash,
        [Parameter(Mandatory)][string]$Label
    )
    if (!(Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "${Label}: Setup.exe left no $installedExe."
    }
    $status = Get-ManagedDesktopStatus
    if (!$status) { throw "${Label}: the installed CycleArc did not report desktop readiness." }
    $runningPath = ConvertTo-InstallAbsolutePath ([string]$status.ExecutablePath)
    # A development instance answering the same pipe is not this installation.
    if (!(Test-InstallPathWithin $runningPath $installRoot)) {
        throw "${Label}: the answering desktop is $runningPath, not the managed install at $installRoot."
    }
    if ((ConvertTo-InstallAbsolutePath $developmentExe).Equals($runningPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "${Label}: a development instance answered instead of the managed install."
    }
    $actual = Get-BuildLocalSha256 $installedExe
    if ($actual -cne $ExpectedHash) { throw "${Label}: installed SHA-256 $actual is not this build's $ExpectedHash." }
    Write-Host ("{0}: PID {1} at {2} SHA-256 {3}" -f $Label, $status.ProcessId, $runningPath, $actual)
    $status
}

function Stop-ManagedDesktop {
    if (!(Test-Path -LiteralPath $installedExe -PathType Leaf)) { return }
    $status = Invoke-DesktopStatus -Executable $installedExe -LogDirectory $WorkRoot
    if (!$status -or ![bool]$status.Succeeded) { return }
    Invoke-DesktopShutdown -Executable $installedExe -LogDirectory $WorkRoot | Out-Null
    Write-Host "Stopped the managed desktop after verification."
}

$summary = [ordered]@{}
try {
    Write-Step 'Runner state before the first build'
    Write-Host ("Managed install present: {0}" -f (Test-ManagedInstallRoot $installRoot))
    Write-Host ("Data root present: {0}" -f (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ProMeter')))

    # --- A: the plain double-click, with no arguments and nothing injected. ---
    Write-Step 'Build A: build-local.cmd with no arguments'
    New-MarkerSource 'build-a'
    $runA = Invoke-BuildLocalEntryPoint -Label 'build-a'
    if ($runA.ExitCode -ne 0) { throw "build-local.cmd (A) exited $($runA.ExitCode); see $($runA.StandardOutput)" }
    if (Test-Path -LiteralPath $failureMarker -PathType Leaf) { throw 'A successful run still left a failure marker behind.' }
    $hashA = Get-BuildLocalSha256 $stagingExe
    $versionA = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagingExe).FileVersion
    $statusA = Assert-RunningManagedBuild -ExpectedHash $hashA -Label 'Build A'
    $pidA = [int]$statusA.ProcessId
    $summary['A version'] = $versionA
    $summary['A hash'] = $hashA
    $summary['A pid'] = $pidA

    # --- B: the same version number, different executable content. ---
    Write-Step 'Build B: same version, different content, installed over A'
    New-MarkerSource 'build-b-replaces-a'
    $runB = Invoke-BuildLocalEntryPoint -Label 'build-b' -Arguments @('-Fast')
    if ($runB.ExitCode -ne 0) { throw "build-local.cmd (B) exited $($runB.ExitCode); see $($runB.StandardOutput)" }
    $hashB = Get-BuildLocalSha256 $stagingExe
    $versionB = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagingExe).FileVersion
    if ($versionB -ne $versionA) { throw "The A/B check needs one version number, got '$versionA' then '$versionB'." }
    if ($hashB -ceq $hashA) { throw 'Build B produced the same executable as A, so the replacement was not exercised.' }
    $statusB = Assert-RunningManagedBuild -ExpectedHash $hashB -Label 'Build B'
    $pidB = [int]$statusB.ProcessId
    if ($pidB -eq $pidA) { throw "The A desktop (PID $pidA) is still the running process; it was never stopped and restarted." }
    $leftoverA = Get-Process -Id $pidA -ErrorAction SilentlyContinue
    if ($leftoverA) {
        $leftoverA.Dispose()
        throw "The A desktop PID $pidA is still running after B installed over it."
    }
    $summary['B version'] = $versionB
    $summary['B hash'] = $hashB
    $summary['B pid'] = $pidB

    # --- Failure: a broken build must not touch the installed, running app. ---
    Write-Step 'Failure: a broken build keeps the running installation'
    Set-MarkerSource "namespace CycleArc; this is deliberately not valid C#"
    $runFail = Invoke-BuildLocalEntryPoint -Label 'build-failure'
    if ($runFail.ExitCode -eq 0) { throw 'A broken build still reported success through CMD.' }
    if (!(Test-Path -LiteralPath $failureMarker -PathType Leaf)) { throw 'The failed run wrote no stage marker for build-local.cmd.' }
    $failureText = Get-Content -LiteralPath $failureMarker -Raw
    Write-Host "----- last-failure.txt -----"
    Write-Host $failureText
    if ($failureText -notmatch 'Stage: build') { throw "The failure was not reported at the build stage: $failureText" }
    if ($failureText -notmatch 'installed version is unchanged') {
        throw "A pre-install failure must say the installation is unchanged: $failureText"
    }
    $cmdOutput = Get-Content -LiteralPath $runFail.StandardOutput -Raw
    if ($cmdOutput -notmatch 'Stage: build') { throw 'The stage and cause did not reach the CMD window.' }
    $statusStill = Assert-RunningManagedBuild -ExpectedHash $hashB -Label 'After the failed build'
    if ([int]$statusStill.ProcessId -ne $pidB) {
        throw "A failed build replaced the running desktop (PID $pidB -> $($statusStill.ProcessId))."
    }
    $summary['failure exit code'] = $runFail.ExitCode
    $summary['failure stage'] = 'build'
    $summary['installation after failure'] = "unchanged, PID $pidB"

    Write-Step 'Result'
    $summary.GetEnumerator() | ForEach-Object { Write-Host ("{0,-28} {1}" -f $_.Key, $_.Value) }
    Write-Host 'build-local.cmd end-to-end verification passed.'
}
finally {
    Remove-MarkerSource
    try { Stop-ManagedDesktop } catch { Write-Host "Could not stop the managed desktop: $($_.Exception.Message)" }
}
