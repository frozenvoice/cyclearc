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
    foreach ($argument in @('--claude-statusline', '--claude-statusline-bridge', '--claude-stop-failure-bridge')) {
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
