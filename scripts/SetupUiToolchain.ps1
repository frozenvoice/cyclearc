#Requires -Version 7.0
<#
.SYNOPSIS
    Checks that this machine can build the Native AOT setup UI.

.DESCRIPTION
    Shared by Build-Local.ps1 and Package.ps1 so both demand the same thing. This is a
    development prerequisite: it is needed to build CycleArc-Setup.exe from source, never to
    run the finished installer.

    .NET's Native AOT publish on Windows requires Visual Studio 2022 with the "Desktop
    development with C++" workload, and the SDK locates that toolset through vswhere. Finding
    vswhere.exe proves none of it - Visual Studio can be installed without the C++ components -
    so this asks vswhere for an installation that actually carries the VC tools, then confirms
    the MSVC linker and a Windows SDK import library are really on disk.

    Nothing here installs anything or needs elevation.
#>
Set-StrictMode -Version Latest

# The component that carries the MSVC compiler and linker in the C++ workload.
$script:SetupUiVcComponent = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'

function Find-VsWhere {
    <#
    .SYNOPSIS
        vswhere.exe, from PATH or the fixed location the VS Installer always uses.
    #>
    $onPath = Get-Command vswhere -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (!$base) { continue }
        $candidate = Join-Path $base 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $null
}

function Invoke-VsWhere {
    param([Parameter(Mandatory)][string]$VsWherePath, [Parameter(Mandatory)][string[]]$Arguments)
    $output = & $VsWherePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { return @() }
    @($output | ForEach-Object { [string]$_ } | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
}

function Get-MsvcLinkerPath {
    <#
    .SYNOPSIS
        The MSVC linker inside a Visual Studio installation, or $null when the C++ tools are absent.
    #>
    param([Parameter(Mandatory)][string]$InstallationPath)
    $versionFile = Join-Path $InstallationPath 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt'
    if (!(Test-Path -LiteralPath $versionFile -PathType Leaf)) { return $null }
    $version = (Get-Content -LiteralPath $versionFile -TotalCount 1 -ErrorAction SilentlyContinue)
    if (!$version) { return $null }
    $version = ([string]$version).Trim()
    if (!$version) { return $null }
    $linker = Join-Path $InstallationPath "VC/Tools/MSVC/$version/bin/Hostx64/x64/link.exe"
    if (Test-Path -LiteralPath $linker -PathType Leaf) { return $linker }
    $null
}

function Get-WindowsSdkLibrary {
    <#
    .SYNOPSIS
        A Windows SDK x64 import library, which the AOT link step needs alongside MSVC.
    #>
    $roots = @()
    foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots',
                       'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots')) {
        try {
            $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if ($entry -and $entry.KitsRoot10) { $roots += [string]$entry.KitsRoot10 }
        }
        catch { }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($base) { $roots += (Join-Path $base 'Windows Kits/10') }
    }
    foreach ($root in ($roots | Where-Object { $_ } | Select-Object -Unique)) {
        $libRoot = Join-Path $root 'Lib'
        if (!(Test-Path -LiteralPath $libRoot -PathType Container)) { continue }
        $candidates = @(Get-ChildItem -LiteralPath $libRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending)
        foreach ($candidate in $candidates) {
            $library = Join-Path $candidate.FullName 'um/x64/kernel32.lib'
            if (Test-Path -LiteralPath $library -PathType Leaf) { return $library }
        }
    }
    $null
}

