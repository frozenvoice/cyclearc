#requires -Version 7.0
<#
.SYNOPSIS
Compares six saved, complete default-GC idle runs without launching measurement processes.
.DESCRIPTION
Supply directories in optimized, fixed, fixed, optimized, optimized, fixed order.
Each must contain one default-GC trial with 30-second phases and a 20-second warmup.
All six runs must share the known seven-phase legacy or nine-phase current matrix.
All observations remain in the output, including unequal workloads. This script makes
no causality or regression decision. Old schema-1 reports without diagnostics remain
readable, with unavailable metrics represented as null. Source reports/logs are read
only. OutputDirectory must be new. Quantiles interpolate at (count - 1) * percentile.
.EXAMPLE
./scripts/Compare-IdleCpu.ps1 -RunDirectories $sixOrderedDirectories -OutputDirectory ./artifacts/idle-cpu-comparison
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateCount(6, 6)][string[]]$RunDirectories,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw "OutputDirectory must be new: $outputRoot" }

function Get-Value([AllowNull()][object]$Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Get-Delta([AllowNull()][object]$Before, [AllowNull()][object]$After, [string]$Name) {
    $start = Get-Value $Before $Name
    $end = Get-Value $After $Name
    if ($null -eq $start -or $null -eq $end) { return $null }
    $delta = [double]$end - [double]$start
    if ($delta -lt 0) { throw "Cumulative metric decreased: $Name" }
    return $delta
}
function Get-Statistics([AllowNull()][object[]]$Values) {
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object {
        $number = [double]$_
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { throw 'Non-finite metric.' }
        $number
    } | Sort-Object)
    $quantiles = @{}
    foreach ($entry in @{ Median = 0.5; P95 = 0.95 }.GetEnumerator()) {
        $value = $null
        if ($numbers.Count) {
            $index = ($numbers.Count - 1) * $entry.Value
            $lower = [int][Math]::Floor($index)
            $upper = [int][Math]::Ceiling($index)
            $value = $numbers[$lower] + ($numbers[$upper] - $numbers[$lower]) * ($index - $lower)
        }
        $quantiles[$entry.Key] = $value
    }
    [pscustomobject]@{
        Count = $numbers.Count; Median = $quantiles.Median; P95 = $quantiles.P95
        Minimum = if ($numbers.Count) { $numbers[0] } else { $null }
        Maximum = if ($numbers.Count) { $numbers[-1] } else { $null }
    }
}
function Get-MetricStatistics([object[]]$Items, [string]$Name) {
    Get-Statistics @($Items | ForEach-Object { Get-Value $_ $Name })
}
function Get-CounterDeltas([AllowNull()][object]$Before, [AllowNull()][object]$After) {
    $deltas = [ordered]@{}
    if ($null -ne $Before -and $null -ne $After) {
        foreach ($property in $Before.PSObject.Properties) {
            $afterValue = Get-Value $After $property.Name
            if ($null -ne $property.Value -and $null -ne $afterValue) {
                $deltas[$property.Name] = [double]$afterValue - [double]$property.Value
            }
        }
    }
    [pscustomobject]$deltas
}
function Get-CounterEquivalence([AllowNull()][object]$Optimized, [AllowNull()][object]$Fixed) {
    $names = @($activeCounters)
    if ($null -ne $Optimized) { $names += @($Optimized.PSObject.Properties.Name) }
    if ($null -ne $Fixed) { $names += @($Fixed.PSObject.Properties.Name) }
    $values = [ordered]@{}
    foreach ($name in @($names | Sort-Object -Unique)) {
        $left = Get-Value $Optimized $name
        $right = Get-Value $Fixed $name
        $values[$name] = [pscustomobject]@{
            Optimized = $left; Fixed = $right
            Equal = if ($null -eq $left -or $null -eq $right) { $null } else { $left -eq $right }
        }
    }
    [pscustomobject]$values
}
function Get-Pair([object]$Optimized, [object]$Fixed, [int]$Pair, [string]$Phase) {
    $before = Get-CounterEquivalence $Optimized.CountersBefore $Fixed.CountersBefore
    $after = Get-CounterEquivalence $Optimized.CountersAfter $Fixed.CountersAfter
    $reasons = @(
        foreach ($boundary in @('Before', 'After')) {
            $counts = if ($boundary -eq 'Before') { $before } else { $after }
            foreach ($counter in $activeCounters) {
                if ((Get-Value $counts $counter).Equal -ne $true) { "Active counter unequal or unavailable at ${boundary}: $counter" }
            }
        }
    )
    $pairedMetrics = [ordered]@{}
    foreach ($metric in $metrics) {
        $left = Get-Value $Optimized $metric
        $right = Get-Value $Fixed $metric
        $available = $null -ne $left -and $null -ne $right
        $ratioAvailable = $available -and [double]$left -ne 0
        $pairedMetrics[$metric] = [pscustomobject]@{
            Optimized = $left; Fixed = $right
            Delta = if ($available) { [double]$right - [double]$left } else { $null }
            Ratio = if ($ratioAvailable) { [double]$right / [double]$left } else { $null }
            PercentChange = if ($ratioAvailable) { 100 * ([double]$right / [double]$left - 1) } else { $null }
        }
    }
    [pscustomobject][ordered]@{
        Pair = $Pair; Phase = $Phase; OptimizedOrder = $Optimized.Order; FixedOrder = $Fixed.Order
        ComparisonEligible = $reasons.Count -eq 0; ComparisonReasons = $reasons
        CounterEquivalenceBefore = $before; CounterEquivalenceAfter = $after
        PassiveTicksEqual = if ($Phase -eq 'whole-run') {
            if ($null -ne $Optimized.PassiveTicks -and $null -ne $Fixed.PassiveTicks) { $Optimized.PassiveTicks -eq $Fixed.PassiveTicks } else { $null }
        } else { $null }
        Metrics = [pscustomobject]$pairedMetrics
    }
}
function Write-Csv([object[]]$Rows, [string]$Name) {
    $Rows | ForEach-Object {
        $flat = [ordered]@{}
        foreach ($property in $_.PSObject.Properties) {
            $value = $property.Value
            $flat[$property.Name] = if ($null -eq $value) { '' }
                elseif ($value -is [double] -or $value -is [float] -or $value -is [decimal]) { $value.ToString([Globalization.CultureInfo]::InvariantCulture) }
                elseif ($value -is [ValueType] -or $value -is [string]) { $value }
                else { ConvertTo-Json -InputObject $value -Depth 20 -Compress }
        }
        [pscustomobject]$flat
    } | Export-Csv -LiteralPath (Join-Path $outputRoot $Name) -NoTypeInformation -Encoding utf8NoBOM
}

