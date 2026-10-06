#requires -Version 7.0
# Deterministic saved-report fixtures only: no clocks, app process, build or measurement.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-idle-comparison-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$compare = Join-Path $repoRoot 'scripts/Compare-IdleCpu.ps1'
$summarize = Join-Path $repoRoot 'scripts/Summarize-Idle.ps1'
$phaseNames = @('warmup', 'tray-idle', 'widget-before-flyout', 'flyout-visible', 'tray-after-flyout',
    'widget-visible', 'post-refresh-widget', 'tray-after-refresh', 'widget-after-churn')
$assertions = 0
function Assert-Fixture([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "Idle CPU comparison regression: $Message" }
    $script:assertions++
}
function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}
function Get-Counters([int]$Index) {
    [pscustomobject]@{ CodexStarts = 1; CodexQuotaRequests = 2; ClaudeLiveRequests = 2
        CursorUsageRequests = 2; CursorHttpRequests = 2; ClaudeAuthCalls = 0
        ClaudeHistoryReadCalls = $Index; Changed = $Index; SyntheticRequestMilliseconds = 10 }
}
function Get-Sample([double]$Seconds, [string]$Phase = '', [double]$Private = 100) {
    [pscustomobject]@{ Phase = $Phase; ElapsedSeconds = $Seconds; CpuTotalMilliseconds = 2 * $Seconds
        TotalAllocatedBytesApprox = 1000 * $Seconds; TotalGcPauseMilliseconds = 0
        Gen0Collections = 0; Gen1Collections = 0; Gen2Collections = 0; HasGcSnapshot = $false
        WorkingSetBytes = 200; PrivateWorkingSetBytes = $Private; PrivateBytes = 300 + $Private
        ManagedAllocatedBytes = 30; LastGcHeapBytes = $null; LastGcCommittedBytes = $null
        LastGcFragmentedBytes = $null; NonGcPrivateCommitResidualEstimateBytes = $null
        HandleCount = 20; GdiObjectCount = 2; UserObjectCount = 3; ThreadCount = 4 }
}
function New-Run([string]$Name, [bool]$Diagnostics = $true, [bool]$Transitions = $true, [string[]]$Names = $phaseNames) {
    $root = Join-Path $testRoot $Name
    $null = New-Item -ItemType Directory -Path $root
    $phases = @()
    $samples = @()
    $probes = @()
    for ($index = 0; $index -lt $Names.Count; $index++) {
        $phaseName = $Names[$index]
        $seconds = $index * 10
        $phase = [ordered]@{ Name = $phaseName; Start = Get-Sample $seconds; End = Get-Sample ($seconds + 10)
            CountersBefore = Get-Counters $index; CountersAfter = Get-Counters ($index + 1)
            UiBefore = @{ Row = 1 }; UiAfter = @{ Row = 1 } }
        if ($Diagnostics) {
            $phase.DiagnosticsBefore = @{ ProcessCycles = 100; UiThreadCycles = 10; RefreshSnapshotCalls = 2; Handles = @{ Total = 20; ByType = @{ Event = 10; Thread = 10 } } }
            $phase.DiagnosticsAfter = @{ ProcessCycles = 140; UiThreadCycles = 15; RefreshSnapshotCalls = 3; Handles = @{ Total = 22; ByType = @{ Event = 11; Thread = 11 } } }
            $phase.ThreadCpuMilliseconds = @{ ui = 1; other = 2 }
        }
        $phases += [pscustomobject]$phase
        for ($sampleIndex = 0; $sampleIndex -lt 3; $sampleIndex++) {
            $time = $seconds + $sampleIndex + 1
            $samples += Get-Sample $time $phaseName (100 + 100 * $sampleIndex)
            $probes += [pscustomobject]@{ Phase = $phaseName; QueuedMilliseconds = 1000 * $time
                ExecutedMilliseconds = 1000 * $time + 10; DueToExecutionMilliseconds = @(1, 3, 7)[$sampleIndex]
                QueueToExecutionMilliseconds = @(1, 2, 5)[$sampleIndex] }
        }
        # Labels alone must not admit observations crossing either phase boundary.
        $samples += Get-Sample ($seconds - 0.1) $phaseName 90000
        $probes += [pscustomobject]@{ Phase = $phaseName; QueuedMilliseconds = 1000 * ($seconds + 9)
            ExecutedMilliseconds = 1000 * ($seconds + 11); DueToExecutionMilliseconds = 90000; QueueToExecutionMilliseconds = 90000 }
    }
    $report = [ordered]@{ SchemaVersion = 1; Status = 'complete'; Configuration = 'default'; Trial = 1
        ProcessId = 123; PhaseSeconds = 30; WarmupSeconds = 20; ProductionVersion = 'synthetic'; HarnessVersion = 'synthetic'
        Runtime = 'synthetic'; Phases = $phases; Samples = $samples; UiProbes = $probes; VirtualMemoryInventories = @()
        Entry = Get-Sample 0; Ready = Get-Sample 1; Final = Get-Sample 100; EntryToReadyMilliseconds = 1000
        RefreshMilliseconds = 10; BeforeRefresh = Get-Counters 0; AfterRefresh = Get-Counters 1
        Counters = Get-Counters 9; PassiveTicks = 50; AutomaticTicks = 1; DisplayTicks = 50
        Assertions = @('synthetic'); Limitations = @('synthetic') }
    if ($Transitions) {
        $report.Transitions = @(@{ Name = 'reopen'; StartMilliseconds = 1; SynchronousActionMilliseconds = 2
            UntilDispatcherIdleMilliseconds = 4; UiCreated = @{ Row = 0 } })
    }
    Write-Json (Join-Path $root 'report.json') $report
    Write-Json (Join-Path $root 'run.json') ([ordered]@{ SchemaVersion = 1; Status = 'complete'; Pilot = $false
        Trials = 1; PhaseSeconds = 30; WarmupSeconds = 20; Configurations = @('default'); SourceHead = 'synthetic'
        FingerprintBefore = @(); FingerprintAfter = @()
        Results = @(@{ Status = 'complete'; ExitCode = 0; TimedOut = $false; Report = 'report.json'; ProcessId = 123
            Configuration = 'default'; Trial = 1; LaunchToReadyObservedWallMilliseconds = 1000 }) })
    Set-Content -LiteralPath (Join-Path $root 'stdout.log') -Value 'preserved synthetic log'
    $root
}
function Assert-Rejected([string[]]$Directories, [string]$Output, [string]$Message) {
    $rejected = $false
    try { & $compare -RunDirectories $Directories -OutputDirectory $Output } catch { $rejected = $true }
    Assert-Fixture $rejected $Message
    Assert-Fixture (!(Test-Path -LiteralPath $Output)) 'Rejected input must not create output.'
}
$directories = @(1..6 | ForEach-Object { New-Run "new-$_" })
$fixed = Get-Content -LiteralPath (Join-Path $directories[1] 'report.json') -Raw | ConvertFrom-Json
$fixed.Phases[5].CountersBefore.ClaudeLiveRequests = 3
$fixed.Phases[5].CountersAfter.ClaudeHistoryReadCalls = 99
Write-Json (Join-Path $directories[1] 'report.json') $fixed
$historyOnly = Get-Content -LiteralPath (Join-Path $directories[5] 'report.json') -Raw | ConvertFrom-Json
$historyOnly.Phases[5].CountersAfter.ClaudeHistoryReadCalls = 77
$historyOnly.PassiveTicks = 51
Write-Json (Join-Path $directories[5] 'report.json') $historyOnly
$sourceHashes = @(Get-ChildItem -LiteralPath $testRoot -Recurse -File | Get-FileHash | Select-Object Path, Hash)
$output = Join-Path $testRoot 'comparison-new'
& $compare -RunDirectories $directories -OutputDirectory $output
$summary = Get-Content -LiteralPath (Join-Path $output 'comparison.json') -Raw | ConvertFrom-Json
Assert-Fixture ($summary.SchemaVersion -eq 1 -and $summary.Status -eq 'complete') 'Output schema/status.'
Assert-Fixture ($summary.Phases.Count -eq 54 -and $summary.Runs.Count -eq 6 -and $summary.Pairs.Count -eq 30) 'Every run/phase/pair retained.'
$phase = $summary.Phases[5]
Assert-Fixture ($phase.SampleCount -eq 3 -and $phase.UiProbeCount -eq 3) 'Cross-boundary observations excluded.'
Assert-Fixture ($phase.ProcessCyclesDelta -eq 40 -and $phase.UiThreadCyclesDelta -eq 5) 'Cycle deltas.'
Assert-Fixture ($phase.RefreshSnapshotCallsDelta -eq 1) 'Refresh diagnostic delta.'
Assert-Fixture ([Math]::Abs($phase.UiDueP95Milliseconds - 6.6) -lt 1e-10 -and [Math]::Abs($phase.UiQueueP95Milliseconds - 4.7) -lt 1e-10) 'Exact interpolated percentiles.'
Assert-Fixture ($phase.UiDueMaxMilliseconds -eq 7 -and $phase.UiQueueMaxMilliseconds -eq 5) 'Dispatcher maxima.'
Assert-Fixture ($phase.MedianPrivateWorkingSetBytes -eq 200 -and $phase.MedianPrivateBytes -eq 500) 'Within-phase memory medians.'
Assert-Fixture ($phase.CpuMilliseconds -eq 20 -and $phase.CpuOneCorePercent -eq 0.2) 'Sampler CPU delta and one-core percent.'
Assert-Fixture ($phase.AllocatedBytesApproxDelta -eq 10000 -and $summary.Runs[0].AllocatedBytesApproxDelta -eq 100000) 'Phase and whole-run allocation.'
Assert-Fixture ($phase.HandlesBefore.ByType.Event -eq 10 -and $phase.HandlesAfter.ByType.Thread -eq 11) 'Handle type inventory preserved.'
$pair = @($summary.Pairs | Where-Object { $_.Pair -eq 1 -and $_.Phase -eq 'widget-visible' })[0]
Assert-Fixture (!$pair.ComparisonEligible -and $pair.Metrics.ProcessCyclesDelta.Delta -eq 0) 'Unequal active observations retained with eligibility false.'
Assert-Fixture ($pair.CounterEquivalenceAfter.ClaudeHistoryReadCalls.Equal -eq $false) 'History equivalence recorded.'
$historyPair = @($summary.Pairs | Where-Object { $_.Pair -eq 3 -and $_.Phase -eq 'widget-visible' })[0]
Assert-Fixture ($historyPair.ComparisonEligible -and $historyPair.CounterEquivalenceAfter.ClaudeHistoryReadCalls.Equal -eq $false) 'History mismatch remains visible without changing active eligibility.'
Assert-Fixture ($summary.Pairs[29].PassiveTicksEqual -eq $false -and $summary.Pairs[29].ComparisonEligible) 'Passive mismatch recorded without changing active eligibility.'
$aggregate = @($summary.EligiblePairAggregates | Where-Object Phase -eq 'widget-visible')[0]
Assert-Fixture ($aggregate.EligiblePairs -eq 2 -and $aggregate.DeltaStatistics.ProcessCyclesDelta.Count -eq 2) 'Only eligible pairs enter aggregates.'
Assert-Fixture ($summary.Pairs[10].OptimizedOrder -eq 4 -and $summary.Pairs[10].FixedOrder -eq 3) 'Alternating order pair orientation.'
Assert-Fixture ($summary.TransitionSummary[0].Observations -eq 3 -and $summary.Transitions.Count -eq 6) 'Transition observations and statistics retained.'
Assert-Fixture ($summary.Sources[0].Logs.Count -eq 1 -and $summary.Sources[0].ReportSha256.Length -eq 64) 'Source logs and report identities recorded.'
$csv = @(Import-Csv -LiteralPath (Join-Path $output 'pairs.csv'))
Assert-Fixture ($csv.Count -eq 30 -and $csv[0].ProcessCyclesDeltaOptimized -eq '40') 'Readable paired CSV metric columns.'
foreach ($source in $sourceHashes) { Assert-Fixture ((Get-FileHash -LiteralPath $source.Path).Hash -eq $source.Hash) 'Source report/log untouched.' }
$alreadyExists = $false
try { & $compare -RunDirectories $directories -OutputDirectory $output } catch { $alreadyExists = $true }
Assert-Fixture $alreadyExists 'Existing output preserved.'
Assert-Rejected @($directories[0], $directories[0], $directories[2], $directories[3], $directories[4], $directories[5]) (Join-Path $testRoot 'duplicate-output') 'Duplicate source directories rejected.'

