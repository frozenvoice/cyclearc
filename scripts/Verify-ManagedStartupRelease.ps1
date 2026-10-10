[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$ProcessId
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:CYCLEARC_DISPOSABLE_PROFILE -cne '1') {
    throw 'Managed startup release probe requires an explicitly disposable Windows profile.'
}
if (![IO.Path]::IsPathFullyQualified($InstallRoot)) {
    throw 'The managed installation root must be an absolute path.'
}
$resolvedRoot = [IO.Path]::GetFullPath($InstallRoot)
if (!(Test-Path -LiteralPath (Join-Path $resolvedRoot 'current/CycleArc.exe') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $resolvedRoot 'Update.exe') -PathType Leaf)) {
    throw 'The startup release probe requires a managed installation.'
}

# Observation only. Keep the established process/mutex/Restart Manager release
# contract, rather than replacing it with a delay or a directory-rename test.
. (Join-Path $PSScriptRoot 'LocalInstall.ps1')
Wait-InstallDesktopReleased -ProcessId $ProcessId -InstallRoot $resolvedRoot | ConvertTo-Json -Depth 4
