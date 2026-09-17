#Requires -Version 7.0
<#
.SYNOPSIS
    End-to-end verification of the installed CycleArc: Setup, in-app update, failed-start
    recovery and removal, including the Claude callbacks a removal must clean up.

.DESCRIPTION
    Builds three genuinely different test executables (A, B and a build that quits before the
    desktop reports readiness), packages each with the production packaging script, installs A
    with its real Setup.exe, and then drives the real in-app update to B through the production
    update window, coordinator, Velopack client, recovery supervisor and Update.exe. It then
    forces the failure build to prove that the supervisor restores and restarts the previous
    installation, and finally removes the installation and verifies the Claude cleanup.

    WHAT THIS SCRIPT CANNOT ISOLATE. -InstallTo moves the installation directory, but the
    following are machine- or user-wide and are NOT isolated by any switch or environment
    variable: the CycleArc data root (%LOCALAPPDATA%\ProMeter, resolved through the Windows
    known-folder API, so LOCALAPPDATA cannot be redirected), the single-instance mutex, the
    desktop IPC pipe, the update recovery root (%LOCALAPPDATA%\CycleArc-update-recovery), the
    uninstall registry entry and Start Menu/Desktop shortcuts. Run this on a disposable Windows
    VM or a dedicated throwaway Windows user account only. The script refuses to start when it
    finds an existing installation, data root or running CycleArc, and requires
    -ConfirmDisposableEnvironment.

    The update feed is a local directory read by a test-only build flavour
    (CYCLEARC_TEST_E2E). HTTPS enforcement, package hashing and every other production check
    are untouched, and no public GitHub release is read or written. Claude data is synthetic:
    no login, credential or live subscription request is involved, so callback results here are
    not evidence of real Claude subscription usage.

.PARAMETER ConfirmDisposableEnvironment
    Required. Confirms this Windows user profile is disposable.

.PARAMETER InstallTo
    Installation directory for Setup.exe. Defaults to the normal %LOCALAPPDATA%\CycleArc.

.PARAMETER WorkRoot
    Directory for builds, packages and logs. Defaults to a new folder under TEMP.

.PARAMETER KeepInstallation
    Skip the removal phase, leaving the installed app in place for manual inspection.
#>
[CmdletBinding()]
param(
    [switch]$ConfirmDisposableEnvironment,
    [string]$InstallTo,
    [string]$WorkRoot,
    [switch]$KeepInstallation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$UiSmokeProject = Join-Path $RepoRoot 'tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj'
$DesktopProject = Join-Path $RepoRoot 'src/CycleArc/CycleArc.csproj'
$DataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ProMeter'
$RecoveryRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CycleArc-update-recovery'
if (!$InstallTo) { $InstallTo = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CycleArc' }
$InstallTo = [IO.Path]::GetFullPath($InstallTo)
if (!$WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) ("cyclearc-installed-e2e-" + [Guid]::NewGuid().ToString('N')) }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)

$Builds = [ordered]@{
    A    = @{ Version = '9.9.1'; Constants = @('CYCLEARC_TEST_E2E') }
    B    = @{ Version = '9.9.2'; Constants = @('CYCLEARC_TEST_E2E') }
    Fail = @{ Version = '9.9.3'; Constants = @('CYCLEARC_TEST_E2E', 'CYCLEARC_TEST_FAIL_STARTUP') }
}
$Evidence = [ordered]@{}
$script:StepNumber = 0