function Resolve-SetupUiToolchain {
    <#
    .SYNOPSIS
        Reports whether the Native AOT prerequisites are present, and what is missing if not.
    .DESCRIPTION
        The three probes are injectable so the focused tests can exercise each outcome without
        installing or uninstalling Visual Studio.
    #>
    param(
        [scriptblock]$VsWhereLocator,
        [scriptblock]$VsWhereInvoker,
        [scriptblock]$LinkerProbe,
        [scriptblock]$SdkProbe
    )
    if (!$VsWhereLocator) { $VsWhereLocator = { Find-VsWhere } }
    if (!$VsWhereInvoker) { $VsWhereInvoker = { param($path, $arguments) Invoke-VsWhere -VsWherePath $path -Arguments $arguments } }
    if (!$LinkerProbe) { $LinkerProbe = { param($installation) Get-MsvcLinkerPath -InstallationPath $installation } }
    if (!$SdkProbe) { $SdkProbe = { Get-WindowsSdkLibrary } }

    $result = [ordered]@{
        Ok = $false; VsWhere = $null; Installation = $null; Linker = $null; SdkLibrary = $null; Missing = $null
    }

    $vswhere = & $VsWhereLocator
    if (!$vswhere) {
        $result.Missing = 'vswhere.exe was not found on PATH or under Program Files, so no Visual Studio installation could be queried'
        return [pscustomobject]$result
    }
    $result.VsWhere = $vswhere

    # Ask for an installation that actually carries the C++ tools, rather than any at all.
    $installations = & $VsWhereInvoker $vswhere @(
        '-latest', '-prerelease', '-products', '*',
        # The setup UI is built with the VS 2022 toolchain. Keep VS 2019 and any
        # future major edition out of this probe even when they carry the same
        # component id.
        '-version', '[17.0,18.0)',
        '-requires', $script:SetupUiVcComponent,
        '-property', 'installationPath'
    )
    $installation = @($installations) | Select-Object -First 1
    if (!$installation) {
        $result.Missing = "no Visual Studio installation reports the C++ component $script:SetupUiVcComponent"
        return [pscustomobject]$result
    }
    $result.Installation = $installation

    $linker = & $LinkerProbe $installation
    if (!$linker) {
        $result.Missing = "the MSVC linker (link.exe) is missing from $installation; the C++ tools are reported but not installed"
        return [pscustomobject]$result
    }
    $result.Linker = $linker

    $sdk = & $SdkProbe
    if (!$sdk) {
        $result.Missing = 'no Windows SDK x64 import library (um\x64\kernel32.lib) was found'
        return [pscustomobject]$result
    }
    $result.SdkLibrary = $sdk

    $result.Ok = $true
    [pscustomobject]$result
}

function Add-VsWhereToPath {
    <#
    .SYNOPSIS
        Puts vswhere.exe on PATH when it is only in its fixed install location.
    .DESCRIPTION
        The .NET SDK's AOT targets invoke vswhere by name, so a machine with the Build Tools but
        no vswhere on PATH otherwise fails with a bare "'vswhere.exe' is not recognized".
    #>
    if (Get-Command vswhere -ErrorAction SilentlyContinue) { return $true }
    $found = Find-VsWhere
    if (!$found) { return $false }
    $env:PATH = "$env:PATH;$(Split-Path -Parent $found)"
    $true
}

function Assert-SetupUiToolchain {
    <#
    .SYNOPSIS
        Fails early, with what to install, when this machine cannot build the setup UI.
    #>
    param([string]$RepoRoot, [scriptblock]$Resolver)
    if ($RepoRoot) {
        $project = Join-Path $RepoRoot 'src/CycleArc.Setup/CycleArc.Setup.csproj'
        if (!(Test-Path -LiteralPath $project -PathType Leaf)) {
            throw "The setup UI project is missing at $project. Nothing was built, stopped or installed."
        }
    }
    if (!$Resolver) { $Resolver = { Resolve-SetupUiToolchain } }
    $toolchain = & $Resolver
    if (!$toolchain.Ok) {
        throw ("Building CycleArc-Setup.exe needs Visual Studio 2022 with the " +
            "'Desktop development with C++' workload: $($toolchain.Missing). " +
            "Install that workload with its default components, then retry. " +
            "This is only needed to build the installer from source - running CycleArc-Setup.exe does not need it. " +
            "Nothing was built, stopped or installed.")
    }
    # vswhere must also be reachable by name for the SDK's own AOT targets.
    if (!(Add-VsWhereToPath)) {
        throw "vswhere.exe could not be put on PATH for the Native AOT build. Nothing was built, stopped or installed."
    }
    $toolchain
}
