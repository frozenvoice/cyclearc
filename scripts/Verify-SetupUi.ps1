#Requires -Version 7.0
<#
.SYNOPSIS
    Drives the distributable CycleArc-Setup.exe through its own window and captures each
    screen: confirmation, progress and completion.

.DESCRIPTION
    This is the path a person takes when they double-click the downloaded installer. It
    really installs, so it belongs on a disposable Windows machine only. It first checks
    that cancelling before Install changes nothing, then installs for real by clicking the
    installer's own buttons, and finally checks the completion page's Run choice is honoured.

.PARAMETER SetupPath
    The installer to drive. This must be the distributed CycleArc-Setup.exe, not the engine.

.PARAMETER OutputDirectory
    Where the screenshots and the report are written.

.PARAMETER ConfirmDisposableEnvironment
    Required. This installs CycleArc for the current Windows user.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$ConfirmDisposableEnvironment,
    [int]$InstallTimeoutSeconds = 600
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsWindows) { throw 'CycleArc-Setup.exe is a Windows installer; run this on Windows.' }
if (!$ConfirmDisposableEnvironment) {
    throw 'Pass -ConfirmDisposableEnvironment. This really installs CycleArc for the current user.'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SetupUi {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

$idInstall = 100
$idCancel = 101
$idRun = 102
$bmClick = 0x00F5
$bmGetCheck = 0x00F0
$bmSetCheck = 0x00F1
$wmClose = 0x0010

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$SetupPath = [IO.Path]::GetFullPath($SetupPath)
if (!(Test-Path -LiteralPath $SetupPath -PathType Leaf)) { throw "No installer at $SetupPath" }

function Get-CycleArcState {
    [ordered]@{
        Programs = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs\CycleArc\current\CycleArc.exe')
        Legacy   = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'CycleArc\current\CycleArc.exe')
        Data     = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'ProMeter')
        Registry = Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CycleArc'
        Running  = @(Get-Process -Name 'CycleArc' -ErrorAction SilentlyContinue).Count
    }
}

function Wait-SetupWindow([Diagnostics.Process]$Process, [int]$Seconds = 60) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        $Process.Refresh()
        if ($Process.HasExited) { throw "The installer exited (code $($Process.ExitCode)) before showing a window." }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return $Process.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    }
    throw "No installer window appeared within $Seconds seconds."
}

function Save-WindowImage([IntPtr]$Handle, [string]$Path) {
    [void][SetupUi]::SetForegroundWindow($Handle)
    Start-Sleep -Milliseconds 600
    $rect = New-Object SetupUi+RECT
    [void][SetupUi]::GetWindowRect($Handle, [ref]$rect)
    $width = [Math]::Max(1, $rect.R - $rect.L)
    $height = [Math]::Max(1, $rect.B - $rect.T)
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    [void][SetupUi]::PrintWindow($Handle, $dc, 2)
    $graphics.ReleaseHdc($dc)
    $graphics.Dispose()
    $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Captured $Path"
}

function Get-ControlText([IntPtr]$Handle) {
    $builder = New-Object Text.StringBuilder 512
    [void][SetupUi]::GetWindowTextW($Handle, $builder, $builder.Capacity)
    $builder.ToString()
}

$report = [ordered]@{}
$before = Get-CycleArcState
Write-Host "Before: $($before | ConvertTo-Json -Compress)"
$report['before'] = $before

# --- 1. Cancel before Install changes nothing. -------------------------------------------
Write-Host '=== Cancel before install ==='
$cancelProcess = Start-Process -FilePath $SetupPath -PassThru
try {
    $handle = Wait-SetupWindow $cancelProcess
    Save-WindowImage $handle (Join-Path $OutputDirectory 'setup-1-confirm.png')
    $installButton = [SetupUi]::GetDlgItem($handle, $idInstall)
    $cancelButton = [SetupUi]::GetDlgItem($handle, $idCancel)
    if ($installButton -eq [IntPtr]::Zero -or $cancelButton -eq [IntPtr]::Zero) {
        throw 'The confirmation screen is missing its Install or Cancel button.'
    }
    $report['confirmInstallButton'] = Get-ControlText $installButton
    $report['confirmCancelButton'] = Get-ControlText $cancelButton
    [void][SetupUi]::SendMessage($cancelButton, $bmClick, [IntPtr]::Zero, [IntPtr]::Zero)
    if (!$cancelProcess.WaitForExit(30000)) { throw 'The installer did not close after Cancel.' }
    $report['cancelExitCode'] = $cancelProcess.ExitCode
    if ($cancelProcess.ExitCode -ne 2) { throw "Cancel exited $($cancelProcess.ExitCode), expected 2." }
}
finally { try { if (!$cancelProcess.HasExited) { $cancelProcess.Kill($true) } } catch { } }

