#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/LocalInstall.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Assert-TestDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test target' }
}
function Invoke-TestGit([string]$Directory, [string[]]$Arguments) {
    # Synthetic commits must not run a user's hooks or request their signing key.
    $output = @(& git -c core.hooksPath= -c commit.gpgsign=false -c init.templateDir= -C $Directory @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "Synthetic git failed: $($output -join ' ')" }
    $output -join "`n"
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
function New-TestCycleArcTree([string]$Directory) {
    New-Item -ItemType Directory -Path (Join-Path $Directory 'src/CycleArc') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Directory 'CycleArc.sln') -Value 'synthetic solution'
    Set-Content -LiteralPath (Join-Path $Directory 'src/CycleArc/CycleArc.csproj') -Value 'synthetic project'
}
try {
    foreach ($previousFile in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
    foreach ($scenario in @('success', 'transient', 'old-locked', 'staging-failed', 'startup-failed')) {
        $directory = Join-Path $testRoot ($previousFile + '-' + $scenario)
        $local = Join-Path $directory 'local'
        $staging = Join-Path $directory 'staging'
        $backup = Join-Path $directory 'backup'
        foreach ($path in @($local, $staging, $backup)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
        Set-Content -LiteralPath (Join-Path $local $previousFile) -Value 'old'
        Set-Content -LiteralPath (Join-Path $staging 'CycleArc.exe') -Value 'new'
        Set-Content -LiteralPath (Join-Path $backup $previousFile) -Value 'stale'
        $state = @{ Moves = 0; Starts = [Collections.Generic.List[string]]::new() }
        $move = {
            param($Source, $Destination)
            if ($Source -eq $local -and $Destination -eq $backup) {
                $state.Moves++
                if ($scenario -eq 'old-locked' -or ($scenario -eq 'transient' -and $state.Moves -lt 3)) { throw 'Synthetic lock' }
            }
            if ($scenario -eq 'staging-failed' -and $Source -eq $staging) { throw 'Synthetic staging failure' }
            Move-Item -LiteralPath $Source -Destination $Destination
        }
        $start = {
            param($Directory)
            $version = (Get-Content -LiteralPath (Resolve-InstalledExecutable $Directory) -Raw).Trim()
            $state.Starts.Add($version)
            if ($scenario -eq 'startup-failed' -and $version -eq 'new') { throw 'Synthetic startup failure' }
        }
        $failed = $false
        try { Install-StagedApp $staging $local $backup ${function:Assert-TestDirectory} $move $start -Attempts 3 -DelayMilliseconds 1 }
        catch { $failed = $true }
        $expectFailure = $scenario -in @('old-locked', 'staging-failed', 'startup-failed')
        if ($failed -ne $expectFailure) { throw "Unexpected outcome: $scenario" }
        $expected = if ($expectFailure) { 'old' } else { 'new' }
        $expectedFile = if ($expectFailure) { $previousFile } else { 'CycleArc.exe' }
        if ((Get-Content -LiteralPath (Join-Path $local $expectedFile) -Raw).Trim() -ne $expected) { throw "Wrong installed version: $scenario" }
        if ($state.Starts[-1] -ne $expected) { throw "Wrong restarted version: $scenario" }
        if ($scenario -eq 'transient' -and $state.Moves -ne 3) { throw 'Transient lock was not retried' }
        if (!$expectFailure -and (Get-Content -LiteralPath (Join-Path $backup $previousFile) -Raw).Trim() -ne 'old') { throw 'Previous version lost' }
    }
    }
    Write-Host 'PASS: 15 isolated installer deployment/retry/rollback scenarios across CycleArc and both legacy executable names.'

    $layoutRoot = Join-Path $testRoot 'layout'
    $primaryRoot = Join-Path $layoutRoot 'primary'
    $linkedRoot = Join-Path $layoutRoot 'linked'
    New-Item -ItemType Directory -Path $primaryRoot -Force | Out-Null
    New-TestCycleArcTree $primaryRoot
    Invoke-TestGit $primaryRoot @('init', '-b', 'main') | Out-Null
    Invoke-TestGit $primaryRoot @('config', 'user.email', 'cyclearc-test@example.invalid') | Out-Null
    Invoke-TestGit $primaryRoot @('config', 'user.name', 'CycleArc release test') | Out-Null
    Invoke-TestGit $primaryRoot @('add', '.') | Out-Null
    Invoke-TestGit $primaryRoot @('commit', '-m', 'synthetic CycleArc tree') | Out-Null
    Invoke-TestGit $primaryRoot @('worktree', 'add', '--detach', $linkedRoot, 'HEAD') | Out-Null

    $layout = Resolve-InstallLayout -RepoRoot $linkedRoot
    if (!$layout.UsesGitPrimary) { throw 'Linked worktree did not use Git primary installation mode' }
    if (![IO.Path]::GetFullPath($layout.InstallRoot).Equals([IO.Path]::GetFullPath($primaryRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Linked worktree resolved the wrong install root: $($layout.InstallRoot)"
    }
    if (![IO.Path]::GetFullPath($layout.LocalDir).Equals([IO.Path]::GetFullPath((Join-Path $primaryRoot 'publish/local')), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Linked worktree resolved the wrong local directory: $($layout.LocalDir)"
    }

    $primaryLayout = Resolve-InstallLayout -RepoRoot $primaryRoot
    if (!$primaryLayout.UsesGitPrimary -or ![IO.Path]::GetFullPath($primaryLayout.InstallRoot).Equals([IO.Path]::GetFullPath($primaryRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Primary worktree did not resolve to itself'
    }

    $exportRoot = Join-Path $layoutRoot 'export'
    New-Item -ItemType Directory -Path $exportRoot -Force | Out-Null
    New-TestCycleArcTree $exportRoot
    $exportLayout = Resolve-InstallLayout -RepoRoot $exportRoot
    if ($exportLayout.UsesGitPrimary -or ![IO.Path]::GetFullPath($exportLayout.InstallRoot).Equals([IO.Path]::GetFullPath($exportRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Non-Git export did not fall back to its current repository'
    }

    $sourceExe = Join-Path $exportRoot 'CycleArc.exe'
    Set-Content -LiteralPath $sourceExe -Value 'validated executable'
    $copyStage = Join-Path $primaryRoot 'publish/.dev-install-staging/copy-test'
    New-Item -ItemType Directory -Path $copyStage -Force | Out-Null
    $copiedExe = Copy-ValidatedExecutable -SourcePath $sourceExe -DestinationDirectory $copyStage `
        -SourceRoots @($exportRoot) -DestinationRoots @($primaryRoot)
    if (!(Test-Path -LiteralPath $copiedExe -PathType Leaf) -or (Get-Content -Raw $copiedExe).Trim() -ne 'validated executable') {
        throw 'Validated executable was not copied to the primary staging directory'
    }
    Assert-Throws {
        Copy-ValidatedExecutable -SourcePath $sourceExe -DestinationDirectory $copyStage `
            -SourceRoots @($exportRoot) -DestinationRoots @($primaryRoot)
    } 'must be empty'
    Assert-InstallPath -Path $copyStage -AllowedRoots @($primaryRoot) | Out-Null
    Remove-Item -LiteralPath $copyStage -Recurse -Force

    $malformedRoot = Join-Path $layoutRoot 'malformed'
    New-Item -ItemType Directory -Path $malformedRoot -Force | Out-Null
    New-TestCycleArcTree $malformedRoot
    Set-Content -LiteralPath (Join-Path $malformedRoot '.git') -Value 'gitdir: Z:\missing\worktree'
    Assert-Throws { Resolve-InstallLayout -RepoRoot $malformedRoot } 'Git metadata lookup failed'
    Assert-Throws { Assert-InstallPath -Path (Join-Path $layoutRoot 'outside') -AllowedRoots @($primaryRoot) } 'outside the allowed roots'

    $lease = New-InstallLease -InstallRoot $primaryRoot -AllowedRoots @($primaryRoot)
    Assert-Throws { New-InstallLease -InstallRoot $primaryRoot -AllowedRoots @($primaryRoot) } 'Another dev-run installation'
    $lease.Dispose()
    Assert-TestDirectory $linkedRoot
    Invoke-TestGit $primaryRoot @('worktree', 'remove', '--force', $linkedRoot) | Out-Null
    Write-Host 'PASS: Git primary/linked-worktree, non-Git fallback, malformed metadata, path-boundary, and install-lease guards.'

    # --- Wait-InstallDesktopReleased: the installer must not run over a live
    # installation, and a desktop that never lets go has to fail the caller
    # rather than be waited out silently. ---
    $freeMutexName = 'CycleArc-test-free-' + [guid]::NewGuid().ToString('N')
    $released = Wait-InstallDesktopReleased -ProcessId 0 -MutexName $freeMutexName -TimeoutSeconds 5
    if ($released.ElapsedSeconds -gt 2) { throw "An already-released installation waited $($released.ElapsedSeconds)s" }
    Write-Host 'PASS: an already-released installation returns at once.'

    # --- Regression: a handle inside the installation blocks the release wait. ----------------
    # Waiting only for the process and the mutex let a same-version repair start while something
    # still held the directory, which Velopack then reported as Windows error 5 on rename. These
    # use a real handle on a real file, held by this process, and never touch a real installation.
    $lockRoot = Join-Path $testRoot 'install-lock'
    New-Item -ItemType Directory -Path (Join-Path $lockRoot 'current') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $lockRoot 'current/CycleArc.exe') -Value 'current'
    Set-Content -LiteralPath (Join-Path $lockRoot 'Update.exe') -Value 'updater'

    # Nothing holds it yet.
    $idle = Get-InstallDirectoryLock -InstallRoot $lockRoot
    if (!$idle.Queried) { throw "An untouched installation could not be queried: $(Format-InstallDirectoryLock $idle)" }
    if ($idle.HasHolder) { throw "An untouched installation was reported as held: $(Format-InstallDirectoryLock $idle)" }

    $held = [IO.File]::Open((Join-Path $lockRoot 'current/CycleArc.exe'), 'Open', 'Read', 'None')
    try {
        $lock = Get-InstallDirectoryLock -InstallRoot $lockRoot
        if (!$lock.Queried) { throw "A held installation could not be queried: $(Format-InstallDirectoryLock $lock)" }
        if (!$lock.HasHolder) { throw 'A held installation file was not detected' }
        if (@($lock.Holders)[0].ProcessId -ne $PID) {
            throw "The holder was reported as PID $(@($lock.Holders)[0].ProcessId), expected this process ($PID)"
        }
        $described = Format-InstallDirectoryLock $lock
        if ($described -notmatch [regex]::Escape("PID $PID")) { throw "The holder was not named: $described" }

        # The wait refuses to declare the installation free while that handle is open, and says
        # who holds it. No process is running and no mutex is held, so the old check would pass.
        $waitError = ''
        try {
            Wait-InstallDesktopReleased -ProcessId 0 -MutexName 'Local\CycleArc-test-absent-mutex' `
                -TimeoutSeconds 2 -PollMilliseconds 200 -InstallRoot $lockRoot | Out-Null
        }
        catch { $waitError = $_.Exception.Message }
        if (!$waitError) { throw 'The release wait passed while the installation was still held' }
        if ($waitError -notmatch 'held by') { throw "The wait did not report the holder: $waitError" }
        if ($waitError -notmatch [regex]::Escape("PID $PID")) { throw "The wait did not name the holder: $waitError" }
    }
    finally { $held.Dispose() }

    # Released again: the same wait now succeeds.
    $freed = Wait-InstallDesktopReleased -ProcessId 0 -MutexName 'Local\CycleArc-test-absent-mutex' `
        -TimeoutSeconds 5 -PollMilliseconds 200 -InstallRoot $lockRoot
    if ($null -eq $freed) { throw 'The release wait did not succeed after the handle was closed' }
    if ($freed.InstallRoot -ne $lockRoot) { throw 'The release result did not report the installation it checked' }

    # An installation path that does not exist is not reported as held.
    $absent = Get-InstallDirectoryLock -InstallRoot (Join-Path $testRoot 'install-lock-absent')
    if (!$absent.Queried -or $absent.HasHolder) { throw 'A missing installation was reported as held' }
    Write-Host 'PASS: a handle inside the installation is detected, named, and waited out.'

    # --- Regression: "could not ask" is not "nobody holds it". --------------------------
    # Every Restart Manager failure used to return the same empty array as a clean "none", so
    # a wait that asked for the InstallRoot check passed on a directory it never confirmed.
    # These inject the query result, so each outcome is exercised without a real holder.
    $queryRoot = Join-Path $testRoot 'lock-query'
    New-Item -ItemType Directory -Path (Join-Path $queryRoot 'current') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $queryRoot 'current/CycleArc.exe') -Value 'current'
    Set-Content -LiteralPath (Join-Path $queryRoot 'Update.exe') -Value 'updater'
    $absentMutex = 'Local\CycleArc-test-absent-' + [guid]::NewGuid().ToString('N')

    # A. Queried, nothing reported -> the wait passes.
    $none = Get-InstallDirectoryLock -InstallRoot $queryRoot -Invoker { New-LockQueryResult -Status Queried }
    if (!$none.Queried -or $none.HasHolder) { throw 'A clean query was not reported as queried-with-no-holder' }
    if ((Format-InstallDirectoryLock $none) -ne 'no holder was reported') {
        throw "A clean query was described as: $(Format-InstallDirectoryLock $none)"
    }
    $passed = Wait-InstallDesktopReleased -ProcessId 0 -MutexName $absentMutex -TimeoutSeconds 3 `
        -PollMilliseconds 100 -InstallRoot $queryRoot -LockInvoker { New-LockQueryResult -Status Queried }
    if ($null -eq $passed) { throw 'A clean query did not let the wait finish' }

    # B. Queried with a holder -> the wait refuses and names it.
    $holder = New-LockQueryResult -Status Queried -Holders @([pscustomobject]@{ ProcessId = 4242; Name = 'Synthetic'; Path = 'C:\synthetic.exe' })
    if (!$holder.HasHolder) { throw 'A reported holder was not surfaced' }
    $heldError = ''
    try {
        Wait-InstallDesktopReleased -ProcessId 0 -MutexName $absentMutex -TimeoutSeconds 1 `
            -PollMilliseconds 100 -InstallRoot $queryRoot -LockInvoker { $holder } | Out-Null
    }
    catch { $heldError = $_.Exception.Message }
    if (!$heldError) { throw 'The wait passed while a holder was reported' }
    if ($heldError -notmatch 'PID 4242') { throw "The holder was not named: $heldError" }

    # C + D. Each API failure is an Unknown that the wait must not treat as a pass.
    foreach ($case in @(
        @{ Reason = 'RmStartSession failed'; Code = 5 },
        @{ Reason = 'RmRegisterResources failed'; Code = 87 },
        @{ Reason = 'RmGetList failed'; Code = 6 }
    )) {
        $unknown = New-LockQueryResult -Status Unknown -Reason $case.Reason -Code $case.Code
        if ($unknown.Queried -or !$unknown.Unknown) { throw "$($case.Reason) was not reported as unknown" }
        $described = Format-InstallDirectoryLock $unknown
        if ($described -notmatch 'could not determine') { throw "Unknown was described as: $described" }
        if ($described -notmatch [regex]::Escape("code $($case.Code)")) { throw "The API code was lost: $described" }
        $unknownError = ''
        try {
            Wait-InstallDesktopReleased -ProcessId 0 -MutexName $absentMutex -TimeoutSeconds 1 `
                -PollMilliseconds 100 -InstallRoot $queryRoot -LockInvoker { $unknown } | Out-Null
        }
        catch { $unknownError = $_.Exception.Message }
        if (!$unknownError) { throw "$($case.Reason) was treated as a clean pass" }
        if ($unknownError -notmatch 'could not be determined') {
            throw "An unanswerable check was not reported as such: $unknownError"
        }
    }

    # E. The real API on a file nothing holds answers cleanly, so the retry loop's success
    #    path returns a Queried result rather than falling through to Unknown. The
    #    queried-with-holder path is covered by the real handle test above.
    $realQuery = Get-FileLockingProcess -Path @((Join-Path $queryRoot 'Update.exe'))
    if (!$realQuery.Queried) {
        throw "A real query on an unheld file was not queried: $(Format-InstallDirectoryLock $realQuery)"
    }
    if ($realQuery.HasHolder) { throw 'An unheld file was reported as held' }

    # F. Repeated ERROR_MORE_DATA ends as unknown inside the attempt budget, not as "none"
    #    and not as an endless loop.
    $exhausted = New-LockQueryResult -Status Unknown -Code 234 `
        -Reason 'RmGetList kept reporting ERROR_MORE_DATA after 4 attempts'
    if ($exhausted.Queried) { throw 'Exhausted retries were reported as a clean query' }
    $exhaustedText = Format-InstallDirectoryLock $exhausted
    if ($exhaustedText -notmatch 'ERROR_MORE_DATA' -or $exhaustedText -notmatch 'code 234') {
        throw "Exhausted retries were described as: $exhaustedText"
    }
    # The bound is real: the source retries with the reported size a limited number of times.
    $lockSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/LocalInstall.ps1') -Raw
    if ($lockSource -notmatch 'MaxListAttempts') { throw 'The ERROR_MORE_DATA retry is unbounded' }
    if ($lockSource -notmatch 'attempt -le \$MaxListAttempts') { throw 'The retry loop is not bounded by MaxListAttempts' }

    # G. A failed auxiliary query still describes itself, so a caller can print it beside the
    #    original Setup error rather than losing either.
    $auxiliary = Format-InstallDirectoryLock (New-LockQueryResult -Status Unknown -Reason 'RmStartSession failed' -Code 5)
    if (!$auxiliary) { throw 'A failed auxiliary query produced no description' }
    if ((Format-InstallDirectoryLock $null) -ne 'the installation was not checked') {
        throw 'An absent query result was not described'
    }
    Write-Host 'PASS: Restart Manager separates queried-none, queried-holder and could-not-ask.'

    $heldMutexName = 'CycleArc-test-held-' + [guid]::NewGuid().ToString('N')
    $heldMutex = [Threading.Mutex]::new($false, $heldMutexName)
    try {
        $heldWatch = [Diagnostics.Stopwatch]::StartNew()
        Assert-Throws {
            Wait-InstallDesktopReleased -ProcessId 0 -MutexName $heldMutexName -TimeoutSeconds 2 -PollMilliseconds 200
        } 'single-instance mutex'
        $heldWatch.Stop()
        if ($heldWatch.Elapsed.TotalSeconds -lt 2) { throw 'The held mutex was not actually waited for' }
        if ($heldWatch.Elapsed.TotalSeconds -gt 15) { throw "The timeout took $($heldWatch.Elapsed.TotalSeconds)s" }
        # Still inside the held-mutex scope: the caller has to be told the
        # installer was not started, not just that the wait expired.
        Assert-Throws {
            Wait-InstallDesktopReleased -ProcessId 0 -MutexName $heldMutexName -TimeoutSeconds 2 -PollMilliseconds 200
        } 'Refusing to run the installer over a live installation'
    }
    finally { $heldMutex.Dispose() }
    Write-Host 'PASS: a still-held single-instance mutex fails, saying the installer was not started.'

    $liveStart = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $liveStart.UseShellExecute = $false
    $liveStart.CreateNoWindow = $true
    foreach ($liveArgument in @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30')) {
        [void]$liveStart.ArgumentList.Add($liveArgument)
    }
    $liveChild = [Diagnostics.Process]::Start($liveStart)
    try {
        Assert-Throws {
            Wait-InstallDesktopReleased -ProcessId $liveChild.Id -MutexName $freeMutexName -TimeoutSeconds 2 -PollMilliseconds 200
        } "PID $($liveChild.Id) is still running"
        Write-Host 'PASS: a desktop process that is still running fails by PID, and is never terminated.'
        if ($liveChild.HasExited) { throw 'The wait terminated the process it was only supposed to observe' }
    }
    finally {
        try { if (!$liveChild.HasExited) { $liveChild.Kill($true) } } catch { }
        try { $null = $liveChild.WaitForExit(5000) } catch { }
        $liveChild.Dispose()
    }

    # A desktop outside publish/local must be found without selecting callbacks,
    # another Windows session, or an unrelated program with the same filename.
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $outsideExe = Join-Path $testRoot 'outside/CycleArc.exe'
    $legacyExe = Join-Path $testRoot 'known/prometer.exe'
    $snapshots = @(
        [pscustomobject]@{ ProcessId=101; Path=$outsideExe; SessionId=$session; CommandLine=('"' + $outsideExe + '" --show'); ProductName='CycleArc'; OriginalFilename='CycleArc.dll' },
        [pscustomobject]@{ ProcessId=102; Path=$outsideExe; SessionId=($session + 1); CommandLine='CycleArc.exe'; ProductName='CycleArc'; OriginalFilename='CycleArc.dll' },
        [pscustomobject]@{ ProcessId=103; Path=$outsideExe; SessionId=$session; CommandLine='CycleArc.exe'; ProductName='Unrelated'; OriginalFilename='Other.dll' },
        [pscustomobject]@{ ProcessId=104; Path=$legacyExe; SessionId=$session; CommandLine='prometer.exe'; ProductName='Legacy'; OriginalFilename='prometer.dll' }
    )
    $selected = @(Get-CycleArcDesktopProcess -ProcessSnapshots $snapshots -KnownExecutablePaths @($legacyExe))
    if (($selected.ProcessId -join ',') -ne '101,104') { throw 'Wrong desktop process selection' }
    $unclassified = [pscustomobject]@{ ProcessId=106; Path=$outsideExe; SessionId=$session; CommandLine=''; CommandLineAvailable=$false; ProductName='CycleArc'; OriginalFilename='CycleArc.dll' }
    if (@(Get-CycleArcDesktopProcess -ProcessSnapshots @($unclassified) -KnownExecutablePaths @($outsideExe)).Count) {
        throw 'A process with an unreadable command line was selected for termination'
    }
    foreach ($argument in @('--claude-statusline', '--claude-statusline-bridge', '--claude-stop-failure-bridge', '--apply-update', '--desktop-status', '--desktop-shutdown')) {
        foreach ($quoted in @($false, $true)) {
            $argText = if ($quoted) { '"' + $argument + '"' } else { $argument }
            $callback = [pscustomobject]@{ ProcessId=105; Path=$outsideExe; SessionId=$session; CommandLine=('"' + $outsideExe + '" ' + $argText + ' synthetic'); ProductName='CycleArc'; OriginalFilename='CycleArc.dll' }
            if (@(Get-CycleArcDesktopProcess -ProcessSnapshots @($callback) -KnownExecutablePaths @($outsideExe)).Count) {
                throw "A headless receiver was selected for termination: $argText"
            }
        }
    }
    foreach ($desktopCommand in @('"C:\--claude-statusline\CycleArc.exe" --show', 'CycleArc.exe --show --claude-statusline')) {
        if (Test-CycleArcHeadlessCommandLine $desktopCommand) { throw "A desktop command was mistaken for a callback: $desktopCommand" }
    }

    # App startup uses createdNew, so even an unowned named mutex blocks a new app.
    # Use only a unique synthetic name; never acquire the user's desktop mutex.
    $mutexName = 'Local\CycleArc-install-test-' + [guid]::NewGuid().ToString('N')
    Assert-InstallDesktopMutexAbsent -MutexName $mutexName
    $held = [Threading.Mutex]::new($false, $mutexName)
    try { Assert-Throws { Assert-InstallDesktopMutexAbsent -MutexName $mutexName } 'mutex is still present' }
    finally { $held.Dispose() }
    Assert-InstallDesktopMutexAbsent -MutexName $mutexName

    # Exercise actual Windows process termination only against this test's child.
    $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '[Threading.Thread]::Sleep(60000)') -WindowStyle Hidden -PassThru
    try {
        $null = $child.Handle
        $pidOnly = [pscustomobject]@{ ProcessId=$child.Id; Path=$child.Path }
        if (Stop-CycleArcDesktopProcess -ProcessRecord $pidOnly) { throw 'A PID without a retained process was stopped' }
        $wrong = [pscustomobject]@{ Process=$child; ProcessId=$child.Id; Path=$outsideExe }
        if (Stop-CycleArcDesktopProcess -ProcessRecord $wrong) { throw 'Mismatched process path was stopped' }
        if ($child.HasExited) { throw 'Path mismatch terminated the child' }
        $owned = [pscustomobject]@{ Process=$child; ProcessId=$child.Id; Path=$child.Path }
        if (!(Stop-CycleArcDesktopProcess -ProcessRecord $owned)) { throw 'Owned child was not stopped' }
        if (!$child.HasExited) { throw 'Process termination returned before exit' }
        if (Stop-CycleArcDesktopProcess -ProcessRecord $owned) { throw 'Already exited child was treated as running' }
    }
    finally {
        if (!$child.HasExited) { $child.Kill(); $null = $child.WaitForExit(5000) }
        $child.Dispose()
    }
    Write-Host 'PASS: outside-install desktop discovery, legacy paths, session/product isolation, headless routing, mutex conflicts, and actual process termination.'
}
finally {
    # All recursive removals are verified against the unique test root.
    foreach ($directory in Get-ChildItem -LiteralPath $testRoot -Directory) {
        Assert-TestDirectory $directory.FullName
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    Remove-Item -LiteralPath $testRoot
}
