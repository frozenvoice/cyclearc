#Requires -Version 7.0
<#
.SYNOPSIS
    Build the current checkout, package the distributable CycleArc-Setup.exe, then open
    that installer so the person can approve or cancel the installation.

.PARAMETER Fast
    Forwarded to dev-run.ps1: skip unit tests only after they have already passed.

.PARAMETER NoInstall
    Stop after a successful package. Does not open the installer or run Setup.exe.

.PARAMETER SilentInstall
    Install without the setup window, for CI and scripted runs. The default path always
    shows the installer and waits for the person to approve or cancel it.

.PARAMETER LoadOnly
    Dot-source the functions without running the default path.
#>
[CmdletBinding()]
param(
    [switch]$Fast,
    [switch]$NoInstall,
    [switch]$SilentInstall,
    [switch]$LoadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BuildLocalRepoRoot {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

# Where a brand new installation goes. An installation that already exists keeps its own
# location: Get-ManagedInstallRoot is consulted first, and this default never moves it.
function Get-DefaultManagedInstallRoot {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc'
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

$script:BuildLocalStarted = $null

function Format-BuildLocalElapsed([Diagnostics.Stopwatch]$Stopwatch) {
    if (!$Stopwatch) { return '00:00.0' }
    $elapsed = $Stopwatch.Elapsed
    '{0:00}:{1:00}.{2}' -f [int][math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds, [int][math]::Floor($elapsed.Milliseconds / 100.0)
}

function Write-BuildLocalTiming([string]$Message) {
    Write-Host ('[{0}] {1}' -f (Format-BuildLocalElapsed $script:BuildLocalStarted), $Message)
}

function Set-BuildLocalStage {
    param([string]$Stage, [switch]$Quiet)
    $script:BuildLocalStage = $Stage
    if ($Stage -and !$Quiet) { Write-BuildLocalTiming $Stage }
}

function Get-BuildLocalStage { $script:BuildLocalStage }

function Test-BuildLocalVerificationStage([string]$Stage) {
    @(
        'preflight', 'release-guard', 'restore', 'tool-restore', 'build',
        'ui-smoke-desktop-instance', 'local-install-regression', 'build-local-regression',
        'unit-test', 'ui-smoke-full', 'publish', 'package', 'package-verify'
    ) -contains $Stage
}

function Get-BuildLocalStageGuidance([string]$Stage) {
    switch ($Stage) {
        'preflight' { 'Nothing was built, stopped or installed. The installed CycleArc is unchanged and still running.' }
        'stop-desktop' { 'The running CycleArc could not be stopped over desktop IPC. Setup.exe was not started and the installed version is unchanged; that desktop may still be running. Close it from its tray and retry.' }
        'install' { 'CycleArc-Setup.exe was already started, so the installation may be partially replaced. Read the Setup log before assuming the previous version is intact.' }
        'verify-install' { 'CycleArc-Setup.exe already ran and changed the installation, but the result is not this build. Do not assume the previous version is intact.' }
        'start' { 'This build is installed. Only starting it or confirming readiness failed, so the previous version is already gone.' }
        'package' { 'This run did not produce a usable CycleArc-Setup.exe. Setup.exe was not started, the running CycleArc was not stopped, and the installed version is unchanged.' }
        default {
            if (Test-BuildLocalVerificationStage $Stage) {
                "dev-run.ps1 failed at $Stage. Setup.exe was not started, the running CycleArc was not stopped, and the installed version is unchanged."
            }
            else {
                'See the log for the failing step; do not assume the previous installation is intact.'
            }
        }
    }
}

function Get-DevRunChildStage([string]$LogDirectory) {
    if (!$LogDirectory) { return $null }
    $path = Join-Path $LogDirectory 'dev-run.stage'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $value = @(Get-Content -LiteralPath $path -TotalCount 1 -ErrorAction SilentlyContinue)
    if ($value.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$value[0])) { return $null }
    ([string]$value[0]).Trim()
}

function Resolve-BuildLocalReportedStage([string]$ParentStage, [string]$LogDirectory) {
    if ($ParentStage -eq 'build') {
        $child = Get-DevRunChildStage $LogDirectory
        if ($child) { return $child }
    }
    $ParentStage
}

function Write-BuildLocalCapturedProgress([string]$Path) {
    if (!$Path -or !(Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $lines = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue | Where-Object {
        $_ -match '^\[[0-9]{2}:[0-9]{2}\.[0-9]\]' -or $_ -match '^Failed at:' -or $_ -match '^Elapsed:'
    })
    foreach ($line in $lines) { Write-Host $line }
}

# --- Live child progress -----------------------------------------------------------------
# dev-run.ps1 announces every stage on stdout as it starts and finishes, but the parent
# captured that stdout to a file and only read it back after the whole run had ended. The
# stage lines are therefore streamed out of the capture file while the child is still
# running, so the console shows the current step rather than going silent for minutes.

$script:BuildLocalChildStage = $null
$script:BuildLocalChildStageAt = $null

function Reset-BuildLocalChildProgress {
    $script:BuildLocalChildStage = $null
    $script:BuildLocalChildStageAt = $null
}

function Format-BuildLocalSpan([TimeSpan]$Span) {
    if ($Span.Ticks -lt 0) { $Span = [TimeSpan]::Zero }
    '{0:00}:{1:00}.{2}' -f [int][math]::Floor($Span.TotalMinutes), $Span.Seconds, [int][math]::Floor($Span.Milliseconds / 100.0)
}

# One line of child output. Only dev-run.ps1's own '##dev-run##' markers are shown: several
# of its stages run nested regression scripts that print '[mm:ss.d] name' stage lines of
# their own, and those are a different run's stages, not this one's progress. dev-run's
# stamp is time since it started, so the stage's own cost is derived here and labelled
# separately; the two are never shown as one number.
function Write-BuildLocalChildProgressLine([string]$Line) {
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    if ($Line -notmatch '##dev-run##\s+([0-9]{2}):([0-9]{2})\.([0-9])\s+(start|passed|failed)\s+(.+)$') { return }
    $sinceStart = [TimeSpan]::FromMilliseconds(([int]$Matches[1] * 60000) + ([int]$Matches[2] * 1000) + ([int]$Matches[3] * 100))
    $state = $Matches[4]
    $stage = $Matches[5].Trim()
    if ($state -eq 'start') {
        $script:BuildLocalChildStage = $stage
        $script:BuildLocalChildStageAt = $sinceStart
        Write-Host ('    dev-run {0} elapsed | {1} running...' -f (Format-BuildLocalSpan $sinceStart), $stage)
        return
    }
    $cost = ''
    if ($script:BuildLocalChildStage -eq $stage -and $null -ne $script:BuildLocalChildStageAt) {
        $cost = ' (stage took {0})' -f (Format-BuildLocalSpan ($sinceStart - $script:BuildLocalChildStageAt))
    }
    $outcome = if ($state -eq 'passed') { 'passed' } else { 'FAILED' }
    Write-Host ('    dev-run {0} elapsed | {1} {2}{3}' -f (Format-BuildLocalSpan $sinceStart), $stage, $outcome, $cost)
    Reset-BuildLocalChildProgress
}

function New-BuildLocalProgressReader([string]$Path) {
    [pscustomobject]@{
        Path    = $Path
        Offset  = [int64]0
        Decoder = [Text.UTF8Encoding]::new($false).GetDecoder()
        Partial = ''
        Fault   = $null
    }
}

# Emits every newline-terminated line that appeared since the last pump, exactly once.
# A UTF-8 sequence or a line split across two reads is carried over rather than printed
# twice or mangled: the decoder keeps the partial character, $Partial keeps the partial line.
function Invoke-BuildLocalProgressPump {
    param([Parameter(Mandatory)]$Reader, [switch]$Final)
    if (!$Reader -or $Reader.Fault) { return }
    try {
        if (Test-Path -LiteralPath $Reader.Path -PathType Leaf) {
            # The child's capture file is open for writing, so this read has to share it.
            $stream = [IO.File]::Open($Reader.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $pending = $stream.Length - $Reader.Offset
                if ($pending -gt 0) {
                    $stream.Position = $Reader.Offset
                    $buffer = [byte[]]::new([int][math]::Min([int64]1048576, $pending))
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    if ($read -gt 0) {
                        $Reader.Offset += $read
                        $chars = [char[]]::new($Reader.Decoder.GetCharCount($buffer, 0, $read, $false))
                        [void]$Reader.Decoder.GetChars($buffer, 0, $read, $chars, 0, $false)
                        $Reader.Partial = $Reader.Partial + (-join $chars)
                    }
                }
            }
            finally { $stream.Dispose() }
        }
        if (!$Reader.Partial) { return }
        $segments = ($Reader.Partial -replace "`r`n", "`n") -split "`n"
        for ($i = 0; $i -lt $segments.Count - 1; $i++) { Write-BuildLocalChildProgressLine $segments[$i] }
        $Reader.Partial = $segments[$segments.Count - 1]
        if ($Final -and $Reader.Partial) {
            Write-BuildLocalChildProgressLine $Reader.Partial
            $Reader.Partial = ''
        }
    }
    catch {
        # A progress-reading failure is not the child's failure. Record it, stop reading, and
        # let the caller report it separately instead of losing it or blaming the build.
        $Reader.Fault = $_.Exception.Message
    }
}

function Get-BuildLocalLogTail {
    param([string]$Path, [int]$Lines = 80)
    if (!$Path -or !(Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) { return @() }
    @(Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue)
}

function Write-BuildLocalLogTail {
    param([string]$Path, [int]$Lines = 80)
    $tail = @(Get-BuildLocalLogTail -Path $Path -Lines $Lines)
    if ($tail.Count -eq 0) { return }
    Write-Host "----- $Path (last $Lines lines) -----"
    foreach ($line in $tail) { Write-Host $line }
}

function Write-BuildLocalFailure {
    param(
        [string]$Stage = 'unknown',
        [string]$Message = '',
        [string]$LogDirectory,
        [string]$LogPath,
        [string]$Elapsed = ''
    )
    # Never let reporting a failure fail: an empty stage or message still has to
    # produce a readable line rather than a parameter binding error.
    if (!$Stage) { $Stage = 'unknown' }
    if (!$Message) { $Message = 'no error message was available' }
    $lines = @(
        'CycleArc build-local failed.',
        "Failed at: $Stage",
        "Stage: $Stage"
    )
    if ($Elapsed) { $lines += "Elapsed: $Elapsed" }
    $lines += @(
        "Cause: $Message",
        (Get-BuildLocalStageGuidance $Stage)
    )
    if ($LogPath) { $lines += "Log: $LogPath" }
    if ($LogDirectory -and (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        $childLog = Join-Path $LogDirectory 'dev-run.err.log'
        if (Test-Path -LiteralPath $childLog -PathType Leaf) {
            $lines += "Child log: $childLog"
        }
        foreach ($name in @('dev-run.err.log', 'dev-run.out.log')) {
            $captured = Join-Path $LogDirectory $name
            if (!(Test-Path -LiteralPath $captured -PathType Leaf)) { continue }
            $lines += "Captured ${name}: $captured"
            $tail = @(Get-BuildLocalLogTail -Path $captured)
            if ($tail.Count -gt 0) {
                $lines += "----- $name (tail) -----"
                $lines += $tail
            }
        }
    }
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

# Native AOT links with MSVC, which the .NET SDK locates through vswhere. A machine with the
# Build Tools but no vswhere on PATH otherwise fails with a bare "'vswhere.exe' is not
# recognized". Package.ps1 carries the same helper so each script runs on its own.
function Add-VsWhereToPath {
    if (Get-Command vswhere -ErrorAction SilentlyContinue) { return $true }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (!$base) { continue }
        $candidate = Join-Path $base 'Microsoft Visual Studio/Installer'
        if (Test-Path -LiteralPath (Join-Path $candidate 'vswhere.exe') -PathType Leaf) {
            $env:PATH = "$env:PATH;$candidate"
            return $true
        }
    }
    return $false
}

# The setup UI is Native AOT, which links with MSVC through vswhere. Checking this up front
# turns a failure forty minutes into the run - at packaging - into an immediate, actionable one.
function Assert-SetupUiToolchain {
    param([string]$RepoRoot)
    $project = Join-Path $RepoRoot 'src/CycleArc.Setup/CycleArc.Setup.csproj'
    if (!(Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "The setup UI project is missing at $project. Nothing was built, stopped or installed."
    }
    if (Add-VsWhereToPath) { return $true }
    throw ('Building CycleArc-Setup.exe needs the Visual Studio Build Tools with the C++ workload ' +
        '(vswhere.exe was not found under Program Files). Install "Desktop development with C++", ' +
        'then retry. Nothing was built, stopped or installed.')
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
        [switch]$NoNewWindow,
        [string]$StandardOutputPath,
        [string]$StandardErrorPath,
        [int]$OutputDrainMilliseconds = 15000,
        [switch]$StreamProgress,
        [int]$ProgressIntervalMilliseconds = 250
    )
    Write-Host ("    > {0} {1}" -f $FilePath, ($ArgumentList -join ' '))
    $captureOutput = ![string]::IsNullOrWhiteSpace($StandardOutputPath)
    $captureError = ![string]::IsNullOrWhiteSpace($StandardErrorPath)
    if ($captureOutput) { Write-Host "    capturing stdout: $StandardOutputPath" }
    if ($captureError) { Write-Host "    capturing stderr: $StandardErrorPath" }
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = [bool]$NoNewWindow
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $ArgumentList) { [void]$start.ArgumentList.Add($argument) }
    # Redirect both streams together so a child that writes one pipe cannot fill
    # the other unread buffer and stall before WaitForExit sees it leave.
    if ($captureOutput -or $captureError) {
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
    }
    $process = [Diagnostics.Process]::Start($start)
    if (!$process) { throw "Could not start $FilePath" }
    $outFile = $null
    $errFile = $null
    $stdoutTask = $null
    $stderrTask = $null
    $progress = $null
    try {
        if ($start.RedirectStandardOutput) {
            if ($captureOutput) {
                # FileShare.Read so progress can be read back while this run writes it, and an
                # unbuffered handle so a stage line reaches the file when the child emits it
                # rather than sitting in a 4 KB buffer until the run ends.
                $outFile = [IO.FileStream]::new($StandardOutputPath, [IO.FileMode]::Create,
                    [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
                $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
            }
            else {
                $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            }
        }
        if ($start.RedirectStandardError) {
            if ($captureError) {
                $errFile = [IO.FileStream]::new($StandardErrorPath, [IO.FileMode]::Create,
                    [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
                $stderrTask = $process.StandardError.BaseStream.CopyToAsync($errFile)
            }
            else {
                $stderrTask = $process.StandardError.ReadToEndAsync()
            }
        }
        if ($StreamProgress -and $captureOutput) {
            Reset-BuildLocalChildProgress
            $progress = New-BuildLocalProgressReader $StandardOutputPath
        }
        $timedOut = $false
        if ($progress) {
            # Short waits keep the console current without spinning: each one blocks in the OS
            # until the child exits or the interval elapses. The overall budget is unchanged.
            $budget = [Diagnostics.Stopwatch]::StartNew()
            $interval = [math]::Max(50, $ProgressIntervalMilliseconds)
            while (!$process.WaitForExit($interval)) {
                Invoke-BuildLocalProgressPump -Reader $progress
                if ($budget.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                    try { $process.Kill($true) } catch { }
                    $timedOut = $true
                    break
                }
            }
        }
        elseif (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            $timedOut = $true
        }
        $drainTasks = [Collections.Generic.List[Threading.Tasks.Task]]::new()
        if ($stdoutTask) { [void]$drainTasks.Add($stdoutTask) }
        if ($stderrTask) { [void]$drainTasks.Add($stderrTask) }
        if ($drainTasks.Count -gt 0) {
            try { [void][Threading.Tasks.Task]::WaitAll($drainTasks.ToArray(), $OutputDrainMilliseconds) } catch { }
        }
        if ($outFile) { try { $outFile.Dispose() } catch { }; $outFile = $null }
        if ($errFile) { try { $errFile.Dispose() } catch { }; $errFile = $null }
        if ($progress) {
            Invoke-BuildLocalProgressPump -Reader $progress -Final
            if ($progress.Fault) {
                # Distinct from the child's own outcome, and never silent.
                Write-Host "    warning: live progress could not be read from $StandardOutputPath ($($progress.Fault)); the captured log below is complete."
                Write-BuildLocalCapturedProgress $StandardOutputPath
            }
        }
        if ($timedOut) {
            Write-BuildLocalLogTail -Path $StandardErrorPath
            Write-BuildLocalLogTail -Path $StandardOutputPath
            throw "$FilePath did not finish within $TimeoutSeconds seconds (PID $($process.Id))."
        }
        $exitCode = [int]$process.ExitCode
        if ($exitCode -ne 0) {
            Write-BuildLocalLogTail -Path $StandardErrorPath
            Write-BuildLocalLogTail -Path $StandardOutputPath
        }
        return $exitCode
    }
    finally {
        if ($outFile) { try { $outFile.Dispose() } catch { } }
        if ($errFile) { try { $errFile.Dispose() } catch { } }
        $process.Dispose()
    }
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
        [string]$Reason = '',
        [object]$Result
    )
    if (!$Reason) { $Reason = 'no cause was recorded' }
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
        $exited = $false
        while ($deadline.Elapsed.TotalSeconds -lt 25) {
            # The PID going away is what success looks like, so it is tested
            # rather than raised: -ErrorAction Stop recorded a TerminatingError
            # in the transcript every time a shutdown actually worked.
            $running = Get-Process -Id ([int]$status.ProcessId) -ErrorAction SilentlyContinue
            if (!$running) { $exited = $true; break }
            $hasExited = $running.HasExited
            $running.Dispose()
            if ($hasExited) { $exited = $true; break }
            Start-Sleep -Milliseconds 400
        }
        if (!$exited) {
            $leftover = Get-Process -Id ([int]$status.ProcessId) -ErrorAction SilentlyContinue
            if ($leftover) {
                $stillRunning = !$leftover.HasExited
                $leftover.Dispose()
                if ($stillRunning) {
                    throw "CycleArc PID $($status.ProcessId) is still running at $($status.ExecutablePath) after shutdown. Close it from the tray. The installer was not started."
                }
            }
        }
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

function Get-SetupStateValue([string]$StateFile) {
    if (!$StateFile -or !(Test-Path -LiteralPath $StateFile -PathType Leaf)) { return $null }
    $value = @(Get-Content -LiteralPath $StateFile -TotalCount 1 -ErrorAction SilentlyContinue)
    if ($value.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$value[0])) { return $null }
    ([string]$value[0]).Trim()
}

<#
.SYNOPSIS
    Runs CycleArc-Setup.exe and waits for it correctly.
.DESCRIPTION
    The setup window waits for a person before it installs anything, and that wait is not a
    stalled build. The installer reports which of the two it is through its state file, so
    the time spent on the confirmation screen gets a person-sized budget and the installation
    itself gets a much shorter one. Without a state file - an older installer, or a stub in a
    test - the whole run falls back to the approval budget rather than being cut short.
#>
function Invoke-SetupProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [string]$StateFile,
        [int]$ApprovalTimeoutSeconds = 3600,
        [int]$InstallTimeoutSeconds = 1200
    )
    Write-Host ("    > {0} {1}" -f $FilePath, ($ArgumentList -join ' '))
    $startParameters = @{ FilePath = $FilePath; PassThru = $true }
    if ($ArgumentList.Count -gt 0) {
        $startParameters['ArgumentList'] = @($ArgumentList | ForEach-Object { ConvertTo-BuildLocalArgument $_ })
    }
    if ($WorkingDirectory) { $startParameters['WorkingDirectory'] = $WorkingDirectory }
    $process = Start-Process @startParameters
    if (!$process) { throw "Could not start $FilePath" }

    $waited = [Diagnostics.Stopwatch]::StartNew()
    $installStarted = $null
    $announced = $false
    try {
        while (!$process.WaitForExit(250)) {
            $state = Get-SetupStateValue $StateFile
            if ($state -eq 'installing' -and !$installStarted) {
                $installStarted = [Diagnostics.Stopwatch]::StartNew()
                Write-BuildLocalTiming 'install approved; installing'
            }
            elseif (!$announced -and $state -eq 'awaiting-approval') {
                $announced = $true
                Write-BuildLocalTiming 'waiting for you to approve or cancel the installer'
            }

            if ($installStarted) {
                if ($installStarted.Elapsed.TotalSeconds -ge $InstallTimeoutSeconds) {
                    try { $process.Kill($true) } catch { }
                    throw "CycleArc-Setup.exe did not finish installing within $InstallTimeoutSeconds seconds."
                }
            }
            elseif ($waited.Elapsed.TotalSeconds -ge $ApprovalTimeoutSeconds) {
                try { $process.Kill($true) } catch { }
                throw "CycleArc-Setup.exe was left unanswered for $ApprovalTimeoutSeconds seconds; nothing was installed."
            }
        }

        [int]$process.ExitCode
    }
    finally { $process.Dispose() }
}

function Get-SetupArguments {
    param(
        [Parameter(Mandatory)][string]$SetupLog,
        [string]$ExistingRoot,
        [string]$DefaultRoot,
        [switch]$Silent
    )
    # No --silent by default: running the installer has to show its confirmation screen and
    # wait for the person. Unattended runs opt in explicitly.
    $arguments = if ($Silent) { @('--silent', '--log', $SetupLog) } else { @('--log', $SetupLog) }
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
        [switch]$SilentInstall,
        [string]$ManagedRoot,
        [scriptblock]$DevRun,
        [scriptblock]$PackagedSetup,
        [scriptblock]$StopDesktop,
        [scriptblock]$RunSetup,
        [scriptblock]$StartLauncher,
        [scriptblock]$ProbeStatus
    )
    $script:BuildLocalStarted = [Diagnostics.Stopwatch]::StartNew()
    Set-BuildLocalStage 'preflight'
    $lease = $null
    $logDirectory = $null
    $logPath = $null
    $transcript = $null
    try {
        $RepoRoot = ConvertTo-InstallAbsolutePath $RepoRoot
        Assert-CycleArcTree -Root $RepoRoot
        Assert-BuildLocalTools
        # Only when this run will really package the installer. A caller that supplies its
        # own packaged Setup.exe never builds the setup UI, so it must not need the toolchain.
        if (!$PackagedSetup) { Assert-SetupUiToolchain -RepoRoot $RepoRoot | Out-Null }
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
            $devRunOut = Join-Path $logDirectory 'dev-run.out.log'
            $devRunErr = Join-Path $logDirectory 'dev-run.err.log'
            $devRunStage = Join-Path $logDirectory 'dev-run.stage'
            if (Test-Path -LiteralPath $devRunStage -PathType Leaf) { Remove-Item -LiteralPath $devRunStage -Force }
            $previousStageFile = $env:CYCLEARC_DEV_RUN_STAGE_FILE
            $env:CYCLEARC_DEV_RUN_STAGE_FILE = $devRunStage
            try {
                $exitCode = Invoke-ExternalProcess -FilePath $pwsh -ArgumentList $arguments -WorkingDirectory $RepoRoot `
                    -TimeoutSeconds 1800 -NoNewWindow -StandardOutputPath $devRunOut -StandardErrorPath $devRunErr `
                    -StreamProgress
            }
            finally {
                if ($null -eq $previousStageFile) { Remove-Item Env:CYCLEARC_DEV_RUN_STAGE_FILE -ErrorAction SilentlyContinue }
                else { $env:CYCLEARC_DEV_RUN_STAGE_FILE = $previousStageFile }
            }
            if ($exitCode -ne 0) {
                throw "dev-run.ps1 -NoLaunch failed (exit $exitCode). Setup.exe was not started; the running installation was left unchanged. Log: $logPath stdout: $devRunOut stderr: $devRunErr"
            }
            # Stage progress was printed while dev-run was running; repeating the captured
            # copy here would only duplicate it.
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
            (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CycleArc-dev/CycleArc.exe')
        )
        # An unattended run still stops the desktop itself. The interactive path must not:
        # the person approves the installation first, and the installer stops the app after
        # that, so cancelling leaves a running CycleArc exactly as it was.
        if ($SilentInstall) {
            # Only now, after a green gate and a packaged Setup.exe, is the running
            # installation touched. A failed build above leaves it running.
            Set-BuildLocalStage 'stop-desktop'
            if ($StopDesktop) { & $StopDesktop }
            else {
                Stop-VerifiedCycleArcDesktop -ProbeExecutable $stagingExe -LogDirectory $logDirectory -KnownExecutablePaths $known
            }
        }

        Set-BuildLocalStage 'install'
        $setupLog = Join-Path $logDirectory 'setup.log'
        $setupStateFile = Join-Path $logDirectory 'setup.state'
        if (Test-Path -LiteralPath $setupStateFile -PathType Leaf) { Remove-Item -LiteralPath $setupStateFile -Force }
        $setupArgs = Get-SetupArguments -SetupLog $setupLog -ExistingRoot $existingRoot -DefaultRoot $defaultRoot `
            -Silent:$SilentInstall
        $previousSetupState = $env:CYCLEARC_SETUP_STATE_FILE
        $env:CYCLEARC_SETUP_STATE_FILE = $setupStateFile
        try {
            $setupExit = if ($RunSetup) {
                & $RunSetup $setupPath $setupArgs
            }
            elseif ($SilentInstall) {
                # The installer's own directory: Setup.exe is launched detached from
                # this script's working directory, and an unnamed one is not ''.
                Invoke-WindowedProcess -FilePath $setupPath -ArgumentList $setupArgs `
                    -WorkingDirectory (Split-Path -Parent (ConvertTo-InstallAbsolutePath $setupPath)) -TimeoutSeconds 600
            }
            else {
                Invoke-SetupProcess -FilePath $setupPath -ArgumentList $setupArgs `
                    -WorkingDirectory (Split-Path -Parent (ConvertTo-InstallAbsolutePath $setupPath)) `
                    -StateFile $setupStateFile
            }
        }
        finally {
            if ($null -eq $previousSetupState) { Remove-Item Env:CYCLEARC_SETUP_STATE_FILE -ErrorAction SilentlyContinue }
            else { $env:CYCLEARC_SETUP_STATE_FILE = $previousSetupState }
        }

        # Cancelling is neither success nor failure: nothing was installed and the previous
        # installation is untouched, so this run stops here and says so.
        if (!$SilentInstall -and $setupExit -eq 2) {
            Set-BuildLocalStage 'install-cancelled' -Quiet
            Write-Host ''
            Write-Host 'Installation cancelled in the setup window.'
            Write-Host "Setup: $setupPath"
            Write-Host 'Nothing was installed and the previous installation is unchanged.'
            Write-Host "Log: $logPath"
            return [pscustomobject]@{
                Branch = $git.Branch
                Head = $git.Head
                Setup = $setupPath
                Cancelled = $true
                InstallRoot = $installRoot
                Log = $logPath
            }
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
        # The setup window owns whether the app runs: its completion page has a Run
        # checkbox. Starting it again here would ignore a person who cleared that box, and
        # requiring a readiness answer would turn their choice into a build failure.
        if (!$SilentInstall -and !$StartLauncher -and !$ProbeStatus) {
            $status = Invoke-DesktopStatus -Executable $installedExe -LogDirectory $logDirectory
            $running = $status -and [bool]$status.Succeeded
            Write-Host ''
            Write-Host 'CycleArc is installed.'
            Write-Host "Branch: $($git.Branch)"
            Write-Host "HEAD: $($git.Head)"
            Write-Host "Setup: $setupPath"
            Write-Host "Installed: $installedExe"
            Write-Host "SHA-256 match: $publishedHash"
            Write-Host ("Running: {0}" -f $(if ($running) { "yes, PID $($status.ProcessId) path $($status.ExecutablePath)" } else { 'no (not selected in the installer)' }))
            Write-Host "Log: $logPath"
            return [pscustomobject]@{
                Branch = $git.Branch
                Head = $git.Head
                Setup = $setupPath
                Installed = $installedExe
                Sha256 = $publishedHash
                InstallRoot = $installRoot
                Running = [bool]$running
                ProcessId = $(if ($running) { [int]$status.ProcessId } else { 0 })
                Log = $logPath
            }
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
    catch {
        $stage = Resolve-BuildLocalReportedStage (Get-BuildLocalStage) $logDirectory
        Set-BuildLocalStage $stage -Quiet
        Write-Host ''
        Write-Host (Write-BuildLocalFailure -Stage $stage -Message $_.Exception.Message `
            -LogDirectory $logDirectory -LogPath $logPath -Elapsed (Format-BuildLocalElapsed $script:BuildLocalStarted))
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
    try { Invoke-BuildLocal -RepoRoot $root -Fast:$Fast -NoInstall:$NoInstall -SilentInstall:$SilentInstall | Out-Null }
    catch { exit 1 }
    exit 0
}
