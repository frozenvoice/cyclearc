# Focused SDK/bootstrap regressions run on both Windows PowerShell 5.1 and PowerShell 7.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/SetupUiPrerequisites.ps1')
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
Assert-SdkTest (!(Test-CycleArcSelectedSdk '10.0.101' '10.0.200')) 'below-minimum SDK was accepted'
$sdkConfiguration = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
Assert-SdkTest ($sdkConfiguration.sdk.version -eq '10.0.100') 'minimum must remain stable and independent of the local feature band'
Assert-SdkTest ($sdkConfiguration.sdk.rollForward -eq 'latestFeature' -and $sdkConfiguration.sdk.allowPrerelease -eq $false) 'selection must stay on stable 10.0 SDKs'

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-sdk-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$startingPath = $env:PATH
$startingDotnetRoot = $env:DOTNET_ROOT
$startingDotnetRootX64 = $env:DOTNET_ROOT_X64
$startingDirectory = (Get-Location).Path
try {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $fixtureRoot
    $hostRoot = Join-Path $fixtureRoot 'explicit-sdk'
    New-Item -ItemType Directory -Path $hostRoot | Out-Null
    $peBytes = New-Object byte[] 128
    $peBytes[0] = 0x4D; $peBytes[1] = 0x5A; $peBytes[60] = 64
    $peBytes[64] = 0x50; $peBytes[65] = 0x45; $peBytes[68] = 0x64; $peBytes[69] = 0x86
    $hostPath = Join-Path $hostRoot 'dotnet.exe'
    [IO.File]::WriteAllBytes($hostPath, $peBytes)
    $env:DOTNET_ROOT_X64 = $hostRoot
    $env:DOTNET_ROOT = Join-Path $fixtureRoot 'missing-sdk'
    Assert-SdkTest ((Resolve-CycleArcDotnetHost) -eq $hostPath) 'explicit x64 SDK root was hidden by the system SDK'
    $env:DOTNET_ROOT_X64 = Join-Path $fixtureRoot 'missing-sdk'
    $env:DOTNET_ROOT = $hostRoot
    Assert-SdkTest ((Resolve-CycleArcDotnetHost) -eq $hostPath) 'explicit general SDK root was hidden by the system SDK'
    $peBytes[68] = 0x4C; $peBytes[69] = 0x01
    [IO.File]::WriteAllBytes($hostPath, $peBytes)
    Assert-SdkTest (!(Test-CycleArcDotnetX64Host $hostPath)) 'x86 dotnet host was accepted as x64'
    Assert-SdkTest ((Resolve-CycleArcDotnetHost) -ne $hostPath) 'x86 explicit root was accepted for the x64 build'
    $env:DOTNET_ROOT = $startingDotnetRoot
    $env:DOTNET_ROOT_X64 = $startingDotnetRootX64
    $script:ProbeCalls = @()
    $script:SelectedSdk = '10.0.401'
    $script:SdkInventory = @('8.0.424 [C:\Program Files\dotnet\sdk]', '10.0.401 [C:\Program Files\dotnet\sdk]')
    $script:SelectionExit = 0
    $script:InventoryExit = 0
    $probeArguments = @{
        RepoRoot = $fixtureRoot
        DotnetResolver = { 'C:\Program Files\dotnet\dotnet.exe' }
        DotnetInvoker = {
            param($path, $arguments)
            $script:ProbeCalls += [pscustomobject]@{ Directory = (Get-Location).Path; Path = $path; Arguments = $arguments }
            if ($arguments[0] -eq '--list-sdks') { return [pscustomobject]@{ ExitCode = $script:InventoryExit; Output = $script:SdkInventory } }
            [pscustomobject]@{ ExitCode = $script:SelectionExit; Output = @($script:SelectedSdk) }
        }
    }
    $state = Get-CycleArcDotnetSdkState @probeArguments
    Assert-SdkTest ($state.Status -eq 'Ready' -and $state.Selected -eq '10.0.401') 'coexisting SDKs were rejected'
    Assert-SdkTest ($state.Installed -contains '8.0.424' -and $state.Installed -contains '10.0.401') 'inventory was lost'
    Assert-SdkTest ($script:ProbeCalls.Count -eq 2) 'inventory and selection must each be probed'
    Assert-SdkTest ($script:ProbeCalls[0].Arguments[0] -eq '--list-sdks' -and $script:ProbeCalls[1].Arguments[0] -eq '--version') 'wrong probe commands'
    Assert-SdkTest (@($script:ProbeCalls | Where-Object { $_.Directory -ne $fixtureRoot }).Count -eq 0) 'SDK resolved outside checkout'
    Assert-SdkTest ((Get-Location).Path -eq $startingDirectory) 'probe changed caller directory'
    Assert-CycleArcDotnetSdk @probeArguments
    Assert-SdkTest ($env:PATH.StartsWith('C:\Program Files\dotnet;')) 'selected x64 host was not enabled for the build'

    # Company PC has only 8.0.424: inventory succeeds while global.json resolution fails
    # with a multi-line diagnostic. It must never be represented as the selected version.
    $script:SdkInventory = @('8.0.424 [C:\Program Files\dotnet\sdk]')
    $script:SelectionExit = 145
    $script:SelectedSdk = "The command could not be loaded.`nA compatible .NET SDK was not found.`nRequested SDK version: 10.0.100"
    $state = Get-CycleArcDotnetSdkState @probeArguments
    Assert-SdkTest ($state.Status -eq 'Missing' -and $null -eq $state.Selected -and $state.Installed[0] -eq '8.0.424') 'company SDK failure was misclassified'
    Assert-SdkTest ($state.Diagnostic -like '*command could not be loaded*') 'raw diagnostic was not retained for logs'
    $message = $null
    try { Assert-CycleArcDotnetSdk @probeArguments } catch { $message = $_.Exception.Message }
    Assert-SdkTest ($message -like '*minimum 10.0.100*' -and $message -like '*8.0.424*' -and $message -notlike '*command could not be loaded*') 'human guidance leaked raw CLI output'

    foreach ($selection in @('8.0.424', '11.0.100', '10.0.99', '10.0.401-preview.1')) {
        $script:SelectedSdk = $selection
        $script:SelectionExit = 0
        $script:SdkInventory = @("$selection [C:\Program Files\dotnet\sdk]")
        Assert-SdkTest ((Get-CycleArcDotnetSdkState @probeArguments).Status -eq 'Missing') "unsupported selection $selection was accepted"
    }
    $script:SelectedSdk = '10.0.401'
    $script:SdkInventory = @('10.0.401 [C:\Program Files\dotnet\sdk]')
    $script:SelectionExit = 1
    Assert-SdkTest ((Get-CycleArcDotnetSdkState @probeArguments).Status -eq 'Missing') 'failed selection was accepted'
    $script:SelectionExit = 0
    $script:InventoryExit = 1
    Assert-SdkTest ((Get-CycleArcDotnetSdkState @probeArguments).Status -eq 'Missing') 'failed inventory was accepted'
    $script:InventoryExit = 0
    $absentArguments = $probeArguments.Clone()
    $absentArguments.DotnetResolver = { $null }
    Assert-SdkTest ((Get-CycleArcDotnetSdkState @absentArguments).Status -eq 'Missing') 'absent host was accepted'
    $throwArguments = $probeArguments.Clone()
    $throwArguments.DotnetInvoker = { throw 'Synthetic native launch failure' }
    Assert-SdkTest ((Get-CycleArcDotnetSdkState @throwArguments).Diagnostic -eq 'Synthetic native launch failure') 'probe exception not retained'
    Assert-SdkTest ((Get-Location).Path -eq $startingDirectory) 'failed probe changed caller directory'

    # Exercise redirected native stderr on PS5 as well as PS7, rather than mocking
    # the implementation of Invoke-CycleArcDotnetProbe.
    $nativeProbe = Join-Path $fixtureRoot 'probe.cmd'
    [IO.File]::WriteAllText($nativeProbe, @'
@echo off
if "%~1"=="--list-sdks" (
    echo 8.0.424 [C:\SDK]
    exit /b 0
)
echo The command could not be loaded. 1>&2
echo A compatible SDK was not found. 1>&2
exit /b 145
'@)
    $nativeState = Get-CycleArcDotnetSdkState -RepoRoot $fixtureRoot -DotnetResolver { $nativeProbe }
    Assert-SdkTest ($nativeState.Status -eq 'Missing' -and $nativeState.Installed -contains '8.0.424' -and
        $null -eq $nativeState.Selected -and $nativeState.Diagnostic -like '*command could not be loaded*') 'native stderr escaped the SDK diagnostic boundary'

    # All install boundaries are fake; these tests never download or execute software.
    $fixtureBytes = [Text.Encoding]::UTF8.GetBytes('synthetic Microsoft SDK installer')
    $sha = [Security.Cryptography.SHA512]::Create()
    try { $fixtureHash = ([BitConverter]::ToString($sha.ComputeHash($fixtureBytes))).Replace('-', '') }
    finally { $sha.Dispose() }
    function New-SdkMetadataFile([string]$Version, [string]$Rid = 'win-x64', [string]$Url = '') {
        if (!$Url) { $Url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$Version/dotnet-sdk-$Version-$Rid.exe" }
        [pscustomobject]@{ version = $Version; files = @([pscustomobject]@{
            name = "dotnet-sdk-$Rid.exe"; rid = $Rid; url = $Url; hash = $fixtureHash
        }) }
    }
    $script:SdkMetadata = [pscustomobject]@{ 'channel-version' = '10.0'; releases = @(
        [pscustomobject]@{ sdk = (New-SdkMetadataFile '10.0.100') },
        [pscustomobject]@{ sdks = @((New-SdkMetadataFile '10.0.401'), (New-SdkMetadataFile '10.0.500-preview.1'), (New-SdkMetadataFile '11.0.100'), (New-SdkMetadataFile '10.0.402' 'win-x86')) }
    ) }
    Assert-SdkTest ((Resolve-CycleArcDotnetSdkInstaller $script:SdkMetadata '10.0.100').Version -eq '10.0.401') 'latest compatible stable x64 SDK was not selected'
    $badMetadata = [pscustomobject]@{ 'channel-version' = '10.0'; releases = @([pscustomobject]@{ sdk = (New-SdkMetadataFile '10.0.401' 'win-x64' 'https://attacker.invalid/dotnet-sdk-10.0.401-win-x64.exe') }) }
    $rejected = $false
    try { Resolve-CycleArcDotnetSdkInstaller $badMetadata '10.0.100' | Out-Null } catch { $rejected = $true }
    Assert-SdkTest $rejected 'non-Microsoft installer endpoint was accepted'
    $previewOnly = [pscustomobject]@{ 'channel-version' = '10.0'; releases = @([pscustomobject]@{ sdk = (New-SdkMetadataFile '10.0.500-preview.1') }) }
    $rejected = $false
    try { Resolve-CycleArcDotnetSdkInstaller $previewOnly '10.0.100' | Out-Null } catch { $rejected = $true }
    Assert-SdkTest $rejected 'preview-only metadata was accepted'

    $script:InstallResolverCalls = 0
    $script:InstallTrace = @()
    $script:InstallExit = 0
    $script:InstallReady = $true
    $script:SignatureValid = $true
    $script:TamperedDownload = $false
    $script:NativeFailure = 0
    $missing = [pscustomobject]@{ Status = 'Missing'; MinimumVersion = '10.0.100'; Installed = @('8.0.424'); Selected = $null; DotnetPath = $null }
    $ready = [pscustomobject]@{ Status = 'Ready'; MinimumVersion = '10.0.100'; Installed = @('8.0.424', '10.0.401'); Selected = '10.0.401'; DotnetPath = $null }
    $installArguments = @{
        RepoRoot = $fixtureRoot
        Resolver = {
            param($root)
            Assert-SdkTest ($root -eq $fixtureRoot) 'installer re-probed the wrong checkout'
            $script:InstallResolverCalls++
            if ($script:InstallResolverCalls -gt 1 -and $script:InstallReady) { return $ready }
            $missing
        }
        MetadataReader = {
            param($uri)
            Assert-SdkTest ($uri -eq 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json') 'wrong metadata endpoint'
            $script:SdkMetadata
        }
        TemporaryDirectoryFactory = { New-SetupUiPrerequisiteTempDirectory }
        Downloader = {
            param($uri, $destination)
            Assert-SdkTest ($uri -like '*10.0.401*win-x64.exe') 'wrong downloaded SDK'
            $script:InstallTrace += 'Download'
            if ($script:TamperedDownload) { [IO.File]::WriteAllText($destination, 'tampered') }
            else { [IO.File]::WriteAllBytes($destination, $fixtureBytes) }
        }
        SignatureValidator = { param($path) $script:InstallTrace += 'Signature'; $script:SignatureValid }
        ProcessRunner = {
            param($path, $arguments)
            $script:InstallTrace += 'Process'
            Assert-SdkTest (($arguments -join ' ') -eq '/install /passive /norestart') 'SDK installer must be visible and must not restart'
            if ($script:NativeFailure) { throw (New-Object ComponentModel.Win32Exception $script:NativeFailure) }
            $script:InstallExit
        }
    }
    $result = Invoke-CycleArcDotnetSdkInstall @installArguments
    Assert-SdkTest ($result.Status -eq 'Ready' -and $script:InstallResolverCalls -eq 2) 'SDK installation was not re-probed'
    Assert-SdkTest (($script:InstallTrace -join ',') -eq 'Download,Signature,Process') 'download/signature/launch order changed'

    foreach ($scenario in @(
        @{ Code = 1223; Status = 'Cancelled' }, @{ Code = 1602; Status = 'Cancelled' },
        @{ Code = 2147944002; Status = 'Cancelled' }, @{ Code = 3010; Status = 'RebootRequired' },
        @{ Code = 1641; Status = 'RebootRequired' }, @{ Code = 1625; Status = 'PolicyBlocked' },
        @{ Code = 2147944025; Status = 'PolicyBlocked' }, @{ Code = 1260; Status = 'PolicyBlocked' }
    )) {
        $script:InstallResolverCalls = 0
        $script:InstallExit = $scenario.Code
        $result = Invoke-CycleArcDotnetSdkInstall @installArguments
        Assert-SdkTest ($result.Status -eq $scenario.Status -and $script:InstallResolverCalls -eq 1) "exit $($scenario.Code) was falsely ready"
    }
    foreach ($native in @(1223, 5, 1260)) {
        $script:InstallResolverCalls = 0
        $script:NativeFailure = $native
        $result = Invoke-CycleArcDotnetSdkInstall @installArguments
        $expected = if ($native -eq 1223) { 'Cancelled' } else { 'PolicyBlocked' }
        Assert-SdkTest ($result.Status -eq $expected) "native failure $native was not handled"
    }
    $script:NativeFailure = 0
    $script:InstallExit = 0
    $script:InstallReady = $false
    $script:InstallResolverCalls = 0
    $rejected = $false
    try { Invoke-CycleArcDotnetSdkInstall @installArguments | Out-Null } catch { $rejected = $_.Exception.Message -like '*still cannot select*' }
    Assert-SdkTest ($rejected -and $script:InstallResolverCalls -eq 2) 'installer exit zero bypassed actual SDK readiness'
    $script:InstallReady = $true

    foreach ($failure in @('Signature', 'Hash')) {
        $script:InstallResolverCalls = 0
        $script:InstallTrace = @()
        $script:SignatureValid = $failure -ne 'Signature'
        $script:TamperedDownload = $failure -eq 'Hash'
        $rejected = $false
        try { Invoke-CycleArcDotnetSdkInstall @installArguments | Out-Null } catch { $rejected = $_.Exception.Message -like '*not executed*' }
        Assert-SdkTest ($rejected -and $script:InstallTrace -notcontains 'Process') "$failure failure launched the SDK installer"
    }
    $script:SignatureValid = $true
    $script:TamperedDownload = $false
    $script:InstallResolverCalls = 0
    $script:InstallExit = 1618
    $rejected = $false
    try { Invoke-CycleArcDotnetSdkInstall @installArguments | Out-Null } catch { $rejected = $_.Exception.Message -like '*exit 1618*' }
    Assert-SdkTest $rejected 'busy installer was accepted'
    $alreadyReady = $installArguments.Clone()
    $alreadyReady.Resolver = { param($root) $ready }
    $alreadyReady.MetadataReader = { throw 'Already-ready machine must not download metadata' }
    Assert-SdkTest ((Invoke-CycleArcDotnetSdkInstall @alreadyReady).Status -eq 'Ready') 'already-ready SDK was not reused'
}
finally {
    $env:PATH = $startingPath
    $env:DOTNET_ROOT = $startingDotnetRoot
    $env:DOTNET_ROOT_X64 = $startingDotnetRootX64
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedFixture) -like 'CycleArc-sdk-*') {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
Write-Host 'PASS: checkout SDK probes, company SDK guidance, official stable x64 selection, hash/signature checks, cancellation, policy, reboot and mandatory re-probe.'
