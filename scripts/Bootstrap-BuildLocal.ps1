#Requires -Version 5.1
<#
.SYNOPSIS
    Windows PowerShell entry point when PowerShell 7 is not on PATH.
.DESCRIPTION
    The shared coordinator owns the single prerequisite approval. This script never
    treats a re-entry marker as approval and never changes execution policy.
#>
[CmdletBinding()]
param(
    [Alias('Fast')][switch]$BootstrapFast,
    [Alias('NoInstall')][switch]$BootstrapNoInstall,
    [Alias('SilentInstall')][switch]$BootstrapSilentInstall,
    [Alias('NoPrerequisitePrompt')][switch]$BootstrapNoPrerequisitePrompt,
    [Alias('LoadOnly')][switch]$BootstrapLoadOnly
)

Set-StrictMode -Version Latest

function Test-CycleArcPowerShellVersion {
    param([Parameter(Mandatory)][string]$Path)
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $Path
    $process.StartInfo.Arguments = '-NoProfile -NonInteractive -Command "if ($PSVersionTable.PSVersion.Major -ge 7 -and !$PSVersionTable.PSVersion.PreReleaseLabel) { exit 0 }; exit 1"'
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    try {
        if (!$process.Start()) { return $false }
        if (!$process.WaitForExit(10000)) {
            try { $process.Kill() } catch { }
            return $false
        }
        $process.ExitCode -eq 0
    }
    catch { $false }
    finally { $process.Dispose() }
}

function Find-CycleArcPowerShell {
    param([scriptblock]$CommandFinder, [scriptblock]$VersionProbe, [string[]]$InstallRoots)
    if (!$CommandFinder) { $CommandFinder = { Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source } }
    if (!$VersionProbe) { $VersionProbe = { param($path) Test-CycleArcPowerShellVersion -Path $path } }
    if (!$InstallRoots) { $InstallRoots = @($env:ProgramW6432, $env:ProgramFiles) }
    $candidates = @(& $CommandFinder)
    foreach ($installRoot in $InstallRoots) {
        if ($installRoot) { $candidates += Join-Path $installRoot 'PowerShell/7/pwsh.exe' }
    }
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf) -and (& $VersionProbe $candidate)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    $null
}

function Get-CycleArcPowerShellRelease {
    param([scriptblock]$MetadataReader, [Parameter(Mandatory)][string]$TemporaryDirectory)
    if (!$MetadataReader) {
        $MetadataReader = {
            param($root)
            $metadataPath = Join-Path $root 'powershell-release.json'
            Invoke-SetupUiOfficialDownload -Uri 'https://api.github.com/repos/PowerShell/PowerShell/releases/latest' -Destination $metadataPath
            if ((Get-Item -LiteralPath $metadataPath).Length -gt 2MB) { throw 'PowerShell release metadata exceeded the size limit.' }
            Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -ErrorAction Stop
        }
    }
    $release = & $MetadataReader $TemporaryDirectory
    $tag = [string](Get-SetupUiProperty $release 'tag_name')
    if ($tag -notmatch '^v(7\.[0-9]+\.[0-9]+)$' -or
        (Get-SetupUiProperty $release 'prerelease') -ne $false -or
        (Get-SetupUiProperty $release 'draft') -ne $false) {
        throw 'The official PowerShell release metadata did not identify a stable PowerShell 7 release.'
    }
    $version = $tag.Substring(1)
    $name = 'PowerShell-' + $version + '-win-x64.msi'
    $expectedUri = 'https://github.com/PowerShell/PowerShell/releases/download/' + $tag + '/' + $name
    $assets = @(Get-SetupUiProperty $release 'assets' | Where-Object {
        (Get-SetupUiProperty $_ 'name') -ceq $name -and
        (Get-SetupUiProperty $_ 'browser_download_url') -ceq $expectedUri
    })
    if ($assets.Count -ne 1) { throw 'The official stable PowerShell release has no unambiguous Windows x64 MSI. Install PowerShell 7 manually from https://aka.ms/powershell.' }
    [pscustomobject]@{ Version = $version; Name = $name; Uri = $expectedUri }
}

