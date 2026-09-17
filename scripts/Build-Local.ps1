#Requires -Version 7.0
<#
.SYNOPSIS
    Build the current checkout, package CycleArc-Setup.exe, install the managed
    app under the existing Velopack location, and start it.

.PARAMETER Fast
    Forwarded to dev-run.ps1: skip unit tests only after they have already passed.

.PARAMETER NoInstall
    Stop after a successful package. Does not stop a running desktop or run Setup.exe.

.PARAMETER LoadOnly
    Dot-source the functions without running the default path.
#>
[CmdletBinding()]
param(
    [switch]$Fast,
    [switch]$NoInstall,
    [switch]$LoadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BuildLocalRepoRoot {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-DefaultManagedInstallRoot {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CycleArc'
}

function ConvertTo-BuildLocalArgument([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    $Value
}

function Get-BuildLocalSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-BuildLocalGitState([string]$RepoRoot) {
    $branch = (Invoke-InstallGit -RepoRoot $RepoRoot -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    $head = (Invoke-InstallGit -RepoRoot $RepoRoot -Arguments @('rev-parse', 'HEAD')).Trim()
    $porcelain = (Invoke-InstallGit -RepoRoot $RepoRoot -Arguments @('status', '--porcelain')).Trim()
    [pscustomobject]@{
        Branch = $branch
        Head = $head
        Dirty = ![string]::IsNullOrWhiteSpace($porcelain)
    }
}

function Test-BuildLocalDotnetSdk([string[]]$SdkList) {
    foreach ($line in @($SdkList)) {
        if ($line -match '^\s*(\d+)\.' -and [int]$Matches[1] -ge 8) { return $true }
    }
    $false
}

function Assert-BuildLocalTools {
    foreach ($name in @('pwsh', 'dotnet', 'git')) {
        if (!(Get-Command $name -ErrorAction SilentlyContinue)) {
            throw "Missing required tool '$name'. Install PowerShell 7 and the .NET 8 SDK, then retry. The current installation was not replaced."
        }
    }
    $sdks = @(& dotnet --list-sdks 2>&1 | ForEach-Object { $_.ToString() })
    if (!(Test-BuildLocalDotnetSdk $sdks)) {
        throw "A .NET SDK 8 or newer is required to build CycleArc (found '$($sdks -join '; ')'). The current installation was not replaced."
    }
}

function New-BuildLocalLogDirectory([string]$RepoRoot) {
    $directory = Join-Path $RepoRoot 'artifacts/build-local'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Assert-InstallPath -Path $directory -AllowedRoots @((Join-Path $RepoRoot 'artifacts')) | Out-Null
    $directory
}

function Invoke-ExternalProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 1800,
        [switch]$NoNewWindow
    )
    Write-Host ("    > {0} {1}" -f $FilePath, ($ArgumentList -join ' '))
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = [bool]$NoNewWindow
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $ArgumentList) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (!$process) { throw "Could not start $FilePath" }
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "$FilePath did not finish within $TimeoutSeconds seconds (PID $($process.Id))."
        }
        return [int]$process.ExitCode
    }
    finally { $process.Dispose() }
}

function Invoke-WindowedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 600
    )
    Write-Host ("    > {0} {1}" -f $FilePath, ($ArgumentList -join ' '))
    $quoted = @($ArgumentList | ForEach-Object { ConvertTo-BuildLocalArgument $_ })
    $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -WorkingDirectory $WorkingDirectory -PassThru
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        throw "$FilePath did not finish within $TimeoutSeconds seconds (PID $($process.Id))."
    }
    return [int]$process.ExitCode
}

function Read-DesktopStatusJson([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $text = (Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue)
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Invoke-DesktopStatus {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$LogDirectory
    )
    $output = Join-Path $LogDirectory ("desktop-status-" + [Guid]::NewGuid().ToString('N') + '.json')
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.ArgumentList.Add('--desktop-status')
    $process = [Diagnostics.Process]::Start($start)
    if (!$process) { return $null }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $null = $process.StandardError.ReadToEnd()
        if (!$process.WaitForExit(20000)) {
            try { $process.Kill($true) } catch { }
            return $null
        }
        if ($stdout) { [IO.File]::WriteAllText($output, $stdout) }
        $status = Read-DesktopStatusJson $output
        if ($process.ExitCode -ne 0 -and !$status) { return $null }
        return $status
    }
    finally { $process.Dispose() }
}

