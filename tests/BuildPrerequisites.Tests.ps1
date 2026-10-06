#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts/BuildPrerequisites.ps1')
function Assert-Prerequisite([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
# Any accidental CycleArc question fails these interactive flows immediately.
function Read-Host { throw 'Prerequisite preparation must not ask for input.' }
function New-FixtureState([bool]$PowerShell, [bool]$Sdk, [bool]$Toolchain) {
    $components = @(
        [pscustomobject]@{ Name='PowerShell'; Label='PowerShell 7'; Status=$(if ($PowerShell) {'Ready'} else {'Missing'}) },
        [pscustomobject]@{ Name='DotNetSdk'; Label='.NET 10 SDK'; Status=$(if ($Sdk) {'Ready'} else {'Missing'}) },
        [pscustomobject]@{ Name='VisualStudio'; Label='Visual Studio 2022'; Status=$(if ($Toolchain) {'Ready'} else {'Missing'}) },
        [pscustomobject]@{ Name='VcTools'; Label='MSVC'; Status=$(if ($Toolchain) {'Ready'} else {'Missing'}) }
    )
    [pscustomobject]@{ Status=$(if ($PowerShell -and $Sdk -and $Toolchain) {'Ready'} else {'Missing'});
        Components=$components; DotNetSdk=[pscustomobject]@{ Status=$components[1].Status; MinimumVersion='10.0.100'; Selected=$(if ($Sdk) {'10.0.401'} else {$null}); Installed=@('8.0.424') };
        Toolchain=[pscustomobject]@{ Ok=$Toolchain } }
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-coordinator-' + [guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalCi = $env:CI
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    $script:psReady=$false; $script:sdkReady=$false; $script:vcReady=$false
    $script:installs=@(); $script:probes=0; $script:observedInstalls=@()
    $flow=@{ RepoRoot=$fixtureRoot; InteractiveProbe={ $true }; PathEnabler={ $true };
        StateResolver={ param($root) $script:probes++; $script:observedInstalls += ($script:installs -join ','); New-FixtureState $script:psReady $script:sdkReady $script:vcReady };
        PowerShellInstaller={ $script:installs += 'PowerShell'; $script:psReady=$true; [pscustomobject]@{Status='Ready'} };
        SdkInstaller={ $script:installs += 'SDK'; $script:sdkReady=$true; [pscustomobject]@{Status='Ready'} };
        ToolchainInstaller={ $script:installs += 'C++/SDK'; $script:vcReady=$true; [pscustomobject]@{Status='Ready'} } }
    $result=Invoke-CycleArcBuildPrerequisites @flow
    Assert-Prerequisite ($result.Status -eq 'Ready') 'all prerequisites must continue in same call'
    Assert-Prerequisite (($installs -join ',') -eq 'PowerShell,SDK,C++/SDK') 'missing prerequisites must install in order without input'
    Assert-Prerequisite ($probes -eq 5 -and ($observedInstalls -join ';') -eq ';PowerShell;PowerShell,SDK;PowerShell,SDK,C++/SDK;PowerShell,SDK,C++/SDK') 'probe must occur between every installation and before continuation'
    $script:installs=@()
    Invoke-CycleArcBuildPrerequisites @flow | Out-Null
    Assert-Prerequisite ($installs.Count -eq 0) 'ready tools require no installation'
    $script:sdkReady=$false
    $result=Invoke-CycleArcBuildPrerequisites @flow
    Assert-Prerequisite ($result.Status -eq 'Ready' -and ($installs -join ',') -eq 'SDK') 'company PC only8 installs SDK only without input'
    $script:sdkReady=$false; $script:vcReady=$false; $script:installs=@(); $script:observedInstalls=@()
    $result=Invoke-CycleArcBuildPrerequisites @flow
    Assert-Prerequisite ($result.Status -eq 'Ready' -and ($installs -join ',') -eq 'SDK,C++/SDK' -and ($observedInstalls -join ';') -eq ';SDK;SDK,C++/SDK;SDK,C++/SDK') 'SDK and C++ must prepare sequentially with real probes and no input'
    $repair=$flow.Clone(); $script:vcReady=$false; $script:installs=@()
    $repair.StateResolver={
        $state=New-FixtureState $true $true $script:vcReady
        if (!$script:vcReady) { $state.Components[3].Status='RepairRequired' }
        $state
    }
    $result=Invoke-CycleArcBuildPrerequisites @repair
    Assert-Prerequisite ($result.Status -eq 'Ready' -and ($installs -join ',') -eq 'C++/SDK') 'RepairRequired must immediately prepare only the affected toolchain'
    foreach ($suppression in @(@{NoPrompt=$true}, @{SilentInstall=$true}, @{})) {
        $script:sdkReady=$false; $script:installs=@()
        $blocked=$flow.Clone(); $blocked.InteractiveProbe={ $false }
        $thrown=$false
        try { Invoke-CycleArcBuildPrerequisites @blocked @suppression | Out-Null } catch { $thrown=$true }
        Assert-Prerequisite ($thrown -and $installs.Count -eq 0) 'suppressed invocations must never install'
    }
    foreach ($suppression in @(@{NoPrompt=$true}, @{SilentInstall=$true})) {
        $thrown=$false
        try { Invoke-CycleArcBuildPrerequisites @flow @suppression | Out-Null } catch { $thrown=$true }
        Assert-Prerequisite ($thrown -and $installs.Count -eq 0) 'suppression cannot be overridden by interactive probe'
    }
    $env:CI='true'
    $ciFlow=$flow.Clone(); $ciFlow.Remove('InteractiveProbe')
    $thrown=$false
    try { Invoke-CycleArcBuildPrerequisites @ciFlow | Out-Null } catch { $thrown=$_.Exception.Message -like '*Automatic installation is disabled*' }
    Assert-Prerequisite ($thrown -and $installs.Count -eq 0) 'actual CI environment must disable automatic installation'
    $env:CI=$originalCi
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
    foreach ($reason in @('Authenticode signature verification failed', 'Company policy blocked installer')) {
        $script:reason=$reason; $script:installs=@()
        $failed=$flow.Clone(); $failed.SdkInstaller={ $script:installs += 'SDK'; throw $script:reason }
        $thrown=$false
        try { Invoke-CycleArcBuildPrerequisites @failed | Out-Null } catch { $thrown=$_.Exception.Message.Contains($reason) -and $_.Exception.Message.Contains('.NET 10 SDK') }
        Assert-Prerequisite ($thrown -and ($installs -join ',') -eq 'SDK') 'signature/policy failure must retain the exact cause and never continue'
    }
    $script:installs=@()
    $reboot=$flow.Clone(); $reboot.StateResolver={ $state=New-FixtureState $true $false $false; $state.Components[2].Status='RebootRequired'; $state }
    $result=Invoke-CycleArcBuildPrerequisites @reboot
    Assert-Prerequisite ($result.Status -eq 'RebootRequired' -and $installs.Count -eq 0) 'pre-existing reboot requirement must stop before any installer'
    $result=Invoke-CycleArcBuildPrerequisites @flow -ManualPrerequisites
    Assert-Prerequisite ($result.Status -eq 'Manual' -and $installs.Count -eq 0) 'explicit manual mode shows instructions without preparing or continuing'
    $script:sdkReady=$true; $script:vcReady=$true
    $result=Invoke-CycleArcBuildPrerequisites @flow -ManualPrerequisites
    Assert-Prerequisite ($result.Status -eq 'Manual' -and $installs.Count -eq 0) 'explicit manual mode never builds even with ready prerequisites'
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
    Write-Host 'PASS: no-input SDK8-only continuation, sequential installs/repairs and probes, manual mode, safe failures and noninteractive suppression (all installers fake).'
}
finally {
    $env:PATH = $originalPath
    $env:CI = $originalCi
    Remove-Item Function:Read-Host
    $full=[IO.Path]::GetFullPath($fixtureRoot)
    if ($full.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $full) -like 'CycleArc-coordinator-*') {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