# Older schema-1 reports omitted cycle/handle diagnostics and transitions entirely.
$oldDirectories = @(1..6 | ForEach-Object { New-Run "old-$_" $false $false })
$missingCounter = Get-Content -LiteralPath (Join-Path $oldDirectories[1] 'report.json') -Raw | ConvertFrom-Json
$missingCounter.Phases[2].CountersBefore.PSObject.Properties.Remove('CodexStarts')
Write-Json (Join-Path $oldDirectories[1] 'report.json') $missingCounter
$oldOutput = Join-Path $testRoot 'comparison-old'
& $compare -RunDirectories $oldDirectories -OutputDirectory $oldOutput
$old = Get-Content -LiteralPath (Join-Path $oldOutput 'comparison.json') -Raw | ConvertFrom-Json
Assert-Fixture ($null -eq $old.Phases[0].ProcessCyclesDelta -and $null -eq $old.Phases[0].HandlesBefore) 'Missing old diagnostics remain null.'
Assert-Fixture ($old.Transitions.Count -eq 0 -and $old.TransitionSummary.Count -eq 0) 'Missing old transitions are supported.'
Assert-Fixture ($null -eq $old.Pairs[0].Metrics.ProcessCyclesDelta.Delta -and $old.Pairs[0].ComparisonEligible) 'Old unavailable metrics do not invent values or disqualify known matching counters.'
Assert-Fixture (!$old.Pairs[2].ComparisonEligible -and $null -eq $old.Pairs[2].CounterEquivalenceBefore.CodexStarts.Equal) 'Unknown active count excludes pair instead of treating null as matching zero.'
$sevenPhaseNames = @('warmup', 'tray-idle', 'flyout-visible', 'tray-after-flyout',
    'widget-visible', 'post-refresh-widget', 'tray-after-refresh')
