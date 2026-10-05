#requires -Version 7.0
<#
.SYNOPSIS
Summarizes saved synthetic idle measurement reports without launching processes.
.DESCRIPTION
Reads a complete Measure-Idle.ps1 run and its child reports. Writes summary.json and
summary.csv without overwriting existing files. Optional OutputDirectory must be new.
Quantiles use linear interpolation at (count - 1) * percentile. Comparisons pair the
same trial and phase with default; ratios are unknown when default is zero or missing.
No configuration is automatically recommended or adopted.
.EXAMPLE
./scripts/Summarize-Idle.ps1 -RunDirectory ./artifacts/idle-memory/run-20261005-example
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runRoot = (Resolve-Path -LiteralPath $RunDirectory).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!(Test-Path -LiteralPath $runRoot -PathType Container)) { throw 'RunDirectory must be a measurement directory.' }
$runPath = Join-Path $runRoot 'run.json'
$run = Get-Content -LiteralPath $runPath -Raw | ConvertFrom-Json
if ($run.SchemaVersion -ne 1 -or $run.Status -ne 'complete') {
    throw 'The measurement batch is incomplete or unsupported; no summary was written.'
}
if (@($run.Results).Count -ne [int]$run.Trials * @($run.Configurations).Count) {
    throw 'The number of saved child results does not match the requested trial/configuration matrix.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $outputRoot = $runRoot }
else {
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $outputRoot) { throw "Explicit OutputDirectory must be new: $outputRoot" }
}
$jsonOutput = Join-Path $outputRoot 'summary.json'
$csvOutput = Join-Path $outputRoot 'summary.csv'
foreach ($path in @($jsonOutput, $csvOutput)) {
    if (Test-Path -LiteralPath $path) { throw "Existing summary is preserved: $path" }
}

function Get-Quantile([double[]]$SortedValues, [double]$Fraction) {
    if ($SortedValues.Count -eq 0) { return $null }
    $index = ($SortedValues.Count - 1) * $Fraction
    $lower = [int][Math]::Floor($index)
    $upper = [int][Math]::Ceiling($index)
    $SortedValues[$lower] + ($SortedValues[$upper] - $SortedValues[$lower]) * ($index - $lower)
}

function Get-NumberStatistics([AllowNull()][object[]]$Values) {
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object {
        $number = [double]$_
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { throw 'Report contains a non-finite metric.' }
        $number
    } | Sort-Object)
    $sum = 0.0
    foreach ($number in $numbers) { $sum += $number }
    [pscustomobject][ordered]@{
        Count = $numbers.Count
        Minimum = if ($numbers.Count) { $numbers[0] } else { $null }
        Maximum = if ($numbers.Count) { $numbers[-1] } else { $null }
        Mean = if ($numbers.Count) { $sum / $numbers.Count } else { $null }
        Median = Get-Quantile $numbers 0.5
        P95 = Get-Quantile $numbers 0.95
    }
}

function Get-MetricStatistics([object[]]$Items, [string]$Property) {
    $values = @($Items | ForEach-Object {
        $member = $_.PSObject.Properties[$Property]
        if ($null -ne $member) { $member.Value }
    })
    Get-NumberStatistics $values
}

function Get-CounterDeltas([object]$Before, [object]$After) {
    $deltas = [ordered]@{}
    foreach ($property in $Before.PSObject.Properties) {
        $afterProperty = $After.PSObject.Properties[$property.Name]
        if ($null -ne $afterProperty -and $null -ne $property.Value -and $null -ne $afterProperty.Value) {
            $deltas[$property.Name] = [double]$afterProperty.Value - [double]$property.Value
        }
    }
    [pscustomobject]$deltas
}

function Format-CsvNumber([AllowNull()][object]$Value, [double]$Divisor = 1) {
    if ($null -eq $Value) { return '' }
    ([double]$Value / $Divisor).ToString('0.######', [Globalization.CultureInfo]::InvariantCulture)
}

$phaseNames = @('warmup', 'tray-idle', 'flyout-visible', 'tray-after-flyout',
    'widget-visible', 'post-refresh-widget', 'tray-after-refresh')