function Test-CycleArcWingetOfficialSource {
    param([Parameter(Mandatory)][string]$Path, [scriptblock]$SourceReader)
    if (!$SourceReader) {
        $SourceReader = {
            param($file)
            $raw = @(& $file source export --name winget --disable-interactivity 2>$null)
            if ($LASTEXITCODE -ne 0) { return $null }
            ($raw -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        }
    }
    try {
        $source = & $SourceReader $Path
        return (Get-SetupUiProperty $source 'Name') -ceq 'winget' -and
            (Get-SetupUiProperty $source 'Arg') -ceq 'https://cdn.winget.microsoft.com/cache' -and
            (Get-SetupUiProperty $source 'Type') -ceq 'Microsoft.PreIndexed.Package' -and
            (Get-SetupUiProperty $source 'Identifier') -ceq 'Microsoft.Winget.Source_8wekyb3d8bbwe'
    }
    catch { $false }
}

function Invoke-CycleArcPowerShellInstall {
    <# Called only after the coordinator has received explicit interactive approval. #>
    [CmdletBinding()]
    param(
        [scriptblock]$PowerShellResolver,
        [scriptblock]$WingetResolver,
        [scriptblock]$WingetSourceValidator,
        [scriptblock]$WingetRunner,
        [scriptblock]$MetadataReader,
        [scriptblock]$Downloader,
        [scriptblock]$SignatureValidator,
        [scriptblock]$ProcessRunner,
        [scriptblock]$TemporaryDirectoryFactory
    )
    if (!$PowerShellResolver) { $PowerShellResolver = { Find-CycleArcPowerShell } }
    if (!$WingetResolver) { $WingetResolver = { Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source } }
    if (!$WingetSourceValidator) { $WingetSourceValidator = { param($path) Test-CycleArcWingetOfficialSource -Path $path } }
    if (!$WingetRunner) { $WingetRunner = { param($path, $arguments) & $path @arguments | Out-Host; $LASTEXITCODE } }
    if (!$Downloader) { $Downloader = { param($uri, $path) Invoke-SetupUiOfficialDownload -Uri $uri -Destination $path } }
    if (!$SignatureValidator) { $SignatureValidator = { param($path) Test-SetupUiMicrosoftAuthenticode -Path $path } }
    if (!$ProcessRunner) { $ProcessRunner = { param($path, $arguments) Invoke-SetupUiPrerequisiteInstaller -FilePath $path -ArgumentList $arguments } }
    if (!$TemporaryDirectoryFactory) { $TemporaryDirectoryFactory = { New-SetupUiPrerequisiteTempDirectory } }
    $existing = & $PowerShellResolver
    if ($existing) { return [pscustomobject]@{ Status = 'Ready'; Path = $existing; ExitCode = 0 } }

    $temporaryDirectory = $null
    try {
        $temporaryDirectory = & $TemporaryDirectoryFactory
        $packagePath = $null
        $winget = & $WingetResolver
        if ($winget -and (& $SignatureValidator $winget) -and (& $WingetSourceValidator $winget)) {
            # Download rather than winget install so the actual MSI also passes our
            # Microsoft Authenticode check before any installation starts.
            $downloadRoot = Join-Path $temporaryDirectory 'winget'
            New-Item -ItemType Directory -Path $downloadRoot -ErrorAction Stop | Out-Null
            Write-Host 'Downloading Microsoft.PowerShell from the official winget source.'
            $wingetCode = & $WingetRunner $winget @('download', '--id', 'Microsoft.PowerShell', '--exact', '--source', 'winget',
                '--architecture', 'x64', '--installer-type', 'wix', '--download-directory', $downloadRoot,
                '--skip-dependencies', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
            if ($wingetCode -eq 0) {
                $packages = @(Get-ChildItem -LiteralPath $downloadRoot -Filter '*.msi' -File)
                if ($packages.Count -eq 1) { $packagePath = $packages[0].FullName }
            }
            if (!$packagePath) { Write-Host 'WinGet did not provide one x64 MSI; using the official PowerShell release download.' }
        }
        if (!$packagePath) {
            $release = Get-CycleArcPowerShellRelease -MetadataReader $MetadataReader -TemporaryDirectory $temporaryDirectory
            $packagePath = Join-Path $temporaryDirectory $release.Name
            & $Downloader $release.Uri $packagePath
        }
        if (!(Test-SetupUiTempChildPath -Root $temporaryDirectory -Path $packagePath) -or
            !(Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'The PowerShell MSI was not downloaded inside its unique temporary directory.' }
        if (!(& $SignatureValidator $packagePath)) { throw 'The PowerShell MSI failed Authenticode validation for Microsoft Corporation. It was not executed.' }
        Write-Host 'Verified the Microsoft signature; starting the PowerShell 7 MSI with visible progress.'
        $msiexec = Join-Path $env:SystemRoot 'System32/msiexec.exe'
        $code = & $ProcessRunner $msiexec @('/i', $packagePath, '/passive', '/norestart', 'ADD_PATH=1', 'ENABLE_PSREMOTING=0', 'USE_MU=0', 'ENABLE_MU=0')
        if ($code -in @(5, 1260, 1625)) { return [pscustomobject]@{ Status = 'PolicyBlocked'; Path = $null; ExitCode = [int]$code } }
        if ($code -in @(1223, 1602)) { return [pscustomobject]@{ Status = 'Cancelled'; Path = $null; ExitCode = [int]$code } }
        if ($code -in @(3010, 1641)) { return [pscustomobject]@{ Status = 'RebootRequired'; Path = $null; ExitCode = [int]$code } }
        if ($code -ne 0) { throw "PowerShell installation failed with exit code $code. Company IT approval may be required." }
        $verified = & $PowerShellResolver
        if (!$verified) { throw 'PowerShell 7 could not be verified after installation. Run build-local.cmd again after installing it manually from https://aka.ms/powershell.' }
        [pscustomobject]@{ Status = 'Ready'; Path = $verified; ExitCode = 0 }
    }
    catch {
        $nativeCode = Get-SetupUiNativeErrorCode $_.Exception
        if ($nativeCode -eq 1223) { return [pscustomobject]@{ Status = 'Cancelled'; Path = $null; ExitCode = 1223 } }
        if ($nativeCode -in @(5, 1260, 1625)) { return [pscustomobject]@{ Status = 'PolicyBlocked'; Path = $null; ExitCode = [int]$nativeCode } }
        throw
    }
    finally { Remove-SetupUiPrerequisiteTempDirectory -Path $temporaryDirectory }
}

function Write-CycleArcBootstrapFailure {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Reason, [object]$ErrorRecord)
    try {
        $directory = Join-Path $RepoRoot 'artifacts/build-local'
        New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
        $log = Join-Path $directory 'bootstrap.log'
        $safeReason = $Reason -replace '[\x00-\x1f\x7f]', ' '
        $summary = "Failed at: prerequisite bootstrap`r`nReason: $safeReason`r`nDiagnostic log: $log`r`nCycleArc was not built, stopped or installed by this bootstrap."
        $details = @((Get-Date).ToUniversalTime().ToString('o'), $summary)
        if ($ErrorRecord) { $details += ($ErrorRecord | Out-String); $details += $ErrorRecord.Exception.ToString() }
        $details | Add-Content -LiteralPath $log -Encoding UTF8 -ErrorAction Stop
        Set-Content -LiteralPath (Join-Path $directory 'last-failure.txt') -Value $summary -Encoding UTF8 -ErrorAction Stop
        $log
    }
    catch { $null } # A diagnostic write failure must not replace the original failure.
}

function Invoke-CycleArcBuildLocalBootstrap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string[]]$BuildArguments = @(),
        [switch]$NoPrompt,
        [switch]$SilentInstall,
        [scriptblock]$PowerShellResolver,
        [scriptblock]$PrerequisiteRunner,
        [scriptblock]$BuildRunner,
        [scriptblock]$FailureRecorder
    )
    if ($env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP) { throw 'CycleArc prerequisite bootstrap re-entry was blocked. No additional prerequisite installation was attempted.' }
    if (!$PowerShellResolver) { $PowerShellResolver = { Find-CycleArcPowerShell } }
    if (!$PrerequisiteRunner) { $PrerequisiteRunner = { param($root, $noPrompt, $silent) Invoke-CycleArcBuildPrerequisites -RepoRoot $root -NoPrompt:$noPrompt -SilentInstall:$silent } }
    if (!$BuildRunner) { $BuildRunner = { param($path, $scriptPath, $arguments) & $path -NoProfile -File $scriptPath @arguments; $LASTEXITCODE } }
    if (!$FailureRecorder) { $FailureRecorder = { param($root, $reason) Write-CycleArcBootstrapFailure -RepoRoot $root -Reason $reason | Out-Null } }
    $previousMarker = [Environment]::GetEnvironmentVariable('CYCLEARC_BUILD_LOCAL_BOOTSTRAP')
    try {
        $env:CYCLEARC_BUILD_LOCAL_BOOTSTRAP = '1'
        $powerShell = & $PowerShellResolver
        if (!$powerShell) {
            $prerequisites = & $PrerequisiteRunner $RepoRoot ([bool]$NoPrompt) ([bool]$SilentInstall)
            if (!$prerequisites -or $prerequisites.Status -ne 'Ready') {
                $status = if ($prerequisites) { $prerequisites.Status } else { 'Unknown' }
                & $FailureRecorder $RepoRoot ("Prerequisite preparation stopped: $status. Manual installation or company IT approval may be required.")
                return 1
            }
            $powerShell = & $PowerShellResolver
            if (!$powerShell) { throw 'PowerShell 7 is still unavailable after prerequisite setup. Run build-local.cmd again after manual installation.' }
        }
        & $BuildRunner $powerShell (Join-Path $RepoRoot 'scripts/Build-Local.ps1') $BuildArguments
    }
    finally { [Environment]::SetEnvironmentVariable('CYCLEARC_BUILD_LOCAL_BOOTSTRAP', $previousMarker) }
}