$legacyDirectories = @(1..6 | ForEach-Object { New-Run "legacy7-$_" $false $false $sevenPhaseNames })
$legacyOutput = Join-Path $testRoot 'comparison-legacy7'
& $compare -RunDirectories $legacyDirectories -OutputDirectory $legacyOutput
$legacy = Get-Content -LiteralPath (Join-Path $legacyOutput 'comparison.json') -Raw | ConvertFrom-Json
Assert-Fixture ($legacy.PhaseNames.Count -eq 7 -and $legacy.Phases.Count -eq 42 -and $legacy.Pairs.Count -eq 24) 'Known seven-phase legacy matrix preserved.'
Assert-Fixture ($legacy.EligiblePairAggregates.Count -eq 8 -and 'widget-after-churn' -notin $legacy.EligiblePairAggregates.Phase) 'Legacy output has no phantom missing-phase aggregates.'
Assert-Fixture ($null -eq $legacy.Phases[0].ProcessCyclesDelta -and $legacy.Pairs[0].ComparisonEligible) 'Seven-phase missing diagnostics remain null.'
$mixedDirectories = @($legacyDirectories[0..4]) + $directories[4]
Assert-Rejected $mixedDirectories (Join-Path $testRoot 'mixed-matrix-output') 'Mixed known phase matrices rejected.'
$unknownPath = Join-Path $legacyDirectories[5] 'report.json'
$unknown = Get-Content -LiteralPath $unknownPath -Raw | ConvertFrom-Json
$unknown.Phases[0].Name = 'unknown-phase'
Write-Json $unknownPath $unknown
Assert-Rejected $legacyDirectories (Join-Path $testRoot 'unknown-matrix-output') 'Unknown phase matrix rejected.'
# Exercise the existing summarizer with legacy and current report shapes.
foreach ($root in @($directories[0], $oldDirectories[0], $legacyDirectories[0])) {
    & $summarize -RunDirectory $root
    $existing = Get-Content -LiteralPath (Join-Path $root 'summary.json') -Raw | ConvertFrom-Json
    $expectedCount = if ($root -eq $legacyDirectories[0]) { 7 } else { 9 }
    Assert-Fixture ($existing.TrialPhases.Count -eq $expectedCount -and $existing.SchemaVersion -eq 1) 'Existing summarizer accepts known old and new schema-1 fixtures.'
}
$invalidPath = Join-Path $oldDirectories[0] 'run.json'
$invalid = Get-Content -LiteralPath $invalidPath -Raw | ConvertFrom-Json
$invalid.Status = 'running'
Write-Json $invalidPath $invalid
Assert-Rejected $oldDirectories (Join-Path $testRoot 'incomplete-output') 'Incomplete run rejected.'
$invalid.Status = 'complete'
$invalid.Results[0].Report = Join-Path $directories[0] 'report.json'
Write-Json $invalidPath $invalid
Assert-Rejected $oldDirectories (Join-Path $testRoot 'escape-output') 'Child escaping its source directory rejected.'
Write-Host "Passed $assertions deterministic idle comparison assertions. Fixtures retained: $testRoot"
