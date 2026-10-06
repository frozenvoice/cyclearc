#Requires -Version 5.1
<# Shows one native acknowledgement for a typed policy/reboot prerequisite outcome. #>
[CmdletBinding()]
param(
    [Alias('FailureMarker')][string]$FailureDialogMarker,
    [Alias('LoadOnly')][switch]$FailureDialogLoadOnly
)
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'SetupUiPrerequisites.ps1')

function Show-BuildLocalPrerequisiteFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FailureMarker,
        [switch]$NoPrompt,
        [switch]$SilentInstall,
        [switch]$ManualPrerequisites,
        [scriptblock]$InteractiveProbe,
        [scriptblock]$DialogPresenter
    )
    if ($NoPrompt -or $SilentInstall -or $ManualPrerequisites -or $env:CYCLEARC_NO_PAUSE) { return }
    if (Test-SetupUiCiEnvironment) { return }
    if (!$InteractiveProbe) {
        $InteractiveProbe = {
            Test-SetupUiPrerequisiteInteractive
        }
    }
    if (!(& $InteractiveProbe)) { return }
    if (!(Test-Path -LiteralPath $FailureMarker -PathType Leaf)) { return }
    # This run's marker is the source of the reason and log path, never a replay of
    # an old transcript. Bound reads, and only acknowledge prerequisite failures.
    if ((Get-Item -LiteralPath $FailureMarker).Length -gt 64KB) { return }
    $message = [IO.File]::ReadAllText($FailureMarker)
    if ($message -notmatch '(?m)^Failed at: (preflight|prerequisite bootstrap)\r?$') { return }
    if ($message -notmatch '(?m)^Prerequisite status: (PolicyBlocked|RebootRequired)\r?$') { return }
    if (!$DialogPresenter) {
        $DialogPresenter = {
            param($text)
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                $text,
                'CycleArc build prerequisites',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            ) | Out-Null
        }
    }
    if ($message.Length -gt 8192) { $message = $message.Substring(0, 8192) }
    & $DialogPresenter ($message.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine + "Failure record: $FailureMarker") | Out-Null
}

if (!$FailureDialogLoadOnly) {
    try { Show-BuildLocalPrerequisiteFailure -FailureMarker $FailureDialogMarker }
    catch { Write-Host "The prerequisite error could not be displayed. Failure record: $FailureDialogMarker" }
    # Acknowledgement must never replace the build/bootstrap exit code held by CMD.
    exit 0
}