$afterCancel = Get-CycleArcState
foreach ($key in $before.Keys) {
    if ($before[$key] -ne $afterCancel[$key]) {
        throw "Cancelling changed '$key': $($before[$key]) -> $($afterCancel[$key])"
    }
}
Write-Host 'Cancel before install changed nothing.'

# --- 2. Install for real, clearing the Run checkbox on the completion page. ----------------
Write-Host '=== Install ==='
$installProcess = Start-Process -FilePath $SetupPath -PassThru
try {
    $handle = Wait-SetupWindow $installProcess
    $installButton = [SetupUi]::GetDlgItem($handle, $idInstall)
    [void][SetupUi]::SendMessage($installButton, $bmClick, [IntPtr]::Zero, [IntPtr]::Zero)

    # The progress screen only exists while the engine runs, so capture it promptly.
    Start-Sleep -Milliseconds 900
    if (!$installProcess.HasExited) {
        Save-WindowImage $handle (Join-Path $OutputDirectory 'setup-2-progress.png')
    }

    # The completion page is reached when the Run checkbox becomes visible.
    $runCheck = [IntPtr]::Zero
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $InstallTimeoutSeconds) {
        $installProcess.Refresh()
        if ($installProcess.HasExited) { throw "The installer exited (code $($installProcess.ExitCode)) before the completion screen." }
        $candidate = [SetupUi]::GetDlgItem($handle, $idRun)
        if ($candidate -ne [IntPtr]::Zero -and [SetupUi]::IsWindowVisible($candidate)) { $runCheck = $candidate; break }
        Start-Sleep -Milliseconds 300
    }
    if ($runCheck -eq [IntPtr]::Zero) { throw "No completion screen within $InstallTimeoutSeconds seconds." }

    Save-WindowImage $handle (Join-Path $OutputDirectory 'setup-3-done.png')
    $report['doneRunCheckbox'] = Get-ControlText $runCheck
    $report['doneFinishButton'] = Get-ControlText ([SetupUi]::GetDlgItem($handle, $idInstall))

    # Clear Run, so the completion choice can be checked against what actually happens.
    if ([SetupUi]::SendMessage($runCheck, $bmGetCheck, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -eq 1) {
        [void][SetupUi]::SendMessage($runCheck, $bmSetCheck, [IntPtr]0, [IntPtr]::Zero)
    }
    $report['runClearedBeforeFinish'] = $true

    [void][SetupUi]::SendMessage([SetupUi]::GetDlgItem($handle, $idInstall), $bmClick, [IntPtr]::Zero, [IntPtr]::Zero)
    if (!$installProcess.WaitForExit(60000)) { throw 'The installer did not close after Finish.' }
    $report['installExitCode'] = $installProcess.ExitCode
    if ($installProcess.ExitCode -ne 0) { throw "Install exited $($installProcess.ExitCode), expected 0." }
}
finally { try { if (!$installProcess.HasExited) { $installProcess.Kill($true) } } catch { } }

$afterInstall = Get-CycleArcState
Write-Host "After:  $($afterInstall | ConvertTo-Json -Compress)"
$report['after'] = $afterInstall

if (!$afterInstall.Programs -and !$afterInstall.Legacy) { throw 'Nothing was installed at either known root.' }
$installRoot = if ($afterInstall.Programs) { Join-Path $env:LOCALAPPDATA 'Programs\CycleArc' } else { Join-Path $env:LOCALAPPDATA 'CycleArc' }
$report['installRoot'] = $installRoot
if (!(Test-Path -LiteralPath (Join-Path $installRoot 'CycleArc.exe') -PathType Leaf)) {
    throw "The stable launcher is missing at $installRoot"
}
# The completion page's cleared Run box must be honoured.
Start-Sleep -Seconds 3
$running = @(Get-Process -Name 'CycleArc' -ErrorAction SilentlyContinue)
$report['runningAfterClearedRun'] = $running.Count
if ($running.Count -gt $before.Running) {
    foreach ($process in $running) { try { $process.Kill() } catch { } }
    throw 'The installer started CycleArc even though Run was cleared on the completion page.'
}
Write-Host 'The cleared Run choice was honoured.'

$reportPath = Join-Path $OutputDirectory 'setup-ui-report.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Report: $reportPath"
Write-Host 'Setup UI verification passed: confirm, progress and completion screens, cancel and Run choice.'
