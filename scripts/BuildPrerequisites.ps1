#Requires -Version 5.1
# Shared source-build coordinator. Windows PowerShell can run this before pwsh exists.
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'SetupUiToolchain.ps1')
. (Join-Path $PSScriptRoot 'SetupUiPrerequisites.ps1')
. (Join-Path $PSScriptRoot 'DotnetSdk.ps1')
. (Join-Path $PSScriptRoot 'Bootstrap-BuildLocal.ps1') -LoadOnly

function Get-CycleArcBuildPrerequisiteState {
    param([string]$RepoRoot, [scriptblock]$PowerShellResolver, [scriptblock]$SdkResolver,
        [scriptblock]$ToolchainResolver, [scriptblock]$ExistingInstallationResolver, [scriptblock]$WindowsSdkResolver)
    if (!$PowerShellResolver) { $PowerShellResolver = { Find-CycleArcPowerShell } }
    if (!$SdkResolver) { $SdkResolver = { param($root) Get-CycleArcDotnetSdkState -RepoRoot $root } }
    if (!$ToolchainResolver) { $ToolchainResolver = { Resolve-SetupUiToolchain } }
    if (!$ExistingInstallationResolver) { $ExistingInstallationResolver = { Resolve-SetupUiExistingInstallation } }
    if (!$WindowsSdkResolver) { $WindowsSdkResolver = { Get-WindowsSdkLibrary } }
    $pwsh = & $PowerShellResolver
    $sdk = & $SdkResolver $RepoRoot
    $toolchain = & $ToolchainResolver
    if (!$sdk -or !$toolchain) { throw 'A build prerequisite probe returned no result.' }
    $existing = $null
    if (!$toolchain.Ok) { $existing = & $ExistingInstallationResolver }
    $windowsSdk = if ($toolchain.SdkLibrary) { $toolchain.SdkLibrary } else { & $WindowsSdkResolver }
    $vs = if ($toolchain.Installation) { 'Ready' } elseif ($existing) { 'Ready' } else { 'Missing' }
    if ($existing -and (Get-SetupUiProperty $existing 'IsRebootRequired')) { $vs = 'RebootRequired' }
    elseif ($existing -and !(Get-SetupUiProperty $existing 'IsComplete')) { $vs = 'RepairRequired' }
    $components = @(
        [pscustomobject]@{ Name = 'PowerShell'; Label = 'PowerShell 7'; Status = $(if ($pwsh) { 'Ready' } else { 'Missing' }); Detail = $pwsh },
        [pscustomobject]@{ Name = 'DotNetSdk'; Label = '.NET 10 SDK'; Status = $sdk.Status; Detail = $sdk.Selected },
        [pscustomobject]@{ Name = 'VisualStudio'; Label = 'Visual Studio 2022'; Status = $vs; Detail = $toolchain.Installation },
        [pscustomobject]@{ Name = 'VcTools'; Label = 'Desktop development with C++ / MSVC x64/x86'; Status = $(if ($toolchain.Installation) { 'Ready' } else { 'Missing' }); Detail = $null },
        [pscustomobject]@{ Name = 'Linker'; Label = 'MSVC x64 link.exe'; Status = $(if ($toolchain.Linker) { 'Ready' } elseif ($toolchain.Installation) { 'RepairRequired' } else { 'Missing' }); Detail = $toolchain.Linker },
        [pscustomobject]@{ Name = 'WindowsSdk'; Label = 'Windows SDK x64 kernel32.lib'; Status = $(if ($windowsSdk) { 'Ready' } else { 'Missing' }); Detail = $windowsSdk }
    )
    [pscustomobject]@{ Status = $(if (@($components | Where-Object Status -ne 'Ready').Count -eq 0 -and $toolchain.Ok) { 'Ready' } else { 'Missing' });
        Components = $components; PowerShellPath = $pwsh; DotNetSdk = $sdk; Toolchain = $toolchain }
}