if (!$BootstrapLoadOnly) {
    $ErrorActionPreference = 'Stop'
    $script:CycleArcBootstrapHandedOff = $false
    $bootstrapExitCode = 1
    $bootstrapRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    try {
        $buildArguments = @()
        foreach ($name in @('Fast', 'NoInstall', 'SilentInstall', 'NoPrerequisitePrompt')) {
            if (Get-Variable -Name ('Bootstrap' + $name) -ValueOnly) { $buildArguments += '-' + $name }
        }
        $bootstrapNoPrompt = [bool]$BootstrapNoPrerequisitePrompt
        $bootstrapSilent = [bool]$BootstrapSilentInstall
        . (Join-Path $PSScriptRoot 'BuildPrerequisites.ps1')
        $result = Invoke-CycleArcBuildLocalBootstrap -RepoRoot $bootstrapRepoRoot -BuildArguments $buildArguments -NoPrompt:$bootstrapNoPrompt -SilentInstall:$bootstrapSilent -BuildRunner {
            param($path, $scriptPath, $arguments)
            $script:CycleArcBootstrapHandedOff = $true
            & $path -NoProfile -File $scriptPath @arguments
            $LASTEXITCODE
        }
        $bootstrapExitCode = [int](@($result) | Select-Object -Last 1)
    }
    catch {
        $bootstrapReason = 'Prerequisite bootstrap failed while importing, checking or preparing Microsoft build tools.'
        # Keep controlled tool-specific coordinator errors visible; interpreter errors
        # remain in the diagnostic log instead of being dumped into the console.
        if ($_.Exception.Message.StartsWith('Could not prepare ')) {
            $bootstrapReason = $_.Exception.Message -replace '[\x00-\x1f\x7f]', ' '
        }
        Write-Host $bootstrapReason
        Write-Host 'Install PowerShell 7 manually from https://aka.ms/powershell if needed. Company-managed PCs may require IT approval.'
        if (!$script:CycleArcBootstrapHandedOff) {
            $bootstrapLog = Write-CycleArcBootstrapFailure -RepoRoot $bootstrapRepoRoot -Reason $bootstrapReason -ErrorRecord $_
            if ($bootstrapLog) { Write-Host "Diagnostic log: $bootstrapLog" }
            Write-Host 'The bootstrap did not build, stop or install CycleArc.'
        }
    }
    # Once handed off, Build-Local.ps1 owns its failure summary and console pause.
    if ($bootstrapExitCode -ne 0 -and !$script:CycleArcBootstrapHandedOff -and
        $env:CYCLEARC_BUILD_LOCAL_CMD -eq '1' -and
        (Get-Command Test-SetupUiPrerequisiteInteractive -ErrorAction SilentlyContinue) -and
        (Test-SetupUiPrerequisiteInteractive -NoPrompt:$bootstrapNoPrompt -SilentInstall:$bootstrapSilent)) {
        try { Read-Host 'Press Enter to close this window' | Out-Null } catch { }
    }
    exit $bootstrapExitCode
}
