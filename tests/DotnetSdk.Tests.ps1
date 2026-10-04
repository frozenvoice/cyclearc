#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/DotnetSdk.ps1')
function Assert-SdkTest([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "SDK regression failed: $Message" }
}
foreach ($valid in @('10.0.100', '10.0.101', '10.0.401')) {
    Assert-SdkTest (Test-CycleArcSelectedSdk $valid) "stable SDK $valid was rejected"
}
foreach ($invalid in @('8.0.424', '9.0.100', '11.0.100', '10.1.100', '10.0.99', '10.0.100-preview.1', 'garbage')) {
    Assert-SdkTest (!(Test-CycleArcSelectedSdk $invalid)) "unsupported SDK $invalid was accepted"
}
$sdkConfiguration = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
Assert-SdkTest ($sdkConfiguration.sdk.version -eq '10.0.100') 'minimum must be a released stable SDK, independent of the local installed feature band'
Assert-SdkTest ($sdkConfiguration.sdk.rollForward -eq 'latestFeature' -and $sdkConfiguration.sdk.allowPrerelease -eq $false) 'selection must stay on stable 10.0 SDKs'

# Prove the SDK check resolves from the checkout, rejects selection failure, and restores cwd.
# These fixtures never run a build, remove files or touch an installed application.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-sdk-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $fixtureRoot
    $script:ObservedSdkDirectory = $null
    $script:FixtureSelectedSdk = '10.0.401'
    $script:FixtureSdkExitCode = 0
    function dotnet {
        $script:ObservedSdkDirectory = (Get-Location).Path
        $global:LASTEXITCODE = $script:FixtureSdkExitCode
        $script:FixtureSelectedSdk
    }
    $startingDirectory = (Get-Location).Path
    Assert-CycleArcDotnetSdk -RepoRoot $fixtureRoot
    Assert-SdkTest ($script:ObservedSdkDirectory -eq $fixtureRoot) 'selection was checked outside the requested checkout'
    Assert-SdkTest ((Get-Location).Path -eq $startingDirectory) 'SDK selection changed the caller directory'
    foreach ($selection in @('8.0.424', '11.0.100', '10.0.401-preview.1')) {
        $script:FixtureSelectedSdk = $selection
        $rejected = $false
        try { Assert-CycleArcDotnetSdk -RepoRoot $fixtureRoot } catch { $rejected = $_.Exception.Message -like '*stable .NET 10 SDK*' }
        Assert-SdkTest $rejected "selected SDK $selection was accepted"
    }
    $script:FixtureSelectedSdk = '10.0.401'
    $script:FixtureSdkExitCode = 1
    $rejected = $false
    try { Assert-CycleArcDotnetSdk -RepoRoot $fixtureRoot } catch { $rejected = $_.Exception.Message -like '*stable .NET 10 SDK*' }
    Assert-SdkTest $rejected 'a failed SDK resolution was accepted'
    Assert-SdkTest ((Get-Location).Path -eq $startingDirectory) 'failed SDK selection changed the caller directory'
}
finally {
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedFixture) -like 'CycleArc-sdk-*') {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
Write-Host 'PASS: stable .NET 10 SDK selection, checkout resolution, failure rejection and cwd preservation.'