function Write-CycleArcBuildPrerequisiteState {
    param([object]$State)
    Write-Host 'Preparing CycleArc build environment...'
    foreach ($component in $State.Components) {
        $mark = if ($component.Status -eq 'Ready') { '[OK]' } else { '[--]' }
        $detail = [string](Get-SetupUiProperty $component 'Detail')
        $display = if ($component.Name -eq 'DotNetSdk' -and $detail) { $detail } else { $component.Status }
        Write-Host ('  {0} {1}: {2}' -f $mark, $component.Label, $display)
    }
    if ($State.DotNetSdk.Status -ne 'Ready') {
        Write-Host ('  Required: stable .NET 10 SDK >= {0}' -f $State.DotNetSdk.MinimumVersion)
        Write-Host ('  Selected: {0}' -f $(if ($State.DotNetSdk.Selected) { $State.DotNetSdk.Selected } else { 'none' }))
        Write-Host ('  Installed: {0}' -f $(if (@($State.DotNetSdk.Installed).Count) { $State.DotNetSdk.Installed -join ', ' } else { 'none' }))
    }
}

function Write-CycleArcBuildManualInstructions {
    Write-Host 'Official manual installation:'
    Write-Host '  PowerShell 7: https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows'
    Write-Host '  Windows x64 .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
    Write-Host '  Visual Studio 2022 Installer: Desktop development with C++ (recommended MSVC and Windows SDK components).'
    Write-Host 'Company-managed PCs may require administrator or IT approval.'
}

