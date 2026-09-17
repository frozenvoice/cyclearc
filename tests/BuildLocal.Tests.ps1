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
    New-Item -ItemType Directory -Path (Join-Path $Directory 'src/CycleArc') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Directory 'CycleArc.sln') -Value 'synthetic solution'
    Set-Content -LiteralPath (Join-Path $Directory 'src/CycleArc/CycleArc.csproj') -Value 'synthetic project'
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
    if ($scriptText -notmatch 'current/CycleArc.exe') {
        throw 'Build-Local.ps1 must verify the managed current\\CycleArc.exe'
    }
    if ($cmdText -notmatch 'scripts\\Build-Local.ps1') { throw 'build-local.cmd must call scripts\\Build-Local.ps1' }
    if ($cmdText -notmatch 'pause') { throw 'build-local.cmd must pause on failure so the window stays open' }
    Write-Host 'PASS: build-local.cmd / Build-Local.ps1 structural guards.'

    if (!(Test-BuildLocalDotnetSdk @('8.0.415 [C:\Program Files\dotnet\sdk\8.0.415]'))) {
        throw 'An 8.x SDK listing must be accepted'
    }
    if (!(Test-BuildLocalDotnetSdk @('10.0.400 [C:\Program Files\dotnet\sdk\10.0.400]'))) {
        throw 'A newer default SDK must be accepted when it can build net8.0'
    }
    if (Test-BuildLocalDotnetSdk @('6.0.428 [C:\Program Files\dotnet\sdk\6.0.428]')) {
        throw 'A 6.x SDK listing must not be treated as sufficient'
    }
    if (!(Test-BuildLocalDotnetSdk @('10.0.400 [C:\Program Files\dotnet\sdk\10.0.400]', '8.0.415 [C:\Program Files\dotnet\sdk\8.0.415]'))) {
        throw 'A mixed 8.x and 10.x SDK listing must be accepted'
    }
    Write-Host 'PASS: .NET SDK 8+ detection ignores a newer default --version.'

    $defaultRoot = Get-DefaultManagedInstallRoot
    $log = Join-Path $testRoot 'setup.log'
    $silentDefault = @(Get-SetupArguments -SetupLog $log)
    if (($silentDefault -join ' ') -ne "--silent --log $log") { throw "Default Setup arguments were $($silentDefault -join ' ')" }
    $silentDefaultRoot = @(Get-SetupArguments -SetupLog $log -ExistingRoot $defaultRoot -DefaultRoot $defaultRoot)
    if ($silentDefaultRoot -contains '--installto') { throw 'Default managed root must not pass --installto' }
    $custom = Join-Path $testRoot 'custom-install'
    $withCustom = @(Get-SetupArguments -SetupLog $log -ExistingRoot $custom -DefaultRoot $defaultRoot)
    if ($withCustom -notcontains '--installto') { throw 'A custom managed root must be passed to Setup.exe --installto' }
    Write-Host 'PASS: Setup.exe argument selection for default vs custom install roots.'

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
        Invoke-BuildLocal -RepoRoot $tree -ManagedRoot (Join-Path $testRoot 'install-a') -DevRun { throw 'synthetic build failure' } -RunSetup {
            param($setup, $args)
            $script:setupCalled = $true
            0
        } -StopDesktop { }
    } 'synthetic build failure'
    if ($setupCalled) { throw 'Setup.exe ran after a failed build' }
    Write-Host 'PASS: failed build does not start Setup.exe.'

    $oldPack = New-FakePublished $tree 'published-a' 'old-setup'
    (Get-Item -LiteralPath $oldPack.SetupPath).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-2)
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -ManagedRoot (Join-Path $testRoot 'install-b') -DevRun { } `
            -PackagedSetup { $oldPack } -StopDesktop { }
    } 'older than this run'
    Write-Host 'PASS: leftover Setup.exe is refused.'

    $installC = Join-Path $testRoot 'install-c'
    $packC = New-FakePublished $tree 'published-c' 'setup-c'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -ManagedRoot $installC -DevRun { } -PackagedSetup { $packC } -StopDesktop { } -RunSetup {
            param($setup, $arguments)
            if ($arguments -notcontains '--silent') { throw 'Setup.exe was not invoked silently' }
            New-Item -ItemType Directory -Path (Join-Path $installC 'current') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $installC 'current/CycleArc.exe') -Value 'stale-previous-build'
            Set-Content -LiteralPath (Join-Path $installC 'CycleArc.exe') -Value 'launcher'
            0
        }
    } 'does not match this build'
    Write-Host 'PASS: same-version skip is reported as failure.'

    $installD = Join-Path $testRoot 'install-d'
    $packD = New-FakePublished $tree 'published-d' 'setup-d'
    $script:started = $false
    $result = Invoke-BuildLocal -RepoRoot $tree -ManagedRoot $installD -DevRun { } -PackagedSetup { $packD } -StopDesktop { } -RunSetup {
        param($setup, $arguments)
        New-Item -ItemType Directory -Path (Join-Path $installD 'current') -Force | Out-Null
        Copy-Item -LiteralPath $packD.StagingExe -Destination (Join-Path $installD 'current/CycleArc.exe')
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
    $packE = New-FakePublished $tree 'published-e' 'setup-e'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $tree -ManagedRoot $installE -DevRun { } -PackagedSetup { $packE } -StopDesktop { } -RunSetup { 7 }
    } 'CycleArc-Setup.exe failed'
    if (Test-Path -LiteralPath (Join-Path $installE 'current/CycleArc.exe')) {
        throw 'A failed Setup.exe still wrote an installed executable'
    }
    Write-Host 'PASS: Setup.exe failure does not report a successful replacement.'

    $lease = New-InstallLease -InstallRoot $tree -AllowedRoots @($tree)
    try {
        Assert-Throws { Invoke-BuildLocal -RepoRoot $tree -ManagedRoot (Join-Path $testRoot 'install-f') -DevRun { } } 'Another dev-run installation'
    }
    finally { $lease.Dispose() }
    Write-Host 'PASS: overlapping build-local runs are rejected by the existing install lease.'

    $spaceRoot = Join-Path $testRoot 'path with space'
    New-GitCycleArcTree $spaceRoot
    $spaceInstall = Join-Path $testRoot 'install space'
    $spacePack = New-FakePublished $spaceRoot 'published-space' 'setup-space'
    $spaceResult = Invoke-BuildLocal -RepoRoot $spaceRoot -ManagedRoot $spaceInstall -DevRun { } -PackagedSetup { $spacePack } -StopDesktop { } -RunSetup {
        param($setup, $arguments)
        New-Item -ItemType Directory -Path (Join-Path $spaceInstall 'current') -Force | Out-Null
        Copy-Item -LiteralPath $spacePack.StagingExe -Destination (Join-Path $spaceInstall 'current/CycleArc.exe')
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
}
finally {
    foreach ($directory in @(Get-ChildItem -LiteralPath $testRoot -Directory -ErrorAction SilentlyContinue)) {
        Assert-TestDirectory $directory.FullName
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
