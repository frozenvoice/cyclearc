#Requires -Version 7.0
<# Execute the real gate in an isolated synthetic checkout. No build, app, installer,
   credential, registry or shortcut operation is performed by these adapters. #>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-dev-check-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $testRoot 'checkout'
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts'), (Join-Path $fixture 'tests') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'dev-run.ps1') -Destination $fixture
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/LocalInstall.ps1') -Destination (Join-Path $fixture 'scripts')
Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Value '<Project><Version>1.0.0</Version></Project>'
$adapter = @'
function Write-SyntheticOperation([string]$Kind, [string[]]$Arguments = @()) {
    @{ kind = $Kind; arguments = @($Arguments) } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath $env:CYCLEARC_SYNTHETIC_LOG
}
function Assert-CycleArcDotnetSdk {
    param([string]$RepoRoot)
    Write-SyntheticOperation 'sdk'
    if ($env:CYCLEARC_SYNTHETIC_FAILURE -eq 'sdk') { throw 'synthetic SDK failure' }
}
function Get-CycleArcDesktopProcess {
    param([string[]]$KnownExecutablePaths)
    Write-SyntheticOperation 'desktop-preflight'
    if ($env:CYCLEARC_SYNTHETIC_FAILURE -eq 'desktop') {
        $handle = [pscustomobject]@{}
        $handle | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
        [pscustomobject]@{ ProcessId = 1234; Path = (Join-Path $RepoRoot 'src/CycleArc/bin/CycleArc.exe'); Process = $handle }
    }
}
function dotnet {
    $arguments = @($args | ForEach-Object { [string]$_ })
    Write-SyntheticOperation 'dotnet' $arguments
    $global:LASTEXITCODE = if ($env:CYCLEARC_SYNTHETIC_FAILURE -eq $arguments[0]) { 17 } else { 0 }
    if ($global:LASTEXITCODE -eq 0 -and $arguments[0] -eq 'publish') {
        $output = $arguments[[Array]::IndexOf($arguments, '-o') + 1]
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $output 'CycleArc.exe') -Value 'synthetic executable'
    }
}
'@
Set-Content -LiteralPath (Join-Path $fixture 'scripts/DotnetSdk.ps1') -Value $adapter
Set-Content -LiteralPath (Join-Path $fixture 'scripts/SetupUiToolchain.ps1') -Value @'
function Assert-SetupUiToolchain {
    param([string]$RepoRoot)
    Write-SyntheticOperation 'aot-toolchain'
}
'@
foreach ($name in @(
    'VerificationWorkflow', 'DotnetSdk', 'BuildPrerequisites', 'BootstrapBuildLocal',
    'Release', 'ReleaseDependencies', 'LocalInstall', 'BuildLocal', 'BuildLocalFailure', 'InstalledUpdateScript'
)) {
    Set-Content -LiteralPath (Join-Path $fixture "tests/$name.Tests.ps1") -Value "Write-SyntheticOperation '$name-regression'"
}
Set-Content -LiteralPath (Join-Path $fixture 'scripts/Package.ps1') -Value @'
param([string]$PublishedDir, [string]$OutputDir, [string]$Version)
Write-SyntheticOperation 'package'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $OutputDir 'CycleArc-Setup.exe') -Value 'synthetic setup'
'@
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$savedLog = $env:CYCLEARC_SYNTHETIC_LOG
$savedFailure = $env:CYCLEARC_SYNTHETIC_FAILURE

