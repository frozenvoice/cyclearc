#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/SetupUiPrerequisites.ps1')
. (Join-Path $repoRoot 'scripts/Bootstrap-BuildLocal.ps1') -LoadOnly

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message (expected '$Expected', got '$Actual')" }
}
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Fragment) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -notlike "*$Fragment*") { throw }; return }
    throw "Expected an exception containing '$Fragment'."
}
function New-BootstrapTestDirectory {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))
    New-Item -Path $path -ItemType Directory | Out-Null
    $path
}
function Get-TestPowerShellRelease {
    [pscustomobject]@{
        tag_name = 'v7.6.1'; draft = $false; prerelease = $false
        assets = @([pscustomobject]@{
            name = 'PowerShell-7.6.1-win-x64.msi'
            browser_download_url = 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/PowerShell-7.6.1-win-x64.msi'
        })
    }
}

# Evaluate the actual CMD version predicate with synthetic host metadata. The probe
# itself is silent, so the existing redirected CMD failure summary remains single.
$cmdText = Get-Content -LiteralPath (Join-Path $repoRoot 'build-local.cmd') -Raw
$probeMatch = [regex]::Match($cmdText, 'pwsh -NoProfile -NonInteractive -Command "(?<probe>[^"]+)" >nul 2>&1\s*if errorlevel 1 goto bootstrap_start')
Assert-True $probeMatch.Success 'CMD rejects failed stable-host probe through PS5 bootstrap'
Assert-True ($cmdText -match 'where pwsh >nul 2>&1\s*if errorlevel 1 goto bootstrap_start') 'CMD missing-host path enters the same bootstrap'
$directBuildIndex = $cmdText.IndexOf('pwsh -NoProfile -File "%ROOT%scripts\Build-Local.ps1"')
Assert-True ($directBuildIndex -gt $probeMatch.Index) 'stable host is verified before direct build handoff'
$predicate = $probeMatch.Groups['probe'].Value -replace '\$PSVersionTable', '$fakeVersionTable' -replace 'exit 0', 'return $true' -replace 'exit 1', 'return $false'
function Test-CmdSyntheticHost([int]$Major, [string]$PreReleaseLabel) {
    $fakeVersionTable = [pscustomobject]@{ PSVersion = [pscustomobject]@{ Major = $Major; PreReleaseLabel = $PreReleaseLabel } }
    & ([scriptblock]::Create($predicate))
}
Assert-True (!(Test-CmdSyntheticHost 6 '')) 'legacy PowerShell routes to bootstrap'
Assert-True (!(Test-CmdSyntheticHost 7 'preview.1')) 'preview PowerShell routes to bootstrap'
Assert-True (Test-CmdSyntheticHost 7 '') 'stable PowerShell 7 can directly enter build flow'
Write-Host 'PASS: CMD validates stable PowerShell 7 before handoff; missing, legacy and preview hosts bootstrap.'

# Dot-sourcing helpers must not replace build switches in their caller.
$Fast = $true; $NoInstall = $true; $SilentInstall = $true; $NoPrerequisitePrompt = $true; $LoadOnly = $false
. (Join-Path $repoRoot 'scripts/Bootstrap-BuildLocal.ps1') -LoadOnly
Assert-True ($Fast -and $NoInstall -and $SilentInstall -and $NoPrerequisitePrompt -and !$LoadOnly) 'helper loading preserves caller switches'

$root = New-BootstrapTestDirectory
try {
    $standard = Join-Path $root 'PowerShell/7/pwsh.exe'
    New-Item -ItemType Directory -Path (Split-Path $standard -Parent) | Out-Null
    Set-Content -LiteralPath $standard -Value 'fake executable'
    $found = Find-CycleArcPowerShell -CommandFinder { $null } -InstallRoots @($root) -VersionProbe { $true }
    Assert-Equal $standard $found 'stale PATH discovery uses standard install directory'
    Assert-True (!(Find-CycleArcPowerShell -CommandFinder { $null } -InstallRoots @($root) -VersionProbe { $false })) 'unverified pwsh is rejected'
}
finally { Remove-SetupUiPrerequisiteTempDirectory $root }
Write-Host 'PASS: helper loading preserves flags and stale PATH discovery validates PowerShell.'