function Invoke-DesktopShutdown {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$LogDirectory
    )
    $output = Join-Path $LogDirectory ("desktop-shutdown-" + [Guid]::NewGuid().ToString('N') + '.json')
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.ArgumentList.Add('--desktop-shutdown')
    $process = [Diagnostics.Process]::Start($start)
    if (!$process) { throw "Could not start $Executable --desktop-shutdown" }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if (!$process.WaitForExit(30000)) {
            try { $process.Kill($true) } catch { }
            throw "$Executable --desktop-shutdown did not finish within 30 seconds."
        }
        if ($stdout) { [IO.File]::WriteAllText($output, $stdout) }
        if ($process.ExitCode -ne 0) {
            $detail = if ($stdout) { $stdout.Trim() } elseif ($stderr) { $stderr.Trim() } else { "exit $($process.ExitCode)" }
            throw "CycleArc desktop shutdown failed: $detail"
        }
        return Read-DesktopStatusJson $output
    }
    finally { $process.Dispose() }
}

function Stop-VerifiedCycleArcDesktop {
    param(
        [Parameter(Mandatory)][string]$ProbeExecutable,
        [Parameter(Mandatory)][string]$LogDirectory,
        [string[]]$KnownExecutablePaths = @()
    )
    $status = Invoke-DesktopStatus -Executable $ProbeExecutable -LogDirectory $LogDirectory
    if ($status -and [bool]$status.Succeeded) {
        Write-Host "Stopping CycleArc desktop PID $($status.ProcessId) path $($status.ExecutablePath)"
        $ack = Invoke-DesktopShutdown -Executable $ProbeExecutable -LogDirectory $LogDirectory
        if (!$ack -or ![bool]$ack.Succeeded) {
            throw "The running CycleArc did not accept a shutdown request. Close it from the tray and retry. Path: $($status.ExecutablePath)"
        }
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($deadline.Elapsed.TotalSeconds -lt 25) {
            try {
                $running = Get-Process -Id ([int]$status.ProcessId) -ErrorAction Stop
                if ($running.HasExited) { break }
            }
            catch { break }
            Start-Sleep -Milliseconds 400
        }
        try {
            $leftover = Get-Process -Id ([int]$status.ProcessId) -ErrorAction Stop
            if (!$leftover.HasExited) {
                throw "CycleArc PID $($status.ProcessId) is still running at $($status.ExecutablePath) after shutdown. Close it from the tray. The installer was not started."
            }
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
        Assert-InstallDesktopMutexAbsent
        return
    }

    $desktops = @(Get-CycleArcDesktopProcess -KnownExecutablePaths $KnownExecutablePaths)
    try {
        if ($desktops.Count -eq 0) { return }
        $summary = ($desktops | ForEach-Object { "PID $($_.ProcessId) $($_.Path)" }) -join '; '
        throw "CycleArc is running but did not answer desktop IPC ($summary). Close that desktop from its tray. Refusing to guess-kill it."
    }
    finally {
        foreach ($record in $desktops) {
            if ($record.Process) { try { $record.Process.Dispose() } catch { } }
        }
    }
}

function Get-SetupArguments {
    param(
        [Parameter(Mandatory)][string]$SetupLog,
        [string]$ExistingRoot,
        [string]$DefaultRoot
    )
    $arguments = @('--silent', '--log', $SetupLog)
    if (!$DefaultRoot) { $DefaultRoot = Get-DefaultManagedInstallRoot }
    if ($ExistingRoot -and !(ConvertTo-InstallAbsolutePath $ExistingRoot).Equals(
        (ConvertTo-InstallAbsolutePath $DefaultRoot), [StringComparison]::OrdinalIgnoreCase)) {
        $arguments += @('--installto', (ConvertTo-InstallAbsolutePath $ExistingRoot))
    }
    $arguments
}

function Wait-DesktopReady {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$LogDirectory,
        [int]$TimeoutSeconds = 60
    )
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $status = Invoke-DesktopStatus -Executable $Executable -LogDirectory $LogDirectory
        if ($status -and [bool]$status.Succeeded) { return $status }
        Start-Sleep -Milliseconds 500
    }
    throw "The installed CycleArc did not report desktop readiness within $TimeoutSeconds seconds."
}

