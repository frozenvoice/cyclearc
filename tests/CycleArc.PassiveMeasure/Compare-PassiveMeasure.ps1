[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Before,
    [Parameter(Mandatory)][string[]]$After,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
function Read-Rows([string[]]$paths) {
    foreach ($path in $paths) {
        $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($row in $report.Rows) {
            $row | Add-Member -NotePropertyName MeasurementLabel -NotePropertyValue $report.Label -PassThru
        }
    }
}
function Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Missing measurement rows.' }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
function Range([double[]]$values) {
    $sorted = @($values | Sort-Object)
    return @($sorted[0], $sorted[-1])
}
function Run-Stats([object[]]$rows) {
    foreach ($run in ($rows | Group-Object MeasurementLabel)) {
        [pscustomobject]@{
            Label = $run.Name
            Trials = $run.Count
            MillisecondsMedian = Median $run.Group.MillisecondsPerTick
            MillisecondsRange = Range $run.Group.MillisecondsPerTick
            AllocatedBytesMedian = Median $run.Group.AllocatedBytesPerTick
            AllocatedBytesRange = Range $run.Group.AllocatedBytesPerTick
        }
    }
}

$baselineRows = @(Read-Rows $Before)
$afterRows = @(Read-Rows $After)
$groups = $baselineRows | Group-Object Scenario, Accounts, SamplesPerAccount
$afterGroups = $afterRows | Group-Object Scenario, Accounts, SamplesPerAccount
if (@(Compare-Object @($groups.Name | Sort-Object) @($afterGroups.Name | Sort-Object)).Count) {
    throw 'Reports contain different fixture scenarios.'
}
$summary = foreach ($group in $groups) {
    $old = @($group.Group)
    $new = @($afterGroups | Where-Object Name -eq $group.Name | ForEach-Object Group)
    $oldShape = @($old | ForEach-Object { "$($_.TotalHistorySamples)/$($_.HistoryBytes)/$($_.Ticks)/$($_.DesktopHistoryFileOpens)/$($_.DesktopHistoryParseCalls)/$($_.FakeAuthCalls)/$($_.Changed)" } | Sort-Object -Unique)
    $newShape = @($new | ForEach-Object { "$($_.TotalHistorySamples)/$($_.HistoryBytes)/$($_.Ticks)/$($_.DesktopHistoryFileOpens)/$($_.DesktopHistoryParseCalls)/$($_.FakeAuthCalls)/$($_.Changed)" } | Sort-Object -Unique)
    if (@(Compare-Object $oldShape $newShape).Count) { throw "Fixture/counter mismatch: $($group.Name)" }
    $oldAllocated = Median $old.AllocatedBytesPerTick
    $newAllocated = Median $new.AllocatedBytesPerTick
    [pscustomobject]@{
        Scenario = $old[0].Scenario
        Accounts = $old[0].Accounts
        SamplesPerAccount = $old[0].SamplesPerAccount
        TotalHistorySamples = $old[0].TotalHistorySamples
        HistoryBytesRange = Range $old.HistoryBytes
        BaselineTrials = $old.Count
        AfterTrials = $new.Count
        BaselineMillisecondsMedian = Median $old.MillisecondsPerTick
        AfterMillisecondsMedian = Median $new.MillisecondsPerTick
        BaselineMillisecondsRange = Range $old.MillisecondsPerTick
        AfterMillisecondsRange = Range $new.MillisecondsPerTick
        BaselineAllocatedBytesMedian = $oldAllocated
        AfterAllocatedBytesMedian = $newAllocated
        BaselineAllocatedBytesRange = Range $old.AllocatedBytesPerTick
        AfterAllocatedBytesRange = Range $new.AllocatedBytesPerTick
        AllocationReductionPercent = 100 * ($oldAllocated - $newAllocated) / $oldAllocated
        BaselineRuns = @(Run-Stats $old)
        AfterRuns = @(Run-Stats $new)
        DesktopHistoryOpensPerTick = $old[0].DesktopHistoryFileOpens / $old[0].Ticks
        DesktopHistoryParsesPerTick = $old[0].DesktopHistoryParseCalls / $old[0].Ticks
        FakeAuthPerTick = $old[0].FakeAuthCalls / $old[0].Ticks
        ChangedPerTick = $old[0].Changed / $old[0].Ticks
    }
}
if ($Output) {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
}
$summary | Format-Table Scenario, Accounts, SamplesPerAccount, BaselineMillisecondsMedian,
    AfterMillisecondsMedian, BaselineAllocatedBytesMedian, AfterAllocatedBytesMedian, AllocationReductionPercent -AutoSize
