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

# The stage the run reached. A failure message may only claim that the previous
# installation is intact while the run never got as far as starting Setup.exe.
$script:BuildLocalStage = 'preflight'

function Set-BuildLocalStage([string]$Stage) { $script:BuildLocalStage = $Stage }

function Get-BuildLocalStage { $script:BuildLocalStage }

function Get-BuildLocalStageGuidance([string]$Stage) {
    switch ($Stage) {
        'preflight' { 'Nothing was built, stopped or installed. The installed CycleArc is unchanged and still running.' }
        'build' { 'dev-run.ps1 failed before packaging. Setup.exe was not started, the running CycleArc was not stopped, and the installed version is unchanged.' }
        'package' { 'This run did not produce a usable CycleArc-Setup.exe. Setup.exe was not started, the running CycleArc was not stopped, and the installed version is unchanged.' }
        'stop-desktop' { 'The running CycleArc could not be stopped over desktop IPC. Setup.exe was not started and the installed version is unchanged; that desktop may still be running. Close it from its tray and retry.' }
        'install' { 'CycleArc-Setup.exe was already started, so the installation may be partially replaced. Read the Setup log before assuming the previous version is intact.' }
        'verify-install' { 'CycleArc-Setup.exe already ran and changed the installation, but the result is not this build. Do not assume the previous version is intact.' }
        'start' { 'This build is installed. Only starting it or confirming readiness failed, so the previous version is already gone.' }
        default { 'See the log for the failing step; do not assume the previous installation is intact.' }
    }
}

