#requires -Version 7.0
<#
.SYNOPSIS
Runs repeated, isolated Windows idle measurements of an already published synthetic harness.
.DESCRIPTION
Build and publish CycleArc.IdleMeasure separately, then supply its executable explicitly.
This script never launches or controls the installed CycleArc app. Every child gets a new
fixture directory and process-local GC settings. Reports and logs survive failures; no
fixture, installation, user setting, or existing output directory is deleted or replaced.
.EXAMPLE
./scripts/Measure-Idle.ps1 -Executable ./artifacts/idle-measure/CycleArc.IdleMeasure.exe
.EXAMPLE
./scripts/Measure-Idle.ps1 -Executable ./artifacts/idle-measure/CycleArc.IdleMeasure.exe -Pilot
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory,
    [ValidateRange(1, 3)][int]$Trials = 3,
    [ValidateRange(5, 600)][int]$PhaseSeconds = 30,
    [ValidateRange(0, 600)][int]$WarmupSeconds = 20,
    [ValidateSet('default', 'conserve5', 'conserve7', 'concurrent-off')]
    [string[]]$Configurations = @('default', 'conserve5', 'conserve7', 'concurrent-off'),
    [switch]$Pilot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsWindows()) { throw 'Idle measurements require Windows.' }
if ($Configurations.Count -eq 0 -or @($Configurations | Select-Object -Unique).Count -ne $Configurations.Count) {
    throw 'Supply at least one configuration, with no duplicates.'
}
$Configurations = @($Configurations | ForEach-Object { $_.ToLowerInvariant() })
if ($Pilot) { $Trials = 1; $PhaseSeconds = 10; $WarmupSeconds = 5 }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executablePath = (Resolve-Path -LiteralPath $Executable).ProviderPath
$executableFile = Get-Item -LiteralPath $executablePath
# An explicit dedicated file identity is required. In particular, CycleArc.exe and its
# launcher can never become measurement targets through an inferred install directory.
if ($executableFile.PSIsContainer -or $executableFile.Name -ine 'CycleArc.IdleMeasure.exe' -or
    $executableFile.VersionInfo.OriginalFilename -notin @('CycleArc.IdleMeasure.dll', 'CycleArc.IdleMeasure.exe')) {
    throw 'Executable must be the published CycleArc.IdleMeasure.exe with matching assembly resource identity.'
}
$executableDirectory = $executableFile.DirectoryName

function Get-MeasurementFingerprint {
    $paths = @($executablePath)
    $paths += @(Get-ChildItem -LiteralPath $executableDirectory -File | Where-Object {
        $_.Name -ieq 'CycleArc.IdleMeasure.runtimeconfig.json' -or $_.Name -ilike 'CycleArc*.dll' -or
        $_.Name -in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'wpfgfx_cor3.dll')
    } | Sort-Object Name | Select-Object -ExpandProperty FullName)
    foreach ($path in $paths) {
        $file = Get-Item -LiteralPath $path
        [pscustomobject][ordered]@{
            Path = $file.FullName
            Length = $file.Length
            SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            FileVersion = if ($file.Extension -in @('.exe', '.dll')) { $file.VersionInfo.FileVersion } else { $null }
            ProductVersion = if ($file.Extension -in @('.exe', '.dll')) { $file.VersionInfo.ProductVersion } else { $null }
        }
    }
}

function Assert-MeasurementFingerprint([object[]]$Fingerprint) {
    if (($Fingerprint | ConvertTo-Json -Depth 5 -Compress) -cne $script:initialFingerprintJson) {
        throw 'Measured executable or companion assembly/runtimeconfig changed. Reports are preserved; no retry was run.'
    }
}

function Write-MeasurementJson([string]$Path, [object]$Value) {
    # All paths belong to the fresh output directory owned by this invocation. Atomic
    # replacement makes a partial parent metadata write distinguishable from a report.
    $temporary = $Path + '.writing'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $Path, $true)
}

function Read-MeasurementMarker([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop } catch { }
    }
    return $null
}

function Get-MeasurementSourceHead {
    $head = @(& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not identify the repository source HEAD.'
    }
    [string]$head[0]
}

$initialFingerprint = @(Get-MeasurementFingerprint)
$script:initialFingerprintJson = $initialFingerprint | ConvertTo-Json -Depth 5 -Compress
$sourceHead = Get-MeasurementSourceHead
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/idle-memory/run-{0}-{1}' -f
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw "OutputDirectory must be new; existing data is preserved: $outputRoot" }
$null = New-Item -ItemType Directory -Path $outputRoot
$timeoutSeconds = 7 * $PhaseSeconds + $WarmupSeconds + 120
$results = [Collections.Generic.List[object]]::new()
$runMetadata = [ordered]@{
    SchemaVersion = 1
    Status = 'running'
    Pilot = [bool]$Pilot
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    CompletedUtc = $null
    SourceHead = $sourceHead
    SourceHeadAfter = $null
    Executable = $executablePath
    FingerprintBefore = $initialFingerprint
    FingerprintAfter = $null
    OutputDirectory = $outputRoot
    Trials = $Trials
    PhaseSeconds = $PhaseSeconds
    WarmupSeconds = $WarmupSeconds
    ChildTimeoutSeconds = $timeoutSeconds
    Configurations = $Configurations
    Host = [ordered]@{
        OS = [Environment]::OSVersion.VersionString
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        OSArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        LogicalProcessors = [Environment]::ProcessorCount
    }
    Results = $results
    Failure = $null
}
$indexPath = Join-Path $outputRoot 'run.json'
Write-MeasurementJson $indexPath $runMetadata
Write-Host "Synthetic idle measurements: $outputRoot"
Write-Host "Pilot=$([bool]$Pilot), trials=$Trials, phase=${PhaseSeconds}s, warmup=${WarmupSeconds}s; no builds or automatic retries."