$release = Get-CycleArcPowerShellRelease -TemporaryDirectory ([IO.Path]::GetTempPath()) -MetadataReader { Get-TestPowerShellRelease }
Assert-Equal '7.6.1' $release.Version 'stable official metadata version'
Assert-Throws { Get-CycleArcPowerShellRelease -TemporaryDirectory ([IO.Path]::GetTempPath()) -MetadataReader { $r = Get-TestPowerShellRelease; $r.prerelease = $true; $r } } 'stable'
Assert-Throws { Get-CycleArcPowerShellRelease -TemporaryDirectory ([IO.Path]::GetTempPath()) -MetadataReader { $r = Get-TestPowerShellRelease; $r.assets[0].browser_download_url = 'https://example.org/PowerShell.msi'; $r } } 'unambiguous'
Write-Host 'PASS: preview releases and unofficial asset URLs are rejected.'

$script:powerShellProbes = 0
$script:installLaunches = 0
$script:downloadUrl = $null
$script:installArguments = @()
$installed = Invoke-CycleArcPowerShellInstall -PowerShellResolver {
    $script:powerShellProbes++; if ($script:powerShellProbes -gt 1) { 'C:\Program Files\PowerShell\7\pwsh.exe' }
} -WingetResolver { $null } -MetadataReader { Get-TestPowerShellRelease } -Downloader {
    param($uri, $path) $script:downloadUrl = $uri; Set-Content -LiteralPath $path -Value 'fake MSI'
} -SignatureValidator { $true } -ProcessRunner {
    param($path, $arguments) $script:installLaunches++; $script:installArguments = $arguments; 0
} -TemporaryDirectoryFactory { New-BootstrapTestDirectory }
Assert-Equal 'Ready' $installed.Status 'official MSI install status'
Assert-Equal 1 $script:installLaunches 'MSI launched once'
Assert-Equal 2 $script:powerShellProbes 'installed PowerShell is rediscovered'
Assert-Equal $release.Uri $script:downloadUrl 'downloaded official asset URL'
Assert-True ($script:installArguments -contains '/passive' -and $script:installArguments -contains '/norestart') 'MSI uses visible progress and no reboot'
Assert-True ($script:installArguments -notcontains '/quiet') 'MSI is not silent'

$script:rejectedLaunches = 0
Assert-Throws { Invoke-CycleArcPowerShellInstall -PowerShellResolver { $null } -WingetResolver { $null } -MetadataReader { Get-TestPowerShellRelease } -Downloader {
    param($uri, $path) Set-Content -LiteralPath $path -Value 'unsigned fake MSI'
} -SignatureValidator { $false } -ProcessRunner { $script:rejectedLaunches++; 0 } -TemporaryDirectoryFactory { New-BootstrapTestDirectory } } 'Authenticode'
Assert-Equal 0 $script:rejectedLaunches 'unsigned MSI never starts'
foreach ($code in @(1602, 1223, 3010, 1641, 5, 1260, 1625)) {
    $script:fakeInstallerCode = $code
    $result = Invoke-CycleArcPowerShellInstall -PowerShellResolver { $null } -WingetResolver { $null } -MetadataReader { Get-TestPowerShellRelease } -Downloader {
        param($uri, $path) Set-Content -LiteralPath $path -Value 'fake MSI'
    } -SignatureValidator { $true } -ProcessRunner { $script:fakeInstallerCode } -TemporaryDirectoryFactory { New-BootstrapTestDirectory }
    $expected = if ($code -in @(3010, 1641)) { 'RebootRequired' } elseif ($code -in @(5, 1260, 1625)) { 'PolicyBlocked' } else { 'Cancelled' }
    Assert-Equal $expected $result.Status "installer exit $code status"
}
Write-Host 'PASS: signed MSI is installed once and cancellation/reboot stop continuation.'