function Write-Step([string]$Message) {
    $script:StepNumber++
    Write-Host ""
    Write-Host ("[{0}] {1}" -f $script:StepNumber, $Message) -ForegroundColor Cyan
}
function Write-Fact([string]$Name, $Value) {
    $Evidence[$Name] = $Value
    Write-Host ("    {0}: {1}" -f $Name, $Value)
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "VERIFICATION FAILED: $Message" }
}
function Get-Sha256([string]$Path) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Expected file does not exist: $Path"
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}
function Get-FileVersionText([string]$Path) {
    [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Path)).FileVersion
}
# The call operator quotes arguments correctly and waits for console programs such as dotnet.
function Invoke-Checked([string]$Executable, [string[]]$Arguments, [string]$What) {
    Write-Host "    > $Executable $($Arguments -join ' ')"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "VERIFICATION FAILED: $What exited with $LASTEXITCODE." }
}
function ConvertTo-CommandLineArgument([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    $Value
}
# Setup.exe, Update.exe and CycleArc.exe are Windows GUI programs, so the call operator does
# not reliably wait for them. Start them explicitly and wait for a real exit code.
function Invoke-Windowed([string]$Executable, [string[]]$Arguments, [string]$What, [int]$TimeoutSeconds = 900) {
    Write-Host "    > $Executable $($Arguments -join ' ')"
    $quoted = @($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ })
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -PassThru
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        throw "VERIFICATION FAILED: $What did not finish within $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) { throw "VERIFICATION FAILED: $What exited with $($process.ExitCode)." }
}
function Invoke-UiSmoke([string[]]$Arguments, [string]$What) {
    Invoke-Checked 'dotnet' (@('run', '--project', $UiSmokeProject, '-c', 'Release', '--no-build', '--') + $Arguments) $What
}
function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$What) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "VERIFICATION FAILED: timed out after $TimeoutSeconds seconds waiting for $What."
}