function Invoke-BuildLocal {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [switch]$Fast,
        [switch]$NoInstall,
        [string]$ManagedRoot,
        [scriptblock]$DevRun,
        [scriptblock]$PackagedSetup,
        [scriptblock]$StopDesktop,
        [scriptblock]$RunSetup,
        [scriptblock]$StartLauncher,
        [scriptblock]$ProbeStatus
    )
    $RepoRoot = ConvertTo-InstallAbsolutePath $RepoRoot
    Assert-CycleArcTree -Root $RepoRoot
    Assert-BuildLocalTools
    $layout = Resolve-InstallLayout -RepoRoot $RepoRoot
    $lease = $null
    $logDirectory = $null
    $transcript = $null
    try {
        $lease = New-InstallLease -InstallRoot $layout.InstallRoot -AllowedRoots @($layout.InstallRoot)
        $logDirectory = New-BuildLocalLogDirectory $RepoRoot
        $startedAt = Get-Date
        $logPath = Join-Path $logDirectory ('build-local-{0:yyyyMMdd-HHmmss}.log' -f $startedAt)
        $transcript = $logPath
        Start-Transcript -LiteralPath $logPath | Out-Null
        $git = Get-BuildLocalGitState $RepoRoot
        Write-Host "Branch: $($git.Branch)"
        Write-Host "HEAD: $($git.Head)"
        Write-Host ("Working tree: {0}" -f $(if ($git.Dirty) { 'has local changes (building the current checkout, not pulling)' } else { 'clean' }))
        Write-Host "Log: $logPath"

        if ($DevRun) {
            & $DevRun
        }
        else {
            $pwsh = (Get-Command pwsh).Source
            $devRun = Join-Path $RepoRoot 'dev-run.ps1'
            $arguments = @('-NoProfile', '-File', $devRun, '-NoLaunch')
            if ($Fast) { $arguments += '-Fast' }
            $exitCode = Invoke-ExternalProcess -FilePath $pwsh -ArgumentList $arguments -WorkingDirectory $RepoRoot -TimeoutSeconds 1800 -NoNewWindow
            if ($exitCode -ne 0) {
                throw "dev-run.ps1 -NoLaunch failed (exit $exitCode). Setup.exe was not started; the running installation was left unchanged. Log: $logPath"
            }
        }

        $stagingExe = Join-Path $RepoRoot 'publish/.dev-staging/CycleArc.exe'
        $setupPath = Join-Path $RepoRoot 'publish/.dev-velopack/CycleArc-Setup.exe'
        if ($PackagedSetup) {
            $pack = & $PackagedSetup
            if ($pack) {
                if ($pack.StagingExe) { $stagingExe = [string]$pack.StagingExe }
                if ($pack.SetupPath) { $setupPath = [string]$pack.SetupPath }
            }
        }
        if (!(Test-Path -LiteralPath $stagingExe -PathType Leaf)) {
            throw "Published CycleArc.exe is missing at $stagingExe. Setup.exe was not started."
        }
        if (!(Test-Path -LiteralPath $setupPath -PathType Leaf)) {
            throw "This run did not produce CycleArc-Setup.exe at $setupPath. An older Setup.exe will not be used."
        }
        $setupItem = Get-Item -LiteralPath $setupPath
        if ($setupItem.LastWriteTimeUtc -lt $startedAt.ToUniversalTime().AddSeconds(-5)) {
            throw "CycleArc-Setup.exe at $setupPath is older than this run. Refusing to install a leftover installer."
        }
        $publishedHash = Get-BuildLocalSha256 $stagingExe
        $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagingExe).FileVersion
        Write-Host "Packaged Setup: $setupPath"
        Write-Host "Published CycleArc.exe SHA-256: $publishedHash"
        Write-Host "Published FileVersion: $publishedVersion"

        if ($NoInstall) {
            Write-Host "NoInstall: package is ready; the managed installation was not changed."
            return [pscustomobject]@{
                Branch = $git.Branch
                Head = $git.Head
                SetupPath = $setupPath
                PublishedHash = $publishedHash
                InstalledExe = $null
                HashMatched = $false
                Running = $false
                LogPath = $logPath
            }
        }

        $defaultRoot = if ($ManagedRoot) { ConvertTo-InstallAbsolutePath $ManagedRoot } else { Get-DefaultManagedInstallRoot }
        $existingRoot = if ($ManagedRoot) {
            if (Test-ManagedInstallRoot $defaultRoot) { $defaultRoot } else { $null }
        }
        else {
            Get-ManagedInstallRoot
        }
        $installRoot = if ($existingRoot) { $existingRoot } else { $defaultRoot }
        Write-Host "Managed install root: $installRoot"

        $known = @(
            (Join-Path $installRoot 'CycleArc.exe'),
            (Join-Path $installRoot 'current/CycleArc.exe'),
            (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc/CycleArc.exe')
        )
        if ($StopDesktop) { & $StopDesktop }
        else {
            Stop-VerifiedCycleArcDesktop -ProbeExecutable $stagingExe -LogDirectory $logDirectory -KnownExecutablePaths $known
        }

        $setupLog = Join-Path $logDirectory 'setup.log'
        $setupArgs = Get-SetupArguments -SetupLog $setupLog -ExistingRoot $existingRoot -DefaultRoot $defaultRoot
        $setupExit = if ($RunSetup) {
            & $RunSetup $setupPath $setupArgs
        }
        else {
            Invoke-WindowedProcess -FilePath $setupPath -ArgumentList $setupArgs -TimeoutSeconds 600
        }
        if ($setupExit -ne 0) {
            throw "CycleArc-Setup.exe failed (exit $setupExit). See $setupLog. The previous installation was not reported as replaced."
        }

        $installedExe = Join-Path $installRoot 'current/CycleArc.exe'
        $launcher = Join-Path $installRoot 'CycleArc.exe'
        if (!(Test-Path -LiteralPath $installedExe -PathType Leaf)) {
            throw "Setup.exe finished but $installedExe is missing."
        }
        if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) {
            throw "Setup.exe finished but the stable launcher is missing: $launcher"
        }
        $installedHash = Get-BuildLocalSha256 $installedExe
        if ($installedHash -cne $publishedHash) {
            throw "Installed $installedExe SHA-256 $installedHash does not match this build $publishedHash. Same-version Setup.exe appears to have left the previous executable in place."
        }

        if ($StartLauncher) { & $StartLauncher $launcher }
        else {
            $null = Start-Process -FilePath $launcher -ArgumentList @('--show') -WorkingDirectory $installRoot
        }
        $status = if ($ProbeStatus) { & $ProbeStatus }
        else { Wait-DesktopReady -Executable $installedExe -LogDirectory $logDirectory }
        if (!$status -or ![bool]$status.Succeeded) {
            throw "The installed CycleArc did not report desktop readiness. Log: $logPath"
        }

        $runningPath = ConvertTo-InstallAbsolutePath ([string]$status.ExecutablePath)
        $currentFull = ConvertTo-InstallAbsolutePath $installedExe
        $runningHash = Get-BuildLocalSha256 $currentFull
        if ($runningHash -cne $publishedHash) {
            throw "The running desktop is not this build (installed $runningHash vs published $publishedHash)."
        }
        if (!$runningPath.Equals($currentFull, [StringComparison]::OrdinalIgnoreCase) -and
            !$runningPath.Equals((ConvertTo-InstallAbsolutePath $launcher), [StringComparison]::OrdinalIgnoreCase)) {
            throw "The running CycleArc is $runningPath, not the managed install at $currentFull."
        }

        Write-Host ""
        Write-Host "CycleArc local install is running."
        Write-Host "Branch: $($git.Branch)"
        Write-Host "HEAD: $($git.Head)"
        Write-Host "Setup: $setupPath"
        Write-Host "Installed: $installedExe"
        Write-Host "SHA-256 match: $publishedHash"
        Write-Host "Running PID: $($status.ProcessId) path $($status.ExecutablePath)"
        Write-Host "Log: $logPath"
        [pscustomobject]@{
            Branch = $git.Branch
            Head = $git.Head
            SetupPath = $setupPath
            PublishedHash = $publishedHash
            InstalledExe = $installedExe
            HashMatched = $true
            Running = $true
            ProcessId = $status.ProcessId
            LogPath = $logPath
        }
    }
    finally {
        if ($transcript) { try { Stop-Transcript | Out-Null } catch { } }
        if ($lease) { $lease.Dispose() }
    }
}

if (!$LoadOnly -and $MyInvocation.InvocationName -ne '.') {
    $localInstall = Join-Path $PSScriptRoot 'LocalInstall.ps1'
    . $localInstall
    $root = Get-BuildLocalRepoRoot
    Set-Location -LiteralPath $root
    Invoke-BuildLocal -RepoRoot $root -Fast:$Fast -NoInstall:$NoInstall | Out-Null
}