$metrics = @('MedianWorkingSetBytes', 'MedianPrivateWorkingSetBytes', 'MedianPrivateBytes',
    'MedianManagedAllocatedBytes', 'MedianLastGcHeapBytes', 'MedianLastGcCommittedBytes',
    'MedianLastGcFragmentedBytes', 'MedianNonGcPrivateCommitResidualEstimateBytes',
    'MedianHandleCount', 'MedianGdiObjectCount', 'MedianUserObjectCount', 'MedianThreadCount',
    'CpuMilliseconds', 'CpuOneCorePercent', 'AllocatedBytesApproxDelta',
    'Gen0CollectionsDelta', 'Gen1CollectionsDelta', 'Gen2CollectionsDelta', 'GcPauseMillisecondsDelta',
    'UiDueP50Milliseconds', 'UiDueP95Milliseconds', 'UiDueMaxMilliseconds',
    'UiQueueP50Milliseconds', 'UiQueueP95Milliseconds', 'UiQueueMaxMilliseconds')
$trialPhases = [Collections.Generic.List[object]]::new()
$trialRuns = [Collections.Generic.List[object]]::new()
$seenChildren = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($result in $run.Results) {
    if ($result.Status -ne 'complete' -or $result.ExitCode -ne 0 -or $result.TimedOut) {
        throw 'At least one child failed or timed out; no summary was written.'
    }
    $reportPath = if ([IO.Path]::IsPathRooted([string]$result.Report)) {
        [IO.Path]::GetFullPath([string]$result.Report)
    } else { [IO.Path]::GetFullPath((Join-Path $runRoot ([string]$result.Report))) }
    if (!$reportPath.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A child report points outside RunDirectory.'
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.SchemaVersion -ne 1 -or $report.Status -ne 'complete' -or
        $report.Configuration -cne $result.Configuration -or $report.Trial -ne $result.Trial -or
        $report.ProcessId -ne $result.ProcessId -or $report.PhaseSeconds -ne $run.PhaseSeconds -or
        $report.WarmupSeconds -ne $run.WarmupSeconds) {
        throw "Child report identity, duration, or completion does not match its parent: $reportPath"
    }
    $childKey = '{0}:{1}' -f $report.Trial, $report.Configuration
    if (!$seenChildren.Add($childKey) -or $report.Configuration -notin $run.Configurations -or
        $report.Trial -lt 1 -or $report.Trial -gt $run.Trials) {
        throw 'Saved reports contain a duplicate or unexpected trial/configuration.'
    }
    if (@($report.Phases).Count -ne $phaseNames.Count -or
        (@($report.Phases.Name) -join ',') -cne ($phaseNames -join ',')) {
        throw "Child report does not contain the seven expected phases: $reportPath"
    }
    $trialRuns.Add([pscustomobject][ordered]@{
        Trial = $report.Trial
        Configuration = $report.Configuration
        Report = $reportPath
        ProcessId = $report.ProcessId
        Runtime = $report.Runtime
        ProductionVersion = $report.ProductionVersion
        HarnessVersion = $report.HarnessVersion
        LaunchToReadyObservedWallMilliseconds = $result.LaunchToReadyObservedWallMilliseconds
        EntryToReadyMilliseconds = $report.EntryToReadyMilliseconds
        EntryToReadyCpuMilliseconds = $report.Ready.CpuTotalMilliseconds - $report.Entry.CpuTotalMilliseconds
        EntryToReadyAllocatedBytesApproxDelta = $report.Ready.TotalAllocatedBytesApprox - $report.Entry.TotalAllocatedBytesApprox
        RefreshMilliseconds = $report.RefreshMilliseconds
        Transitions = if ($null -ne $report.PSObject.Properties['Transitions']) { $report.Transitions } else { @() }
        RefreshCounterDeltas = Get-CounterDeltas $report.BeforeRefresh $report.AfterRefresh
        Entry = $report.Entry
        Ready = $report.Ready
        Final = $report.Final
        FinalCounters = $report.Counters
        PassiveTicks = $report.PassiveTicks
        AutomaticTicks = $report.AutomaticTicks
        DisplayTicks = $report.DisplayTicks
        Assertions = $report.Assertions
        Limitations = $report.Limitations
    })
    foreach ($phase in $report.Phases) {
        $wallMilliseconds = 1000 * ($phase.End.ElapsedSeconds - $phase.Start.ElapsedSeconds)
        $cpuMilliseconds = $phase.End.CpuTotalMilliseconds - $phase.Start.CpuTotalMilliseconds
        $allocatedDelta = $phase.End.TotalAllocatedBytesApprox - $phase.Start.TotalAllocatedBytesApprox
        $pauseDelta = $phase.End.TotalGcPauseMilliseconds - $phase.Start.TotalGcPauseMilliseconds
        if ($wallMilliseconds -lt 0 -or $cpuMilliseconds -lt 0 -or $allocatedDelta -lt 0 -or $pauseDelta -lt 0) {
            throw 'A cumulative process metric decreased between phase boundaries.'
        }
        # Exclude observations crossing a phase boundary even if their phase label
        # was captured just before the transition. Endpoints remain separately saved.
        $samples = @($report.Samples | Where-Object {
            $_.Phase -ceq $phase.Name -and $_.ElapsedSeconds -ge $phase.Start.ElapsedSeconds -and
            $_.ElapsedSeconds -le $phase.End.ElapsedSeconds
        })
        $probes = @($report.UiProbes | Where-Object {
            $_.Phase -ceq $phase.Name -and $_.QueuedMilliseconds -ge $phase.Start.ElapsedSeconds * 1000 -and
            $_.ExecutedMilliseconds -le $phase.End.ElapsedSeconds * 1000
        })
        if ($phase.Name -ne 'warmup' -and ($samples.Count -eq 0 -or $probes.Count -eq 0)) {
            throw "Steady phase has no process samples or UI probes: $childKey / $($phase.Name)"
        }
        $due = Get-MetricStatistics $probes 'DueToExecutionMilliseconds'
        $queue = Get-MetricStatistics $probes 'QueueToExecutionMilliseconds'
        $native = @($report.VirtualMemoryInventories | Where-Object { $_.Phase -ceq $phase.Name })
        $row = [ordered]@{
            Trial = $report.Trial
            Configuration = $report.Configuration
            Phase = $phase.Name
            SampleCount = $samples.Count
            UiProbeCount = $probes.Count
            LastGcSnapshotSampleCount = @($samples | Where-Object { $_.HasGcSnapshot }).Count
            DurationMilliseconds = $wallMilliseconds
            CpuMilliseconds = $cpuMilliseconds
            CpuOneCorePercent = if ($wallMilliseconds -gt 0) { 100 * $cpuMilliseconds / $wallMilliseconds } else { $null }
            AllocatedBytesApproxDelta = $allocatedDelta
            Gen0CollectionsDelta = $phase.End.Gen0Collections - $phase.Start.Gen0Collections
            Gen1CollectionsDelta = $phase.End.Gen1Collections - $phase.Start.Gen1Collections
            Gen2CollectionsDelta = $phase.End.Gen2Collections - $phase.Start.Gen2Collections
            GcPauseMillisecondsDelta = $pauseDelta
            UiDueP50Milliseconds = $due.Median
            UiDueP95Milliseconds = $due.P95
            UiDueMaxMilliseconds = $due.Maximum
            UiQueueP50Milliseconds = $queue.Median
            UiQueueP95Milliseconds = $queue.P95
            UiQueueMaxMilliseconds = $queue.Maximum
            FixtureCounterDeltas = Get-CounterDeltas $phase.CountersBefore $phase.CountersAfter
            NativeVirtualMemoryInventory = if ($native.Count -eq 1) { $native[0] } else { $null }
            Start = $phase.Start
            End = $phase.End
        }
        foreach ($sampleMetric in @('WorkingSetBytes', 'PrivateWorkingSetBytes', 'PrivateBytes',
            'ManagedAllocatedBytes', 'LastGcHeapBytes', 'LastGcCommittedBytes', 'LastGcFragmentedBytes',
            'NonGcPrivateCommitResidualEstimateBytes', 'HandleCount', 'GdiObjectCount', 'UserObjectCount', 'ThreadCount')) {
            $row['Median' + $sampleMetric] = (Get-MetricStatistics $samples $sampleMetric).Median
        }
        $trialPhases.Add([pscustomobject]$row)
    }
}