function Assert-Synthetic([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "Development gate regression: $Message" }
}
function Invoke-SyntheticGate([string[]]$Arguments, [string]$Failure = '') {
    $env:CYCLEARC_SYNTHETIC_LOG = Join-Path $testRoot 'operations.jsonl'
    $env:CYCLEARC_SYNTHETIC_FAILURE = $Failure
    if (Test-Path -LiteralPath $env:CYCLEARC_SYNTHETIC_LOG) { Remove-Item -LiteralPath $env:CYCLEARC_SYNTHETIC_LOG }
    $output = @(& $pwsh -NoProfile -NonInteractive -File (Join-Path $fixture 'dev-run.ps1') @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $operations = if (Test-Path -LiteralPath $env:CYCLEARC_SYNTHETIC_LOG) {
        @(Get-Content -LiteralPath $env:CYCLEARC_SYNTHETIC_LOG | ForEach-Object { $_ | ConvertFrom-Json })
    } else { @() }
    [pscustomobject]@{ ExitCode = $exitCode; Operations = @($operations); Output = $output -join "`n" }
}

try {
    $stage = Join-Path $fixture 'publish/.dev-staging'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $sentinel = Join-Path $stage 'keep.txt'
    Set-Content -LiteralPath $sentinel -Value 'keep existing staged artifact'
    $run = Invoke-SyntheticGate @('-DevelopmentOnly', '-TestFilter', 'FullyQualifiedName~Example', '-TestResultsDirectory', 'TestResults/local')
    Assert-Synthetic ($run.ExitCode -eq 0) $run.Output
    Assert-Synthetic (Test-Path -LiteralPath $sentinel) 'development must preserve staged publish artifacts'
    $dotnet = @($run.Operations | Where-Object kind -eq 'dotnet')
    Assert-Synthetic (($dotnet | ForEach-Object { $_.arguments[0] }) -join ',' -eq 'restore,build,test') 'development may invoke restore/build/test only'
    Assert-Synthetic (($run.Operations | ForEach-Object kind) -join ',' -eq 'sdk,desktop-preflight,dotnet,dotnet,dotnet') 'development must skip full script/tool/AOT/UI/package operations'
    Assert-Synthetic (($dotnet[0].arguments -join ' ') -eq 'restore CycleArc.sln -p:Configuration=Release') 'restore must match Release solution build'
    Assert-Synthetic (($dotnet[1].arguments -join ' ') -eq 'build CycleArc.sln -c Release --no-restore') 'build must reuse matching restore'
    Assert-Synthetic (($dotnet[2].arguments -join ' ').Contains('test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --no-build --filter FullyQualifiedName~Example --logger trx --results-directory')) 'unit filter/evidence must be forwarded to built unit project'
    Assert-Synthetic ($run.Output.Contains('Partial local validation passed')) 'development must report partial validation'
    Write-Host 'PASS: development reuses preflight/restore/build/unit tests; no app, UI, publish, package, install or staging cleanup.'

    $run = Invoke-SyntheticGate @('-DevelopmentOnly', '-BuildOnly')
    Assert-Synthetic ($run.ExitCode -eq 0) $run.Output
    Assert-Synthetic (($run.Operations | Where-Object kind -eq 'dotnet' | ForEach-Object { $_.arguments[0] }) -join ',' -eq 'restore,build') 'BuildOnly must omit tests explicitly'
    foreach ($failure in @('sdk', 'desktop', 'restore', 'build', 'test')) {
        $run = Invoke-SyntheticGate @('-DevelopmentOnly') $failure
        Assert-Synthetic ($run.ExitCode -ne 0) "failure '$failure' must fail"
        Assert-Synthetic (!$run.Output.Contains('Partial local validation passed')) 'failed validation must not claim success'
        $verbs = @($run.Operations | Where-Object kind -eq 'dotnet' | ForEach-Object { $_.arguments[0] })
        $expected = switch ($failure) { 'restore' { 'restore' }; 'build' { 'restore,build' }; 'test' { 'restore,build,test' }; default { '' } }
        Assert-Synthetic (($verbs -join ',') -eq $expected) "failure '$failure' must stop before the next operation"
    }
    Write-Host 'PASS: build-only is explicit; SDK/running-build-output/restore/build/test failures stop immediately.'

    foreach ($invalid in @(
        @('-BuildOnly'), @('-TestFilter', 'Example'), @('-DevelopmentOnly', '-Fast'),
        @('-DevelopmentOnly', '-PreviewDirectory', 'artifacts/preview'),
        @('-DevelopmentOnly', '-TestFilter', ' '), @('-DevelopmentOnly', '-BuildOnly', '-TestFilter', 'Example'),
        @('-DevelopmentOnly', '-BuildOnly', '-TestResultsDirectory', 'TestResults/local'),
        @('-DevelopmentOnly', '-TestResultsDirectory', 'src')
    )) {
        $run = Invoke-SyntheticGate $invalid
        Assert-Synthetic ($run.ExitCode -ne 0) "invalid scope/evidence '$($invalid -join ' ')' must fail"
        Assert-Synthetic ($run.Operations.Count -eq 0) 'invalid scope/evidence must fail before preflight/build'
    }
    Write-Host 'PASS: invalid combinations cannot weaken full validation or write evidence into sources.'

    $run = Invoke-SyntheticGate @('-NoLaunch')
    Assert-Synthetic ($run.ExitCode -eq 0) $run.Output
    $verbs = @($run.Operations | Where-Object kind -eq 'dotnet' | ForEach-Object { $_.arguments[0] })
    Assert-Synthetic (($verbs -join ',') -eq 'restore,tool,build,run,test,run,build,publish,run,publish,run,run') '-NoLaunch must retain full build/UI/flavour/publish/package validation'
    Assert-Synthetic (@($run.Operations | Where-Object kind -eq 'package').Count -eq 1) 'full gate must package once'
    Assert-Synthetic (@($run.Operations | Where-Object kind -eq 'aot-toolchain').Count -eq 1) 'full gate must retain AOT preflight'
    foreach ($name in @('BuildPrerequisites', 'BootstrapBuildLocal', 'ReleaseDependencies', 'BuildLocalFailure')) {
        Assert-Synthetic (@($run.Operations | Where-Object kind -eq "$name-regression").Count -eq 1) "full gate must retain the upstream $name regression"
    }
    Assert-Synthetic (!$run.Output.Contains('Partial local validation passed')) 'full and partial evidence must remain distinguishable'
    Write-Host 'PASS: -NoLaunch still executes the complete gate through package verification.'
}
finally {
    $env:CYCLEARC_SYNTHETIC_LOG = $savedLog
    $env:CYCLEARC_SYNTHETIC_FAILURE = $savedFailure
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    if (!$absolute.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolute) -notlike 'CycleArc-dev-check-*') { throw "Unsafe synthetic cleanup target: $absolute" }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
