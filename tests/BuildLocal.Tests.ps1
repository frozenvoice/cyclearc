#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/LocalInstall.ps1')
. (Join-Path $repoRoot 'scripts/Build-Local.ps1') -LoadOnly

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-build-local-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Assert-TestDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        $absolute -ne [IO.Path]::GetFullPath($testRoot)) {
        throw "Invalid test target: $absolute"
    }
}

function Assert-Throws([scriptblock]$Action, [string]$MessageFragment) {
    $thrown = $false
    try { & $Action }
    catch {
        $thrown = $true
        if ($MessageFragment -and $_.Exception.Message -notlike "*$MessageFragment*") {
            throw "Expected '$MessageFragment' in '$($_.Exception.Message)'"
        }
    }
    if (!$thrown) { throw "Expected an exception containing '$MessageFragment'" }
}

function Invoke-TestGit([string]$Directory, [string[]]$Arguments) {
    $output = @(& git -c core.hooksPath= -c commit.gpgsign=false -c init.templateDir= -C $Directory @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "Synthetic git failed: $($output -join ' ')" }
    $output -join "`n"
}

function New-TestCycleArcTree([string]$Directory) {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $Directory
    New-Item -ItemType Directory -Path (Join-Path $Directory 'src/CycleArc') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Directory 'CycleArc.sln') -Value 'synthetic solution'
    Set-Content -LiteralPath (Join-Path $Directory 'src/CycleArc/CycleArc.csproj') -Value 'synthetic project'
    # The installer project the preflight toolchain check looks for. Never built here: every
    # test that reaches packaging supplies its own -PackagedSetup.
    New-Item -ItemType Directory -Path (Join-Path $Directory 'src/CycleArc.Setup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Directory 'src/CycleArc.Setup/CycleArc.Setup.csproj') -Value 'synthetic setup project'
}