foreach ($code in @(5, 1260, 1625)) {
    $script:fakeInstallerCode = $code
    $result = Invoke-CycleArcPowerShellInstall -PowerShellResolver { $null } -WingetResolver { $null } -MetadataReader { Get-TestPowerShellRelease } -Downloader {
        param($uri, $path) Set-Content -LiteralPath $path -Value 'fake MSI'
    } -SignatureValidator { $true } -ProcessRunner { throw (New-Object ComponentModel.Win32Exception($script:fakeInstallerCode)) } -TemporaryDirectoryFactory { New-BootstrapTestDirectory }
    Assert-Equal 'PolicyBlocked' $result.Status "native policy error $code stops setup"
}
Write-Host 'PASS: Windows policy and access-denied failures return PolicyBlocked without retry.'

$officialSource = { [pscustomobject]@{ Name = 'winget'; Arg = 'https://cdn.winget.microsoft.com/cache'; Type = 'Microsoft.PreIndexed.Package'; Identifier = 'Microsoft.Winget.Source_8wekyb3d8bbwe' } }
Assert-True (Test-CycleArcWingetOfficialSource -Path 'fake-winget' -SourceReader $officialSource) 'official winget source accepted'
Assert-True (!(Test-CycleArcWingetOfficialSource -Path 'fake-winget' -SourceReader { [pscustomobject]@{ Name = 'winget'; Arg = 'https://example.org/cache'; Type = 'Microsoft.PreIndexed.Package'; Identifier = 'Microsoft.Winget.Source_8wekyb3d8bbwe' } })) 'reconfigured winget source rejected'

$script:wingetArguments = @()
$script:wingetMetadataCalls = 0
$script:powerShellProbes = 0
$result = Invoke-CycleArcPowerShellInstall -PowerShellResolver {
    $script:powerShellProbes++; if ($script:powerShellProbes -gt 1) { 'verified-pwsh' }
} -WingetResolver { 'winget.exe' } -WingetSourceValidator { $true } -SignatureValidator { $true } -WingetRunner {
    param($path, $arguments)
    $script:wingetArguments = $arguments
    $index = [Array]::IndexOf($arguments, '--download-directory')
    Set-Content -LiteralPath (Join-Path $arguments[$index + 1] 'Microsoft.PowerShell.msi') -Value 'fake winget MSI'
    0
} -MetadataReader { $script:wingetMetadataCalls++; Get-TestPowerShellRelease } -ProcessRunner { 0 } -TemporaryDirectoryFactory { New-BootstrapTestDirectory }
Assert-Equal 'Ready' $result.Status 'winget download status'
Assert-Equal 0 $script:wingetMetadataCalls 'winget preferred over GitHub fallback'
Assert-True ($script:wingetArguments[0] -eq 'download' -and $script:wingetArguments -contains 'Microsoft.PowerShell' -and $script:wingetArguments -contains 'winget' -and $script:wingetArguments -contains 'wix') 'winget exact official MSI selection'
Assert-True ($script:wingetArguments -notcontains '--ignore-security-hash') 'winget hash checks preserved'
Write-Host 'PASS: WinGet download is preferred and the actual MSI is verified before installation.'

$script:fallbackDownloads = 0
$script:powerShellProbes = 0
$result = Invoke-CycleArcPowerShellInstall -PowerShellResolver {
    $script:powerShellProbes++; if ($script:powerShellProbes -gt 1) { 'verified-pwsh' }
} -WingetResolver { 'reconfigured-winget.exe' } -WingetSourceValidator { $false } -SignatureValidator { $true } -WingetRunner { throw 'unofficial source must not download' } -MetadataReader { Get-TestPowerShellRelease } -Downloader {
    param($uri, $path) $script:fallbackDownloads++; Set-Content -LiteralPath $path -Value 'fake official MSI'
} -ProcessRunner { 0 } -TemporaryDirectoryFactory { New-BootstrapTestDirectory }
Assert-Equal 'Ready' $result.Status 'unofficial source uses official fallback'
Assert-Equal 1 $script:fallbackDownloads 'official fallback downloaded once'