$aggregates = [Collections.Generic.List[object]]::new()
$paired = [Collections.Generic.List[object]]::new()
$pairedAggregates = [Collections.Generic.List[object]]::new()
foreach ($configuration in $run.Configurations) {
    foreach ($phaseName in $phaseNames) {
        $rows = @($trialPhases | Where-Object { $_.Configuration -ceq $configuration -and $_.Phase -ceq $phaseName })
        $aggregateMetrics = [ordered]@{}
        foreach ($metric in $metrics) { $aggregateMetrics[$metric] = Get-MetricStatistics $rows $metric }
        $aggregates.Add([pscustomobject][ordered]@{
            Configuration = $configuration; Phase = $phaseName; Trials = $rows.Count; Metrics = $aggregateMetrics
        })
        if ($configuration -ceq 'default' -or 'default' -notin $run.Configurations) { continue }
        $phasePairs = [Collections.Generic.List[object]]::new()
        foreach ($row in $rows) {
            $baseline = @($trialPhases | Where-Object {
                $_.Configuration -ceq 'default' -and $_.Phase -ceq $phaseName -and $_.Trial -eq $row.Trial
            })
            if ($baseline.Count -ne 1) { throw 'A default comparison has no unique same-trial/same-phase baseline.' }
            $pairedMetrics = [ordered]@{}
            foreach ($metric in $metrics) {
                $defaultValue = $baseline[0].PSObject.Properties[$metric].Value
                $configurationValue = $row.PSObject.Properties[$metric].Value
                $available = $null -ne $defaultValue -and $null -ne $configurationValue
                $ratioAvailable = $available -and [double]$defaultValue -ne 0
                $pairedMetrics[$metric] = [pscustomobject][ordered]@{
                    DefaultValue = $defaultValue
                    ConfigurationValue = $configurationValue
                    Delta = if ($available) { [double]$configurationValue - [double]$defaultValue } else { $null }
                    Ratio = if ($ratioAvailable) { [double]$configurationValue / [double]$defaultValue } else { $null }
                    PercentChange = if ($ratioAvailable) { 100 * ([double]$configurationValue / [double]$defaultValue - 1) } else { $null }
                }
            }
            $pair = [pscustomobject][ordered]@{
                Configuration = $configuration; Trial = $row.Trial; Phase = $phaseName; Metrics = $pairedMetrics
            }
            $phasePairs.Add($pair)
            $paired.Add($pair)
        }
        $comparisonMetrics = [ordered]@{}
        foreach ($metric in $metrics) {
            $comparisonMetrics[$metric] = [pscustomobject][ordered]@{
                Delta = Get-NumberStatistics @($phasePairs | ForEach-Object { $_.Metrics[$metric].Delta })
                Ratio = Get-NumberStatistics @($phasePairs | ForEach-Object { $_.Metrics[$metric].Ratio })
                PercentChange = Get-NumberStatistics @($phasePairs | ForEach-Object { $_.Metrics[$metric].PercentChange })
            }
        }
        $pairedAggregates.Add([pscustomobject][ordered]@{
            Configuration = $configuration; Phase = $phaseName; Pairs = $phasePairs.Count; Metrics = $comparisonMetrics
        })
    }
}

