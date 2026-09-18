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
# Resolved, not assumed: the new-install default is under Programs and an existing
# installation keeps its own root. Falls back to the default for a runner with neither.
$installRoot = Get-ManagedInstallRoot
if (!$installRoot) { $installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc' }
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
    foreach ($name in @('dev-run.err.log', 'dev-run.out.log')) {
        Show-LogTail -Path (Join-Path $directory $name) -Lines 80
    }
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

function Assert-StageProgressReachedCmd {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    # build-local.cmd's own console, not the transcript: this is what the person
    # double-clicking actually watches. dev-run's stage lines have to arrive here
    # while the run is in flight, not only in a log afterwards.
    $text = if (Test-Path -LiteralPath $Path -PathType Leaf) { Get-Content -LiteralPath $Path -Raw } else { '' }
    $running = [regex]::Matches($text, 'dev-run \d\d:\d\d\.\d elapsed \| (?<stage>[^\r\n]+?) running\.\.\.')
    $passed = [regex]::Matches($text, 'dev-run \d\d:\d\d\.\d elapsed \| (?<stage>[^\r\n]+?) passed')
    $stagesSeen = @($running | ForEach-Object { $_.Groups['stage'].Value })
    Write-Host ("${Label}: stages shown live in CMD: {0}" -f ($stagesSeen -join ', '))
    if ($running.Count -lt 2) {
        throw "${Label}: the CMD console showed $($running.Count) in-flight dev-run stages; progress did not stream."
    }
    if ($passed.Count -lt 1) {
        throw "${Label}: no completed dev-run stage reached the CMD console."
    }
    # Only dev-run's own '##dev-run##' markers are streamed, so a nested regression run's
    # stages must not appear here and dev-run's own must.
    foreach ($expected in @('restore', 'build', 'ui-smoke-desktop-instance', 'package-verify')) {
        if ($stagesSeen -notcontains $expected) {
            throw "${Label}: dev-run stage '$expected' never appeared live in the CMD console."
        }
    }
    # The stage's own cost is reported separately from the cumulative elapsed time.
    if ($text -notmatch 'passed \(stage took \d\d:\d\d\.\d\)') {
        throw "${Label}: stage cost was not reported separately from cumulative elapsed time."
    }
    # A completed stage is announced once, not replayed at the end of the run.
    foreach ($stage in ($stagesSeen | Select-Object -Unique)) {
        $repeats = ([regex]::Matches($text, [regex]::Escape("| $stage passed"))).Count
        if ($repeats -gt 1) { throw "${Label}: stage '$stage' was reported passed $repeats times." }
    }
    $stagesSeen
}

# Win32 access to the installer's own controls. The installer is driven by posting BM_CLICK to
# the control IDs it declares, which is the same path a mouse click takes once the button is hit
# tested - no synthetic cursor movement, no SendKeys, no coordinates.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EntryPointUi {
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

function Save-EntryPointWindow {
    param([IntPtr]$Handle, [string]$Path)
    try {
        Add-Type -AssemblyName System.Drawing
        [void][EntryPointUi]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 500
        $rect = New-Object EntryPointUi+RECT
        [void][EntryPointUi]::GetWindowRect($Handle, [ref]$rect)
        $bitmap = New-Object Drawing.Bitmap([Math]::Max(1, $rect.R - $rect.L), [Math]::Max(1, $rect.B - $rect.T))
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        [void][EntryPointUi]::PrintWindow($Handle, $dc, 2)
        $graphics.ReleaseHdc($dc); $graphics.Dispose()
        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Host "Captured $Path"
    }
    catch { Write-Host "Could not capture $Path : $($_.Exception.Message)" }
}

# The installer this run started. Nothing else on a disposable runner is called CycleArc-Setup,
# and this only reads the window handle - it never stops any process by name.
function Wait-EntryPointSetupWindow {
    param([Parameter(Mandatory)][Diagnostics.Process]$Cmd, [int]$Seconds = 1800)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        if ($Cmd.HasExited) { throw "build-local.cmd exited ($($Cmd.ExitCode)) before the installer window appeared." }
        foreach ($candidate in @(Get-Process -Name 'CycleArc-Setup' -ErrorAction SilentlyContinue)) {
            $candidate.Refresh()
            if ($candidate.MainWindowHandle -ne [IntPtr]::Zero -and [EntryPointUi]::IsWindowVisible($candidate.MainWindowHandle)) {
                return [pscustomobject]@{ Process = $candidate; Handle = $candidate.MainWindowHandle }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "No installer window appeared within $Seconds seconds of starting build-local.cmd."
}

<#
.SYNOPSIS
    The real double-click path: build-local.cmd with no arguments, approved through the
    installer's own window.
.DESCRIPTION
    Nothing is injected - the real dev-run.ps1 gate, the real packaging, the real
    CycleArc-Setup.exe. The only automation is BM_CLICK to the installer's Install and Finish
    buttons, and clearing its Run checkbox, so the parent's handling of a real GUI approval is
    what gets exercised.
#>
function Invoke-BuildLocalEntryPointInteractive {
    param(
        [Parameter(Mandatory)][string]$Label,
        [string]$ScreenshotDirectory,
        [switch]$ClearRun,
        [int]$TimeoutSeconds = 5400
    )
    $stdout = Join-Path $WorkRoot "$Label.out.log"
    $stderr = Join-Path $WorkRoot "$Label.err.log"
    $stdin = Join-Path $WorkRoot 'closed-stdin.txt'
    if (!(Test-Path -LiteralPath $stdin -PathType Leaf)) { Set-Content -LiteralPath $stdin -Value '' -NoNewline }
    Write-Host 'Running: cmd /c build-local.cmd   (no arguments; installer approved through its own window)'
    $started = Get-Date
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', 'build-local.cmd') `
        -WorkingDirectory $repoRoot -NoNewWindow -PassThru `
        -RedirectStandardInput $stdin -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $approval = [ordered]@{ Method = 'Win32 BM_CLICK to the installer''s own control IDs' }
    try {
        $window = Wait-EntryPointSetupWindow -Cmd $process
        $handle = $window.Handle
        $approval['confirmShownAfterSeconds'] = [int]((Get-Date) - $started).TotalSeconds
        if ($ScreenshotDirectory) { Save-EntryPointWindow $handle (Join-Path $ScreenshotDirectory 'entry-1-confirm.png') }

        $install = [EntryPointUi]::GetDlgItem($handle, 100)
        if ($install -eq [IntPtr]::Zero) { throw 'The installer window has no Install button.' }
        [void][EntryPointUi]::SendMessage($install, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        $approval['installClicked'] = $true

        Start-Sleep -Milliseconds 900
        if (!$window.Process.HasExited -and $ScreenshotDirectory) {
            Save-EntryPointWindow $handle (Join-Path $ScreenshotDirectory 'entry-2-progress.png')
        }

        # The completion page is reached when the Run checkbox becomes visible.
        $runCheck = [IntPtr]::Zero
        $completion = [Diagnostics.Stopwatch]::StartNew()
        while ($completion.Elapsed.TotalSeconds -lt 900) {
            $window.Process.Refresh()
            if ($window.Process.HasExited) { throw "The installer exited ($($window.Process.ExitCode)) before its completion screen." }
            $candidate = [EntryPointUi]::GetDlgItem($handle, 102)
            if ($candidate -ne [IntPtr]::Zero -and [EntryPointUi]::IsWindowVisible($candidate)) { $runCheck = $candidate; break }
            Start-Sleep -Milliseconds 300
        }
        if ($runCheck -eq [IntPtr]::Zero) { throw 'The installer never reached its completion screen.' }
        if ($ScreenshotDirectory) { Save-EntryPointWindow $handle (Join-Path $ScreenshotDirectory 'entry-3-done.png') }

        # Deliberately hold the completion screen open past the install budget's own scale, so
        # the parent proves it is no longer applying an install deadline to a finished install.
        $hold = 45
        Write-Host "Holding the completion screen for $hold seconds before finishing."
        Start-Sleep -Seconds $hold
        $approval['completionHeldSeconds'] = $hold

        if ($ClearRun -and [EntryPointUi]::SendMessage($runCheck, 0x00F0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -eq 1) {
            [void][EntryPointUi]::SendMessage($runCheck, 0x00F1, [IntPtr]0, [IntPtr]::Zero)
            $approval['runCleared'] = $true
        }
        else { $approval['runCleared'] = $false }

        [void][EntryPointUi]::SendMessage([EntryPointUi]::GetDlgItem($handle, 100), 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        $approval['finishClicked'] = $true

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
    Show-BuildLocalTranscript
    [pscustomobject]@{ ExitCode = $exitCode; StandardOutput = $stdout; StandardError = $stderr; Approval = $approval }
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
    Write-Step 'Build A: build-local.cmd with no arguments, approved in the installer window'
    New-MarkerSource 'build-a'
    $runA = Invoke-BuildLocalEntryPointInteractive -Label 'build-a' -ClearRun `
        -ScreenshotDirectory (Join-Path $WorkRoot 'entry-point-ui')
    $summary['A approval'] = ($runA.Approval.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
    if ($runA.ExitCode -ne 0) { throw "build-local.cmd (A) exited $($runA.ExitCode); see $($runA.StandardOutput)" }
    if (Test-Path -LiteralPath $failureMarker -PathType Leaf) { throw 'A successful run still left a failure marker behind.' }
    $liveStages = Assert-StageProgressReachedCmd -Path $runA.StandardOutput -Label 'Build A'
    $summary['A live stages in CMD'] = ($liveStages -join ', ')
    $hashA = Get-BuildLocalSha256 $stagingExe
    $versionA = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagingExe).FileVersion
    # Run was cleared on the completion page, so nothing should be running yet, and the parent
    # must not have called that a failed build.
    $runningAfterA = @(Get-Process -Name 'CycleArc' -ErrorAction SilentlyContinue)
    if ($runningAfterA.Count -gt 0) {
        throw "Build A cleared Run but $($runningAfterA.Count) CycleArc process(es) are running."
    }
    $summary['A run choice honoured'] = 'yes, nothing started'
    # Start it now so the A/B replacement check below still has a running desktop to replace.
    $launcherA = Join-Path $installRoot 'CycleArc.exe'
    if (!(Test-Path -LiteralPath $launcherA -PathType Leaf)) { throw "Build A left no launcher at $launcherA" }
    $null = Start-Process -FilePath $launcherA -ArgumentList @('--show') -WorkingDirectory $installRoot
    $statusA = Assert-RunningManagedBuild -ExpectedHash $hashA -Label 'Build A'
    $pidA = [int]$statusA.ProcessId
    $summary['A version'] = $versionA
    $summary['A hash'] = $hashA
    $summary['A pid'] = $pidA

    # --- B: the same version number, different executable content. ---
    Write-Step 'Build B: same version, different content, installed over A (-Fast -SilentInstall)'
    New-MarkerSource 'build-b-replaces-a'
    $runB = Invoke-BuildLocalEntryPoint -Label 'build-b' -Arguments @('-Fast', '-SilentInstall')
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
    Write-Step 'Failure: a broken build keeps the running installation (-SilentInstall)'
    Set-MarkerSource "namespace CycleArc; this is deliberately not valid C#"
    $runFail = Invoke-BuildLocalEntryPoint -Label 'build-failure' -Arguments @('-SilentInstall')
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
    # The person watching CMD has to see which sub-stage failed and what the child said,
    # not just that something failed.
    if ($cmdOutput -notmatch 'dev-run \d\d:\d\d\.\d elapsed \| build running\.\.\.') {
        throw 'The failing sub-stage was never shown live in the CMD window.'
    }
    if ($cmdOutput -notmatch 'dev-run \d\d:\d\d\.\d elapsed \| build FAILED') {
        throw "dev-run's own failing stage did not reach the CMD window."
    }
    if ($cmdOutput -notmatch 'error CS') {
        throw 'The real compiler error from the child never reached the CMD window.'
    }
    if ($cmdOutput -notmatch 'dev-run\.out\.log') {
        throw 'The captured log path was not reported to the CMD window.'
    }
    $summary['failure detail in CMD'] = 'sub-stage, child error and log path'
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