$previousMarker = [Environment]::GetEnvironmentVariable('CYCLEARC_BUILD_LOCAL_BOOTSTRAP')
try {
    [Environment]::SetEnvironmentVariable('CYCLEARC_BUILD_LOCAL_BOOTSTRAP', $null)
    $script:bootstrapProbes = 0; $script:bootstrapPrerequisites = 0; $script:bootstrapBuilds = 0
    $result = Invoke-CycleArcBuildLocalBootstrap -RepoRoot $repoRoot -BuildArguments @('-Fast', '-NoInstall') -PowerShellResolver {
        $script:bootstrapProbes++; if ($script:bootstrapProbes -gt 1) { 'verified-pwsh' }
    } -PrerequisiteRunner { param($root, $noPrompt, $silent) $script:bootstrapPrerequisites++; [pscustomobject]@{ Status = 'Ready' } } -BuildRunner {
        param($path, $scriptPath, $arguments) $script:bootstrapBuilds++; Assert-Equal 'verified-pwsh' $path 'verified executable handoff'; Assert-Equal '-Fast,-NoInstall' ($arguments -join ',') 'original build switches forwarded'; 0
    }
    Assert-Equal 0 $result 'bootstrap continuation exit'
    Assert-Equal 1 $script:bootstrapPrerequisites 'one prerequisite coordinator'
    Assert-Equal 1 $script:bootstrapBuilds 'one same-invocation continuation'
    Assert-True (!$env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP) 'bootstrap marker reset after success'
    foreach ($status in @('Cancelled', 'Manual', 'RebootRequired', 'PolicyBlocked')) {
        $script:bootstrapStatus = $status
        $result = Invoke-CycleArcBuildLocalBootstrap -RepoRoot $repoRoot -PowerShellResolver { $null } -PrerequisiteRunner { [pscustomobject]@{ Status = $script:bootstrapStatus } } -BuildRunner { throw 'build must not run' } -FailureRecorder { }
        Assert-Equal 1 $result "$status blocks build"
        Assert-True (!$env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP) 'bootstrap marker reset after stopped setup'
    }
    $result = Invoke-CycleArcBuildLocalBootstrap -RepoRoot $repoRoot -NoPrompt -SilentInstall -PowerShellResolver { $null } -PrerequisiteRunner {
        param($root, $noPrompt, $silent) Assert-True ($noPrompt -and $silent) 'noninteractive switches forwarded to coordinator'; [pscustomobject]@{ Status = 'Manual' }
    } -BuildRunner { throw 'noninteractive setup cannot build when prerequisites are absent' } -FailureRecorder { }
    Assert-Equal 1 $result 'noninteractive setup failure'
    Assert-Throws { Invoke-CycleArcBuildLocalBootstrap -RepoRoot $repoRoot -PowerShellResolver { $null } -PrerequisiteRunner { throw 'fake failure' } } 'fake failure'
    Assert-True (!$env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP) 'bootstrap marker reset after exception'
    $env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP = '1'
    Assert-Throws { Invoke-CycleArcBuildLocalBootstrap -RepoRoot $repoRoot -PowerShellResolver { throw 'resolver must not run' } -PrerequisiteRunner { throw 'installation must not run' } } 're-entry'
}
finally { [Environment]::SetEnvironmentVariable('CYCLEARC_BUILD_LOCAL_BOOTSTRAP', $previousMarker) }
Write-Host 'PASS: one coordinator approval hands off once; failure, cancellation and recursion never build.'

$logRoot = New-BootstrapTestDirectory
try {
    try { throw 'fake import/probe diagnostic' }
    catch { $record = $_ }
    $diagnostic = Write-CycleArcBootstrapFailure -RepoRoot $logRoot -Reason 'Preparation failed.' -ErrorRecord $record
    Assert-True ((Get-Content -LiteralPath $diagnostic -Raw) -match 'fake import/probe diagnostic') 'full bootstrap diagnostic recorded'
    $summary = Get-Content -LiteralPath (Join-Path $logRoot 'artifacts/build-local/last-failure.txt') -Raw
    Assert-True ($summary -match 'Failed at: prerequisite bootstrap' -and $summary -match 'Preparation failed\.') 'short bootstrap failure summary recorded'
    Assert-True ($summary -notmatch 'fake import/probe diagnostic') 'short summary does not print raw full error'
}
finally { Remove-SetupUiPrerequisiteTempDirectory $logRoot }
Write-Host 'PASS: bootstrap diagnostics and concise last-failure summary are recorded separately.'