function Write-BuildLocalFailure {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Message,
        [string]$LogDirectory,
        [string]$LogPath
    )
    $lines = @(
        'CycleArc build-local failed.',
        "Stage: $Stage",
        "Cause: $Message",
        (Get-BuildLocalStageGuidance $Stage)
    )
    if ($LogPath) { $lines += "Log: $LogPath" }
    $text = ($lines -join [Environment]::NewLine)
    if ($LogDirectory -and (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        # build-local.cmd prints this file instead of a blanket claim about the
        # previous installation, so it has to survive a failed transcript.
        try { [IO.File]::WriteAllText((Join-Path $LogDirectory 'last-failure.txt'), $text + [Environment]::NewLine) } catch { }
    }
    $text
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
    $startParameters = @{ FilePath = $FilePath; PassThru = $true }
    if (@($ArgumentList).Count -gt 0) {
        # Start-Process joins -ArgumentList with spaces, so each element carries
        # its own quoting; it also rejects an empty array.
        $startParameters['ArgumentList'] = @($ArgumentList | ForEach-Object { ConvertTo-BuildLocalArgument $_ })
    }
    # Start-Process rejects a blank -WorkingDirectory, and a caller that has no
    # directory to name must leave the parameter out rather than pass ''.
    if (![string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $resolved = ConvertTo-InstallAbsolutePath $WorkingDirectory
        if (!(Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "Working directory does not exist: $resolved"
        }
        $startParameters['WorkingDirectory'] = $resolved
    }
    $process = Start-Process @startParameters
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

function ConvertFrom-DesktopStatusJson([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    try { return $Text | ConvertFrom-Json } catch { return $null }
}

function Read-DesktopStatusJson([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    ConvertFrom-DesktopStatusJson (Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue)
}

function Write-DesktopIpcDiagnostic {
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Reason,
        [object]$Result
    )
    $builder = [Text.StringBuilder]::new()
    [void]$builder.AppendLine(('[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}: {2}' -f (Get-Date), $Stage, $Reason))
    if ($Result) {
        [void]$builder.AppendLine(('    started={0} timedOut={1} killed={2} exitCode={3} elapsedMs={4}' -f `
            $Result.Started, $Result.TimedOut, $Result.Killed,
            $(if ($null -eq $Result.ExitCode) { 'none' } else { $Result.ExitCode }), $Result.ElapsedMilliseconds))
        foreach ($stream in @(
            [pscustomobject]@{ Name = 'stdout'; Text = [string]$Result.StandardOutput },
            [pscustomobject]@{ Name = 'stderr'; Text = [string]$Result.StandardError })) {
            if ([string]::IsNullOrWhiteSpace($stream.Text)) { continue }
            $text = $stream.Text.Trim()
            if ($text.Length -gt 2000) { $text = $text.Substring(0, 2000) + '...[truncated]' }
            [void]$builder.AppendLine("    $($stream.Name): $text")
        }
    }
    $path = Join-Path $LogDirectory 'desktop-ipc.log'
    try { Add-Content -LiteralPath $path -Value $builder.ToString().TrimEnd() } catch { }
    Write-Host ("    desktop IPC: {0} ({1}); see {2}" -f $Reason, $Stage, $path)
    $path
}

function Invoke-DesktopIpcProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [int]$TimeoutMilliseconds = 20000,
        [int]$OutputDrainMilliseconds = 5000
    )
    $description = ($Executable + ' ' + (@($ArgumentList) -join ' ')).Trim()
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ArgumentList)) { [void]$start.ArgumentList.Add($argument) }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = $null
    $startFailure = ''
    try { $process = [Diagnostics.Process]::Start($start) }
    catch { $startFailure = $_.Exception.Message }
    if (!$process) {
        $detail = if ($startFailure) { ": $startFailure" } else { '' }
        return [pscustomobject]@{
            Started = $false
            TimedOut = $false
            Killed = $false
            ExitCode = $null
            StandardOutput = ''
            StandardError = ''
            ProcessId = 0
            Reason = "could not start $description$detail"
            ElapsedMilliseconds = [int]$stopwatch.ElapsedMilliseconds
        }
    }
    $processId = [int]$process.Id
    try {
        # Both pipes are drained concurrently and the wait comes first. A
        # synchronous ReadToEnd on one stream blocks until the child closes it,
        # so a child that stalls, or that fills the other pipe's buffer, would
        # never reach the timeout check at all.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit($TimeoutMilliseconds)
        $killed = $false
        $reason = ''
        if (!$exited) {
            # Only this probe, which this function started, is stopped. The
            # CycleArc desktop it talks to is a separate process and is never
            # terminated here.
            try { $process.Kill($true); $killed = $true } catch { }
            $reason = "$description did not finish within $([int]($TimeoutMilliseconds / 1000))s (probe PID $processId)"
        }
        # WaitForExit(int) does not guarantee that the async readers completed,
        # and a killed probe still has to release its pipes, so collecting the
        # output gets its own budget instead of waiting forever.
        $drained = $false
        try { $drained = [Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), $OutputDrainMilliseconds) }
        catch { $drained = $false }
        $stdout = if ($drained -and $stdoutTask.IsCompletedSuccessfully) { [string]$stdoutTask.Result } else { '' }
        $stderr = if ($drained -and $stderrTask.IsCompletedSuccessfully) { [string]$stderrTask.Result } else { '' }
        if (!$drained -and !$reason) {
            $reason = "$description exited but its output was still open after $([int]($OutputDrainMilliseconds / 1000))s (probe PID $processId)"
        }
        $exitCode = $null
        if ($exited) { try { $exitCode = [int]$process.ExitCode } catch { $exitCode = $null } }
        [pscustomobject]@{
            Started = $true
            TimedOut = !$exited
            Killed = $killed
            ExitCode = $exitCode
            StandardOutput = $stdout
            StandardError = $stderr
            ProcessId = $processId
            Reason = $reason
            ElapsedMilliseconds = [int]$stopwatch.ElapsedMilliseconds
        }
    }
    finally {
        # Never leave behind a probe this function started, whatever failed above.
        try { if (!$process.HasExited) { $process.Kill($true) } } catch { }
        $process.Dispose()
    }
}

function Invoke-DesktopStatus {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$LogDirectory,
        [int]$TimeoutMilliseconds = 20000,
        [int]$OutputDrainMilliseconds = 5000
    )
    $result = Invoke-DesktopIpcProcess -Executable $Executable -ArgumentList @('--desktop-status') `
        -TimeoutMilliseconds $TimeoutMilliseconds -OutputDrainMilliseconds $OutputDrainMilliseconds
    if (!$result.Started -or $result.Reason) {
        Write-DesktopIpcDiagnostic -LogDirectory $LogDirectory -Stage 'desktop-status' -Reason $result.Reason -Result $result | Out-Null
        return $null
    }
    $status = ConvertFrom-DesktopStatusJson $result.StandardOutput
    if ($null -eq $status) {
        # A desktop that is simply not up yet answers with readable JSON, so an
        # unreadable answer is worth recording; a polling caller keeps trying.
        Write-DesktopIpcDiagnostic -LogDirectory $LogDirectory -Stage 'desktop-status' `
            -Reason "exit $(if ($null -eq $result.ExitCode) { 'none' } else { $result.ExitCode }) with no readable status JSON" -Result $result | Out-Null
        return $null
    }
    if ([bool]$status.Succeeded) {
        $output = Join-Path $LogDirectory ('desktop-status-' + [Guid]::NewGuid().ToString('N') + '.json')
        try { [IO.File]::WriteAllText($output, $result.StandardOutput) } catch { }
    }
    $status
}

function Invoke-DesktopShutdown {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$LogDirectory,
        # The desktop itself allows 3s to find the instance, 3s for the
        # acknowledgement and 22s for the process to exit, so the outer budget
        # has to stay above that or it would report a timeout for a working stop.
        [int]$TimeoutMilliseconds = 45000,
        [int]$OutputDrainMilliseconds = 5000
    )
    $result = Invoke-DesktopIpcProcess -Executable $Executable -ArgumentList @('--desktop-shutdown') `
        -TimeoutMilliseconds $TimeoutMilliseconds -OutputDrainMilliseconds $OutputDrainMilliseconds
    if (!$result.Started -or $result.Reason) {
        $log = Write-DesktopIpcDiagnostic -LogDirectory $LogDirectory -Stage 'desktop-shutdown' -Reason $result.Reason -Result $result
        throw "CycleArc desktop shutdown did not complete: $($result.Reason). See $log"
    }
    if ($result.StandardOutput) {
        $output = Join-Path $LogDirectory ('desktop-shutdown-' + [Guid]::NewGuid().ToString('N') + '.json')
        try { [IO.File]::WriteAllText($output, $result.StandardOutput) } catch { }
    }
    if ($result.ExitCode -ne 0) {
        $log = Write-DesktopIpcDiagnostic -LogDirectory $LogDirectory -Stage 'desktop-shutdown' `
            -Reason "exit $(if ($null -eq $result.ExitCode) { 'none' } else { $result.ExitCode })" -Result $result
        $detail = if ($result.StandardOutput) { $result.StandardOutput.Trim() }
            elseif ($result.StandardError) { $result.StandardError.Trim() }
            else { "exit $($result.ExitCode)" }
        throw "CycleArc desktop shutdown failed: $detail. See $log"
    }
    ConvertFrom-DesktopStatusJson $result.StandardOutput
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
    Set-BuildLocalStage 'preflight'
    $lease = $null
    $logDirectory = $null
    $logPath = $null
    $transcript = $null
    try {
        $RepoRoot = ConvertTo-InstallAbsolutePath $RepoRoot
        Assert-CycleArcTree -Root $RepoRoot
        Assert-BuildLocalTools
        $layout = Resolve-InstallLayout -RepoRoot $RepoRoot
        $lease = New-InstallLease -InstallRoot $layout.InstallRoot -AllowedRoots @($layout.InstallRoot)
        $logDirectory = New-BuildLocalLogDirectory $RepoRoot
        # A stale marker from an earlier run must never describe this one.
        $failureMarker = Join-Path $logDirectory 'last-failure.txt'
        if (Test-Path -LiteralPath $failureMarker -PathType Leaf) { Remove-Item -LiteralPath $failureMarker -Force }
        $startedAt = Get-Date
        $logPath = Join-Path $logDirectory ('build-local-{0:yyyyMMdd-HHmmss}.log' -f $startedAt)
        $transcript = $logPath
        Start-Transcript -LiteralPath $logPath | Out-Null
        $git = Get-BuildLocalGitState $RepoRoot
        Write-Host "Branch: $($git.Branch)"
        Write-Host "HEAD: $($git.Head)"
        Write-Host ("Working tree: {0}" -f $(if ($git.Dirty) { 'has local changes (building the current checkout, not pulling)' } else { 'clean' }))
        Write-Host "Log: $logPath"

        Set-BuildLocalStage 'build'
        if ($DevRun) {
            & $DevRun
        }
        else {
            $pwsh = (Get-Command pwsh).Source
            # A distinct name: PowerShell variable names are case-insensitive, so
            # $devRun and the [scriptblock]$DevRun parameter are one variable, and
            # assigning this path to it fails the parameter's type constraint.
            $devRunPath = Join-Path $RepoRoot 'dev-run.ps1'
            if (!(Test-Path -LiteralPath $devRunPath -PathType Leaf)) {
                throw "dev-run.ps1 is missing at $devRunPath. Nothing was built or installed."
            }
            $arguments = @('-NoProfile', '-File', $devRunPath, '-NoLaunch')
            if ($Fast) { $arguments += '-Fast' }
            $exitCode = Invoke-ExternalProcess -FilePath $pwsh -ArgumentList $arguments -WorkingDirectory $RepoRoot -TimeoutSeconds 1800 -NoNewWindow
            if ($exitCode -ne 0) {
                throw "dev-run.ps1 -NoLaunch failed (exit $exitCode). Setup.exe was not started; the running installation was left unchanged. Log: $logPath"
            }
        }

        Set-BuildLocalStage 'package'
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
        # Only now, after a green gate and a packaged Setup.exe, is the running
        # installation touched. A failed build above leaves it running.
        Set-BuildLocalStage 'stop-desktop'
        if ($StopDesktop) { & $StopDesktop }
        else {
            Stop-VerifiedCycleArcDesktop -ProbeExecutable $stagingExe -LogDirectory $logDirectory -KnownExecutablePaths $known
        }

        Set-BuildLocalStage 'install'
        $setupLog = Join-Path $logDirectory 'setup.log'
        $setupArgs = Get-SetupArguments -SetupLog $setupLog -ExistingRoot $existingRoot -DefaultRoot $defaultRoot
        $setupExit = if ($RunSetup) {
            & $RunSetup $setupPath $setupArgs
        }
        else {
            # The installer's own directory: Setup.exe is launched detached from
            # this script's working directory, and an unnamed one is not ''.
            Invoke-WindowedProcess -FilePath $setupPath -ArgumentList $setupArgs `
                -WorkingDirectory (Split-Path -Parent (ConvertTo-InstallAbsolutePath $setupPath)) -TimeoutSeconds 600
        }
        if ($setupExit -ne 0) {
            throw "CycleArc-Setup.exe failed (exit $setupExit). See $setupLog. The previous installation was not reported as replaced."
        }

        Set-BuildLocalStage 'verify-install'
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

        Set-BuildLocalStage 'start'
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
    catch {
        Write-Host ''
        Write-Host (Write-BuildLocalFailure -Stage (Get-BuildLocalStage) -Message $_.Exception.Message `
            -LogDirectory $logDirectory -LogPath $logPath)
        throw
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
    # Invoke-BuildLocal already printed the stage, cause and guidance, and wrote
    # them to artifacts/build-local/last-failure.txt for build-local.cmd to show.
    # Only the nonzero exit code still has to reach CMD.
    try { Invoke-BuildLocal -RepoRoot $root -Fast:$Fast -NoInstall:$NoInstall | Out-Null }
    catch { exit 1 }
    exit 0
}