function New-GitCycleArcTree([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    New-TestCycleArcTree $Directory
    Invoke-TestGit $Directory @('init', '-b', 'main') | Out-Null
    Invoke-TestGit $Directory @('config', 'user.email', 'cyclearc-test@example.invalid') | Out-Null
    Invoke-TestGit $Directory @('config', 'user.name', 'CycleArc release test') | Out-Null
    Invoke-TestGit $Directory @('add', '.') | Out-Null
    Invoke-TestGit $Directory @('commit', '-m', 'synthetic CycleArc tree') | Out-Null
}

function New-FakePublished([string]$Repo, [string]$ExeText, [string]$SetupText) {
    # Successful build fixtures call this inside -PackagedSetup, after the run starts.
    # Pre-creating/reusing Setup.exe makes tests depend on the five-second freshness grace.
    $stage = Join-Path $Repo 'publish/.dev-staging'
    $pack = Join-Path $Repo 'publish/.dev-velopack'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    New-Item -ItemType Directory -Path $pack -Force | Out-Null
    $exe = Join-Path $stage 'CycleArc.exe'
    $setup = Join-Path $pack 'CycleArc-Setup.exe'
    Set-Content -LiteralPath $exe -Value $ExeText
    Set-Content -LiteralPath $setup -Value $SetupText
    [pscustomobject]@{ StagingExe = $exe; SetupPath = $setup }
}

$pwshPath = (Get-Command pwsh).Source
$scriptHost = Join-Path $testRoot 'process scripts'
New-Item -ItemType Directory -Path $scriptHost -Force | Out-Null

function New-TestPowerShellBody([string]$Name, [string]$Body) {
    $path = Join-Path $scriptHost "$Name.ps1"
    Set-Content -LiteralPath $path -Value $Body -Encoding utf8
    $path
}

# A file the platform can launch directly: Process.Start with UseShellExecute
# false runs a shebang script on Unix but never a .cmd on Windows, so callers
# skip these cases on Windows instead of pretending a wrapper would run.
function New-TestLaunchableScript([string]$Name, [string]$BodyPath) {
    if ($IsWindows) { return $null }
    $path = Join-Path $scriptHost $Name
    $lines = @('#!/bin/sh', ('exec "{0}" -NoProfile -NonInteractive -File "{1}" "$@"' -f $pwshPath, $BodyPath))
    Set-Content -LiteralPath $path -Value ($lines -join "`n") -Encoding utf8
    & chmod '+x' $path
    $path
}

function New-TestDevRun([string]$Directory, [string]$MarkerPath, [int]$ExitCode, [string]$ErrorText = '', [string]$StageName = '') {
    $body = @(
        '[CmdletBinding()]',
        'param([switch]$Fast, [switch]$NoLaunch)',
        'Set-StrictMode -Version Latest',
        ('Set-Content -LiteralPath "{0}" -Value "NoLaunch=$NoLaunch Fast=$Fast cwd=$((Get-Location).Path)"' -f $MarkerPath)
    )
    if ($StageName) {
        $body += '$stageFile = $env:CYCLEARC_DEV_RUN_STAGE_FILE'
        $body += ('if ($stageFile) {{ [IO.File]::WriteAllText($stageFile, ''{0}'' + [Environment]::NewLine) }}' -f ($StageName -replace "'", "''"))
    }
    if ($ErrorText) {
        $body += ('[Console]::Error.WriteLine(''{0}'')' -f ($ErrorText -replace "'", "''"))
        $body += '[Console]::Error.Flush()'
    }
    $body += "exit $ExitCode"
    $path = Join-Path $Directory 'dev-run.ps1'
    Set-Content -LiteralPath $path -Value ($body -join "`n") -Encoding utf8
    $path
}

function Get-TestProcessById([int]$ProcessId) {
    try { return Get-Process -Id $ProcessId -ErrorAction Stop } catch { return $null }
}

function Stop-TestProcessById([int]$ProcessId) {
    $process = Get-TestProcessById $ProcessId
    if ($process) { try { $process.Kill() } catch { } finally { $process.Dispose() } }
}

function Invoke-TestBuildFailure([hashtable]$Parameters, [string]$Expected) {
    $console = [Collections.Generic.List[string]]::new()
    $failure = $null
    try {
        Invoke-BuildLocal @Parameters 6>&1 | ForEach-Object { $console.Add([string]$_) }
    }
    catch { $failure = $_.Exception.Message }
    if (!$failure -or !$failure.Contains($Expected)) { throw "Expected '$Expected', got '$failure'" }
    [pscustomobject]@{
        Message = $failure
        Console = $console -join "`n"
        Record = Get-Content -LiteralPath (Join-Path $Parameters.RepoRoot 'artifacts/build-local/last-failure.txt') -Raw
    }
}

function Assert-TestFailureReport($Failure, [string[]]$Required, [string[]]$Forbidden = @()) {
    foreach ($text in @($Failure.Console, $Failure.Record)) {
        foreach ($value in $Required) {
            if (!$text.Contains($value)) { throw "Failure report lost '$value': $text" }
        }
        foreach ($value in $Forbidden) {
            if ($text.Contains($value)) { throw "Failure report leaked '$value': $text" }
        }
    }
}

function Set-TestPreviousDiagnostics([string]$Directory, [string]$Marker) {
    foreach ($entry in @{
        'dev-run.out.log' = "[ui-smoke] START $Marker"
        'dev-run.err.log' = "[ui-smoke] FAIL $Marker`nException: $Marker"
        'dev-run.stage' = $Marker
        'last-failure.txt' = "Stage: $Marker`nCheck: $Marker`nCause: $Marker"
    }.GetEnumerator()) {
        $path = Join-Path $Directory $entry.Key
        Set-Content -LiteralPath $path -Value $entry.Value
        # Even a recent timestamp is not proof that the file belongs to the next run.
        (Get-Item -LiteralPath $path).LastWriteTimeUtc = [datetime]::UtcNow.AddMinutes(1)
    }
}

function Write-TestFailureExample([string]$Name, $Failure) {
    $summary = ($Failure.Record -split '===== CycleArc build-local failure summary =====')[-1].Trim()
    Write-Host "EXAMPLE ($Name):`n$summary"
}

# Keep the real runner, changing only its Process.Start target/budget at the test boundary.
# No machine PATH, executable, policy, build or installed-app operation is changed.
$realExternalProcess = (Get-Command Invoke-ExternalProcess).ScriptBlock

function Invoke-TestExternalProcess {
    [CmdletBinding()]
    param(
        [string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory,
        [int]$TimeoutSeconds, [switch]$NoNewWindow,
        [string]$StandardOutputPath, [string]$StandardErrorPath,
        [switch]$StreamProgress, [object]$ExecutionState
    )
    $script:observedExecutionState = $ExecutionState
    if ($script:externalTestMode -eq 'start-failure') {
        $PSBoundParameters.FilePath = Join-Path $testRoot 'CURRENT-START-FAILURE-missing.exe'
    }
    elseif ($script:externalTestMode -eq 'timeout') { $PSBoundParameters.TimeoutSeconds = 3 }
    & $realExternalProcess @PSBoundParameters
}

try {
    $scriptText = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts/Build-Local.ps1') -Raw
    $cmdText = Get-Content -LiteralPath (Join-Path $repoRoot 'build-local.cmd') -Raw
    if ($scriptText -match '(?i)taskkill') { throw 'Build-Local.ps1 must not use taskkill' }
    if ($cmdText -match '(?i)taskkill') { throw 'build-local.cmd must not use taskkill' }
    if ($scriptText -match 'git pull' -or $scriptText -match 'git checkout' -or $scriptText -match 'git stash') {
        throw 'Build-Local.ps1 must not change the git checkout'
    }
    if ($scriptText -notmatch 'dev-run\.ps1' -or $scriptText -notmatch '-NoLaunch') {
        throw 'Build-Local.ps1 must invoke dev-run.ps1 -NoLaunch in a separate process'
    }
    if ($scriptText -match "(?m)^\s*\.\s+.*dev-run\.ps1") { throw 'Build-Local.ps1 must not dot-source dev-run.ps1' }
    if ($scriptText -notmatch '--silent') { throw 'Build-Local.ps1 must use Setup.exe --silent' }
    if ($scriptText -match "--uninstall") { throw 'Build-Local.ps1 must not uninstall in order to install' }
    if ($scriptText -match 'Copy-ValidatedExecutable' -or $scriptText -match 'Install-StagedApp') {
        throw 'Build-Local.ps1 must not copy a development EXE over the managed Velopack install'
    }
    if ($scriptText -notmatch 'dev-run\.out\.log' -or $scriptText -notmatch 'dev-run\.err\.log') {
        throw 'Build-Local.ps1 must capture dev-run stdout/stderr under artifacts/build-local'
    }
    if ($scriptText -notmatch 'Write-BuildLocalLogTail') {
        throw 'Build-Local.ps1 must print a tail of captured dev-run output when that process fails'
    }
    if ($scriptText -notmatch 'CopyToAsync') {
        throw 'Build-Local.ps1 must drain captured streams asynchronously instead of a blocking ReadToEnd'
    }
    if ($scriptText -notmatch 'CYCLEARC_DEV_RUN_STAGE_FILE' -or $scriptText -notmatch 'dev-run\.stage') {
        throw 'Build-Local.ps1 must record the child sub-stage so a UiSmoke timeout is not reported as Stage: build'
    }
    if ($scriptText -notmatch 'Failed at:') {
        throw 'Build-Local.ps1 must print Failed at: for the recorded sub-stage'
    }
    $devRunText = Get-Content -LiteralPath (Join-Path $repoRoot 'dev-run.ps1') -Raw
    foreach ($stage in @(
        'preflight', 'setup-ui-toolchain', 'workflow-contract', 'release-guard', 'restore', 'tool-restore', 'build', 'ui-smoke-desktop-instance',
        'local-install-regression', 'build-local-regression', 'unit-test', 'ui-smoke-full',
        'publish', 'package', 'package-verify'
    )) {
        if ($devRunText -notmatch [regex]::Escape("Invoke-DevRunStep '$stage'")) {
            throw "dev-run.ps1 is missing fail-fast step '$stage'"
        }
    }
    $desktopStep = $devRunText.IndexOf("Invoke-DevRunStep 'ui-smoke-desktop-instance'")
    $unitStep = $devRunText.IndexOf("Invoke-DevRunStep 'unit-test'")
    $fullSmoke = $devRunText.IndexOf("Invoke-DevRunStep 'ui-smoke-full'")
    $packageStep = $devRunText.IndexOf("Invoke-DevRunStep 'package'")
    if ($desktopStep -lt 0 -or $unitStep -le $desktopStep -or $fullSmoke -le $unitStep -or $packageStep -le $fullSmoke) {
        throw 'dev-run.ps1 must run desktop-instance before unit tests, full UiSmoke and packaging'
    }
    if ($cmdText -notmatch 'scripts\\Build-Local.ps1') { throw 'build-local.cmd must call scripts\\Build-Local.ps1' }
    if ($cmdText -notmatch 'pause') { throw 'build-local.cmd must pause on failure so the window stays open' }
    Write-Host 'PASS: build-local.cmd / Build-Local.ps1 structural guards.'

    if ($cmdText -match '(?i)previous installation was left in place') {
        throw 'build-local.cmd must not claim the previous installation survived regardless of stage'
    }
    if ($cmdText -notmatch 'last-failure\.txt') {
        throw 'build-local.cmd must print the stage Build-Local.ps1 recorded'
    }
    if ($scriptText -match '(?m)^\s*\$devRun\s*=') {
        throw 'Build-Local.ps1 must not assign to $devRun; it is the [scriptblock]$DevRun parameter'
    }
    # A shut-down desktop's PID is gone, which is the success case; raising it
    # puts a TerminatingError in the transcript the person is told to read.
    if ($scriptText -match "Get-Process -Id [^\r\n]*-ErrorAction Stop") {
        throw 'Build-Local.ps1 must not treat an absent desktop PID as a terminating error'
    }
    Write-Host 'PASS: build-local.cmd reports the recorded stage instead of a blanket claim.'

    # Seed a real previous failure, then fail at the real Process.Start boundary in the
    # same repository/log location. Summary-only mocks cannot reproduce this regression.
    $diagnosticTree = Join-Path $testRoot 'consecutive failure diagnostics'
    New-GitCycleArcTree $diagnosticTree
    $oldRun = 'OLD-RUN-DO-NOT-REPORT'
    New-TestDevRun $diagnosticTree (Join-Path $diagnosticTree 'dev-run-marker.txt') 9 `
        "[ui-smoke] FAIL $oldRun`nException: $oldRun" $oldRun | Out-Null
    $script:diagnosticCalls = 0
    $diagnosticParameters = @{
        RepoRoot = $diagnosticTree
        ManagedRoot = Join-Path $testRoot 'install diagnostic'
        PrerequisitePreflight = { [pscustomobject]@{ Status = 'Ready' } }
        StopDesktop = { $script:diagnosticCalls++ }
        RunSetup = { $script:diagnosticCalls++; 0 }
        StartLauncher = { $script:diagnosticCalls++ }
    }
    $previousRun = Invoke-TestBuildFailure $diagnosticParameters 'failed (exit 9)'
    $diagnosticLogs = Join-Path $diagnosticTree 'artifacts/build-local'
    Set-Content -LiteralPath (Join-Path $diagnosticLogs 'dev-run.out.log') -Value "[ui-smoke] START $oldRun"
    if (!$previousRun.Record.Contains($oldRun)) { throw 'The previous real failure did not seed the fixture' }
    $script:externalTestMode = 'start-failure'
    Set-Item Function:Invoke-ExternalProcess -Value ${function:Invoke-TestExternalProcess}
    try { $startFailure = Invoke-TestBuildFailure $diagnosticParameters 'CURRENT-START-FAILURE-missing.exe' }
    finally { Set-Item Function:Invoke-ExternalProcess -Value $realExternalProcess }
    Assert-TestFailureReport $startFailure @('Stage: build', 'Check: not started', 'Child started: false',
        'Child exit: not available (not started)', 'CURRENT-START-FAILURE-missing.exe') @($oldRun)
    if (!$observedExecutionState.Attempted -or !$observedExecutionState.StartAttempted -or
        $observedExecutionState.Started -or $null -ne $observedExecutionState.ExitCode) {
        throw 'The actual start boundary was not recorded truthfully'
    }
    if ($startFailure.Record -match '(?m)^Details: .*dev-run\.err\.log') { throw 'Start failure details must identify the parent log' }
    if ($diagnosticCalls -ne 0) { throw 'A start failure reached desktop stop, Setup or launch' }
    Write-Host 'PASS: consecutive runs do not report prior logs after a real Process.Start failure.'
    Write-TestFailureExample 'start failure' $startFailure

    # B. No prior files: the same exception/absent exit semantics still hold.
    foreach ($name in @('dev-run.out.log', 'dev-run.err.log', 'dev-run.stage', 'last-failure.txt')) {
        Remove-Item -LiteralPath (Join-Path $diagnosticLogs $name) -Force -ErrorAction SilentlyContinue
    }
    Set-Item Function:Invoke-ExternalProcess -Value ${function:Invoke-TestExternalProcess}
    try { $freshStartFailure = Invoke-TestBuildFailure $diagnosticParameters 'CURRENT-START-FAILURE-missing.exe' }
    finally { Set-Item Function:Invoke-ExternalProcess -Value $realExternalProcess }
    Assert-TestFailureReport $freshStartFailure @('Check: not started', 'Child started: false',
        'Child exit: not available (not started)', 'CURRENT-START-FAILURE-missing.exe') @($oldRun)
    Write-Host 'PASS: a start failure without prior logs keeps the original exception and no exit code.'

    # Fail before any capture preparation; neither stale stages nor stale tails are ours.
    Set-TestPreviousDiagnostics $diagnosticLogs $oldRun
    $savedDevRun = Get-Content -LiteralPath (Join-Path $diagnosticTree 'dev-run.ps1') -Raw
    Remove-Item -LiteralPath (Join-Path $diagnosticTree 'dev-run.ps1')
    try { $missingScript = Invoke-TestBuildFailure $diagnosticParameters 'dev-run.ps1 is missing' }
    finally { Set-Content -LiteralPath (Join-Path $diagnosticTree 'dev-run.ps1') -Value $savedDevRun -Encoding utf8 }
    Assert-TestFailureReport $missingScript @('Stage: build', 'Check: not started', 'dev-run.ps1 is missing') @($oldRun, 'Captured dev-run.')
    Write-Host 'PASS: a pre-capture failure ignores even recent previous stdout/stderr/stage/summary files.'

    # C. stdout is opened/truncated, but a read-sharing lock prevents preparing stderr.
    # Keep that old file present and readable: deleting old files is not the assertion.
    Set-TestPreviousDiagnostics $diagnosticLogs $oldRun
    $childMarker = Join-Path $diagnosticTree 'dev-run-marker.txt'
    Remove-Item -LiteralPath $childMarker -Force
    $errorPath = Join-Path $diagnosticLogs 'dev-run.err.log'
    $errorLock = [IO.File]::Open($errorPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $script:externalTestMode = 'normal'
    Set-Item Function:Invoke-ExternalProcess -Value ${function:Invoke-TestExternalProcess}
    try { $captureFailure = Invoke-TestBuildFailure $diagnosticParameters 'dev-run.err.log' }
    finally { $errorLock.Dispose(); Set-Item Function:Invoke-ExternalProcess -Value $realExternalProcess }
    Assert-TestFailureReport $captureFailure @('Stage: build', 'Check: not started', 'Child started: false',
        'Child exit: not available (not started)', "Cause: $($captureFailure.Message)") @($oldRun, 'Captured dev-run.err.log:', 'Child log:')
    if (!$observedExecutionState.OutputPrepared -or $observedExecutionState.ErrorPrepared -or
        $observedExecutionState.StartAttempted -or $observedExecutionState.Started -or (Test-Path -LiteralPath $childMarker)) {
        throw 'Partial log preparation started a child or claimed the old stderr'
    }
    if (!(Get-Content -LiteralPath $errorPath -Raw).Contains($oldRun)) { throw 'The locked old stderr was changed' }
    $exclusiveOut = [IO.File]::Open((Join-Path $diagnosticLogs 'dev-run.out.log'), [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $exclusiveOut.Dispose()
    Write-Host 'PASS: partial log preparation preserves the I/O failure, ignores old stderr and closes stdout without starting a child.'

    # D. Nonzero and then silent nonzero in exactly the same location overwrite prior
    # captures; the full raw stdout remains even though failure tails are bounded.
    Set-TestPreviousDiagnostics $diagnosticLogs $oldRun
    $currentCause = 'System.InvalidOperationException: CURRENT-CHILD-FAILURE'
    Set-Content -LiteralPath (Join-Path $diagnosticTree 'dev-run.ps1') -Encoding utf8 -Value (@(
        '[CmdletBinding()]', 'param([switch]$Fast, [switch]$NoLaunch)',
        '[IO.File]::WriteAllText($env:CYCLEARC_DEV_RUN_STAGE_FILE, "ui-smoke-full")',
        '1..300 | ForEach-Object { [Console]::Out.WriteLine("PASS: current check $_") }',
        '1..40 | ForEach-Object { [Console]::Out.WriteLine("x" * 2000) }',
        '[Console]::Error.WriteLine("[ui-smoke] FAIL current-check")',
        "[Console]::Error.WriteLine('Exception: $currentCause')", 'exit 7') -join "`n")
    # A best-effort tail read may itself fail. It must not swallow the child's exit.
    $realLogTail = (Get-Command Get-BuildLocalLogTail).ScriptBlock
    Set-Item Function:Get-BuildLocalLogTail -Value { throw 'SECONDARY-TAIL-READ-FAILURE' }
    try { $currentFailure = Invoke-TestBuildFailure $diagnosticParameters 'failed (exit 7)' }
    finally { Set-Item Function:Get-BuildLocalLogTail -Value $realLogTail }
    Assert-TestFailureReport $currentFailure @('Stage: ui-smoke-full', 'Check: current-check',
        "Cause: $currentCause", 'Child started: true', 'Child exit: 7', "Details: $errorPath") @($oldRun, 'SECONDARY-TAIL-READ-FAILURE')
    $currentOut = Get-Content -LiteralPath (Join-Path $diagnosticLogs 'dev-run.out.log')
    if (@($currentOut | Where-Object Length -eq 2000).Count -ne 40) { throw 'The current full stdout was truncated' }
    Write-TestFailureExample 'child nonzero' $currentFailure
    New-TestDevRun $diagnosticTree $childMarker 5 | Out-Null
    $silentCurrent = Invoke-TestBuildFailure $diagnosticParameters 'failed (exit 5)'
    Assert-TestFailureReport $silentCurrent @('Check: not identified from the captured logs',
        'Cause: not identified from the captured logs (dev-run.err.log is empty)', 'Child exit: 5') @($oldRun, $currentCause, 'Check: current-check')
    Write-Host 'PASS: consecutive nonzero children retain current check/cause/exit/raw logs; empty stderr invents no cause.'

    # E. Start succeeds, current output is captured, then the parent times out. The
    # real started child must be stopped; its absence of a normal exit stays unknown.
    Set-TestPreviousDiagnostics $diagnosticLogs $oldRun
    $diagnosticPid = Join-Path $diagnosticTree 'timeout.pid'
    Set-Content -LiteralPath (Join-Path $diagnosticTree 'dev-run.ps1') -Encoding utf8 -Value (@(
        '[CmdletBinding()]', 'param([switch]$Fast, [switch]$NoLaunch)',
        "`$PID | Set-Content -LiteralPath '$diagnosticPid'",
        '[IO.File]::WriteAllText($env:CYCLEARC_DEV_RUN_STAGE_FILE, "ui-smoke-full")',
        '[Console]::Out.WriteLine("[ui-smoke] START current-timeout")',
        '[Console]::Error.WriteLine("Exception: CURRENT-TIMEOUT-OUTPUT")',
        '[Console]::Out.Flush(); [Console]::Error.Flush()', 'Start-Sleep -Seconds 600') -join "`n")
    $script:externalTestMode = 'timeout'
    Set-Item Function:Invoke-ExternalProcess -Value ${function:Invoke-TestExternalProcess}
    Set-Item Function:Get-BuildLocalLogTail -Value { throw 'SECONDARY-TAIL-READ-FAILURE' }
    try {
        $timeoutFailure = Invoke-TestBuildFailure $diagnosticParameters 'did not finish within 3 seconds'
        Set-Item Function:Get-BuildLocalLogTail -Value $realLogTail
        Assert-TestFailureReport $timeoutFailure @('Stage: ui-smoke-full', 'Check: current-timeout (started, no failure marker)',
            'Child started: true', 'Child exit: not available (dev-run.ps1 did not exit normally)') @($oldRun, 'Child started: false', 'SECONDARY-TAIL-READ-FAILURE')
        if (!(Get-Content -LiteralPath $errorPath -Raw).Contains('CURRENT-TIMEOUT-OUTPUT')) { throw 'The current timeout stderr was not preserved' }
        if (!$observedExecutionState.Started -or $null -ne $observedExecutionState.ExitCode) { throw 'Timeout state lost the actual start or invented an exit' }
        $timeoutSummary = ($timeoutFailure.Record -split '===== CycleArc build-local failure summary =====')[-1]
        if (!$timeoutSummary.Contains("Cause: $($timeoutFailure.Message)")) { throw 'The current timeout exception was replaced by child stderr' }
        $leftover = Get-TestProcessById ([int](Get-Content -LiteralPath $diagnosticPid -Raw))
        if ($leftover) { $leftover.Dispose(); throw 'The actual timed-out child remained running' }
        Write-TestFailureExample 'timeout after start' $timeoutFailure
    }
    finally {
        Set-Item Function:Invoke-ExternalProcess -Value $realExternalProcess
        Set-Item Function:Get-BuildLocalLogTail -Value $realLogTail
        if (Test-Path -LiteralPath $diagnosticPid) { Stop-TestProcessById ([int](Get-Content -LiteralPath $diagnosticPid -Raw)) }
    }
    Write-Host 'PASS: timeout records an actual start, current output and parent cause; it leaves no owned child.'

    # F. A successful child can print exception-shaped text; it is not a subsequent
    # packaging failure's cause. The last summary must end with the parent failure.
    Set-TestPreviousDiagnostics $diagnosticLogs $oldRun
    New-TestDevRun $diagnosticTree $childMarker 0 'Exception: SUCCESSFUL-CHILD-NOT-THE-CAUSE' 'ui-smoke-full' | Out-Null
    $packageFailure = Invoke-TestBuildFailure $diagnosticParameters 'Published CycleArc.exe is missing'
    Assert-TestFailureReport $packageFailure @('Stage: package', 'Check: not applicable (dev-run.ps1 succeeded)',
        'Child started: true', 'Child exit: 0', 'installed version is unchanged') @($oldRun)
    $packageSummary = ($packageFailure.Record -split '===== CycleArc build-local failure summary =====')[-1]
    if ($packageSummary.Contains('SUCCESSFUL-CHILD-NOT-THE-CAUSE') -or
        !$packageSummary.Contains("Cause: $($packageFailure.Message)") -or $packageSummary.Contains("Details: $errorPath")) {
        throw 'A successful child supplied the subsequent package failure cause/details'
    }
    if ($diagnosticCalls -ne 0) { throw 'Pre-install diagnostic failures reached desktop stop, Setup or launch' }
    Write-Host 'PASS: exit-zero followed by package failure reports only the current parent cause in its final summary.'

    # Secondary stage/report/lease errors cannot replace the original exception or
    # publish an old failure record. The lease wrapper still disposes the real handle.
    $realReportedStage = (Get-Command Resolve-BuildLocalReportedStage).ScriptBlock
    $realFailureReport = (Get-Command Write-BuildLocalFailure).ScriptBlock
    $realInstallLease = (Get-Command New-InstallLease).ScriptBlock
    Set-Item Function:Resolve-BuildLocalReportedStage -Value { throw 'SECONDARY-STAGE-FAILURE' }
    Set-Item Function:Write-BuildLocalFailure -Value { throw 'SECONDARY-SUMMARY-FAILURE' }
    Set-Item Function:New-InstallLease -Value {
        param($InstallRoot, $AllowedRoots)
        $wrapped = [pscustomobject]@{ Lease = & $realInstallLease -InstallRoot $InstallRoot -AllowedRoots $AllowedRoots }
        $wrapped | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $this.Lease.Dispose(); throw 'SECONDARY-CLEANUP-FAILURE' }
        $wrapped
    }
    $secondaryParameters = $diagnosticParameters.Clone()
    $secondaryParameters.DevRun = { throw 'CURRENT-PRIMARY-FAILURE' }
    $secondaryConsole = [Collections.Generic.List[string]]::new()
    $secondaryMessage = $null
    try {
        try { Invoke-BuildLocal @secondaryParameters 6>&1 | ForEach-Object { $secondaryConsole.Add([string]$_) } }
        catch { $secondaryMessage = $_.Exception.Message }
    }
    finally {
        Set-Item Function:Resolve-BuildLocalReportedStage -Value $realReportedStage
        Set-Item Function:Write-BuildLocalFailure -Value $realFailureReport
        Set-Item Function:New-InstallLease -Value $realInstallLease
    }
    if ($secondaryMessage -ne 'CURRENT-PRIMARY-FAILURE' -or
        !(($secondaryConsole -join "`n").Contains('CURRENT-PRIMARY-FAILURE')) -or
        ($secondaryConsole -join "`n") -match 'SECONDARY-|SUCCESSFUL-CHILD-NOT-THE-CAUSE' -or
        (Test-Path -LiteralPath (Join-Path $diagnosticLogs 'last-failure.txt'))) {
        throw 'Secondary reporting/cleanup replaced the first exception or reused the old record'
    }
    $leaseCheck = & $realInstallLease -InstallRoot $diagnosticTree -AllowedRoots @($diagnosticTree)
    $leaseCheck.Dispose()
    Write-Host 'PASS: secondary stage, summary and lease-cleanup failures preserve the first exception and release the lease.'

    # Exercise the real preflight ordering without building or using the machine's VS.
    # Every subsequent app operation is a forbidden adapter.
    $preflightTree = Join-Path $testRoot 'prerequisite integration'
    New-GitCycleArcTree $preflightTree
    $forbiddenOperation = { throw 'Unexpected build, desktop stop or CycleArc Setup invocation' }
    foreach ($preflightStatus in @('Cancelled', 'Manual', 'RebootRequired', 'PolicyBlocked')) {
        $earlyResult = Invoke-BuildLocal -RepoRoot $preflightTree -PrerequisitePreflight {
            param($root, $noPrompt, $silent)
            [pscustomobject]@{ Status = $preflightStatus }
        } -DevRun $forbiddenOperation -StopDesktop $forbiddenOperation -RunSetup $forbiddenOperation -StartLauncher $forbiddenOperation
        if ($earlyResult.PrerequisiteStatus -ne $preflightStatus -or $earlyResult.Built -or
            (Get-BuildLocalStage) -ne 'preflight') {
            throw "Prerequisite outcome $preflightStatus did not stop before the build"
        }
        $statusMarker = Join-Path $preflightTree 'artifacts/build-local/last-failure.txt'
        if ($preflightStatus -in @('RebootRequired', 'PolicyBlocked')) {
            $statusText = Get-Content -LiteralPath $statusMarker -Raw
            if ($statusText -notmatch 'Failed at: preflight' -or !$statusText.Contains("Prerequisite status: $preflightStatus") -or !$statusText.Contains($earlyResult.LogPath)) {
                throw "$preflightStatus lost its failure marker or diagnostic log path"
            }
        }
    }
    Write-Host 'PASS: prerequisite cancellation, manual guidance, policy block and reboot never build, stop or install CycleArc.'

    $manualResult = Invoke-BuildLocal -RepoRoot $preflightTree -ManualPrerequisites -PrerequisitePreflight {
        param($root, $noPrompt, $silent, $manual)
        if (!$manual) { throw 'The build entry point lost explicit manual prerequisite mode' }
        [pscustomobject]@{ Status = 'Manual' }
    } -DevRun $forbiddenOperation -StopDesktop $forbiddenOperation -RunSetup $forbiddenOperation -StartLauncher $forbiddenOperation
    if ($manualResult.PrerequisiteStatus -ne 'Manual' -or $manualResult.Built) { throw 'Manual prerequisite guidance must return before building' }

    foreach ($suppression in @(@{ NoPrerequisitePrompt = $true }, @{ SilentInstall = $true })) {
        $script:observedSuppression = $null
        Assert-Throws {
            Invoke-BuildLocal -RepoRoot $preflightTree @suppression -PrerequisitePreflight {
                param($root, $noPrompt, $silent)
                $script:observedSuppression = @($noPrompt, $silent)
                throw 'isolated missing vswhere.exe'
            } -DevRun $forbiddenOperation -StopDesktop $forbiddenOperation -RunSetup $forbiddenOperation
        } 'isolated missing vswhere.exe'
        if (!($observedSuppression[0] -or $observedSuppression[1])) {
            throw 'The build entry point lost non-interactive prerequisite suppression'
        }
    }
    $preflightMarker = Join-Path $preflightTree 'artifacts/build-local/last-failure.txt'
    $preflightText = Get-Content -LiteralPath $preflightMarker -Raw
    foreach ($fragment in @('Failed at: preflight', 'Stage: preflight', 'isolated missing vswhere.exe')) {
        if (!$preflightText.Contains($fragment)) { throw "The preflight marker lost: $fragment" }
    }
    if ($preflightText -match 'still running|already ran|changed the installation') {
        throw 'Preflight guidance claimed unobserved installation/process state'
    }
    Write-Host 'PASS: missing prerequisites record preflight before any build or app operation.'

    $readyResult = Invoke-BuildLocal -RepoRoot $preflightTree -NoInstall -PrerequisitePreflight {
        param($root, $noPrompt, $silent)
        [pscustomobject]@{ Status = 'Ready' }
    } -DevRun { New-FakePublished $preflightTree 'ready-publish' 'ready-setup' | Out-Null } -StopDesktop $forbiddenOperation -RunSetup $forbiddenOperation -StartLauncher $forbiddenOperation
    if (!$readyResult.SetupPath -or (Get-BuildLocalStage) -ne 'package') {
        throw 'A verified prerequisite result did not continue into the original build/package flow'
    }
    if (Test-Path -LiteralPath $preflightMarker) { throw 'A successful run retained a stale preflight failure marker' }
    Write-Host 'PASS: a ready toolchain continues the build and clears a previous failure marker.'

    # Real coordinator -> real build entry, with isolated tools and package adapters.
    # Model SDK 8 only, both with ready C++ tools and with missing C++ tools.
    foreach ($missingVc in @($false, $true)) {
        $script:automaticSdkReady = $false; $script:automaticVcReady = !$missingVc; $script:automaticInstalls = @()
        $automaticResult = Invoke-BuildLocal -RepoRoot $preflightTree -NoInstall -PrerequisitePreflight {
            param($root, $noPrompt, $silent, $manual)
            Invoke-CycleArcBuildPrerequisites -RepoRoot $root -NoPrompt:$noPrompt -SilentInstall:$silent -ManualPrerequisites:$manual `
                -InteractiveProbe { $true } -PathEnabler { $true } -StateResolver {
                    [pscustomobject]@{
                        Status = $(if ($script:automaticSdkReady -and $script:automaticVcReady) { 'Ready' } else { 'Missing' })
                        Components = @([pscustomobject]@{ Name='DotNetSdk'; Label='.NET 10 SDK'; Status=$(if ($script:automaticSdkReady) {'Ready'} else {'Missing'}) })
                        DotNetSdk = [pscustomobject]@{ Status=$(if ($script:automaticSdkReady) {'Ready'} else {'Missing'}); MinimumVersion='10.0.100'; Selected=$(if ($script:automaticSdkReady) {'10.0.401'} else {$null}); Installed=@('8.0.424') }
                        Toolchain = [pscustomobject]@{ Ok=$script:automaticVcReady }
                    }
                } -PowerShellInstaller { throw 'PowerShell is already ready' } -SdkInstaller {
                    $script:automaticInstalls += 'SDK'; $script:automaticSdkReady = $true; [pscustomobject]@{Status='Ready'}
                } -ToolchainInstaller {
                    if (!$script:automaticSdkReady) { throw 'C++ started before SDK readiness' }
                    $script:automaticInstalls += 'C++'; $script:automaticVcReady = $true; [pscustomobject]@{Status='Ready'}
                }
        } -DevRun {
            if (!$script:automaticSdkReady -or !$script:automaticVcReady) { throw 'Build started before prerequisites were ready' }
            New-FakePublished $preflightTree 'automatic-publish' 'automatic-setup' | Out-Null
        } -StopDesktop $forbiddenOperation -RunSetup $forbiddenOperation -StartLauncher $forbiddenOperation
        $expectedInstalls = if ($missingVc) { 'SDK,C++' } else { 'SDK' }
        if (!$automaticResult.SetupPath -or ($automaticInstalls -join ',') -ne $expectedInstalls) { throw 'Automatic preparation did not continue building in the same invocation' }
    }
    Write-Host 'PASS: SDK8-only with ready/missing C++ prepares sequentially and continues the same build invocation without a prerequisite menu.'

    # Drive the actual CMD wrapper with a synthetic entry point and the real reporter.
    if ($IsWindows) {
        $cmdTree = Join-Path $testRoot 'cmd preflight 한글'
        New-GitCycleArcTree $cmdTree
        $cmdScripts = Join-Path $cmdTree 'scripts'
        New-Item -ItemType Directory -Path $cmdScripts | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot 'build-local.cmd') -Destination $cmdTree
        $stubText = @'
param([switch]$NoPrerequisitePrompt)
. '__REPO__/scripts/LocalInstall.ps1'
. '__REPO__/scripts/Build-Local.ps1' -LoadOnly
try {
    Invoke-BuildLocal -RepoRoot (Split-Path -Parent $PSScriptRoot) -NoPrerequisitePrompt -PrerequisitePreflight {
        throw 'CMD isolated missing vswhere.exe'
    } -DevRun { throw 'build must not run' } | Out-Null
}
catch { exit 1 }
exit 0
'@
        $stubText = $stubText.Replace('__REPO__', ($repoRoot -replace "'", "''"))
        Set-Content -LiteralPath (Join-Path $cmdScripts 'Build-Local.ps1') -Value $stubText -Encoding utf8
        $cmdStart = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
        $cmdStart.Arguments = '/d /c ""' + (Join-Path $cmdTree 'build-local.cmd') + '" -NoPrerequisitePrompt"'
        $cmdStart.UseShellExecute = $false
        $cmdStart.CreateNoWindow = $true
        $cmdStart.RedirectStandardInput = $true
        $cmdStart.RedirectStandardOutput = $true
        $cmdStart.RedirectStandardError = $true
        $cmdChild = [Diagnostics.Process]::Start($cmdStart)
        try {
            $cmdChild.StandardInput.Close()
            $cmdOutTask = $cmdChild.StandardOutput.ReadToEndAsync()
            $cmdErrTask = $cmdChild.StandardError.ReadToEndAsync()
            if (!$cmdChild.WaitForExit(30000)) {
                $cmdChild.Kill($true)
                throw 'CMD preflight waited for input in a non-interactive run'
            }
            $cmdOutput = $cmdOutTask.GetAwaiter().GetResult() + $cmdErrTask.GetAwaiter().GetResult()
            if ($cmdChild.ExitCode -ne 1) { throw "CMD did not preserve failure exit code: $($cmdChild.ExitCode)" }
            if ($cmdOutput -match 'before it could record a stage|no failure log is available') {
                throw "CMD contradicted its recorded preflight stage: $cmdOutput"
            }
            foreach ($summaryLine in @('CycleArc build-local failed.', 'Failed at: preflight', 'Stage: preflight')) {
                if ([regex]::Matches($cmdOutput, [regex]::Escape($summaryLine)).Count -ne 1) {
                    throw "CMD must show '$summaryLine' exactly once: $cmdOutput"
                }
            }
        }
        finally { $cmdChild.Dispose() }
        Write-Host 'PASS: real CMD failure records preflight once, preserves exit 1 and never waits on redirected stdin.'
    }

    # Execute the production CLI tail with a fake preflight even when its console
    # probe says interactive. Errors must exit, never request an extra closing input.
    $cliTree = Join-Path $testRoot 'cli prerequisite outcomes'
    New-Item -ItemType Directory -Path $cliTree | Out-Null
    Set-Content -LiteralPath (Join-Path $cliTree 'LocalInstall.ps1') -Value '# isolated CLI fixture'
    $cliHeader = @'
param([string]$Outcome, [switch]$ManualPrerequisites)
$Fast=$false; $NoInstall=$false; $SilentInstall=$false; $NoPrerequisitePrompt=$false; $LoadOnly=$false
function Get-BuildLocalRepoRoot { $PSScriptRoot }
function Get-BuildLocalStage { 'preflight' }
function Test-SetupUiPrerequisiteInteractive { $true }
function Read-Host { Set-Content -LiteralPath (Join-Path $PSScriptRoot 'unexpected-input') -Value 'input attempted'; throw 'Unexpected input' }
function Invoke-BuildLocal {
    if ($Outcome -eq 'Throw') { throw 'isolated prerequisite probe failure' }
    [pscustomobject]@{ PrerequisiteStatus=$Outcome; Built=$false }
}
'@
    $cliTailStart = $scriptText.LastIndexOf('if (!$LoadOnly -and $MyInvocation.InvocationName -ne ''.'') {', [StringComparison]::Ordinal)
    if ($cliTailStart -lt 0) { throw 'Could not locate the production build-local CLI entry point' }
    $cliPath = Join-Path $cliTree 'Build-Local.ps1'
    Set-Content -LiteralPath $cliPath -Value ($cliHeader + "`n" + $scriptText.Substring($cliTailStart)) -Encoding utf8
    foreach ($cliCase in @(
        @{Outcome='Cancelled';Manual=$false;Exit=1}, @{Outcome='PolicyBlocked';Manual=$false;Exit=1},
        @{Outcome='RebootRequired';Manual=$false;Exit=1}, @{Outcome='Throw';Manual=$false;Exit=1},
        @{Outcome='Manual';Manual=$true;Exit=0}, @{Outcome='Throw';Manual=$true;Exit=1}
    )) {
        $cliStart = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $cliStart.ArgumentList.Add('-NoProfile'); $cliStart.ArgumentList.Add('-File'); $cliStart.ArgumentList.Add($cliPath)
        $cliStart.ArgumentList.Add('-Outcome'); $cliStart.ArgumentList.Add($cliCase.Outcome)
        if ($cliCase.Manual) { $cliStart.ArgumentList.Add('-ManualPrerequisites') }
        $cliStart.Environment['CYCLEARC_BUILD_LOCAL_CMD']='1'
        $cliStart.UseShellExecute=$false; $cliStart.CreateNoWindow=$true
        $cliStart.RedirectStandardInput=$true; $cliStart.RedirectStandardOutput=$true; $cliStart.RedirectStandardError=$true
        $cliChild=[Diagnostics.Process]::Start($cliStart)
        try {
            $cliChild.StandardInput.Close()
            $cliOut=$cliChild.StandardOutput.ReadToEndAsync(); $cliErr=$cliChild.StandardError.ReadToEndAsync()
            if (!$cliChild.WaitForExit(15000)) { $cliChild.Kill($true); throw 'Prerequisite CLI waited for input' }
            $cliOutput=$cliOut.GetAwaiter().GetResult() + $cliErr.GetAwaiter().GetResult()
            if ($cliChild.ExitCode -ne $cliCase.Exit -or (Test-Path -LiteralPath (Join-Path $cliTree 'unexpected-input'))) {
                throw "Prerequisite CLI outcome $($cliCase.Outcome) exit/input mismatch: $cliOutput"
            }
        }
        finally { $cliChild.Dispose() }
    }
    Write-Host 'PASS: production CLI prerequisite/manual outcomes and probe failures preserve exit codes without closing-input prompts.'


    # PowerShell variable names are case-insensitive, so a local spelled like a
    # parameter is that parameter, and a typed parameter rejects the assignment.
    # This is the bug class that broke the default dev-run branch, so scan for it.
    function Get-ParameterShadowing([string]$Path) {
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$parseErrors)
        if ($parseErrors) { throw "$Path has parse errors: $($parseErrors[0].Message)" }
        $scopes = @($ast)
        $scopes += @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
        foreach ($scope in $scopes) {
            $body = if ($scope -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $scope.Body } else { $scope }
            $parameterBlock = $body.ParamBlock
            if (!$parameterBlock) { continue }
            $declared = @{}
            foreach ($parameter in $parameterBlock.Parameters) {
                $name = $parameter.Name.VariablePath.UserPath
                $type = if ($parameter.StaticType) { $parameter.StaticType.Name } else { 'Object' }
                $declared[$name.ToLowerInvariant()] = [pscustomobject]@{ Name = $name; Type = $type }
            }
            if ($declared.Count -eq 0) { continue }
            $owner = if ($scope -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $scope } else { $null }
            $scopeName = if ($owner) { $owner.Name } else { '<script>' }
            foreach ($assignment in $body.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
                # FindAll descends into nested functions, whose locals live in
                # their own scope and cannot collide with this one's parameters.
                $enclosing = $assignment.Parent
                while ($enclosing -and $enclosing -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) {
                    $enclosing = $enclosing.Parent
                }
                if ($enclosing -ne $owner) { continue }
                if ($assignment.Left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { continue }
                $used = $assignment.Left.VariablePath.UserPath
                $match = $declared[$used.ToLowerInvariant()]
                if (!$match) { continue }
                # Reassigning the parameter under its own spelling is deliberate;
                # a different spelling of the same variable is the accident.
                if ($used -cne $match.Name) {
                    "$([IO.Path]::GetFileName($Path)) line $($assignment.Extent.StartLineNumber): `$$used in $scopeName is the [$($match.Type)]`$$($match.Name) parameter"
                }
            }
        }
    }

    $shadowing = @(
        foreach ($script in @('scripts/Build-Local.ps1', 'scripts/LocalInstall.ps1', 'scripts/Package.ps1', 'dev-run.ps1')) {
            Get-ParameterShadowing (Join-Path $repoRoot $script)
        }
    )
    if ($shadowing.Count -gt 0) { throw "Parameter/local name collisions: $($shadowing -join '; ')" }
    Write-Host 'PASS: no local variable shadows a parameter by spelling in the install scripts.'


    if (Test-BuildLocalDotnetSdk @('8.0.415 [C:\Program Files\dotnet\sdk\8.0.415]')) {
        throw 'An 8.x SDK listing must be rejected'
    }
    if (!(Test-BuildLocalDotnetSdk @('10.0.400 [C:\Program Files\dotnet\sdk\10.0.400]'))) {
        throw 'A stable 10.0 SDK listing must be accepted'
    }
    if (Test-BuildLocalDotnetSdk @('6.0.428 [C:\Program Files\dotnet\sdk\6.0.428]')) {
        throw 'A 6.x SDK listing must not be treated as sufficient'
    }
    if (!(Test-BuildLocalDotnetSdk @('10.0.400 [C:\Program Files\dotnet\sdk\10.0.400]', '8.0.415 [C:\Program Files\dotnet\sdk\8.0.415]'))) {
        throw 'A mixed 8.x and 10.x SDK listing must be accepted'
    }
    foreach ($invalidSdk in @('11.0.100 [sdk]', '10.0.100-preview.1 [sdk]', '10.0.99 [sdk]')) {
        if (Test-BuildLocalDotnetSdk @($invalidSdk)) { throw "Unsupported SDK listing accepted: $invalidSdk" }
    }
    Write-Host 'PASS: .NET SDK 10.0 detection rejects older, next-major and preview SDKs.'

    $defaultRoot = Get-DefaultManagedInstallRoot
    $log = Join-Path $testRoot 'setup.log'
    # The default run shows the installer and waits for a person. Unattended is opt-in.
    $interactiveDefault = @(Get-SetupArguments -SetupLog $log)
    if ($interactiveDefault -contains '--silent') {
        throw "The default Setup run must not be silent, got $($interactiveDefault -join ' ')"
    }
    if (($interactiveDefault -join ' ') -ne "--log $log") {
        throw "Default Setup arguments were $($interactiveDefault -join ' ')"
    }
    $silent = @(Get-SetupArguments -SetupLog $log -Silent)
    if (($silent -join ' ') -ne "--silent --log $log") { throw "Silent Setup arguments were $($silent -join ' ')" }
    $silentDefaultRoot = @(Get-SetupArguments -SetupLog $log -ExistingRoot $defaultRoot -DefaultRoot $defaultRoot -Silent)
    if ($silentDefaultRoot -contains '--installto') { throw 'Default managed root must not pass --installto' }
    $custom = Join-Path $testRoot 'custom-install'
    $withCustom = @(Get-SetupArguments -SetupLog $log -ExistingRoot $custom -DefaultRoot $defaultRoot)
    if ($withCustom -notcontains '--installto') { throw 'A custom managed root must be passed to Setup.exe --installto' }
    # New installs go under Programs; an existing installation keeps its own location.
    $normalizedDefault = ([IO.Path]::GetFullPath($defaultRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $expectedSuffix = [IO.Path]::Combine('Programs', 'CycleArc')
    if (!$normalizedDefault.EndsWith($expectedSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The new-install default is $normalizedDefault, expected it to end with $expectedSuffix"
    }
    Write-Host 'PASS: Setup.exe shows its window by default, is silent only on request, and keeps an existing root.'

    $missing = Join-Path $testRoot 'missing-root'
    if (Test-ManagedInstallRoot $missing) { throw 'An absent directory was treated as a managed install' }
    $layoutDir = Join-Path $testRoot 'managed-layout'
    New-Item -ItemType Directory -Path (Join-Path $layoutDir 'current') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $layoutDir 'current/CycleArc.exe') -Value 'current'
    Set-Content -LiteralPath (Join-Path $layoutDir 'Update.exe') -Value 'updater'
    if (!(Test-ManagedInstallRoot $layoutDir)) { throw 'A Velopack layout was not recognized' }
    Write-Host 'PASS: managed install layout detection.'

    $tree = Join-Path $testRoot 'tree'
    New-GitCycleArcTree $tree
    $script:setupCalled = $false
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install-a') -DevRun { throw 'synthetic build failure' } -RunSetup {
            param($setup, $args)
            $script:setupCalled = $true
            0
        } -StopDesktop { }
    } 'synthetic build failure'
    if ($setupCalled) { throw 'Setup.exe ran after a failed build' }
    Write-Host 'PASS: failed build does not start Setup.exe.'

    $oldPack = New-FakePublished $tree 'published-a' 'old-setup'
    (Get-Item -LiteralPath $oldPack.SetupPath).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-2)
    $script:staleStopped = $false
    $script:staleSetupCalled = $false
    $script:staleStarted = $false
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install-b') -DevRun { } -SilentInstall `
            -PackagedSetup { $oldPack } -StopDesktop { $script:staleStopped = $true } `
            -RunSetup { $script:staleSetupCalled = $true; 0 } -StartLauncher { $script:staleStarted = $true }
    } 'older than this run'
    if ($staleStopped -or $staleSetupCalled -or $staleStarted) {
        throw 'A leftover Setup.exe reached desktop shutdown, installation or launch'
    }
    if ((Get-BuildLocalStage) -ne 'package') { throw 'A leftover Setup.exe must fail at package' }
    Write-Host 'PASS: leftover Setup.exe is refused before desktop shutdown, installation or launch.'

    $installC = Join-Path $testRoot 'install-c'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $installC -DevRun { } `
            -PackagedSetup { New-FakePublished $tree 'published-c' 'setup-c' } -StopDesktop { } -RunSetup {
            param($setup, $arguments)
            # The default run shows the installer; only -SilentInstall suppresses it.
            if ($arguments -contains '--silent') { throw 'The default run must not install silently' }
            New-Item -ItemType Directory -Path (Join-Path $installC 'current') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $installC 'current/CycleArc.exe') -Value 'stale-previous-build'
            Set-Content -LiteralPath (Join-Path $installC 'CycleArc.exe') -Value 'launcher'
            0
        }
    } 'does not match this build'
    Write-Host 'PASS: same-version skip is reported as failure.'

    $installD = Join-Path $testRoot 'install-d'
    $script:started = $false
    $result = Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $installD -DevRun { } `
        -PackagedSetup { New-FakePublished $tree 'published-d' 'setup-d' } -StopDesktop { } -RunSetup {
        param($setup, $arguments)
        New-Item -ItemType Directory -Path (Join-Path $installD 'current') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $tree 'publish/.dev-staging/CycleArc.exe') -Destination (Join-Path $installD 'current/CycleArc.exe')
        Set-Content -LiteralPath (Join-Path $installD 'CycleArc.exe') -Value 'launcher'
        Set-Content -LiteralPath (Join-Path $installD 'Update.exe') -Value 'updater'
        0
    } -StartLauncher { $script:started = $true } -ProbeStatus {
        [pscustomobject]@{
            Succeeded = $true
            ProcessId = 4242
            ExecutablePath = (Join-Path $installD 'current/CycleArc.exe')
            Version = '0.6.0'
            InstanceId = 'synthetic'
        }
    }
    if (!$result.HashMatched -or !$started) { throw 'Successful replacement did not start the launcher or confirm the hash' }
    if ((Get-Content -LiteralPath (Join-Path $installD 'current/CycleArc.exe') -Raw).Trim() -ne 'published-d') {
        throw 'Installed current/CycleArc.exe was not this build'
    }
    Write-Host 'PASS: matching Setup replacement starts the managed current executable.'

    $installE = Join-Path $testRoot 'install-e'
    New-Item -ItemType Directory -Path $installE -Force | Out-Null
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $installE -DevRun { } `
            -PackagedSetup { New-FakePublished $tree 'published-e' 'setup-e' } -StopDesktop { } -RunSetup { 7 }
    } 'CycleArc-Setup.exe failed'
    if (Test-Path -LiteralPath (Join-Path $installE 'current/CycleArc.exe')) {
        throw 'A failed Setup.exe still wrote an installed executable'
    }
    Write-Host 'PASS: Setup.exe failure does not report a successful replacement.'

    $lease = New-InstallLease -InstallRoot $tree -AllowedRoots @($tree)
    try {
        Assert-Throws { Invoke-BuildLocal -RepoRoot $tree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install-f') -DevRun { } } 'Another dev-run installation'
    }
    finally { $lease.Dispose() }
    Write-Host 'PASS: overlapping build-local runs are rejected by the existing install lease.'

    $spaceRoot = Join-Path $testRoot 'path with space'
    New-GitCycleArcTree $spaceRoot
    $spaceInstall = Join-Path $testRoot 'install space'
    $spaceResult = Invoke-BuildLocal -RepoRoot $spaceRoot -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $spaceInstall -DevRun { } `
        -PackagedSetup { New-FakePublished $spaceRoot 'published-space' 'setup-space' } -StopDesktop { } -RunSetup {
        param($setup, $arguments)
        New-Item -ItemType Directory -Path (Join-Path $spaceInstall 'current') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $spaceRoot 'publish/.dev-staging/CycleArc.exe') -Destination (Join-Path $spaceInstall 'current/CycleArc.exe')
        Set-Content -LiteralPath (Join-Path $spaceInstall 'CycleArc.exe') -Value 'launcher'
        0
    } -StartLauncher { } -ProbeStatus {
        [pscustomobject]@{
            Succeeded = $true
            ProcessId = 99
            ExecutablePath = (Join-Path $spaceInstall 'current/CycleArc.exe')
            Version = '0.6.0'
            InstanceId = 'space'
        }
    }
    if (!$spaceResult.HashMatched) { throw 'A repository path with spaces did not complete' }
    Write-Host 'PASS: repository and install paths that contain spaces.'
    # --- Regression: the default branch, with no -DevRun scriptblock injected. ---
    # $devRun as a local name is the same variable as the [scriptblock]$DevRun
    # parameter, so assigning the real script path to it used to fail the
    # parameter's type constraint before the build ever started.
    $defaultTree = Join-Path $testRoot 'default entry'
    New-GitCycleArcTree $defaultTree
    $devRunMarker = Join-Path $defaultTree 'dev-run-marker.txt'
    New-TestDevRun $defaultTree $devRunMarker 0 | Out-Null
    $defaultInstall = Join-Path $testRoot 'install default'
    $script:defaultStopped = $false
    $defaultResult = Invoke-BuildLocal -RepoRoot $defaultTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $defaultInstall `
        -PackagedSetup { New-FakePublished $defaultTree 'published-default' 'setup-default' } -StopDesktop { $script:defaultStopped = $true } -RunSetup {
            param($setup, $arguments)
            New-Item -ItemType Directory -Path (Join-Path $defaultInstall 'current') -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $defaultTree 'publish/.dev-staging/CycleArc.exe') -Destination (Join-Path $defaultInstall 'current/CycleArc.exe')
            Set-Content -LiteralPath (Join-Path $defaultInstall 'CycleArc.exe') -Value 'launcher'
            0
        } -StartLauncher { } -ProbeStatus {
            [pscustomobject]@{
                Succeeded = $true
                ProcessId = 1234
                ExecutablePath = (Join-Path $defaultInstall 'current/CycleArc.exe')
                Version = '0.6.0'
                InstanceId = 'default'
            }
        }
    if (!(Test-Path -LiteralPath $devRunMarker -PathType Leaf)) {
        throw 'The default branch did not run the real dev-run.ps1'
    }
    $devRunText = (Get-Content -LiteralPath $devRunMarker -Raw).Trim()
    if ($devRunText -notmatch 'NoLaunch=True') { throw "dev-run.ps1 did not receive -NoLaunch: $devRunText" }
    if ($devRunText -notmatch 'Fast=False') { throw "dev-run.ps1 received an unexpected -Fast: $devRunText" }
    if ($devRunText -notmatch [regex]::Escape($defaultTree)) {
        throw "dev-run.ps1 did not run in the repository root: $devRunText"
    }
    if (!$defaultResult.HashMatched) {
        throw 'The default -DevRun branch did not complete the install path'
    }
    # Approval comes first: the parent must not stop a running CycleArc before the person
    # has agreed to install. The installer stops it after that, and cancelling leaves it be.
    if ($defaultStopped) {
        throw 'The default run stopped the running app before the installation was approved'
    }
    Write-Host 'PASS: the default branch starts the real dev-run.ps1 and stops nothing before approval.'

    # The unattended path keeps stopping the desktop itself, because no window will do it.
    $silentTree = Join-Path $testRoot 'silent entry'
    New-GitCycleArcTree $silentTree
    New-TestDevRun $silentTree (Join-Path $silentTree 'dev-run-marker.txt') 0 | Out-Null
    $silentManagedRoot = Join-Path $testRoot 'install silent'
    $script:silentStopped = $false
    $script:silentArguments = @()
    $silentResult = Invoke-BuildLocal -RepoRoot $silentTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $silentManagedRoot -SilentInstall `
        -PackagedSetup { New-FakePublished $silentTree 'published-silent' 'setup-silent' } -StopDesktop { $script:silentStopped = $true } -RunSetup {
            param($setup, $arguments)
            $script:silentArguments = @($arguments)
            New-Item -ItemType Directory -Path (Join-Path $silentManagedRoot 'current') -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $silentTree 'publish/.dev-staging/CycleArc.exe') -Destination (Join-Path $silentManagedRoot 'current/CycleArc.exe')
            Set-Content -LiteralPath (Join-Path $silentManagedRoot 'CycleArc.exe') -Value 'launcher'
            0
        } -StartLauncher { } -ProbeStatus {
            [pscustomobject]@{
                Succeeded = $true
                ProcessId = 4321
                ExecutablePath = (Join-Path $silentManagedRoot 'current/CycleArc.exe')
                Version = '0.6.0'
                InstanceId = 'silent'
            }
        }
    if (!$silentResult.HashMatched) { throw '-SilentInstall did not complete the install path' }
    if (!$silentStopped) { throw '-SilentInstall must stop the running desktop itself' }
    if ($silentArguments -notcontains '--silent') {
        throw "-SilentInstall did not pass --silent: $($silentArguments -join ' ')"
    }
    Write-Host 'PASS: -SilentInstall is the only path that installs unattended and stops the desktop itself.'

    # Cancelling in the setup window is neither success nor failure.
    $cancelTree = Join-Path $testRoot 'cancel entry'
    New-GitCycleArcTree $cancelTree
    New-TestDevRun $cancelTree (Join-Path $cancelTree 'dev-run-marker.txt') 0 | Out-Null
    $cancelInstall = Join-Path $testRoot 'install cancel'
    $cancelPackage = { New-FakePublished $cancelTree 'published-cancel' 'setup-cancel' }
    $script:cancelStopped = $false
    $script:cancelStarted = $false
    $cancelResult = Invoke-BuildLocal -RepoRoot $cancelTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $cancelInstall `
        -PackagedSetup $cancelPackage -StopDesktop { $script:cancelStopped = $true } `
        -StartLauncher { $script:cancelStarted = $true } -RunSetup {
            param($setup, $arguments)
            2
        }
    if (!$cancelResult.Cancelled) { throw 'A cancelled installation was not reported as cancelled' }
    if ($cancelStopped) { throw 'A cancelled installation stopped the running app' }
    if ($cancelStarted) { throw 'A cancelled installation still started the app' }
    if (Test-Path -LiteralPath (Join-Path $cancelInstall 'current/CycleArc.exe')) {
        throw 'A cancelled installation still wrote an installation'
    }
    Write-Host 'PASS: cancelling before install changes nothing and is reported as cancelled, not failed.'

    # The real interactive tail must validate the responder, not treat an old
    # development desktop's successful IPC response as this installation running.
    $interactiveTree = Join-Path $testRoot 'interactive responder'
    New-GitCycleArcTree $interactiveTree
    $interactiveRoot = Join-Path $testRoot 'interactive managed root'
    $interactiveArguments = @{
        RepoRoot = $interactiveTree
        ManagedRoot = $interactiveRoot
        PrerequisitePreflight = { [pscustomobject]@{ Status = 'Ready' } }
        DevRun = { }
        PackagedSetup = { New-FakePublished $interactiveTree 'interactive-published' 'interactive-setup' }
        StopDesktop = { throw 'Interactive caller must not stop before installer approval' }
        RunSetup = {
            New-Item -ItemType Directory -Path (Join-Path $interactiveRoot 'current') -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $interactiveTree 'publish/.dev-staging/CycleArc.exe') -Destination (Join-Path $interactiveRoot 'current/CycleArc.exe')
            Set-Content -LiteralPath (Join-Path $interactiveRoot 'CycleArc.exe') -Value 'launcher'
            0
        }
    }
    Assert-Throws {
        Invoke-BuildLocal @interactiveArguments -InteractiveStatusProbe {
            [pscustomobject]@{ Succeeded = $true; ProcessId = 41396; ExecutablePath = (Join-Path $testRoot 'CycleArc-dev/CycleArc.exe'); Version = '0.6.6+old' }
        }
    } 'not the managed install'
    if ((Get-BuildLocalStage) -ne 'start') { throw 'Wrong desktop responder lost its post-install failure stage' }
    $interactiveReady = Invoke-BuildLocal @interactiveArguments -InteractiveStatusProbe {
        [pscustomobject]@{ Succeeded = $true; ProcessId = 123; ExecutablePath = (Join-Path $interactiveRoot 'current/CycleArc.exe'); Version = '0.10.0+current' }
    }
    if (!$interactiveReady.Running -or $interactiveReady.ProcessId -ne 123) { throw 'Verified interactive desktop was not reported running' }
    $uncheckedRun = Invoke-BuildLocal @interactiveArguments -InteractiveStatusProbe { [pscustomobject]@{ Succeeded = $false } }
    if ($uncheckedRun.Running) { throw 'Unchecked Run choice was reported running or relaunched' }
    $currentStatus = [pscustomobject]@{ ExecutablePath = (Join-Path $interactiveRoot 'current/CycleArc.exe'); Version = '0.6.6+old' }
    Assert-Throws {
        Assert-BuildLocalRunningDesktop -Status $currentStatus -InstalledExe $currentStatus.ExecutablePath -Launcher (Join-Path $interactiveRoot 'CycleArc.exe') `
            -PublishedHash (Get-BuildLocalSha256 $currentStatus.ExecutablePath) -PublishedVersion '0.10.0+current'
    } 'not this build'
    Write-Host 'PASS: real interactive tail rejects development and stale-version responders, verifies the managed desktop, and preserves Run unchecked.'

    # Run a real child through the interactive approval wait without an installer
    # or desktop. It cannot mark the engine started until the parent's ACK arrives.
    $preparationScript = Join-Path $testRoot 'approval-handshake.ps1'
    [IO.File]::WriteAllText($preparationScript, @'
param($StateFile, $AckFile, $EngineMarker, $Mode)
[IO.File]::WriteAllText($StateFile, 'awaiting-approval')
Start-Sleep -Milliseconds 150
if ($Mode -eq 'cancel') { [IO.File]::WriteAllText($StateFile, 'cancelled'); exit 2 }
[IO.File]::WriteAllText($StateFile, 'preparing-desktop')
$waited = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath $AckFile) -and $waited.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 10 }
if (!(Test-Path -LiteralPath $AckFile) -or [IO.File]::ReadAllText($AckFile) -cne 'ready') {
    [IO.File]::WriteAllText($StateFile, 'failed'); exit 1
}
[IO.File]::WriteAllText($EngineMarker, 'started-after-ack')
[IO.File]::WriteAllText($StateFile, 'done')
exit 0
'@)
    foreach ($mode in @('approved', 'cancel', 'shutdown-failure')) {
        $statePath = Join-Path $testRoot ("approval-$mode.state")
        $ackPath = Join-Path $testRoot ("approval-$mode.ack")
        $engineMarker = Join-Path $testRoot ("approval-$mode.engine")
        $script:approvedStops = 0
        $invokeApproval = {
            Invoke-SetupProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-File', $preparationScript, $statePath, $ackPath, $engineMarker, $mode) `
                -StateFile $statePath -DesktopAckFile $ackPath -PollMilliseconds 10 -PrepareDesktop {
                    if ([IO.File]::ReadAllText($statePath) -cne 'preparing-desktop' -or (Test-Path -LiteralPath $engineMarker)) {
                        throw 'Desktop stop happened before approval or after installation'
                    }
                    $script:approvedStops++
                    if ($mode -eq 'shutdown-failure') { throw 'Synthetic verified shutdown failure' }
                }
        }
        if ($mode -eq 'shutdown-failure') {
            Assert-Throws $invokeApproval 'Synthetic verified shutdown failure'
        }
        else {
            $exit = & $invokeApproval
            if ($exit -ne $(if ($mode -eq 'cancel') { 2 } else { 0 })) { throw "Approval child returned wrong exit for $mode" }
        }
        if ($approvedStops -ne $(if ($mode -eq 'cancel') { 0 } else { 1 }) -or
            (Test-Path -LiteralPath $engineMarker) -ne ($mode -eq 'approved')) {
            throw "Approval/stop/install ordering was violated for $mode"
        }
    }
    Write-Host 'PASS: actual child waits for approved verified shutdown; cancellation and shutdown failure never start the engine.'

    # A real installer failure is still a failure, and is not confused with cancelling.
    # Model an arbitrary delay after cancellation without making the suite sleep.
    (Get-Item -LiteralPath (Join-Path $cancelTree 'publish/.dev-velopack/CycleArc-Setup.exe')).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-2)
    $failInstall = Join-Path $testRoot 'install setup-failure'
    $script:failureSetupCalled = $false
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $cancelTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $failInstall `
            -PackagedSetup $cancelPackage -StopDesktop { } -RunSetup {
                param($setup, $arguments)
                $script:failureSetupCalled = $true
                1
            }
    } 'CycleArc-Setup.exe failed (exit 1)'
    if (!$failureSetupCalled -or (Get-BuildLocalStage) -ne 'install') {
        throw 'The build after cancellation did not produce a fresh installer and reach installation'
    }
    Write-Host 'PASS: rebuilding after an aged cancellation reaches the installer and reports exit 1 separately.'

    $fastTree = Join-Path $testRoot 'default fast'
    New-GitCycleArcTree $fastTree
    $fastMarker = Join-Path $fastTree 'dev-run-marker.txt'
    New-TestDevRun $fastTree $fastMarker 0 | Out-Null
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $fastTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install fast') -Fast -StopDesktop { }
    } 'Published CycleArc.exe is missing'
    if ((Get-Content -LiteralPath $fastMarker -Raw) -notmatch 'Fast=True') {
        throw '-Fast was not forwarded to the real dev-run.ps1'
    }
    Write-Host 'PASS: -Fast reaches the real dev-run.ps1 on the default branch.'

    # --- Regression: a failing real dev-run.ps1 leaves the installed app alone. ---
    $failTree = Join-Path $testRoot 'default failure'
    New-GitCycleArcTree $failTree
    $failMarker = 'UISMOKE-SYNTHETIC: Timed out waiting for desktop instance report first.jsonl'
    New-TestDevRun $failTree (Join-Path $failTree 'dev-run-marker.txt') 3 $failMarker | Out-Null
    $script:failStopped = $false
    $script:failSetup = $false
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $failTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install failure') `
            -StopDesktop { $script:failStopped = $true } -RunSetup { $script:failSetup = $true; 0 }
    } 'dev-run.ps1 -NoLaunch failed (exit 3)'
    if ($failStopped) { throw 'A failed build still stopped the running desktop' }
    if ($failSetup) { throw 'A failed build still started Setup.exe' }
    if ((Get-BuildLocalStage) -ne 'build') { throw "A dev-run failure was recorded at stage '$(Get-BuildLocalStage)'" }
    $failureMarker = Join-Path $failTree 'artifacts/build-local/last-failure.txt'
    if (!(Test-Path -LiteralPath $failureMarker -PathType Leaf)) {
        throw 'No last-failure.txt was written for build-local.cmd'
    }
    $failureText = Get-Content -LiteralPath $failureMarker -Raw
    if ($failureText -notmatch 'Stage: build' -or $failureText -notmatch 'exit 3') {
        throw "last-failure.txt does not name the failing stage and cause: $failureText"
    }
    if ($failureText -notmatch 'installed version is unchanged') {
        throw "A pre-install failure must say the installation is unchanged: $failureText"
    }
    $devRunErr = Join-Path $failTree 'artifacts/build-local/dev-run.err.log'
    if (!(Test-Path -LiteralPath $devRunErr -PathType Leaf)) {
        throw 'A failed dev-run did not capture stderr under artifacts/build-local'
    }
    $capturedErr = Get-Content -LiteralPath $devRunErr -Raw
    if ($capturedErr -notmatch [regex]::Escape($failMarker)) {
        throw "Captured stderr did not contain the child error: $capturedErr"
    }
    if ($failureText -notmatch [regex]::Escape($failMarker)) {
        throw "last-failure.txt did not include the captured stderr tail: $failureText"
    }
    if ($failureText -notmatch 'dev-run.err.log \(tail\)') {
        throw "last-failure.txt did not label the captured stderr tail: $failureText"
    }
    Write-Host 'PASS: a real dev-run.ps1 failure stops before the desktop and Setup.exe.'

    # --- Regression: a child sub-stage is the reported failure, not Stage: build. ---
    $stageTree = Join-Path $testRoot 'sub-stage failure'
    New-GitCycleArcTree $stageTree
    $stageMarker = 'UISMOKE-SYNTHETIC: Timed out waiting for desktop instance report first.jsonl'
    New-TestDevRun $stageTree (Join-Path $stageTree 'dev-run-marker.txt') 1 $stageMarker 'ui-smoke-desktop-instance' | Out-Null
    $script:stageStopped = $false
    $script:stageSetup = $false
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $stageTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot (Join-Path $testRoot 'install sub-stage') `
            -StopDesktop { $script:stageStopped = $true } -RunSetup { $script:stageSetup = $true; 0 }
    } 'dev-run.ps1 -NoLaunch failed (exit 1)'
    if ($stageStopped) { throw 'An early targeted-check failure still stopped the running desktop' }
    if ($stageSetup) { throw 'An early targeted-check failure still started Setup.exe' }
    if ((Get-BuildLocalStage) -ne 'ui-smoke-desktop-instance') {
        throw "A desktop-instance failure was recorded at stage '$(Get-BuildLocalStage)'"
    }
    $stageText = Get-Content -LiteralPath (Join-Path $stageTree 'artifacts/build-local/last-failure.txt') -Raw
    if ($stageText -notmatch 'Failed at: ui-smoke-desktop-instance') {
        throw "last-failure.txt did not expose the sub-stage: $stageText"
    }
    if ($stageText -notmatch 'Stage: ui-smoke-desktop-instance') {
        throw "last-failure.txt Stage: still collapsed the failure: $stageText"
    }
    if ($stageText -match 'Stage: build') {
        throw "A desktop-instance timeout must not be reported as Stage: build: $stageText"
    }
    if ($stageText -notmatch 'Child log:') {
        throw "last-failure.txt did not name the child log: $stageText"
    }
    if ($stageText -notmatch [regex]::Escape($stageMarker)) {
        throw "last-failure.txt did not include the child root cause: $stageText"
    }
    if ($stageText -notmatch 'installed version is unchanged') {
        throw "An early targeted-check failure must leave the installation unchanged: $stageText"
    }
    Write-Host 'PASS: a desktop-instance child failure is reported as ui-smoke-desktop-instance, not Stage: build.'

    # --- Regression: the console ends on the child's real cause, not on a long stdout tail. ---
    # stdout carries a long PASS list and very long lines; stderr carries one unique failure.
    $summaryTree = Join-Path $testRoot 'summary failure'
    New-GitCycleArcTree $summaryTree
    $summaryCause = 'System.InvalidOperationException: Case: Korean / Light / zoom 150 / mixed: SYNTHETIC-UNIQUE-CAUSE-4821'
    $summaryBody = @(
        '[CmdletBinding()]',
        'param([switch]$Fast, [switch]$NoLaunch)',
        'if ($env:CYCLEARC_DEV_RUN_STAGE_FILE) { [IO.File]::WriteAllText($env:CYCLEARC_DEV_RUN_STAGE_FILE, "ui-smoke-full`n") }',
        '1..300 | ForEach-Object { [Console]::Out.WriteLine("PASS: synthetic check $_") }',
        '1..40 | ForEach-Object { [Console]::Out.WriteLine("e" * 2000) }',
        '[Console]::Out.WriteLine("[ui-smoke] START observation-removal")',
        '[Console]::Error.WriteLine("[ui-smoke] FAIL observation-removal")',
        ('[Console]::Error.WriteLine(''Exception: {0}'')' -f $summaryCause),
        '[Console]::Error.WriteLine("   at CycleArc.UiSmoke.Synthetic.Run()")',
        '[Console]::Out.Flush(); [Console]::Error.Flush()',
        'exit 7'
    )
    Set-Content -LiteralPath (Join-Path $summaryTree 'dev-run.ps1') -Value ($summaryBody -join "`n") -Encoding utf8
    $script:summaryCalls = 0
    $script:summaryConsole = [Collections.Generic.List[string]]::new()
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $summaryTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } `
            -ManagedRoot (Join-Path $testRoot 'install summary') -StopDesktop { $script:summaryCalls++ } `
            -RunSetup { $script:summaryCalls++; 0 } -StartLauncher { $script:summaryCalls++ } 6>&1 |
            ForEach-Object { foreach ($line in ([string]$_ -split "`r?`n")) { $script:summaryConsole.Add($line) } }
    } 'dev-run.ps1 -NoLaunch failed (exit 7)'
    if ($summaryCalls -ne 0) { throw "A failed build reached stop/Setup/launch $summaryCalls time(s)" }
    $consoleEnd = ($summaryConsole | Where-Object { $_ } | Select-Object -Last 10) -join "`n"
    if (!$consoleEnd.Contains("Cause: $summaryCause") -or !$consoleEnd.Contains('Child exit: 7') -or !$consoleEnd.Contains('Check: observation-removal')) {
        throw "The console does not end with the child's check, cause and exit: $consoleEnd"
    }
    $summaryText = Get-Content -LiteralPath (Join-Path $summaryTree 'artifacts/build-local/last-failure.txt') -Raw
    $summaryEnd = (($summaryText.TrimEnd() -split "`r?`n") | Select-Object -Last 10) -join "`n"
    foreach ($expected in @(
        '===== CycleArc build-local failure summary =====', 'Stage: ui-smoke-full', 'Check: observation-removal',
        "Cause: $summaryCause", 'Child exit: 7',
        ('Details: ' + (Join-Path $summaryTree 'artifacts/build-local/dev-run.err.log')), 'installed version is unchanged')) {
        if (!$summaryEnd.Contains($expected)) { throw "The end of last-failure.txt is missing '$expected': $summaryEnd" }
    }
    if (($summaryText -split "`r?`n" | ForEach-Object Length | Measure-Object -Maximum).Maximum -gt 700) {
        throw 'last-failure.txt still repeats unbounded log lines'
    }
    $capturedOut = Get-Content -LiteralPath (Join-Path $summaryTree 'artifacts/build-local/dev-run.out.log')
    if (@($capturedOut | Where-Object { $_.Length -eq 2000 }).Count -ne 40) { throw 'The full stdout log was not preserved' }
    Write-Host 'PASS: a long stdout tail cannot bury the child failure; the summary ends with check, cause, exit and log.'

    # --- Regression: a nonzero exit with empty stderr stays a failure and invents no cause. ---
    $silentTree = Join-Path $testRoot 'silent failure'
    New-GitCycleArcTree $silentTree
    Set-Content -LiteralPath (Join-Path $silentTree 'dev-run.ps1') -Encoding utf8 -Value (@(
        '[CmdletBinding()]', 'param([switch]$Fast, [switch]$NoLaunch)',
        '1..50 | ForEach-Object { [Console]::Out.WriteLine("PASS: synthetic check $_") }', 'exit 5') -join "`n")
    $script:silentCalls = 0
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $silentTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } `
            -ManagedRoot (Join-Path $testRoot 'install silent') -StopDesktop { $script:silentCalls++ } `
            -RunSetup { $script:silentCalls++; 0 } -StartLauncher { $script:silentCalls++ } 6>$null
    } 'dev-run.ps1 -NoLaunch failed (exit 5)'
    if ($silentCalls -ne 0) { throw "A silent failure reached stop/Setup/launch $silentCalls time(s)" }
    $silentText = Get-Content -LiteralPath (Join-Path $silentTree 'artifacts/build-local/last-failure.txt') -Raw
    foreach ($expected in @('Check: not identified from the captured logs',
        'Cause: not identified from the captured logs (dev-run.err.log is empty)', 'Child exit: 5')) {
        if (!$silentText.Contains($expected)) { throw "A silent failure summary is missing '$expected': $silentText" }
    }
    if ($silentText -match 'Check: observation-removal|SYNTHETIC-UNIQUE') { throw "Another run's failure leaked into this summary: $silentText" }
    Write-Host 'PASS: an empty-stderr nonzero exit is still a failure and its summary does not invent a check or cause.'

    # --- Regression: a post-Setup failure never claims the old install survived. ---
    $lateTree = Join-Path $testRoot 'late failure'
    New-GitCycleArcTree $lateTree
    New-TestDevRun $lateTree (Join-Path $lateTree 'dev-run-marker.txt') 0 'Exception: SUCCESSFUL-CHILD-NOT-THE-CAUSE' 'ui-smoke-full' | Out-Null
    $lateInstall = Join-Path $testRoot 'install late'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $lateTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $lateInstall `
            -PackagedSetup { New-FakePublished $lateTree 'published-late' 'setup-late' } -StopDesktop { } -RunSetup {
            param($setup, $arguments)
            New-Item -ItemType Directory -Path (Join-Path $lateInstall 'current') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $lateInstall 'current/CycleArc.exe') -Value 'a different build'
            Set-Content -LiteralPath (Join-Path $lateInstall 'CycleArc.exe') -Value 'launcher'
            0
        }
    } 'does not match this build'
    if ((Get-BuildLocalStage) -ne 'verify-install') {
        throw "A post-Setup failure was recorded at stage '$(Get-BuildLocalStage)'"
    }
    $lateText = Get-Content -LiteralPath (Join-Path $lateTree 'artifacts/build-local/last-failure.txt') -Raw
    if ($lateText -match 'installed version is unchanged') {
        throw "A failure after Setup.exe ran must not claim the installation is unchanged: $lateText"
    }
    if ($lateText -notmatch 'Do not assume the previous version is intact') {
        throw "A post-Setup failure must warn that the previous version may be gone: $lateText"
    }
    $lateSummary = ($lateText -split '===== CycleArc build-local failure summary =====')[-1]
    if (!$lateSummary.Contains('Child started: true') -or !$lateSummary.Contains('Child exit: 0') -or
        !$lateSummary.Contains('does not match this build') -or $lateSummary.Contains('SUCCESSFUL-CHILD-NOT-THE-CAUSE')) {
        throw 'Post-Setup failure reused the successful child cause or lost its exit'
    }
    Write-Host 'PASS: failure guidance follows the stage the run actually reached.'

    # --- Regression: Invoke-WindowedProcess starts a real process when no
    # WorkingDirectory is supplied, and honours one with spaces and Hangul. ---
    $cwdBody = New-TestPowerShellBody 'report-cwd' @'
param([Parameter(Mandatory)][string]$OutFile, [int]$Code = 0)
Set-Content -LiteralPath $OutFile -Value (Get-Location).Path
exit $Code
'@
    $cwdReport = Join-Path $testRoot 'cwd-report.txt'
    $noDirectoryExit = Invoke-WindowedProcess -FilePath $pwshPath -TimeoutSeconds 120 `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $cwdBody, '-OutFile', $cwdReport, '-Code', '5')
    if ($noDirectoryExit -ne 5) { throw "An omitted -WorkingDirectory did not return the real exit code: $noDirectoryExit" }
    if (!(Test-Path -LiteralPath $cwdReport -PathType Leaf)) { throw 'An omitted -WorkingDirectory did not start the process' }
    Write-Host 'PASS: Invoke-WindowedProcess starts a real process with no WorkingDirectory.'

    $unicodeDirectory = Join-Path $testRoot 'set up 설치 폴더'
    New-Item -ItemType Directory -Path $unicodeDirectory -Force | Out-Null
    $unicodeReport = Join-Path $unicodeDirectory '작업 경로.txt'
    $unicodeExit = Invoke-WindowedProcess -FilePath $pwshPath -WorkingDirectory $unicodeDirectory -TimeoutSeconds 120 `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $cwdBody, '-OutFile', $unicodeReport)
    if ($unicodeExit -ne 0) { throw "A Hangul working directory failed with exit $unicodeExit" }
    $reportedCwd = (Get-Content -LiteralPath $unicodeReport -Raw).Trim()
    if ([IO.Path]::GetFullPath($reportedCwd) -ne [IO.Path]::GetFullPath($unicodeDirectory)) {
        throw "The process did not start in the requested working directory: '$reportedCwd'"
    }
    Write-Host 'PASS: Invoke-WindowedProcess honours a working directory with spaces and Hangul.'

    Assert-Throws {
        Invoke-WindowedProcess -FilePath $pwshPath -WorkingDirectory (Join-Path $testRoot 'absent directory') `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', 'exit 0')
    } 'Working directory does not exist'
    Write-Host 'PASS: a missing working directory is reported instead of passed through blank.'

    # --- Regression: the installer goes through Invoke-WindowedProcess itself.
    # Only Unix can launch a script directly; the Windows Setup.exe launch is
    # covered by the build-local.cmd end-to-end job. ---
    $setupBody = New-TestPowerShellBody 'fake-setup-body' @'
