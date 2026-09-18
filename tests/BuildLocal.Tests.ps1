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
        'preflight', 'release-guard', 'restore', 'tool-restore', 'build', 'ui-smoke-desktop-instance',
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
    # --- Regression: the default branch, with no -DevRun scriptblock injected. ---
    # $devRun as a local name is the same variable as the [scriptblock]$DevRun
    # parameter, so assigning the real script path to it used to fail the
    # parameter's type constraint before the build ever started.
    $defaultTree = Join-Path $testRoot 'default entry'
    New-GitCycleArcTree $defaultTree
    $devRunMarker = Join-Path $defaultTree 'dev-run-marker.txt'
    New-TestDevRun $defaultTree $devRunMarker 0 | Out-Null
    $defaultInstall = Join-Path $testRoot 'install default'
    $defaultPack = New-FakePublished $defaultTree 'published-default' 'setup-default'
    $script:defaultStopped = $false
    $defaultResult = Invoke-BuildLocal -RepoRoot $defaultTree -ManagedRoot $defaultInstall `
        -PackagedSetup { $defaultPack } -StopDesktop { $script:defaultStopped = $true } -RunSetup {
            param($setup, $arguments)
            New-Item -ItemType Directory -Path (Join-Path $defaultInstall 'current') -Force | Out-Null
            Copy-Item -LiteralPath $defaultPack.StagingExe -Destination (Join-Path $defaultInstall 'current/CycleArc.exe')
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
    $silentPack = New-FakePublished $silentTree 'published-silent' 'setup-silent'
    $script:silentStopped = $false
    $script:silentArguments = @()
    $silentResult = Invoke-BuildLocal -RepoRoot $silentTree -ManagedRoot $silentManagedRoot -SilentInstall `
        -PackagedSetup { $silentPack } -StopDesktop { $script:silentStopped = $true } -RunSetup {
            param($setup, $arguments)
            $script:silentArguments = @($arguments)
            New-Item -ItemType Directory -Path (Join-Path $silentManagedRoot 'current') -Force | Out-Null
            Copy-Item -LiteralPath $silentPack.StagingExe -Destination (Join-Path $silentManagedRoot 'current/CycleArc.exe')
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
    $cancelPack = New-FakePublished $cancelTree 'published-cancel' 'setup-cancel'
    $script:cancelStopped = $false
    $script:cancelStarted = $false
    $cancelResult = Invoke-BuildLocal -RepoRoot $cancelTree -ManagedRoot $cancelInstall `
        -PackagedSetup { $cancelPack } -StopDesktop { $script:cancelStopped = $true } `
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

    # A real installer failure is still a failure, and is not confused with cancelling.
    $failInstall = Join-Path $testRoot 'install setup-failure'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $cancelTree -ManagedRoot $failInstall `
            -PackagedSetup { $cancelPack } -StopDesktop { } -RunSetup { param($setup, $arguments) 1 }
    } 'CycleArc-Setup.exe failed (exit 1)'
    Write-Host 'PASS: an installer failure is reported as failure, separately from cancellation.'

    $fastTree = Join-Path $testRoot 'default fast'
    New-GitCycleArcTree $fastTree
    $fastMarker = Join-Path $fastTree 'dev-run-marker.txt'
    New-TestDevRun $fastTree $fastMarker 0 | Out-Null
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $fastTree -ManagedRoot (Join-Path $testRoot 'install fast') -Fast -StopDesktop { }
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
        Invoke-BuildLocal -RepoRoot $failTree -ManagedRoot (Join-Path $testRoot 'install failure') `
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
        Invoke-BuildLocal -RepoRoot $stageTree -ManagedRoot (Join-Path $testRoot 'install sub-stage') `
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

    # --- Regression: a post-Setup failure never claims the old install survived. ---
    $lateTree = Join-Path $testRoot 'late failure'
    New-GitCycleArcTree $lateTree
    New-TestDevRun $lateTree (Join-Path $lateTree 'dev-run-marker.txt') 0 | Out-Null
    $latePack = New-FakePublished $lateTree 'published-late' 'setup-late'
    $lateInstall = Join-Path $testRoot 'install late'
    Assert-Throws {
        Invoke-BuildLocal -RepoRoot $lateTree -ManagedRoot $lateInstall -PackagedSetup { $latePack } -StopDesktop { } -RunSetup {
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
            $setupResult = Invoke-BuildLocal -RepoRoot $setupTree -ManagedRoot $setupInstall -StopDesktop { } `
                -PackagedSetup { [pscustomobject]@{ StagingExe = $setupStagingExe; SetupPath = $realSetup } } `
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