$labels = @('optimized', 'fixed', 'fixed', 'optimized', 'optimized', 'fixed')
$ninePhaseNames = @('warmup', 'tray-idle', 'widget-before-flyout', 'flyout-visible', 'tray-after-flyout',
    'widget-visible', 'post-refresh-widget', 'tray-after-refresh', 'widget-after-churn')
$sevenPhaseNames = @('warmup', 'tray-idle', 'flyout-visible', 'tray-after-flyout',
    'widget-visible', 'post-refresh-widget', 'tray-after-refresh')
$phaseNames = $null
$activeCounters = @('CodexStarts', 'CodexQuotaRequests', 'ClaudeLiveRequests', 'CursorUsageRequests', 'CursorHttpRequests')
$metrics = @('ProcessCyclesDelta', 'UiThreadCyclesDelta', 'RefreshSnapshotCallsDelta', 'CpuMilliseconds',
    'CpuOneCorePercent', 'UiDueP95Milliseconds', 'UiDueMaxMilliseconds', 'UiQueueP95Milliseconds',
    'UiQueueMaxMilliseconds', 'MedianPrivateWorkingSetBytes', 'MedianPrivateBytes',
    'AllocatedBytesApproxDelta', 'HandleCountStart', 'HandleCountEnd')
$roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$phases = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$transitions = [Collections.Generic.List[object]]::new()
$sources = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt 6; $index++) {
    $root = (Resolve-Path -LiteralPath $RunDirectories[$index]).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (!$roots.Add($root)) { throw 'Run directories must be distinct; repeated observations cannot substitute for runs.' }
    $run = Get-Content -LiteralPath (Join-Path $root 'run.json') -Raw | ConvertFrom-Json
    if ($run.SchemaVersion -ne 1 -or $run.Status -ne 'complete' -or $run.Trials -ne 1 -or
        @($run.Configurations).Count -ne 1 -or $run.Configurations[0] -cne 'default' -or
        $run.PhaseSeconds -ne 30 -or $run.WarmupSeconds -ne 20 -or $run.Pilot -or @($run.Results).Count -ne 1) {
        throw "Expected one complete default-GC trial, phase30/warmup20: $root"
    }
    $result = $run.Results[0]
    if ($result.Status -ne 'complete' -or $result.ExitCode -ne 0 -or $result.TimedOut) { throw "Failed child: $root" }
    $reportPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($result.Report)) { $result.Report } else { Join-Path $root $result.Report }))
    if (!$reportPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Child report escapes its run directory.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.SchemaVersion -ne 1 -or $report.Status -ne 'complete' -or $report.Trial -ne 1 -or
        $report.Configuration -cne 'default' -or $report.ProcessId -ne $result.ProcessId -or
        $result.Trial -ne 1 -or $result.Configuration -cne 'default' -or
        $report.PhaseSeconds -ne 30 -or $report.WarmupSeconds -ne 20) { throw "Child identity mismatch: $reportPath" }
    $matrix = @($report.Phases.Name) -join ','
    if ($matrix -cne ($ninePhaseNames -join ',') -and $matrix -cne ($sevenPhaseNames -join ',')) {
        throw "Unknown child phase matrix: $reportPath"
    }
    if ($null -eq $phaseNames) { $phaseNames = @($report.Phases.Name) }
    elseif ($matrix -cne ($phaseNames -join ',')) { throw 'All six runs must use the same known phase matrix.' }
    $order = $index + 1
    $label = $labels[$index]
    $sources.Add([pscustomobject]@{
        Order = $order; Artifact = $label; RunDirectory = $root; Report = $reportPath
        ReportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
        Parent = $run
        Logs = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.log' | Select-Object -ExpandProperty FullName)
    })
    foreach ($phase in $report.Phases) {
        $duration = 1000 * (Get-Delta $phase.Start $phase.End 'ElapsedSeconds')
        $cpu = Get-Delta $phase.Start $phase.End 'CpuTotalMilliseconds'
        $samples = @($report.Samples | Where-Object {
            $_.Phase -ceq $phase.Name -and $_.ElapsedSeconds -ge $phase.Start.ElapsedSeconds -and $_.ElapsedSeconds -le $phase.End.ElapsedSeconds
        })
        $probes = @($report.UiProbes | Where-Object {
            $_.Phase -ceq $phase.Name -and $_.QueuedMilliseconds -ge $phase.Start.ElapsedSeconds * 1000 -and $_.ExecutedMilliseconds -le $phase.End.ElapsedSeconds * 1000
        })
        if ($phase.Name -ne 'warmup' -and (!$samples.Count -or !$probes.Count)) { throw "Missing steady phase samples/probes: $reportPath / $($phase.Name)" }
        $before = Get-Value $phase 'DiagnosticsBefore'
        $after = Get-Value $phase 'DiagnosticsAfter'
        $due = Get-MetricStatistics $probes 'DueToExecutionMilliseconds'
        $queue = Get-MetricStatistics $probes 'QueueToExecutionMilliseconds'
        $phases.Add([pscustomobject][ordered]@{
            Order = $order; Artifact = $label; Phase = $phase.Name; SampleCount = $samples.Count; UiProbeCount = $probes.Count
            DurationMilliseconds = $duration; CpuMilliseconds = $cpu
            CpuOneCorePercent = if ($duration -gt 0 -and $null -ne $cpu) { 100 * $cpu / $duration } else { $null }
            ProcessCyclesDelta = Get-Delta $before $after 'ProcessCycles'
            UiThreadCyclesDelta = Get-Delta $before $after 'UiThreadCycles'
            RefreshSnapshotCallsDelta = Get-Delta $before $after 'RefreshSnapshotCalls'
            UiDueP95Milliseconds = $due.P95; UiDueMaxMilliseconds = $due.Maximum
            UiQueueP95Milliseconds = $queue.P95; UiQueueMaxMilliseconds = $queue.Maximum
            MedianPrivateWorkingSetBytes = (Get-MetricStatistics $samples 'PrivateWorkingSetBytes').Median
            MedianPrivateBytes = (Get-MetricStatistics $samples 'PrivateBytes').Median
            AllocatedBytesApproxDelta = Get-Delta $phase.Start $phase.End 'TotalAllocatedBytesApprox'
            HandleCountStart = Get-Value $phase.Start 'HandleCount'; HandleCountEnd = Get-Value $phase.End 'HandleCount'
            HandlesBefore = Get-Value $before 'Handles'; HandlesAfter = Get-Value $after 'Handles'
            CountersBefore = $phase.CountersBefore; CountersAfter = $phase.CountersAfter
            CounterDeltas = Get-CounterDeltas $phase.CountersBefore $phase.CountersAfter
            UiBefore = Get-Value $phase 'UiBefore'; UiAfter = Get-Value $phase 'UiAfter'
            ThreadCpuMillisecondsDiagnostic = Get-Value $phase 'ThreadCpuMilliseconds'
            Start = $phase.Start; End = $phase.End
        })
    }
    $wholeDuration = 1000 * (Get-Delta $report.Entry $report.Final 'ElapsedSeconds')
    $wholeCpu = Get-Delta $report.Entry $report.Final 'CpuTotalMilliseconds'
    $runs.Add([pscustomobject][ordered]@{
        Order = $order; Artifact = $label; Phase = 'whole-run'; Report = $reportPath
        ProductionVersion = $report.ProductionVersion; HarnessVersion = $report.HarnessVersion; Runtime = $report.Runtime
        DurationMilliseconds = $wholeDuration; CpuMilliseconds = $wholeCpu
        CpuOneCorePercent = if ($wholeDuration -gt 0 -and $null -ne $wholeCpu) { 100 * $wholeCpu / $wholeDuration } else { $null }
        AllocatedBytesApproxDelta = Get-Delta $report.Entry $report.Final 'TotalAllocatedBytesApprox'
        HandleCountStart = Get-Value $report.Entry 'HandleCount'; HandleCountEnd = Get-Value $report.Final 'HandleCount'
        CountersBefore = $report.Phases[0].CountersBefore; CountersAfter = $report.Counters
        PassiveTicks = $report.PassiveTicks; AutomaticTicks = $report.AutomaticTicks; DisplayTicks = $report.DisplayTicks
        Entry = $report.Entry; Ready = $report.Ready; Final = $report.Final
        Assertions = $report.Assertions; Limitations = $report.Limitations
    })
    foreach ($transition in @(Get-Value $report 'Transitions')) {
        if ($null -eq $transition) { continue }
        $transitions.Add([pscustomobject]@{ Order = $order; Artifact = $label; Transition = $transition.Name
            StartMilliseconds = $transition.StartMilliseconds; SynchronousActionMilliseconds = $transition.SynchronousActionMilliseconds
            UntilDispatcherIdleMilliseconds = $transition.UntilDispatcherIdleMilliseconds; UiCreated = $transition.UiCreated })
    }
}
$pairs = [Collections.Generic.List[object]]::new()
for ($pairNumber = 1; $pairNumber -le 3; $pairNumber++) {
    $orders = @((2 * $pairNumber - 1), (2 * $pairNumber))
    foreach ($phaseName in @($phaseNames) + 'whole-run') {
        $rows = if ($phaseName -eq 'whole-run') { @($runs | Where-Object { $_.Order -in $orders }) }
            else { @($phases | Where-Object { $_.Order -in $orders -and $_.Phase -ceq $phaseName }) }
        $optimized = @($rows | Where-Object { $_.Artifact -ceq 'optimized' })[0]
        $fixed = @($rows | Where-Object { $_.Artifact -ceq 'fixed' })[0]
        $pairs.Add((Get-Pair $optimized $fixed $pairNumber $phaseName))
    }
}
$aggregates = @(
    foreach ($phaseName in @($phaseNames) + 'whole-run') {
        $eligible = @($pairs | Where-Object { $_.Phase -ceq $phaseName -and $_.ComparisonEligible })
        $statistics = [ordered]@{}
        foreach ($metric in $metrics) {
            $statistics[$metric] = Get-Statistics @($eligible | ForEach-Object { (Get-Value $_.Metrics $metric).Delta })
        }
        [pscustomobject]@{ Phase = $phaseName; RequestedPairs = 3; EligiblePairs = $eligible.Count; DeltaStatistics = [pscustomobject]$statistics }
    }
)
$transitionSummary = @(
    foreach ($name in @($transitions | ForEach-Object { $_.Transition } | Sort-Object -Unique)) {
        foreach ($label in @('optimized', 'fixed')) {
            $rows = @($transitions | Where-Object { $_.Transition -ceq $name -and $_.Artifact -ceq $label })
            [pscustomobject]@{ Artifact = $label; Transition = $name; Observations = $rows.Count
                SynchronousActionMilliseconds = Get-MetricStatistics $rows 'SynchronousActionMilliseconds'
                UntilDispatcherIdleMilliseconds = Get-MetricStatistics $rows 'UntilDispatcherIdleMilliseconds' }
        }
    }
)
$summary = [ordered]@{
    SchemaVersion = 1; Status = 'complete'; ComparedUtc = [DateTime]::UtcNow.ToString('O')
    Order = $labels; PhaseNames = $phaseNames; Sources = $sources; ActiveRequestCounterNames = $activeCounters
    Notes = @('All six source runs and all pairs are retained; inputs and logs are unchanged.'
        'Paired deltas are fixed minus optimized. No causality, significance or regression decision is made.'
        'Eligible aggregates require matching known active counters at both phase boundaries; unequal or unavailable pairs remain in Pairs.'
        'History and other counters are compared at both boundaries; passive/automatic/display ticks are whole-run observations only.'
        'CPU one-core percent uses sampler CPU milliseconds / actual wall milliseconds * 100, without dividing by processor count.'
        'Process/UI cycle deltas are reported only when saved diagnostics exist. Old report cycle boundaries may include inventory cost.'
        'Per-thread CPU snapshots are auxiliary diagnostics with a different inventory interval, not precise cycle-boundary evidence.'
        'Whole-run CPU/allocation covers Entry to Final, including transitions and boundary inventory. Cycle totals are not inferred for gaps.'
        'Memory medians use within-phase samples. UI percentiles interpolate at (count - 1) * percentile; phases are never pooled.')
    Runs = $runs; Phases = $phases; Pairs = $pairs; EligiblePairAggregates = $aggregates
    Transitions = $transitions; TransitionSummary = $transitionSummary
}
$null = New-Item -ItemType Directory -Path $outputRoot
[IO.File]::WriteAllText((Join-Path $outputRoot 'comparison.json'), ($summary | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Write-Csv $phases.ToArray() 'phases.csv'
$pairCsv = @($pairs | ForEach-Object {
    $row = [ordered]@{ Pair = $_.Pair; Phase = $_.Phase; OptimizedOrder = $_.OptimizedOrder; FixedOrder = $_.FixedOrder
        ComparisonEligible = $_.ComparisonEligible; ComparisonReasons = $_.ComparisonReasons
        CounterEquivalenceBefore = $_.CounterEquivalenceBefore; CounterEquivalenceAfter = $_.CounterEquivalenceAfter
        PassiveTicksEqual = $_.PassiveTicksEqual }
    foreach ($metric in $metrics) {
        $values = Get-Value $_.Metrics $metric
        foreach ($statistic in @('Optimized', 'Fixed', 'Delta', 'Ratio', 'PercentChange')) {
            $row[$metric + $statistic] = Get-Value $values $statistic
        }
    }
    [pscustomobject]$row
})
Write-Csv $pairCsv 'pairs.csv'
Write-Csv $runs.ToArray() 'runs.csv'
Write-Csv $transitions.ToArray() 'transitions.csv'
Write-Host "Saved all six runs, $($phases.Count) phases and $($pairs.Count) paired observations: $outputRoot"