$summary = [ordered]@{
    SchemaVersion = 1
    Status = 'complete'
    SummarizedUtc = [DateTime]::UtcNow.ToString('O')
    RunDirectory = $runRoot
    SourceHead = $run.SourceHead
    Pilot = [bool]$run.Pilot
    Trials = $run.Trials
    PhaseSeconds = $run.PhaseSeconds
    WarmupSeconds = $run.WarmupSeconds
    Configurations = $run.Configurations
    FingerprintBefore = $run.FingerprintBefore
    FingerprintAfter = $run.FingerprintAfter
    Notes = @(
        'Memory medians use within-phase periodic samples; raw boundary/startup/final snapshots remain separate.'
        'Cross-trial statistics summarize each trial phase metric; UI percentiles are not pooled across trials.'
        'CPU one-core percent is delta process CPU / actual phase wall duration * 100; it is not divided by host CPU count.'
        'ManagedAllocatedBytes is GC.GetTotalMemory(false), not retained live objects; last-GC sizes can be stale.'
        'Missing GC snapshots and missing counters remain null; zero default values produce null ratios.'
        'Native virtual-memory allocation classes and non-GC private-commit residuals do not attribute native heap ownership.'
        'UI due/queue latency measures scheduling/dispatcher response, not click-to-pixel delay.'
        'Paired differences and ratios are data summaries, not an automatic adoption or statistical-significance decision.'
        $(if ($run.Pilot) { 'Pilot: short diagnostic run; do not treat it as the repeated full comparison.' }
          else { 'Full requested trial matrix completed.' })
        $(if ('default' -notin $run.Configurations) { 'Default configuration was omitted; paired comparisons are unavailable.' }
          else { 'Comparisons pair each configuration with default within the same trial and phase.' })
    )
    Runs = $trialRuns
    TrialPhases = $trialPhases
    ConfigurationPhaseAggregates = $aggregates
    PairedComparisons = $paired
    PairedComparisonAggregates = $pairedAggregates
}