try {
    for ($trial = 1; $trial -le $Trials; $trial++) {
        # Reverse the second block and rotate the third, so every block does not put
        # the same setting first. Configuration labels remain the same across runs.
        $order = @($Configurations)
        if ($trial -eq 2) { [Array]::Reverse($order) }
        elseif ($trial -eq 3 -and $order.Count -gt 1) { $order = @($order[1..($order.Count - 1)]) + $order[0] }
        foreach ($configuration in $order) {
            $before = @(Get-MeasurementFingerprint)
            Assert-MeasurementFingerprint $before
            $childDirectory = Join-Path $outputRoot ('trial-{0}-{1}' -f $trial, $configuration)
            $null = New-Item -ItemType Directory -Path $childDirectory
            $fixtureDirectory = Join-Path $childDirectory 'fixture'
            $null = New-Item -ItemType Directory -Path $fixtureDirectory
            if (@(Get-ChildItem -LiteralPath $fixtureDirectory -Force).Count -ne 0) {
                throw 'Fresh measurement fixture directory is unexpectedly nonempty.'
            }
            $reportPath = Join-Path $childDirectory 'report.json'
            $startInfo = [Diagnostics.ProcessStartInfo]::new($executablePath)
            $startInfo.WorkingDirectory = $executableDirectory
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            foreach ($argument in @('--output', $reportPath, '--fixture-root', $fixtureDirectory,
                '--phase-seconds', [string]$PhaseSeconds, '--warmup-seconds', [string]$WarmupSeconds,
                '--config-label', $configuration, '--trial', [string]$trial)) {
                [void]$startInfo.ArgumentList.Add($argument)
            }
            # Only the child environment changes. Names, never unknown inherited values,
            # are recorded. This covers gcServer/gcConcurrent and hard-limit aliases.
            $removedEnvironmentKeys = @($startInfo.Environment.Keys | Where-Object {
                $_ -match '^(?:DOTNET|COMPlus)_(?:.*gc.*|Heap.*|LOH.*|SOH.*|POH.*|Gen[0-9].*|RetainVM|SegmentSize)$'
            } | Sort-Object)
            foreach ($key in $removedEnvironmentKeys) { [void]$startInfo.Environment.Remove($key) }
            $gcRequests = [ordered]@{}
            switch ($configuration) {
                'conserve5' { $gcRequests['DOTNET_GCConserveMemory'] = '5' }
                'conserve7' { $gcRequests['DOTNET_GCConserveMemory'] = '7' }
                'concurrent-off' { $gcRequests['DOTNET_gcConcurrent'] = '0' }
            }
            foreach ($entry in $gcRequests.GetEnumerator()) { $startInfo.Environment[$entry.Key] = $entry.Value }
            $childMetadata = [ordered]@{
                Trial = $trial
                Configuration = $configuration
                Pilot = [bool]$Pilot
                ProcessId = $null
                LaunchUtc = [DateTime]::UtcNow.ToString('O')
                LaunchToReadyObservedWallMilliseconds = $null
                TotalWallMilliseconds = $null
                ExitCode = $null
                TimedOut = $false
                StoppedOwnedChild = $false
                Status = 'starting'
                Report = $reportPath
                FixtureRoot = $fixtureDirectory
                RemovedInheritedGCEnvironmentKeyNames = $removedEnvironmentKeys
                GCEnvironmentRequests = $gcRequests
                ReadyMarker = $null
                FingerprintBefore = $before
                FingerprintAfter = $null
                Failure = $null
            }
            $childMetadataPath = Join-Path $childDirectory 'parent.json'
            Write-MeasurementJson $childMetadataPath $childMetadata
            $process = $null
            $stdoutFile = $null
            $stderrFile = $null
            $stdoutTask = $null
            $stderrTask = $null
            $drainCancellation = [Threading.CancellationTokenSource]::new()
            $wall = [Diagnostics.Stopwatch]::StartNew()
            $lastHeartbeatMs = 0L
            $lastPhase = ''
            try {
                # Copy both pipes to files concurrently, so a full stderr/stdout pipe
                # cannot deadlock the child and partial output survives a failed run.
                $stdoutFile = [IO.File]::Open((Join-Path $childDirectory 'stdout.log'),
                    [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                $stderrFile = [IO.File]::Open((Join-Path $childDirectory 'stderr.log'),
                    [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                $process = [Diagnostics.Process]::Start($startInfo)
                if (!$process) { throw 'The synthetic child did not start.' }
                $childMetadata.ProcessId = $process.Id
                $childMetadata.Status = 'running'
                Write-MeasurementJson $childMetadataPath $childMetadata
                $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile, 81920, $drainCancellation.Token)
                $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrFile, 81920, $drainCancellation.Token)
                Write-Host "Trial $trial/$Trials $configuration started synthetic PID $($process.Id)."
                while (!$process.WaitForExit(500)) {
                    if ($wall.Elapsed.TotalSeconds -ge $timeoutSeconds) {
                        $childMetadata.TimedOut = $true
                        throw "Synthetic PID $($process.Id) exceeded ${timeoutSeconds}s. No retry was run."
                    }
                    if ($null -eq $childMetadata.LaunchToReadyObservedWallMilliseconds) {
                        $ready = Read-MeasurementMarker ($reportPath + '.ready.json')
                        if ($null -ne $ready) {
                            $childMetadata.LaunchToReadyObservedWallMilliseconds = $wall.ElapsedMilliseconds
                            $childMetadata.ReadyMarker = $ready
                        }
                    }
                    $progress = Read-MeasurementMarker ($reportPath + '.progress.json')
                    $phase = ''
                    if ($null -ne $progress -and $null -ne $progress.PSObject.Properties['phase']) { $phase = [string]$progress.phase }
                    if ($phase -ne $lastPhase -or $wall.ElapsedMilliseconds - $lastHeartbeatMs -ge 15000) {
                        Write-Host ("Trial {0}/{1} {2}: PID {3}, phase={4}, elapsed={5:N0}s" -f
                            $trial, $Trials, $configuration, $process.Id, $phase, $wall.Elapsed.TotalSeconds)
                        $lastPhase = $phase
                        $lastHeartbeatMs = $wall.ElapsedMilliseconds
                    }
                }
                $childMetadata.ExitCode = $process.ExitCode
                if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), 5000)) {
                    throw 'Synthetic child exited but its output pipes did not close within five seconds.'
                }
                if ($process.ExitCode -ne 0) { throw "Synthetic child exited with code $($process.ExitCode)." }
                $report = Read-MeasurementMarker $reportPath
                if ($null -eq $report) { throw 'Synthetic child did not produce a valid final JSON report.' }
                if ($null -eq $childMetadata.LaunchToReadyObservedWallMilliseconds) {
                    throw 'Synthetic child did not expose an observed ready marker during its run.'
                }
                $childMetadata.Status = 'complete'
            }
            catch {
                $childMetadata.Status = 'failed'
                $childMetadata.Failure = $_.Exception.Message
                throw
            }
            finally {
                # Only the exact process started above may be stopped, and only for
                # failure/timeout cleanup. No process-name lookup or installed PID is used.
                if ($null -ne $process) {
                    try {
                        if (!$process.HasExited) {
                            $process.Kill()
                            $childMetadata.StoppedOwnedChild = $true
                            [void]$process.WaitForExit(5000)
                        }
                        if ($process.HasExited) { $childMetadata.ExitCode = $process.ExitCode }
                    } catch { }
                }
                if ($null -ne $stdoutTask -and $null -ne $stderrTask) {
                    try { [void][Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), 5000) } catch { }
                }
                $drainCancellation.Cancel()
                if ($null -ne $process) { $process.Dispose() }
                if ($null -ne $stdoutFile) { $stdoutFile.Dispose() }
                if ($null -ne $stderrFile) { $stderrFile.Dispose() }
                $drainCancellation.Dispose()
                $wall.Stop()
                $childMetadata.TotalWallMilliseconds = $wall.ElapsedMilliseconds
                try { $childMetadata.FingerprintAfter = @(Get-MeasurementFingerprint) }
                catch { $childMetadata.Failure = 'Post-run fingerprint failed: ' + $_.Exception.Message; $childMetadata.Status = 'failed' }
                Write-MeasurementJson $childMetadataPath $childMetadata
                $results.Add([pscustomobject]$childMetadata)
                Write-MeasurementJson $indexPath $runMetadata
            }
            if ($childMetadata.Status -ne 'complete') { throw $childMetadata.Failure }
            Assert-MeasurementFingerprint $childMetadata.FingerprintAfter
            Write-Host "Trial $trial/$Trials $configuration completed; report=$reportPath"
        }
    }
    $runMetadata.FingerprintAfter = @(Get-MeasurementFingerprint)
    Assert-MeasurementFingerprint $runMetadata.FingerprintAfter
    $runMetadata.SourceHeadAfter = Get-MeasurementSourceHead
    if ($runMetadata.SourceHeadAfter -cne $sourceHead) { throw 'Source HEAD changed during the measurement batch.' }
    $runMetadata.Status = 'complete'
}
catch {
    $runMetadata.Status = 'failed'
    $runMetadata.Failure = $_.Exception.Message
    throw
}
finally {
    $runMetadata.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Write-MeasurementJson $indexPath $runMetadata
    Write-Host "Measurement metadata and preserved child files: $indexPath"
}