# The leading comma matters: a function that returns an empty array emits nothing, so the
# caller would see $null and .Count would fail under Set-StrictMode on a clean machine.
function Get-CycleArcProcesses {
    , @(Get-Process -Name 'CycleArc' -ErrorAction SilentlyContinue)
}
# Containment decides this, not a string prefix: the recovery root sits beside the
# installation as 'CycleArc-update-recovery', and a prefix test counted the supervisor's own
# snapshot copy as a second desktop inside the installation. Ask the path API instead - a
# path outside the root relates to it through '..'. Do not filter on HasExited either: .NET
# reports a process it cannot open a handle for as exited, which hid the running desktop.
function Split-PathSegments([string]$Value) {
    try { $full = [IO.Path]::GetFullPath($Value) } catch { return , @() }
    , @($full.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries))
}
function Test-PathUnder([string]$Root, [string]$Path) {
    $rootParts = Split-PathSegments $Root
    $pathParts = Split-PathSegments $Path
    if ($rootParts.Count -eq 0 -or $pathParts.Count -le $rootParts.Count) { return $false }
    for ($index = 0; $index -lt $rootParts.Count; $index++) {
        if ($rootParts[$index] -ine $pathParts[$index]) { return $false }
    }
    $true
}
function Get-ProcessesUnder([string]$Root) {
    $matched = @()
    foreach ($process in Get-CycleArcProcesses) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ($path -and (Test-PathUnder $Root $path)) { $matched += $process }
    }
    , $matched
}
# The one comparison this file has always got right, used elsewhere in the same step:
# an exact full-path match against a known executable. No prefix, no relative path.
function Get-ProcessesRunning([string]$Executable) {
    $target = [IO.Path]::GetFullPath($Executable)
    $matched = @()
    foreach ($process in Get-CycleArcProcesses) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ($path -and ([IO.Path]::GetFullPath($path) -ieq $target)) { $matched += $process }
    }
    , $matched
}
function Get-ProcessSummary($Processes) {
    if ($Processes.Count -eq 0) { return 'none' }
    (($Processes | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = '<unreadable>' }
        "pid $($_.Id) $path"
    }) -join '; ')
}
# The supervisor writes a one-word marker, and now its failure detail, beside the retained copy.
# Reading them turns a blind 15-minute timeout into an immediate, explained failure.
function Get-RecoveryDirectories {
    if (!(Test-Path -LiteralPath $RecoveryRoot)) { return , @() }
    , @(Get-ChildItem -LiteralPath $RecoveryRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName })
}
function Get-RecoveryOutcome([string[]]$Ignore) {
    foreach ($directory in Get-RecoveryDirectories) {
        if ($Ignore -contains $directory) { continue }
        foreach ($marker in @('failed', 'completed')) {
            $path = Join-Path $directory $marker
            if (!(Test-Path -LiteralPath $path)) { continue }
            $detail = ''
            foreach ($name in @('failure.txt', 'apply.log')) {
                $file = Join-Path $directory $name
                if (Test-Path -LiteralPath $file) {
                    $text = (Get-Content -LiteralPath $file -Raw -ErrorAction SilentlyContinue)
                    if ($text) { $detail += "`n--- $name ---`n" + $text.Trim() }
                }
            }
            return [pscustomobject]@{
                Directory = $directory
                Marker    = $marker
                Text      = ((Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue) + '').Trim()
                Detail    = $detail
            }
        }
    }
    $null
}
function Get-Sha256OrNull([string]$Path) {
    try {
        if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    catch { return $null } # The file can be mid-replacement while the updater runs.
}
function Get-DesktopStatus([string]$Executable) {
    if (!(Test-Path -LiteralPath $Executable -PathType Leaf)) { return $null }
    $output = Join-Path $LogRoot ("desktop-status-" + [Guid]::NewGuid().ToString('N') + '.json')
    $process = $null
    try {
        $process = Start-Process -FilePath $Executable -ArgumentList '--desktop-status' -PassThru `
            -NoNewWindow -RedirectStandardOutput $output
        if (!$process.WaitForExit(20000)) {
            try { $process.Kill($true) } catch { }
            return $null
        }
        if ($process.ExitCode -ne 0) { return $null }
        return (Get-Content -LiteralPath $output -Raw | ConvertFrom-Json)
    }
    catch { return $null }
    finally {
        # Release the probe immediately: a status query is not a second running desktop, and
        # the checks below count processes.
        if ($process) { try { $process.Dispose() } catch { } }
        Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
    }
}
function Get-InstalledProcesses([string]$Root) {
    # The union, so a containment test that answers nothing cannot quietly make this a no-op.
    $byIdentity = Get-ProcessesRunning (Join-Path $Root 'current/CycleArc.exe')
    $ids = @($byIdentity | ForEach-Object { $_.Id })
    , @($byIdentity + @(Get-ProcessesUnder $Root | Where-Object { $ids -notcontains $_.Id }))
}
function Stop-InstalledDesktop([string]$Root) {
    # Test-harness step only: the shipped app has no forced-exit entry point.
    foreach ($process in Get-InstalledProcesses $Root) { try { $process.Kill() } catch { } }
    Wait-Until { (Get-InstalledProcesses $Root).Count -eq 0 } 30 'the installed CycleArc processes to exit'
}

function Get-InstallationShortcuts([string]$Root) {
    $shell = New-Object -ComObject WScript.Shell
    $roots = @(
        [Environment]::GetFolderPath('StartMenu'),
        [Environment]::GetFolderPath('CommonStartMenu'),
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    $found = @()
    foreach ($directory in $roots) {
        foreach ($link in @(Get-ChildItem -LiteralPath $directory -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue)) {
            $target = ''
            try { $target = $shell.CreateShortcut($link.FullName).TargetPath } catch { continue }
            if ($target -and $target.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
                $found += [pscustomobject]@{ Link = $link.FullName; Target = $target }
            }
        }
    }
    , $found
}
function Get-UninstallEntries {
    $keys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    $entries = @()
    foreach ($key in $keys) {
        if (!(Test-Path -LiteralPath $key)) { continue }
        foreach ($child in @(Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue)) {
            if ($child.PSChildName -match '(?i)cyclearc') { $entries += $child.PSChildName }
        }
    }
    , $entries
}

# --- Preconditions ------------------------------------------------------------------------

Write-Step 'Checking that this is a disposable Windows environment'
if (!$IsWindows) { throw 'This verification installs and removes a Windows application; run it on Windows.' }
if (!$ConfirmDisposableEnvironment) {
    throw @'
Refusing to run. This script installs, updates and removes CycleArc for the CURRENT Windows
user and writes to %LOCALAPPDATA%\ProMeter, the uninstall registry entry, Start Menu shortcuts,
the single-instance mutex and the desktop IPC pipe. None of those can be isolated by a switch.
Run it on a disposable Windows VM or a dedicated throwaway user, then pass
-ConfirmDisposableEnvironment.
'@
}
Assert-True ((Get-CycleArcProcesses).Count -eq 0) 'a CycleArc process is already running on this machine.'
Assert-True (!(Test-Path -LiteralPath $InstallTo)) "an installation already exists at $InstallTo."
Assert-True (!(Test-Path -LiteralPath $DataRoot)) "CycleArc data already exists at $DataRoot; this must be a fresh profile."
Assert-True (!(Test-Path -LiteralPath $RecoveryRoot)) "an update recovery copy already exists at $RecoveryRoot."
Assert-True ((Get-UninstallEntries).Count -eq 0) 'a CycleArc uninstall registry entry already exists.'
Assert-True ((Get-InstallationShortcuts $InstallTo).Count -eq 0) 'a CycleArc shortcut already exists.'
Write-Fact 'installationRoot' $InstallTo
Write-Fact 'dataRoot' $DataRoot
Write-Fact 'workRoot' $WorkRoot

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$LogRoot = (New-Item -ItemType Directory -Path (Join-Path $WorkRoot 'logs') -Force).FullName
$ClaudeConfig = Join-Path $WorkRoot 'claude-config'
$SeedPath = Join-Path $WorkRoot 'claude-seed.json'
# scripts/Package.ps1 only accepts published and output directories inside the repository's
# publish/ tree, so the test payloads are built there and the rest stays under the work root.
$BuildRoot = Join-Path $RepoRoot 'publish/.installed-e2e'
if (Test-Path -LiteralPath $BuildRoot) { Remove-Item -LiteralPath $BuildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
Write-Fact 'buildRoot' $BuildRoot

# --- Build the distinct test payloads -------------------------------------------------------

Write-Step 'Restoring and building the solution and the packaging tool'
Invoke-Checked 'dotnet' @('restore', (Join-Path $RepoRoot 'CycleArc.sln')) 'dotnet restore'
Invoke-Checked 'dotnet' @('tool', 'restore') 'dotnet tool restore'
Invoke-Checked 'dotnet' @('build', (Join-Path $RepoRoot 'CycleArc.sln'), '-c', 'Release') 'dotnet build'

foreach ($name in $Builds.Keys) {
    $build = $Builds[$name]
    Write-Step "Publishing test build $name ($($build.Version)) and packaging its installer"
    $publish = Join-Path $BuildRoot "$name/app"
    $feed = Join-Path $BuildRoot "$name/feed"
    Invoke-Checked 'dotnet' @(
        'publish', $DesktopProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$($build.Version)", "-p:FileVersion=$($build.Version).0", "-p:AssemblyVersion=$($build.Version).0",
        # An unescaped semicolon would end the property and MSBuild would read the next
        # constant as another switch, so join them with the escaped form.
        "-p:CycleArcTestBuild=$($build.Constants -join '%3B')", '-o', $publish) "publish of test build $name"
    $executable = Join-Path $publish 'CycleArc.exe'
    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    Assert-True ($files.Count -eq 1 -and $files[0].Name -eq 'CycleArc.exe') "test build $name published more than CycleArc.exe."
    & (Join-Path $RepoRoot 'scripts/Package.ps1') -PublishedDir $publish -OutputDir $feed -Version $build.Version
    $build.Executable = $executable
    $build.Sha256 = Get-Sha256 $executable
    $build.FileVersion = Get-FileVersionText $executable
    $build.Feed = $feed
    $build.Setup = Join-Path $feed 'CycleArc-Setup.exe'
    Assert-True (Test-Path -LiteralPath $build.Setup -PathType Leaf) "Setup.exe was not produced for test build $name."
    Write-Fact "build.$name.fileVersion" $build.FileVersion
    Write-Fact "build.$name.sha256" $build.Sha256
}

Write-Step 'Confirming the test builds really differ'
$hashes = @($Builds.Keys | ForEach-Object { $Builds[$_].Sha256 })
Assert-True (($hashes | Select-Object -Unique).Count -eq $hashes.Count) 'two test builds share the same executable hash.'
$versions = @($Builds.Keys | ForEach-Object { $Builds[$_].FileVersion })
Assert-True (($versions | Select-Object -Unique).Count -eq $versions.Count) 'two test builds share the same file version.'
Write-Host '    Distinct file versions and executable hashes confirmed (not an sq.version rewrite).'

# --- Install A ------------------------------------------------------------------------------

Write-Step 'Installing test build A with its real Setup.exe'
Invoke-Windowed $Builds.A.Setup @('--silent', '--installto', $InstallTo, '--log', (Join-Path $LogRoot 'setup-a.log')) 'Setup.exe for build A' 600
$Current = Join-Path $InstallTo 'current/CycleArc.exe'
$Launcher = Join-Path $InstallTo 'CycleArc.exe'
$Updater = Join-Path $InstallTo 'Update.exe'
foreach ($path in @($Current, $Launcher, $Updater)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "the installation is missing $path."
}
Assert-True ((Get-Sha256 $Current) -eq $Builds.A.Sha256) 'the installed executable does not match test build A.'
Assert-True ((Get-FileVersionText $Current) -eq $Builds.A.FileVersion) 'the installed file version does not match test build A.'
Write-Fact 'installed.afterSetup.fileVersion' (Get-FileVersionText $Current)
Write-Fact 'installed.afterSetup.sha256' (Get-Sha256 $Current)
$uninstallEntries = Get-UninstallEntries
Assert-True ($uninstallEntries.Count -ge 1) 'Setup.exe did not register an uninstall entry.'
Write-Fact 'uninstallRegistryEntries' ($uninstallEntries -join ', ')
$shortcutsAfterInstall = Get-InstallationShortcuts $InstallTo
$shortcutSummary = 'none created by this installer configuration'
if ($shortcutsAfterInstall.Count -gt 0) { $shortcutSummary = (($shortcutsAfterInstall | ForEach-Object { $_.Link }) -join ', ') }
Write-Fact 'shortcutsAfterInstall' $shortcutSummary

# --- Seed synthetic Claude and account data --------------------------------------------------

Write-Step 'Seeding a synthetic Claude connection through the production stores'
Invoke-UiSmoke @('--claude-uninstall-seed', $DataRoot, $ClaudeConfig, $Current, $SeedPath) 'Claude connection seeding'
$SeededProfile = (Get-Content -LiteralPath $SeedPath -Raw | ConvertFrom-Json).ProfileId
Write-Fact 'claude.syntheticProfile' $SeededProfile
Invoke-UiSmoke @('--claude-uninstall-installed', $DataRoot, $SeedPath, $InstallTo) 'installed Claude callback check'

# --- Update A to B through the production update path ---------------------------------------

function Start-InstalledDesktop([string]$Feed) {
    $info = [Diagnostics.ProcessStartInfo]::new($Current)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Split-Path -Parent $Current
    $info.ArgumentList.Add('--show')
    $info.Environment['CYCLEARC_TEST_UPDATE_FEED'] = $Feed
    $info.Environment['CYCLEARC_TEST_UPDATE_DRIVE'] = '1'
    $started = [Diagnostics.Process]::Start($info)
    Wait-Until {
        $status = Get-DesktopStatus $Current
        $status -and $status.Succeeded
    } 120 'the installed desktop to report readiness'
    $started
}

Write-Step 'Starting the installed app and driving the real in-app update to build B'
$desktop = Start-InstalledDesktop $Builds.B.Feed
$statusBefore = Get-DesktopStatus $Current
Write-Fact 'desktop.beforeUpdate' ("pid $($statusBefore.ProcessId), $($statusBefore.ExecutablePath), version $($statusBefore.Version)")
Assert-True ([IO.Path]::GetFullPath($statusBefore.ExecutablePath) -ieq [IO.Path]::GetFullPath($Current)) 'the running desktop is not the installed executable.'

Wait-Until { $desktop.HasExited } 600 'the running app to close itself for the approved update'
Wait-Until { (Get-Sha256OrNull $Current) -eq $Builds.B.Sha256 } 600 'the installed executable to become test build B'
Wait-Until {
    $status = Get-DesktopStatus $Current
    $status -and $status.Succeeded -and ([IO.Path]::GetFullPath($status.ExecutablePath) -ieq [IO.Path]::GetFullPath($Current))
} 300 'the updated desktop to report readiness'

$statusAfter = Get-DesktopStatus $Current
Assert-True ((Get-FileVersionText $Current) -eq $Builds.B.FileVersion) 'the updated file version is not test build B.'
Assert-True ($statusAfter.ProcessId -ne $statusBefore.ProcessId) 'the previous desktop process is still the running one.'
Assert-True ((Get-InstalledProcesses $InstallTo).Count -ge 1) 'no CycleArc desktop is running after the update.'
Write-Fact 'desktop.afterUpdate' ("pid $($statusAfter.ProcessId), $($statusAfter.ExecutablePath), version $($statusAfter.Version)")
Write-Fact 'installed.afterUpdate.fileVersion' (Get-FileVersionText $Current)
Write-Fact 'installed.afterUpdate.sha256' (Get-Sha256 $Current)

Write-Step 'Checking preservation, shortcuts and the Claude callback after the update'
foreach ($shortcut in $shortcutsAfterInstall) {
    Assert-True (Test-Path -LiteralPath $shortcut.Link) "the shortcut $($shortcut.Link) did not survive the update."
    Assert-True (Test-Path -LiteralPath $shortcut.Target) "the shortcut $($shortcut.Link) points at a missing file."
}
Invoke-UiSmoke @('--claude-uninstall-installed', $DataRoot, $SeedPath, $InstallTo) 'Claude callback check after the update'

# --- Failed start recovery --------------------------------------------------------------------

Write-Step 'Applying a build that quits before readiness and watching the supervisor recover'
Stop-InstalledDesktop $InstallTo
$recoveryBefore = Get-RecoveryDirectories
$failing = Start-InstalledDesktop $Builds.Fail.Feed
Wait-Until { $failing.HasExited } 600 'the running app to close itself for the failing update'
Wait-Until {
    $outcome = Get-RecoveryOutcome $recoveryBefore
    if ($outcome -and $outcome.Marker -eq 'failed') {
        throw "VERIFICATION FAILED: the recovery supervisor reported '$($outcome.Text)' in $($outcome.Directory).$($outcome.Detail)"
    }
    ((Get-Sha256OrNull $Current) -eq $Builds.B.Sha256) -and ($null -ne (Get-DesktopStatus $Current))
} 900 'the supervisor to restore and restart the previous installation'
$recoveryOutcome = Get-RecoveryOutcome $recoveryBefore
Write-Fact 'recovery.marker' $(if ($recoveryOutcome) { "$($recoveryOutcome.Marker): $($recoveryOutcome.Text)" } else { 'none recorded yet' })
$recovered = Get-DesktopStatus $Current
Assert-True ($recovered -and $recovered.Succeeded) 'no desktop answered after the failed update.'
Assert-True ((Get-Sha256 $Current) -eq $Builds.B.Sha256) 'the failed update left a different executable in place.'
Assert-True ((Get-FileVersionText $Current) -eq $Builds.B.FileVersion) 'the restored file version is not the previous build.'
# Identity, not containment: exactly one process is running the installed executable.
$installedProcesses = Get-ProcessesRunning $Current
Write-Fact 'recovered.installedProcesses' (Get-ProcessSummary $installedProcesses)
# Get-ProcessesUnder answered per call rather than per path here - nothing under the
# installation, everything under the recovery root. Record what it decides and on what,
# because Stop-InstalledDesktop and the notice cleanup below still rely on it.
foreach ($process in Get-CycleArcProcesses) {
    $candidate = $null
    try { $candidate = $process.Path } catch { $candidate = '<unreadable>' }
    Write-Fact "pathCheck.$($process.Id)" ("$candidate | underInstall=" +
        "$(Test-PathUnder $InstallTo $candidate) | underRecovery=$(Test-PathUnder $RecoveryRoot $candidate)")
}
Assert-True ($installedProcesses.Count -eq 1) `
    ("expected exactly one process running $Current after recovery, found " +
        "$($installedProcesses.Count): $(Get-ProcessSummary $installedProcesses)")
Assert-True ([IO.Path]::GetFullPath($recovered.ExecutablePath) -ieq [IO.Path]::GetFullPath($Current)) 'the recovered desktop is not the installed executable.'
Write-Fact 'recovered.fileVersion' (Get-FileVersionText $Current)
Write-Fact 'recovered.sha256' (Get-Sha256 $Current)
Write-Fact 'recovered.desktop' ("pid $($recovered.ProcessId), $($recovered.ExecutablePath)")

# A restored update ends with the supervisor showing a modal notice from its snapshot copy.
# That helper waits for a person, so an unattended run has to acknowledge it explicitly.
# Never the installed desktop, whatever the containment test claims: the supervisor's
# notice runs from its snapshot copy, and the step after this one needs the desktop alive.
function Get-NoticeProcesses {
    @(Get-ProcessesUnder $RecoveryRoot | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        $path -and !([IO.Path]::GetFullPath($path) -ieq [IO.Path]::GetFullPath($Current))
    })
}
$helpers = Get-NoticeProcesses
Write-Fact 'recovery.noticeProcesses' $helpers.Count
foreach ($helper in $helpers) { try { $helper.Kill() } catch { } }
if ($helpers.Count -gt 0) { Wait-Until { (Get-NoticeProcesses).Count -eq 0 } 30 'the recovery notice to close' }
Invoke-UiSmoke @('--claude-uninstall-installed', $DataRoot, $SeedPath, $InstallTo) 'Claude callback check after recovery'

function Write-Evidence {
    Write-Host ''
    Write-Host 'Evidence' -ForegroundColor Green
    foreach ($fact in $Evidence.GetEnumerator()) { Write-Host ("    {0} = {1}" -f $fact.Key, $fact.Value) }
}

if ($KeepInstallation) {
    Write-Step 'Keeping the installation as requested; removal was not verified'
    Write-Evidence
    return
}

# --- Removal ------------------------------------------------------------------------------------

Write-Step 'Removing the installation and verifying the Claude cleanup'
$accountsBeforeRemoval = Get-Sha256 (Join-Path $DataRoot 'codex-accounts.json')
$connectionBeforeRemoval = Get-Sha256 (Join-Path $DataRoot "accounts/$SeededProfile/claude-connection.json")
Invoke-Windowed $Updater @('--uninstall', '--silent', '--log', (Join-Path $LogRoot 'uninstall.log')) 'Update.exe --uninstall' 300
Wait-Until { !(Test-Path -LiteralPath $Current) } 120 'the installed application files to be removed'
Assert-True ((Get-CycleArcProcesses).Count -eq 0) 'a CycleArc process survived removal.'
Assert-True (Test-Path -LiteralPath $DataRoot) 'removal deleted the user data root.'
foreach ($shortcut in $shortcutsAfterInstall) {
    Assert-True (!(Test-Path -LiteralPath $shortcut.Link)) "removal left the shortcut $($shortcut.Link) behind."
}
Invoke-UiSmoke @('--claude-uninstall-removed', $DataRoot, $SeedPath, $InstallTo) 'Claude cleanup check after removal'
$receipt = Get-Content -LiteralPath (Join-Path $DataRoot 'claude-uninstall-cleanup.json') -Raw
Write-Fact 'claude.cleanupReceipt' ($receipt -replace '\s+', ' ')
Assert-True ((Get-Sha256 (Join-Path $DataRoot 'codex-accounts.json')) -eq $accountsBeforeRemoval) 'removal changed the account registry.'
Assert-True ((Get-Sha256 (Join-Path $DataRoot "accounts/$SeededProfile/claude-connection.json")) -eq $connectionBeforeRemoval) 'removal changed the Claude connection record.'

Write-Step 'Verified'
Write-Evidence
Write-Host "Artifacts, logs and the uninstall receipt are under $WorkRoot and $DataRoot." -ForegroundColor Green