Set-StrictMode -Version Latest
$arguments = @($args)
$log = ''
for ($i = 0; $i -lt $arguments.Count - 1; $i++) { if ($arguments[$i] -eq '--log') { $log = $arguments[$i + 1] } }
if (!$log) { exit 64 }
New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
Set-Content -LiteralPath $log -Value ('args=' + ($arguments -join '|') + ' cwd=' + (Get-Location).Path)
$install = $env:CYCLEARC_TEST_INSTALL_ROOT
$staging = $env:CYCLEARC_TEST_STAGING_EXE
New-Item -ItemType Directory -Path (Join-Path $install 'current') -Force | Out-Null
Copy-Item -LiteralPath $staging -Destination (Join-Path $install 'current/CycleArc.exe') -Force
Set-Content -LiteralPath (Join-Path $install 'CycleArc.exe') -Value 'launcher'
exit 0
'@
    $realSetup = New-TestLaunchableScript 'fake-setup' $setupBody
    if ($realSetup) {
        $setupTree = Join-Path $testRoot 'real setup'
        New-GitCycleArcTree $setupTree
        New-TestDevRun $setupTree (Join-Path $setupTree 'dev-run-marker.txt') 0 | Out-Null
        $setupInstall = Join-Path $testRoot 'install real setup'
        $setupStage = Join-Path $setupTree 'publish/.dev-staging'
        New-Item -ItemType Directory -Path $setupStage -Force | Out-Null
        $setupStagingExe = Join-Path $setupStage 'CycleArc.exe'
        Set-Content -LiteralPath $setupStagingExe -Value 'published-real-setup'
        $env:CYCLEARC_TEST_INSTALL_ROOT = $setupInstall
        $env:CYCLEARC_TEST_STAGING_EXE = $setupStagingExe
        try {
            $setupResult = Invoke-BuildLocal -RepoRoot $setupTree -PrerequisitePreflight { [pscustomobject]@{ Status = 'Ready' } } -ManagedRoot $setupInstall -StopDesktop { } `
                -PackagedSetup {
                    Set-Content -LiteralPath $setupStagingExe -Value 'published-real-setup'
                    $freshSetup = New-TestLaunchableScript 'fake-setup' $setupBody
                    [pscustomobject]@{ StagingExe = $setupStagingExe; SetupPath = $freshSetup }
                } `
                -StartLauncher { } -ProbeStatus {
                    [pscustomobject]@{
                        Succeeded = $true
                        ProcessId = 777
                        ExecutablePath = (Join-Path $setupInstall 'current/CycleArc.exe')
                        Version = '0.6.0'
                        InstanceId = 'real-setup'
                    }
                }
        }
        finally {
            Remove-Item Env:CYCLEARC_TEST_INSTALL_ROOT -ErrorAction SilentlyContinue
            Remove-Item Env:CYCLEARC_TEST_STAGING_EXE -ErrorAction SilentlyContinue
        }
        if (!$setupResult.HashMatched) { throw 'The real installer launch did not install this build' }
        $installerLog = Join-Path $setupTree 'artifacts/build-local/setup.log'
        $installerText = Get-Content -LiteralPath $installerLog -Raw
        if ($installerText -match '--silent') {
            throw "The default run invoked the installer silently: $installerText"
        }
        if ($installerText -notmatch [regex]::Escape($installerLog)) {
            throw "The installer did not receive its log path intact: $installerText"
        }
        if ($installerText -notmatch [regex]::Escape((Split-Path -Parent $realSetup))) {
            throw "The installer did not start in the installer directory: $installerText"
        }
        Write-Host 'PASS: the installer is launched with its window and its log path, with no -RunSetup override.'
    }
    else {
        Write-Host 'SKIP: direct script launch is unavailable here; the CMD end-to-end job covers the real Setup.exe launch.'
    }

    # --- Regression: desktop IPC time limits are reached, instead of sitting in
    # a synchronous ReadToEnd that never returns. ---
    $ipcLogDirectory = Join-Path $testRoot 'ipc logs'
    New-Item -ItemType Directory -Path $ipcLogDirectory -Force | Out-Null
    $hangPidFile = Join-Path $ipcLogDirectory 'hang.pid'
    $hangBody = New-TestPowerShellBody 'hang' (@(
        ('$PID | Set-Content -LiteralPath "{0}"' -f $hangPidFile),
        'Start-Sleep -Seconds 600'
    ) -join "`n")
    $hangWatch = [Diagnostics.Stopwatch]::StartNew()
    $hangResult = Invoke-DesktopIpcProcess -Executable $pwshPath -TimeoutMilliseconds 4000 -OutputDrainMilliseconds 4000 `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $hangBody)
    $hangWatch.Stop()
    if (!$hangResult.TimedOut) { throw 'A probe that never exits was not reported as a timeout' }
    if ($hangWatch.Elapsed.TotalSeconds -gt 25) { throw "The time limit took $($hangWatch.Elapsed.TotalSeconds)s to apply" }
    if ($hangResult.Reason -notmatch 'did not finish within') { throw "No cause was recorded: $($hangResult.Reason)" }
    $hangProcessId = [int]((Get-Content -LiteralPath $hangPidFile -Raw).Trim())
    $leftoverProbe = Get-TestProcessById $hangProcessId
    if ($leftoverProbe) {
        $leftoverProbe.Dispose()
        Stop-TestProcessById $hangProcessId
        throw "The timed-out probe PID $hangProcessId was left running"
    }
    Write-Host 'PASS: a probe that never exits is stopped inside its time limit.'

    # A synchronous ReadToEnd on stdout deadlocks here: the child blocks once the
    # unread stderr pipe fills, so it never writes the status the reader waits on.
    $statusJson = '{"succeeded":true,"processId":4321,"exePath":"noisy/CycleArc.exe","version":"0.6.0","instanceId":"noisy"}'
    $noisyBody = New-TestPowerShellBody 'noisy-stderr' (@(
        '$line = "e" * 1024',
        'for ($i = 0; $i -lt 1024; $i++) { [Console]::Error.WriteLine($line) }',
        ('[Console]::Out.Write(''{0}'')' -f $statusJson),
        'exit 0'
    ) -join "`n")
    $noisyWatch = [Diagnostics.Stopwatch]::StartNew()
    $noisyResult = Invoke-DesktopIpcProcess -Executable $pwshPath -TimeoutMilliseconds 60000 `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $noisyBody)
    $noisyWatch.Stop()
    if ($noisyResult.TimedOut) { throw 'A probe writing large stderr was treated as a timeout' }
    if ($noisyResult.StandardError.Length -lt 1000000) {
        throw "stderr was not drained: $($noisyResult.StandardError.Length) bytes"
    }
    $noisyStatus = ConvertFrom-DesktopStatusJson $noisyResult.StandardOutput
    if (!$noisyStatus -or ![bool]$noisyStatus.Succeeded) { throw 'stdout was lost while stderr was drained' }
    Write-Host ('PASS: a probe writing {0} bytes of stderr still returns its status ({1:n1}s).' -f `
        $noisyResult.StandardError.Length, $noisyWatch.Elapsed.TotalSeconds)

    # An exited probe whose stdout handle is still held open must not restart an
    # unbounded wait in the output-collection step.
    $holderPidFile = Join-Path $ipcLogDirectory 'holder.pid'
    $holderBody = New-TestPowerShellBody 'holder' (@(
        ('$PID | Set-Content -LiteralPath "{0}"' -f $holderPidFile),
        'Start-Sleep -Seconds 30'
    ) -join "`n")
    # The grandchild inherits this process's stdout handle, so the pipe stays
    # open after the probe itself exits.
    $leakBody = New-TestPowerShellBody 'leak-stdout' (@(
        ('$start = [Diagnostics.ProcessStartInfo]::new("{0}")' -f $pwshPath),
        '$start.UseShellExecute = $false',
        '$start.CreateNoWindow = $true',
        'foreach ($a in @(''-NoProfile'', ''-NonInteractive'', ''-File'')) { [void]$start.ArgumentList.Add($a) }',
        ('[void]$start.ArgumentList.Add("{0}")' -f $holderBody),
        '$child = [Diagnostics.Process]::Start($start)',
        'Start-Sleep -Milliseconds 500',
        '$child.Dispose()',
        'exit 0'
    ) -join "`n")
    try {
        $leakWatch = [Diagnostics.Stopwatch]::StartNew()
        $leakResult = Invoke-DesktopIpcProcess -Executable $pwshPath -TimeoutMilliseconds 30000 -OutputDrainMilliseconds 2000 `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $leakBody)
        $leakWatch.Stop()
        if ($leakWatch.Elapsed.TotalSeconds -gt 25) { throw "Output collection waited $($leakWatch.Elapsed.TotalSeconds)s" }
        if ($leakResult.Reason -notmatch 'output was still open') {
            throw "A bounded output drain was not reported: '$($leakResult.Reason)'"
        }
        Write-Host 'PASS: output collection is bounded when a leftover child holds the pipe open.'
    }
    finally {
        if (Test-Path -LiteralPath $holderPidFile -PathType Leaf) {
            Stop-TestProcessById ([int]((Get-Content -LiteralPath $holderPidFile -Raw).Trim()))
        }
    }

    # --- Regression: the wrappers report the cause instead of returning silence. ---
    $unreadable = Invoke-DesktopStatus -Executable $pwshPath -LogDirectory $ipcLogDirectory -TimeoutMilliseconds 30000
    if ($null -ne $unreadable) { throw 'A probe that cannot answer was treated as a desktop status' }
    Assert-Throws {
        Invoke-DesktopShutdown -Executable $pwshPath -LogDirectory $ipcLogDirectory -TimeoutMilliseconds 30000
    } 'CycleArc desktop shutdown'
    $ipcLog = Join-Path $ipcLogDirectory 'desktop-ipc.log'
    if (!(Test-Path -LiteralPath $ipcLog -PathType Leaf)) { throw 'No desktop IPC diagnostics were written' }
    $ipcText = Get-Content -LiteralPath $ipcLog -Raw
    foreach ($ipcStage in @('desktop-status', 'desktop-shutdown')) {
        if ($ipcText -notmatch [regex]::Escape($ipcStage)) { throw "The IPC log does not record the $ipcStage step" }
    }
    Write-Host 'PASS: unusable desktop IPC answers are logged with their step and cause.'

    # --- Regression: stage progress is visible while the child is still running. ---------
    # A long-lived synthetic child announces two stages, then holds. The parent console is
    # inspected before that child is allowed to finish, so an end-of-run dump cannot pass.
    $liveOut = Join-Path $testRoot 'live-progress.out.log'
    $liveErr = Join-Path $testRoot 'live-progress.err.log'
    $liveReleaseFile = Join-Path $testRoot 'live-progress.release'
    $liveBody = New-TestPowerShellBody 'live-progress' (@(
        # Write-Host with no explicit flush, in dev-run.ps1's exact output shape: the
        # human-readable line plus the '##dev-run##' marker a parent matches. The bare
        # '[mm:ss.d] name' line of a nested run must not be mistaken for a stage of this one.
        'Write-Host "[00:00.1] restore"',
        'Write-Host "##dev-run## 00:00.1 start restore"',
        'Write-Host "[00:02.5] restore passed"',
        'Write-Host "##dev-run## 00:02.5 passed restore"',
        'Write-Host "[00:01.0] package-verify"',
        'Write-Host "[00:01.1] package-verify passed"',
        'Write-Host "[00:02.6] build"',
        'Write-Host "##dev-run## 00:02.6 start build"',
        ('while (!(Test-Path -LiteralPath "{0}")) {{ Start-Sleep -Milliseconds 50 }}' -f $liveReleaseFile),
        'Write-Host "[00:09.9] build passed"',
        'Write-Host "##dev-run## 00:09.9 passed build"',
        'exit 0'
    ) -join "`n")

    # Capture the parent's own console while the child is still blocked.
    $liveTranscript = Join-Path $testRoot 'live-progress.transcript.log'
    $liveWatcher = Start-ThreadJob -ScriptBlock {
        param($transcript, $release)
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($deadline.Elapsed.TotalSeconds -lt 60) {
            if (Test-Path -LiteralPath $transcript -PathType Leaf) {
                $stream = [IO.File]::Open($transcript, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                    ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                try {
                    $reader = [IO.StreamReader]::new($stream)
                    $text = $reader.ReadToEnd()
                }
                finally { $stream.Dispose() }
                if ($text -match 'build running') {
                    Set-Content -LiteralPath $release -Value 'go'
                    return $text
                }
            }
            Start-Sleep -Milliseconds 100
        }
        Set-Content -LiteralPath $release -Value 'timeout'
        return '(the parent console never showed the running stage)'
    } -ArgumentList $liveTranscript, $liveReleaseFile

    Start-Transcript -Path $liveTranscript | Out-Null
    try {
        $liveExit = Invoke-ExternalProcess -FilePath $pwshPath -TimeoutSeconds 90 -NoNewWindow -StreamProgress `
            -StandardOutputPath $liveOut -StandardErrorPath $liveErr `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $liveBody)
    }
    finally { Stop-Transcript | Out-Null }
    $liveObserved = Receive-Job -Job $liveWatcher -Wait -AutoRemoveJob

    if ($liveExit -ne 0) { throw "The live-progress child returned exit $liveExit" }
    if ($liveObserved -notmatch 'restore passed') {
        throw "A completed stage was not shown while the child was still running: $liveObserved"
    }
    if ($liveObserved -notmatch 'build running') {
        throw "The in-flight stage was not shown while the child was still running: $liveObserved"
    }
    if ($liveObserved -match 'build passed') {
        throw 'The watcher released the child too late to prove the output was live.'
    }
    # Cumulative elapsed and the stage's own cost are shown as separate, labelled numbers.
    if ($liveObserved -notmatch 'dev-run 00:02\.5 elapsed \| restore passed \(stage took 00:02\.4\)') {
        throw "Stage elapsed/cost was not reported distinctly: $liveObserved"
    }
    $liveFinal = Get-Content -LiteralPath $liveTranscript -Raw
    if ($liveFinal -notmatch 'build passed') { throw 'The final stage never reached the console.' }
    if ($liveFinal -match 'package-verify') {
        throw "A nested run's stage line was reported as this run's progress."
    }
    # Each completed stage appears exactly once.
    foreach ($once in @('restore passed', 'build passed')) {
        $hits = ([regex]::Matches($liveFinal, [regex]::Escape("| $once"))).Count
        if ($hits -ne 1) { throw "'$once' was printed $hits times instead of once." }
    }
    Write-Host 'PASS: dev-run stage progress reaches the console while the child is still running.'

    # --- Regression: a split UTF-8 sequence is neither mangled nor duplicated. -----------
    $splitLog = Join-Path $testRoot 'split-utf8.log'
    $splitBytes = [Text.Encoding]::UTF8.GetBytes("##dev-run## 00:00.1 start 준비" + "`n" + "##dev-run## 00:01.0 passed 준비" + "`n")
    [IO.File]::WriteAllBytes($splitLog, $splitBytes[0..($splitBytes.Length - 6)])
    $splitReader = New-BuildLocalProgressReader $splitLog
    Reset-BuildLocalChildProgress
    $firstHalf = & { Invoke-BuildLocalProgressPump -Reader $splitReader } 6>&1 | Out-String
    [IO.File]::WriteAllBytes($splitLog, $splitBytes)
    $secondHalf = & { Invoke-BuildLocalProgressPump -Reader $splitReader -Final } 6>&1 | Out-String
    $splitAll = $firstHalf + $secondHalf
    if ($splitAll -match [char]0xFFFD) { throw "A split UTF-8 sequence decoded to a replacement character: $splitAll" }
    if ($splitAll -notmatch '준비 passed') { throw "The split line never completed: $splitAll" }
    if (([regex]::Matches($splitAll, '준비 running')).Count -ne 1) {
        throw "A partially written line was printed more than once: $splitAll"
    }
    if ($splitReader.Fault) { throw "Reading a live log reported a fault: $($splitReader.Fault)" }
    Write-Host 'PASS: a partially written UTF-8 stage line is neither mangled nor repeated.'

    # --- Regression: a progress-reading failure is reported apart from the child's own. ---
    $faultReader = New-BuildLocalProgressReader (Join-Path $testRoot 'no-such-directory/never.log')
    $faultReader.Fault = 'synthetic read failure'
    Invoke-BuildLocalProgressPump -Reader $faultReader -Final
    if ($faultReader.Fault -ne 'synthetic read failure') { throw 'A recorded progress fault was cleared.' }
    Write-Host 'PASS: a progress-reading fault is kept distinct from the child result.'

    # --- Regression: the AOT prerequisite check looks at components, not at vswhere.exe. ---
    # The probes are injected, so each outcome is exercised without installing or removing
    # any part of Visual Studio.
    $fakeVsWhere = Join-Path $testRoot 'fake-vswhere.exe'
    Set-Content -LiteralPath $fakeVsWhere -Value 'not a real vswhere'
    $fakeInstall = Join-Path $testRoot 'fake-vs'
    $fakeLinker = Join-Path $fakeInstall 'link.exe'
    $fakeSdk = Join-Path $testRoot 'fake-kernel32.lib'

    # A. No vswhere at all.
    $noVsWhere = Resolve-SetupUiToolchain -VsWhereLocator { $null }
    if ($noVsWhere.Ok) { throw 'A machine without vswhere was reported as ready' }
    if ($noVsWhere.Missing -notmatch 'vswhere') { throw "Unexpected reason: $($noVsWhere.Missing)" }

    # B. vswhere present, but no installation carries the C++ tools. This is the case the old
    #    check passed: vswhere.exe existed, so it declared the machine ready.
    $noCpp = Resolve-SetupUiToolchain -VsWhereLocator { $fakeVsWhere } -VsWhereInvoker { param($p, $a) @() }
    if ($noCpp.Ok) { throw 'Visual Studio without the C++ tools was reported as ready' }
    if ($noCpp.Missing -notmatch 'VC\.Tools\.x86\.x64') { throw "Unexpected reason: $($noCpp.Missing)" }
    # The component really is what it asks vswhere for.
    $script:observedVsWhereArgs = @()
    $null = Resolve-SetupUiToolchain -VsWhereLocator { $fakeVsWhere } `
        -VsWhereInvoker { param($p, $a) $script:observedVsWhereArgs = @($a); @() }
    if ($observedVsWhereArgs -notcontains '-requires') { throw 'vswhere was not asked to require a component' }
    if ($observedVsWhereArgs -notcontains 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64') {
        throw "vswhere was not asked for the C++ tools: $($observedVsWhereArgs -join ' ')"
    }

    # C. The component is reported but the linker is not on disk.
    $noLinker = Resolve-SetupUiToolchain -VsWhereLocator { $fakeVsWhere } `
        -VsWhereInvoker { param($p, $a) @($fakeInstall) } -LinkerProbe { param($i) $null }
    if ($noLinker.Ok) { throw 'A missing MSVC linker was reported as ready' }
    if ($noLinker.Missing -notmatch 'link\.exe') { throw "Unexpected reason: $($noLinker.Missing)" }

    # C2. Linker present, Windows SDK missing.
    $noSdk = Resolve-SetupUiToolchain -VsWhereLocator { $fakeVsWhere } `
        -VsWhereInvoker { param($p, $a) @($fakeInstall) } -LinkerProbe { param($i) $fakeLinker } -SdkProbe { $null }
    if ($noSdk.Ok) { throw 'A missing Windows SDK was reported as ready' }
    if ($noSdk.Missing -notmatch 'Windows SDK') { throw "Unexpected reason: $($noSdk.Missing)" }

    # D. Everything present.
    $ready = Resolve-SetupUiToolchain -VsWhereLocator { $fakeVsWhere } `
        -VsWhereInvoker { param($p, $a) @($fakeInstall) } -LinkerProbe { param($i) $fakeLinker } -SdkProbe { $fakeSdk }
    if (!$ready.Ok) { throw "A complete toolchain was rejected: $($ready.Missing)" }
    if ($ready.Linker -ne $fakeLinker -or $ready.SdkLibrary -ne $fakeSdk) {
        throw 'The resolved toolchain did not report the components it found'
    }

    # E. vswhere only in its fixed install location, not on PATH, is still found.
    $installerDir = Join-Path $testRoot 'pf/Microsoft Visual Studio/Installer'
    New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installerDir 'vswhere.exe') -Value 'stub'
    # PATH and both Program Files roots are isolated, so this really exercises the fixed
    # location rather than finding the machine's own vswhere.
    $previousProgramFilesX86 = ${env:ProgramFiles(x86)}
    $previousProgramFiles = $env:ProgramFiles
    $previousPath = $env:PATH
    ${env:ProgramFiles(x86)} = Join-Path $testRoot 'pf'
    $env:ProgramFiles = Join-Path $testRoot 'pf-none'
    $env:PATH = Join-Path $testRoot 'empty-path'
    try {
        $located = Find-VsWhere
        if (!$located) { throw 'vswhere in its standard location was not found' }
        if ((Split-Path -Parent $located) -ne $installerDir) { throw "Found the wrong vswhere: $located" }
    }
    finally {
        ${env:ProgramFiles(x86)} = $previousProgramFilesX86
        $env:ProgramFiles = $previousProgramFiles
        $env:PATH = $previousPath
    }

    # The failure message has to say what to install, and must not imply that running the
    # finished installer needs the C++ tools.
    $assertMessage = ''
    try { Assert-SetupUiToolchain -Resolver { [pscustomobject]@{ Ok = $false; Missing = 'test reason' } } }
    catch { $assertMessage = $_.Exception.Message }
    foreach ($fragment in @('Desktop development with C++', 'test reason', 'build the installer from source')) {
        if ($assertMessage -notmatch [regex]::Escape($fragment)) {
            throw "The prerequisite failure did not mention '$fragment': $assertMessage"
        }
    }
    Write-Host 'PASS: the AOT prerequisite check verifies the C++ components, not just vswhere.exe.'

    & (Join-Path $repoRoot 'tests/SetupUiPrerequisites.Tests.ps1')

    # --- Regression: only 'installing' is held to the install deadline. -------------------
    # A synthetic installer walks the real state file through the real states. The budgets are
    # seconds, not minutes, and every step is released by a file the test writes, so no case
    # depends on timing luck and none of them waits out a real 20-minute timeout.
    function New-TestSetupStub {
        param(
            [Parameter(Mandatory)][string]$Directory,
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][string[]]$States,
            [int]$ExitCode = 0,
            [string]$HangInState
        )
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $release = Join-Path $Directory "$Name.release"
        $body = @(
            '$ErrorActionPreference = ''Stop''',
            '$state = $env:CYCLEARC_SETUP_STATE_FILE',
            'function Report([string]$value) {',
            '    if ($state) { [IO.File]::WriteAllText($state, $value + [Environment]::NewLine) }',
            '}'
        )
        foreach ($stateName in $States) {
            $body += ("Report '{0}'" -f $stateName)
            if ($HangInState -and $stateName -eq $HangInState) {
                # Never released: this is the hang the install deadline has to catch.
                $body += 'Start-Sleep -Seconds 600'
            }
            else {
                $body += ('while (!(Test-Path -LiteralPath "{0}.{1}")) {{ Start-Sleep -Milliseconds 50 }}' -f $release, $stateName)
            }
        }
        $body += "exit $ExitCode"
        $path = Join-Path $Directory "$Name.ps1"
        Set-Content -LiteralPath $path -Value ($body -join "`n") -Encoding utf8
        [pscustomobject]@{ Script = $path; Release = $release }
    }

    function Start-TestSetup {
        param(
            [Parameter(Mandatory)]$Stub,
            [Parameter(Mandatory)][string]$StateFile,
            [int]$Approval = 30,
            [int]$Install = 3,
            [int]$Completion = 3
        )
        if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
        $job = Start-ThreadJob -ScriptBlock {
            param($repoRoot, $pwshPath, $script, $stateFile, $approval, $install, $completion)
            . (Join-Path $repoRoot 'scripts/LocalInstall.ps1')
            . (Join-Path $repoRoot 'scripts/Build-Local.ps1') -LoadOnly
            # Set here, not in the caller: the child inherits it when Invoke-SetupProcess
            # starts it, and restoring it in the caller would race that start.
            $env:CYCLEARC_SETUP_STATE_FILE = $stateFile
            try {
                $code = Invoke-SetupProcess -FilePath $pwshPath -StateFile $stateFile `
                    -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $script) `
                    -ApprovalTimeoutSeconds $approval -InstallTimeoutSeconds $install `
                    -CompletionTimeoutSeconds $completion -PollMilliseconds 50
                [pscustomobject]@{ ExitCode = $code; Error = $null }
            }
            catch { [pscustomobject]@{ ExitCode = $null; Error = $_.Exception.Message } }
        } -ArgumentList $repoRoot, $pwshPath, $Stub.Script, $StateFile, $Approval, $Install, $Completion
        $job
    }

    function Wait-TestState {
        param([Parameter(Mandatory)][string]$StateFile, [Parameter(Mandatory)][string]$Expected, [int]$Seconds = 20)
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
            if ((Get-SetupStateValue $StateFile) -eq $Expected) { return }
            Start-Sleep -Milliseconds 50
        }
        throw "The installer never reported '$Expected' (last was '$(Get-SetupStateValue $StateFile)')."
    }

    $setupStateRoot = Join-Path $testRoot 'setup-state'
    New-Item -ItemType Directory -Path $setupStateRoot -Force | Out-Null

    # A. Waiting for approval does not spend the install budget.
    $approvalState = Join-Path $setupStateRoot 'approval.state'
    $approvalStub = New-TestSetupStub -Directory $setupStateRoot -Name 'approval' -States @('awaiting-approval', 'installing', 'done')
    $approvalJob = Start-TestSetup -Stub $approvalStub -StateFile $approvalState -Install 2 -Completion 20
    Wait-TestState -StateFile $approvalState -Expected 'awaiting-approval'
    # Longer than the install budget, entirely inside the approval wait.
    Start-Sleep -Seconds 3
    foreach ($stateName in @('awaiting-approval', 'installing', 'done')) {
        Set-Content -LiteralPath "$($approvalStub.Release).$stateName" -Value 'go'
        Start-Sleep -Milliseconds 150
    }
    $approvalResult = Receive-Job -Job $approvalJob -Wait -AutoRemoveJob
    if ($approvalResult.Error) { throw "Approval wait consumed the install budget: $($approvalResult.Error)" }
    if ($approvalResult.ExitCode -ne 0) { throw "Approval case exited $($approvalResult.ExitCode)" }
    Write-Host 'PASS: waiting for approval does not spend the install timeout.'

    # B. A finished install left on the completion screen is not an install timeout.
    $doneState = Join-Path $setupStateRoot 'done.state'
    $doneStub = New-TestSetupStub -Directory $setupStateRoot -Name 'done' -States @('installing', 'done')
    $doneJob = Start-TestSetup -Stub $doneStub -StateFile $doneState -Install 2 -Completion 30
    Wait-TestState -StateFile $doneState -Expected 'installing'
    Set-Content -LiteralPath "$($doneStub.Release).installing" -Value 'go'
    Wait-TestState -StateFile $doneState -Expected 'done'
    # Hold the completion screen well past the install budget.
    Start-Sleep -Seconds 4
    Set-Content -LiteralPath "$($doneStub.Release).done" -Value 'go'
    $doneResult = Receive-Job -Job $doneJob -Wait -AutoRemoveJob
    if ($doneResult.Error) { throw "A finished install was reported as a timeout: $($doneResult.Error)" }
    if ($doneResult.ExitCode -ne 0) { throw "Completion case exited $($doneResult.ExitCode)" }
    Write-Host 'PASS: holding the completion screen does not turn a finished install into a timeout.'

    # C. Reading the error screen does not bury the real failure under a timeout.
    $failState = Join-Path $setupStateRoot 'failed.state'
    $failStub = New-TestSetupStub -Directory $setupStateRoot -Name 'failed' -States @('installing', 'failed') -ExitCode 1
    $failJob = Start-TestSetup -Stub $failStub -StateFile $failState -Install 2 -Completion 30
    Wait-TestState -StateFile $failState -Expected 'installing'
    Set-Content -LiteralPath "$($failStub.Release).installing" -Value 'go'
    Wait-TestState -StateFile $failState -Expected 'failed'
    Start-Sleep -Seconds 4
    Set-Content -LiteralPath "$($failStub.Release).failed" -Value 'go'
    $failResult = Receive-Job -Job $failJob -Wait -AutoRemoveJob
    if ($failResult.Error) { throw "The error screen wait replaced the real failure: $($failResult.Error)" }
    if ($failResult.ExitCode -ne 1) { throw "The installer's own exit code was lost (got $($failResult.ExitCode))" }
    Write-Host 'PASS: reading the error screen keeps the installer''s own failure exit code.'

    # D. A genuine hang while installing still fails inside the install budget.
    $hangState = Join-Path $setupStateRoot 'hang.state'
    $hangStub = New-TestSetupStub -Directory $setupStateRoot -Name 'hang' -States @('installing') -HangInState 'installing'
    $hangWatch = [Diagnostics.Stopwatch]::StartNew()
    $hangJob = Start-TestSetup -Stub $hangStub -StateFile $hangState -Install 2 -Completion 30
    $hangResult = Receive-Job -Job $hangJob -Wait -AutoRemoveJob
    $hangWatch.Stop()
    if (!$hangResult.Error) { throw 'A hang while installing did not fail.' }
    if ($hangResult.Error -notmatch 'did not finish installing') {
        throw "A hang while installing was misreported: $($hangResult.Error)"
    }
    if ($hangWatch.Elapsed.TotalSeconds -gt 25) {
        throw "The install timeout took $($hangWatch.Elapsed.TotalSeconds)s"
    }
    Write-Host 'PASS: a hang while installing still fails inside the install timeout.'

    # E. Cancelling before install keeps its own result contract.
    $cancelState = Join-Path $setupStateRoot 'cancelled.state'
    $cancelStub = New-TestSetupStub -Directory $setupStateRoot -Name 'cancelled' -States @('awaiting-approval', 'cancelled') -ExitCode 2
    $cancelJob = Start-TestSetup -Stub $cancelStub -StateFile $cancelState -Install 2 -Completion 20
    Wait-TestState -StateFile $cancelState -Expected 'awaiting-approval'
    Set-Content -LiteralPath "$($cancelStub.Release).awaiting-approval" -Value 'go'
    Wait-TestState -StateFile $cancelState -Expected 'cancelled'
    Set-Content -LiteralPath "$($cancelStub.Release).cancelled" -Value 'go'
    $cancelResult = Receive-Job -Job $cancelJob -Wait -AutoRemoveJob
    if ($cancelResult.Error) { throw "Cancelling reported an error: $($cancelResult.Error)" }
    if ($cancelResult.ExitCode -ne 2) { throw "Cancel exited $($cancelResult.ExitCode), expected 2" }
    Write-Host 'PASS: cancelling before install still returns its own exit code.'

    # --- Regression: Invoke-ExternalProcess drains stderr and honours its timeout. ---
    $extOut = Join-Path $testRoot 'ext-out.log'
    $extErr = Join-Path $testRoot 'ext-err.log'
    $floodBody = New-TestPowerShellBody 'ext-flood' (@(
        '$line = "e" * 1024',
        'for ($i = 0; $i -lt 1024; $i++) { [Console]::Error.WriteLine($line) }',
        '[Console]::Error.Flush()',
        'exit 7'
    ) -join "`n")
    $floodWatch = [Diagnostics.Stopwatch]::StartNew()
    $floodExit = Invoke-ExternalProcess -FilePath $pwshPath -TimeoutSeconds 60 -NoNewWindow `
        -StandardOutputPath $extOut -StandardErrorPath $extErr `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $floodBody)
    $floodWatch.Stop()
    if ($floodExit -ne 7) { throw "A large-stderr child returned exit $floodExit" }
    if ($floodWatch.Elapsed.TotalSeconds -gt 25) {
        throw "Draining 1 MB of stderr deadlocked or stalled ($($floodWatch.Elapsed.TotalSeconds)s)"
    }
    if ((Get-Item -LiteralPath $extErr).Length -lt 1000000) {
        throw "Captured stderr was truncated: $((Get-Item -LiteralPath $extErr).Length) bytes"
    }
    Write-Host 'PASS: Invoke-ExternalProcess drains a 1 MB stderr child without deadlocking.'

    $hangOut = Join-Path $testRoot 'ext-hang.out.log'
    $hangErr = Join-Path $testRoot 'ext-hang.err.log'
    $hangPidFile = Join-Path $testRoot 'ext-hang.pid'
    $hangBody = New-TestPowerShellBody 'ext-hang' (@(
        ('$PID | Set-Content -LiteralPath "{0}"' -f $hangPidFile),
        'Start-Sleep -Seconds 600'
    ) -join "`n")
    $hangWatch = [Diagnostics.Stopwatch]::StartNew()
    Assert-Throws {
        Invoke-ExternalProcess -FilePath $pwshPath -TimeoutSeconds 3 -NoNewWindow `
            -StandardOutputPath $hangOut -StandardErrorPath $hangErr `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $hangBody)
    } 'did not finish within'
    $hangWatch.Stop()
    if ($hangWatch.Elapsed.TotalSeconds -gt 25) {
        throw "Invoke-ExternalProcess timeout took $($hangWatch.Elapsed.TotalSeconds)s"
    }
    $extHangPid = [int]((Get-Content -LiteralPath $hangPidFile -Raw).Trim())
    $leftoverHang = Get-TestProcessById $extHangPid
    if ($leftoverHang) {
        $leftoverHang.Dispose()
        Stop-TestProcessById $extHangPid
        throw "The timed-out Invoke-ExternalProcess child PID $extHangPid was left running"
    }
    Write-Host 'PASS: Invoke-ExternalProcess stops a hanging child inside its time limit.'

}
finally {
    foreach ($directory in @(Get-ChildItem -LiteralPath $testRoot -Directory -ErrorAction SilentlyContinue)) {
        Assert-TestDirectory $directory.FullName
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
