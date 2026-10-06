#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts/BuildPrerequisites.ps1')
function Assert-Prerequisite([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function New-FixtureState([bool]$PowerShell, [bool]$Sdk, [bool]$Toolchain) {
    $components = @(
        [pscustomobject]@{ Name='PowerShell'; Label='PowerShell 7'; Status=$(if ($PowerShell) {'Ready'} else {'Missing'}) },
        [pscustomobject]@{ Name='DotNetSdk'; Label='.NET 10 SDK'; Status=$(if ($Sdk) {'Ready'} else {'Missing'}) }
    )
    [pscustomobject]@{ Status=$(if ($PowerShell -and $Sdk -and $Toolchain) {'Ready'} else {'Missing'});
        Components=$components; DotNetSdk=[pscustomobject]@{ Status=$components[1].Status; MinimumVersion='10.0.100'; Selected=$(if ($Sdk) {'10.0.401'} else {$null}); Installed=@('8.0.424') };
        Toolchain=[pscustomobject]@{ Ok=$Toolchain } }
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-coordinator-' + [guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    $script:psReady=$false; $script:sdkReady=$false; $script:vcReady=$false
    $script:questions=0; $script:installs=@(); $script:probes=0
    $flow=@{ RepoRoot=$fixtureRoot; InteractiveProbe={ $true }; PathEnabler={ $true };
        StateResolver={ param($root) $script:probes++; New-FixtureState $script:psReady $script:sdkReady $script:vcReady };
        Interaction={ $script:questions++; '1' };
        PowerShellInstaller={ $script:installs += 'PowerShell'; $script:psReady=$true; [pscustomobject]@{Status='Ready'} };
        SdkInstaller={ $script:installs += 'SDK'; $script:sdkReady=$true; [pscustomobject]@{Status='Ready'} };
        ToolchainInstaller={ $script:installs += 'C++/SDK'; $script:vcReady=$true; [pscustomobject]@{Status='Ready'} } }
    $result=Invoke-CycleArcBuildPrerequisites @flow
    Assert-Prerequisite ($result.Status -eq 'Ready') 'all prerequisites must continue in same call'
    Assert-Prerequisite ($questions -eq 1 -and ($installs -join ',') -eq 'PowerShell,SDK,C++/SDK') 'one approval and ordered missing installations'
    Assert-Prerequisite ($probes -ge 5) 'each installation and final readiness require actual reprobes'
    $script:questions=0; $script:installs=@()
    Invoke-CycleArcBuildPrerequisites @flow | Out-Null
    Assert-Prerequisite ($questions -eq 0 -and $installs.Count -eq 0) 'ready tools require no prompt or installation'
    $script:sdkReady=$false
    Invoke-CycleArcBuildPrerequisites @flow | Out-Null
    Assert-Prerequisite (($installs -join ',') -eq 'SDK' -and $questions -eq 1) 'company PC only8 installs SDK only'
    foreach ($suppression in @(@{NoPrompt=$true}, @{SilentInstall=$true}, @{})) {
        $script:sdkReady=$false; $script:questions=0; $script:installs=@()
        $blocked=$flow.Clone(); $blocked.InteractiveProbe={ $false }
        $thrown=$false
        try { Invoke-CycleArcBuildPrerequisites @blocked @suppression | Out-Null } catch { $thrown=$true }
        Assert-Prerequisite ($thrown -and $questions -eq 0 -and $installs.Count -eq 0) 'suppressed invocations must never prompt or install'
    }
    foreach ($suppression in @(@{NoPrompt=$true}, @{SilentInstall=$true})) {
        $thrown=$false
        try { Invoke-CycleArcBuildPrerequisites @flow @suppression | Out-Null } catch { $thrown=$true }
        Assert-Prerequisite ($thrown -and $questions -eq 0 -and $installs.Count -eq 0) 'suppression cannot be overridden by interactive probe'
    }
    foreach ($outcome in @('Cancelled','RebootRequired','PolicyBlocked')) {
        $script:outcome=$outcome; $script:sdkReady=$false; $script:vcReady=$false; $script:installs=@()
        $stopped=$flow.Clone(); $stopped.SdkInstaller={ $script:installs += 'SDK'; [pscustomobject]@{Status=$script:outcome} }
        $result=Invoke-CycleArcBuildPrerequisites @stopped
        Assert-Prerequisite ($result.Status -eq $outcome -and ($installs -join ',') -eq 'SDK') 'failed SDK preparation must stop before C++ or build'
    }
    $notDetected=$flow.Clone(); $notDetected.SdkInstaller={ [pscustomobject]@{Status='Ready'} }
    $thrown=$false
    try { Invoke-CycleArcBuildPrerequisites @notDetected | Out-Null } catch { $thrown=$_.Exception.Message -like '*actual prerequisite*' }
    Assert-Prerequisite $thrown 'successful installer with missing SDK must fail'
    foreach ($choice in @('2','3')) {
        $script:choice=$choice; $script:installs=@()
        $declined=$flow.Clone(); $declined.Interaction={ $script:choice }
        $result=Invoke-CycleArcBuildPrerequisites @declined
        Assert-Prerequisite ($result.Status -eq $(if ($choice -eq '2') {'Manual'} else {'Cancelled'}) -and $installs.Count -eq 0) 'manual/cancel cannot install'
    }
    $state=Get-CycleArcBuildPrerequisiteState -RepoRoot $repoRoot -PowerShellResolver { $null } -SdkResolver { (New-FixtureState $false $false $false).DotNetSdk } `
        -ToolchainResolver { [pscustomobject]@{Ok=$false;Installation=$null;Linker=$null;SdkLibrary=$null} } -ExistingInstallationResolver { $null } -WindowsSdkResolver { 'fake/kernel32.lib' }
    Assert-Prerequisite ($state.Components.Count -eq 6) 'state must describe every prerequisite'
    Assert-Prerequisite (($state.Components | Where-Object Name -eq WindowsSdk).Status -eq 'Ready') 'independent SDK probe cannot be hidden by missing VS'
    $offPath = New-FixtureState $true $true $true
    $offPath | Add-Member NoteProperty PowerShellPath (Join-Path $fixtureRoot 'pwsh/pwsh.exe')
    $offPath.DotNetSdk | Add-Member NoteProperty DotnetPath (Join-Path $fixtureRoot 'dotnet/dotnet.exe')
    $pathFlow = $flow.Clone(); $pathFlow.StateResolver = { $offPath }
    Invoke-CycleArcBuildPrerequisites @pathFlow | Out-Null
    Assert-Prerequisite ($env:PATH.Contains((Join-Path $fixtureRoot 'pwsh')) -and $env:PATH.Contains((Join-Path $fixtureRoot 'dotnet'))) 'newly installed hosts must enter inherited PATH before build handoff'
    Write-Host 'PASS: one approval, SDK8-only continuation, ordered installs/reprobes, failures and noninteractive suppression (all installers fake).'
}
finally {
    $env:PATH = $originalPath
    $full=[IO.Path]::GetFullPath($fixtureRoot)
    if ($full.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $full) -like 'CycleArc-coordinator-*') {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