if (!(Test-Path -LiteralPath $outputRoot)) { $null = New-Item -ItemType Directory -Path $outputRoot }
$temporarySuffix = '.writing-' + [Guid]::NewGuid().ToString('N')
$jsonTemporary = $jsonOutput + $temporarySuffix
$csvTemporary = $csvOutput + $temporarySuffix
[IO.File]::WriteAllText($jsonTemporary, ($summary | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
$trialPhases | ForEach-Object {
    $row = $_
    $csvRow = [ordered]@{ Configuration = $row.Configuration; Trial = $row.Trial; Phase = $row.Phase }
    foreach ($name in @('SampleCount', 'UiProbeCount', 'LastGcSnapshotSampleCount')) { $csvRow[$name] = $row.PSObject.Properties[$name].Value }
    foreach ($name in @('WorkingSetBytes', 'PrivateWorkingSetBytes', 'PrivateBytes', 'ManagedAllocatedBytes',
        'LastGcHeapBytes', 'LastGcCommittedBytes', 'LastGcFragmentedBytes', 'NonGcPrivateCommitResidualEstimateBytes')) {
        $csvRow[$name.Replace('Bytes', '') + 'MedianMiB'] = Format-CsvNumber $row.PSObject.Properties['Median' + $name].Value 1048576
    }
    foreach ($name in @('HandleCount', 'GdiObjectCount', 'UserObjectCount', 'ThreadCount')) {
        $csvRow[$name + 'Median'] = Format-CsvNumber $row.PSObject.Properties['Median' + $name].Value
    }
    foreach ($name in @('DurationMilliseconds', 'CpuMilliseconds', 'CpuOneCorePercent', 'AllocatedBytesApproxDelta',
        'Gen0CollectionsDelta', 'Gen1CollectionsDelta', 'Gen2CollectionsDelta', 'GcPauseMillisecondsDelta',
        'UiDueP50Milliseconds', 'UiDueP95Milliseconds', 'UiDueMaxMilliseconds',
        'UiQueueP50Milliseconds', 'UiQueueP95Milliseconds', 'UiQueueMaxMilliseconds')) {
        $csvRow[$name] = Format-CsvNumber $row.PSObject.Properties[$name].Value
    }
    [pscustomobject]$csvRow
} | Export-Csv -LiteralPath $csvTemporary -NoTypeInformation -Encoding utf8NoBOM
# Move without overwrite: even a race creating a final path preserves that file.
[IO.File]::Move($jsonTemporary, $jsonOutput)
[IO.File]::Move($csvTemporary, $csvOutput)
Write-Host "Saved $($trialPhases.Count) trial/phase summaries: $jsonOutput"
Write-Host "Readable per-trial metrics: $csvOutput"