function Invoke-CycleArcBuildPrerequisites {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot, [switch]$NoPrompt, [switch]$SilentInstall,
        [scriptblock]$StateResolver, [scriptblock]$InteractiveProbe, [scriptblock]$Interaction,
        [scriptblock]$PowerShellInstaller, [scriptblock]$SdkInstaller, [scriptblock]$ToolchainInstaller,
        [scriptblock]$PathEnabler)
    if (!$StateResolver) { $StateResolver = { param($root) Get-CycleArcBuildPrerequisiteState -RepoRoot $root } }
    if (!$InteractiveProbe) { $InteractiveProbe = { Test-SetupUiPrerequisiteInteractive } }
    if (!$Interaction) { $Interaction = { Read-Host 'Choose 1, 2 or 3' } }
    if (!$PowerShellInstaller) { $PowerShellInstaller = { Invoke-CycleArcPowerShellInstall } }
    if (!$SdkInstaller) { $SdkInstaller = { param($root) Invoke-CycleArcDotnetSdkInstall -RepoRoot $root } }
    if (!$ToolchainInstaller) { $ToolchainInstaller = { param($root) Invoke-SetupUiPrerequisitePreflight -RepoRoot $root -Approved } }
    if (!$PathEnabler) { $PathEnabler = { Add-VsWhereToPath } }
    $logRoot = Join-Path $RepoRoot 'artifacts/build-local'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $diagnosticPath = Join-Path $logRoot 'prerequisites.log'
    $state = & $StateResolver $RepoRoot
    if (!$state) { throw 'Build prerequisite probe returned no state.' }
    Write-CycleArcBuildPrerequisiteState $state
    $state | ConvertTo-Json -Depth 8 | Add-Content -LiteralPath $diagnosticPath -Encoding UTF8
    if ($state.Status -ne 'Ready') {
        if ($NoPrompt -or $SilentInstall -or !(& $InteractiveProbe)) {
            Write-CycleArcBuildManualInstructions
            throw 'Build prerequisites are missing in a non-interactive invocation. Automatic installation is disabled.'
        }
        Write-Host '[1] Install/repair all missing prerequisites automatically'
        Write-Host '[2] Show manual instructions'
        Write-Host '[3] Cancel'
        $choice = ([string](& $Interaction $state)).Trim()
        if ($choice -eq '2') { Write-CycleArcBuildManualInstructions; return [pscustomobject]@{ Status = 'Manual'; State = $state } }
        if ($choice -ne '1') { return [pscustomobject]@{ Status = 'Cancelled'; State = $state } }
        foreach ($step in @(
            [pscustomobject]@{ Name = 'PowerShell'; Label = 'PowerShell 7'; Install = $PowerShellInstaller },
            [pscustomobject]@{ Name = 'DotNetSdk'; Label = '.NET 10 SDK'; Install = $SdkInstaller },
            [pscustomobject]@{ Name = 'Toolchain'; Label = 'Visual Studio 2022 C++ / Windows SDK'; Install = $ToolchainInstaller }
        )) {
            $needed = if ($step.Name -eq 'Toolchain') { !$state.Toolchain.Ok } else {
                @($state.Components | Where-Object { $_.Name -eq $step.Name -and $_.Status -ne 'Ready' }).Count -gt 0
            }
            if (!$needed) { continue }
            try {
                Write-Host ('Preparing {0}...' -f $step.Label)
                $result = & $step.Install $RepoRoot
                [pscustomobject]@{ Tool = $step.Label; Result = $result } | ConvertTo-Json -Depth 8 | Add-Content -LiteralPath $diagnosticPath -Encoding UTF8
                # Installer exit alone never establishes readiness. Reprobe after every tool.
                $state = & $StateResolver $RepoRoot
                $state | ConvertTo-Json -Depth 8 | Add-Content -LiteralPath $diagnosticPath -Encoding UTF8
                if (!$result -or $result.Status -ne 'Ready') {
                    $status = if ($result) { $result.Status } else { 'Unknown' }
                    $state.Status = $status
                    foreach ($component in $state.Components) {
                        if (($component.Name -eq $step.Name -or ($step.Name -eq 'Toolchain' -and $component.Name -in @('VisualStudio','VcTools','Linker','WindowsSdk'))) -and $component.Status -ne 'Ready') { $component.Status = $status }
                    }
                    if ($status -eq 'RebootRequired') { Write-Host ('{0} requires a Windows reboot. Reboot, then run build-local.cmd again.' -f $step.Label) }
                    elseif ($status -eq 'Cancelled') { Write-Host ('{0} installation was cancelled (installer or Windows UAC).' -f $step.Label) }
                    else { Write-Host ('Could not prepare {0}: {1}. Company IT approval may be required.' -f $step.Label, $status) }
                    Write-Host 'CycleArc was not built, stopped or installed. Existing CycleArc installation is unchanged.'
                    return [pscustomobject]@{ Status = $status; State = $state }
                }
                $stillMissing = if ($step.Name -eq 'Toolchain') { !$state.Toolchain.Ok } else {
                    @($state.Components | Where-Object { $_.Name -eq $step.Name -and $_.Status -ne 'Ready' }).Count -gt 0
                }
                if ($stillMissing) { throw 'The installer completed but actual prerequisite files/commands are still missing.' }
            }
            catch {
                $_ | Out-String | Add-Content -LiteralPath $diagnosticPath -Encoding UTF8
                throw ('Could not prepare {0}. Reason: {1}. Diagnostic log: {2}' -f $step.Label, $_.Exception.Message, $diagnosticPath)
            }
        }
    }
    $state = & $StateResolver $RepoRoot
    if (!$state -or $state.Status -ne 'Ready') { throw 'Build prerequisites could not be verified after preparation.' }
    # Installers update persistent PATH; this still-running process inherits the old one.
    # Use paths established by the final probes for handoff and every child gate command.
    $powerShellPath = [string](Get-SetupUiProperty $state 'PowerShellPath')
    if ($powerShellPath) { $env:PATH = (Split-Path -Parent $powerShellPath) + [IO.Path]::PathSeparator + $env:PATH }
    Enable-CycleArcDotnetHost -Path ([string](Get-SetupUiProperty $state.DotNetSdk 'DotnetPath'))
    if (!(& $PathEnabler)) { throw 'vswhere.exe could not be enabled for the Native AOT build.' }
    Write-CycleArcBuildPrerequisiteState $state
    Write-Host 'Build environment is ready. Continuing CycleArc build...'
    [pscustomobject]@{ Status = 'Ready'; State = $state }
}
