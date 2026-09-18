#Requires -Version 7.0
<#
.SYNOPSIS
    Create the stable CycleArc Windows release assets with Velopack.

.DESCRIPTION
    The publish directory must be the single-file Windows publish output.  A
    clean output directory is used deliberately: vpk creates a delta only when
    an older full package is present, and CycleArc ships full packages only.
    The resulting Setup, full package, release feed, and SHA-256 manifest are
    suitable for the CI artifact consumed by Release.ps1.
#>
[CmdletBinding()]
param(
    [Alias('PublishedDirectory')]
    [Parameter(Mandatory)][string]$PublishedDir,
    [Alias('OutputDirectory')]
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseNotesPath,
    [string]$PackId = 'CycleArc',
    [string]$MainExe = 'CycleArc.exe',
    [string]$Channel = 'win',
    [string]$VpkCommand = 'dotnet',
    [switch]$LoadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PackageVersion {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Package version must be a plain semantic version such as 0.6.0: '$Value'"
    }
    $Value
}

function Assert-NoReparsePoints {
    param([Parameter(Mandatory)][string]$Path)
    if (!(Test-Path -LiteralPath $Path)) { return }
    $root = Get-Item -LiteralPath $Path -Force
    if ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Package path is a reparse point: $Path"
    }
    $nested = @(Get-ChildItem -LiteralPath $Path -Force -Recurse | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint
    })
    if ($nested.Count -gt 0) { throw "Package path contains a reparse point: $($nested[0].FullName)" }
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$StopPath
    )
    $current = [IO.Path]::GetFullPath($Path)
    $stop = [IO.Path]::GetFullPath($StopPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Package path ancestor is a reparse point: $current"
            }
        }
        if ($current.Equals($stop, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package path is outside its expected repository root: $Path"
        }
        $current = $parent
    }
}

function Resolve-PublishExecutable {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ExecutableName
    )
    $full = [IO.Path]::GetFullPath($Directory)
    if (!(Test-Path -LiteralPath $full -PathType Container)) {
        throw "Published directory does not exist: $full"
    }
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    Assert-NoReparseAncestors -Path $full -StopPath $repoRoot
    Assert-NoReparsePoints -Path $full
    $files = @(Get-ChildItem -LiteralPath $full -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -cne $ExecutableName) {
        $names = ($files | ForEach-Object { $_.FullName }) -join ', '
        throw "Published output must contain exactly $ExecutableName; found: $names"
    }
    $files[0].FullName
}

function Clear-PackageOutput {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$PublishedDirectory
    )
    $full = [IO.Path]::GetFullPath($Directory)
    $publishedFull = [IO.Path]::GetFullPath($PublishedDirectory)
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $separator = [IO.Path]::DirectorySeparatorChar
    $allowedRoots = @((Join-Path $repoRoot 'publish'), (Join-Path $repoRoot 'artifacts')) | ForEach-Object {
        [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    $insideAllowedRoot = @($allowedRoots | Where-Object {
        $full.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($_ + $separator, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($insideAllowedRoot.Count -eq 0) {
        throw "Package output must be inside the repository publish/ or artifacts/ directory: $full"
    }
    Assert-NoReparseAncestors -Path $full -StopPath $repoRoot
    if (@($allowedRoots | Where-Object { $full.Equals($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw "Package output must be a child directory, never the publish/ or artifacts/ root: $full"
    }
    if ($full.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $full.Equals([IO.Path]::GetPathRoot($full), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean package output root: $full"
    }
    if ($full.Equals($publishedFull, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($publishedFull + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $publishedFull.StartsWith($full + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published and package output directories overlap: $full and $publishedFull"
    }
    if (Test-Path -LiteralPath $full) { Assert-NoReparsePoints -Path $full }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
    # The output directory is caller-owned release output.  Cleaning it first
    # prevents an older full package from making vpk emit a delta package.
    Get-ChildItem -LiteralPath $full -Force | Remove-Item -Recurse -Force
    $full
}

function Invoke-VpkPack {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string]$PackIdValue,
        [Parameter(Mandatory)][string]$PackVersion,
        [Parameter(Mandatory)][string]$PackDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$EntryPoint,
        [Parameter(Mandatory)][string]$ChannelName,
        [string]$NotesPath
    )
    $arguments = @(
        'tool', 'run', 'vpk', 'pack',
        '--packId', $PackIdValue,
        '--packVersion', $PackVersion,
        '--packDir', $PackDirectory,
        '--mainExe', $EntryPoint,
        '--channel', $ChannelName,
        '--outputDir', $OutputDirectory,
        '--packTitle', $PackIdValue,
        '--packAuthors', 'CycleArc contributors',
        '--noPortable'
    )
    if (![string]::IsNullOrWhiteSpace($NotesPath)) {
        $arguments += @('--releaseNotes', $NotesPath)
    }
    Write-Host "Running: $Command $($arguments -join ' ')"
    $output = @(& $Command @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = [int]$LASTEXITCODE
    if ($output.Count -gt 0) { $output | Write-Host }
    if ($exitCode -ne 0) { throw "Velopack packaging failed with exit code $exitCode" }
}

function Get-HashManifest {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$FileNames
    )
    $lines = foreach ($name in ($FileNames | Sort-Object)) {
        $path = Join-Path $Directory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release asset: $name" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    $manifest = Join-Path $Directory 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($manifest, (($lines -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
    $manifest
}

function Assert-ReleaseFeed {
    param(
        [Parameter(Mandatory)][string]$FeedPath,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedPackageName,
        [Parameter(Mandatory)][string]$ExpectedPackagePath,
        [Parameter(Mandatory)][string]$ExpectedChannel,
        [Parameter(Mandatory)][string]$ExpectedPackId
    )
    try { $feed = Get-Content -LiteralPath $FeedPath -Raw | ConvertFrom-Json -Depth 20 }
    catch { throw "Release feed is not valid JSON: $($_.Exception.Message)" }
    $assets = @($feed.Assets)
    if ($assets.Count -eq 0) { throw 'Release feed contains no Assets entries' }
    $matching = @($assets | Where-Object {
        [string]$_.PackageId -ceq $ExpectedPackId -and
        [string]$_.Version -ceq $ExpectedVersion -and
        [string]$_.Type -ceq 'Full' -and
        [string]$_.FileName -ceq $ExpectedPackageName
    })
    if ($matching.Count -ne 1) {
        throw "Release feed does not contain exactly one $ExpectedChannel full asset for $ExpectedVersion"
    }
    if ([string]$matching[0].FileName -match '(?i)-delta\.nupkg$') {
        throw 'Delta packages are not allowed in the stable CycleArc channel'
    }
    $package = Get-Item -LiteralPath $ExpectedPackagePath -ErrorAction Stop
    $sha1 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA1).Hash.ToUpperInvariant()
    $sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    if ([int64]$matching[0].Size -ne [int64]$package.Length) { throw 'Release feed package Size does not match the full package' }
    if ([string]$matching[0].SHA1 -ine $sha1) { throw 'Release feed package SHA1 does not match the full package' }
    if ([string]::IsNullOrWhiteSpace([string]$matching[0].SHA256) -or [string]$matching[0].SHA256 -ine $sha256) {
        throw 'Release feed package SHA256 does not match the full package'
    }
    $true
}

function Get-SetupUiProjectPath {
    param([string]$RepoRoot)
    if (!$RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
    Join-Path $RepoRoot 'src/CycleArc.Setup/CycleArc.Setup.csproj'
}

# Native AOT links with MSVC, which the .NET SDK locates through vswhere. A machine that has
# the Build Tools but not vswhere on PATH fails with a bare "'vswhere.exe' is not recognized",
# so the well-known Installer directory is added when it is missing.
function Add-VsWhereToPath {
    if (Get-Command vswhere -ErrorAction SilentlyContinue) { return $true }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (!$base) { continue }
        $candidate = Join-Path $base 'Microsoft Visual Studio/Installer'
        if (Test-Path -LiteralPath (Join-Path $candidate 'vswhere.exe') -PathType Leaf) {
            $env:PATH = "$env:PATH;$candidate"
            return $true
        }
    }
    return $false
}

<#
.SYNOPSIS
    Wraps the Velopack engine installer in the CycleArc setup window.
.DESCRIPTION
    The engine keeps performing the installation; it is embedded verbatim in a small Native
    AOT wrapper that shows the confirmation, progress and completion screens. The wrapper
    replaces the engine under the shipped name, so the checksum manifest, the asset list and
    every later verification describe the file people actually download - never the engine.
#>
function New-SetupUiInstaller {
    param(
        [Parameter(Mandatory)][string]$EnginePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [string]$ProjectPath,
        [string]$Command = 'dotnet'
    )
    if (!$ProjectPath) { $ProjectPath = Get-SetupUiProjectPath }
    if (!(Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "The setup UI project is missing at $ProjectPath"
    }
    $engineFull = [IO.Path]::GetFullPath($EnginePath)
    if (!(Test-Path -LiteralPath $engineFull -PathType Leaf)) {
        throw "The Velopack engine installer is missing at $engineFull"
    }
    if (!(Add-VsWhereToPath)) {
        throw 'Native AOT needs the Visual Studio Build Tools (vswhere.exe was not found). Install the C++ build tools, then retry.'
    }

    $projectDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ProjectPath))
    $work = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-setup-ui-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        # A stale obj from a build without the engine silently produces a wrapper with no
        # application inside it, so this always starts from a clean intermediate directory.
        foreach ($stale in @('obj', 'bin')) {
            $path = Join-Path $projectDirectory $stale
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }

        $arguments = @(
            'publish', $ProjectPath, '-c', 'Release', '--nologo',
            "-p:CycleArcEnginePath=$engineFull", '-o', $work
        )
        & $Command @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Building the setup UI failed (exit $LASTEXITCODE)" }

        $built = Join-Path $work 'CycleArc-Setup.exe'
        if (!(Test-Path -LiteralPath $built -PathType Leaf)) {
            throw "The setup UI build produced no CycleArc-Setup.exe in $work"
        }
        # The engine is the bulk of the wrapper; a wrapper near the engine's size proves it
        # was embedded, and one near the bare binary's size proves it was not.
        $engineSize = (Get-Item -LiteralPath $engineFull).Length
        $builtSize = (Get-Item -LiteralPath $built).Length
        if ($builtSize -lt $engineSize) {
            throw "The setup UI ($builtSize bytes) is smaller than the engine it must contain ($engineSize bytes); the engine was not embedded."
        }

        Copy-Item -LiteralPath $built -Destination $OutputPath -Force
        [pscustomobject]@{
            Path       = [IO.Path]::GetFullPath($OutputPath)
            Size       = $builtSize
            EngineSize = $engineSize
        }
    }
    finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Assert-PackageOutput {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$ChannelName,
        [scriptblock]$WrapSetup
    )
    $expectedSetupName = "$PackageId-Setup.exe"
    $setup = @(Get-ChildItem -LiteralPath $Directory -File -Filter '*-Setup.exe')
    if ($setup.Count -ne 1 -or $setup[0].Name -cnotin @($expectedSetupName, "$PackageId-$ChannelName-Setup.exe")) {
        throw "Expected exactly one Velopack Setup.exe for $PackageId/$ChannelName"
    }
    $setup = $setup[0]
    if ($setup.Name -cne $expectedSetupName) {
        $originalSetupName = $setup.Name
        Rename-Item -LiteralPath $setup.FullName -NewName $expectedSetupName
        $assetListPath = Join-Path $Directory "assets.$ChannelName.json"
        if (Test-Path -LiteralPath $assetListPath -PathType Leaf) {
            $assetList = @(Get-Content -LiteralPath $assetListPath -Raw | ConvertFrom-Json)
            foreach ($asset in $assetList) {
                if ($asset.RelativeFileName -ceq $originalSetupName) { $asset.RelativeFileName = $expectedSetupName }
            }
            [IO.File]::WriteAllText($assetListPath, (ConvertTo-Json -InputObject $assetList -Depth 10 -Compress), [Text.UTF8Encoding]::new($false))
        }
        $setup = Get-Item -LiteralPath (Join-Path $Directory $expectedSetupName)
    }
    if ($WrapSetup) {
        & $WrapSetup $setup.FullName | Out-Null
        $setup = Get-Item -LiteralPath (Join-Path $Directory $expectedSetupName)
    }
    $packageName = "$PackageId-$PackageVersion-full.nupkg"
    $package = Join-Path $Directory $packageName
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing full package: $packageName" }
    $feedName = "releases.$ChannelName.json"
    $feed = Join-Path $Directory $feedName
    if (!(Test-Path -LiteralPath $feed -PathType Leaf)) { throw "Missing release feed: $feedName" }
    $deltas = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Name -match '(?i)-delta\.nupkg$' })
    if ($deltas.Count -gt 0) { throw "Delta packages are not allowed: $($deltas.Name -join ', ')" }
    Assert-ReleaseFeed -FeedPath $feed -ExpectedVersion $PackageVersion -ExpectedPackageName $packageName -ExpectedPackagePath $package -ExpectedChannel $ChannelName -ExpectedPackId $PackageId | Out-Null

    $generatedFiles = @(Get-ChildItem -LiteralPath $Directory -File)
    $manifestNames = @($generatedFiles | Where-Object { $_.Name -cne 'SHA256SUMS.txt' } | Select-Object -ExpandProperty Name)
    Get-HashManifest -Directory $Directory -FileNames $manifestNames | Out-Null
    $required = @($setup.Name, $packageName, $feedName, 'SHA256SUMS.txt')
    $unexpected = @($generatedFiles | Where-Object {
        $_.Name -notin $required -and $_.Name -notmatch '^(assets\.' + [regex]::Escape($ChannelName) + '\.json|RELEASES)$'
    })
    if ($unexpected.Count -gt 0) {
        throw "Unexpected package outputs: $($unexpected.Name -join ', ')"
    }
    [pscustomobject]@{
        Setup = (Join-Path $Directory $expectedSetupName)
        FullPackage = $package
        Feed = $feed
        Manifest = (Join-Path $Directory 'SHA256SUMS.txt')
        Files = @(Get-ChildItem -LiteralPath $Directory -File | Select-Object -ExpandProperty FullName)
    }
}

function Invoke-Package {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishedDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$PackageVersion,
        [string]$NotesPath,
        [string]$PackageIdValue = 'CycleArc',
        [string]$EntryPoint = 'CycleArc.exe',
        [string]$ChannelName = 'win',
        [string]$Command = 'dotnet',
        [switch]$NoSetupUi,
        [string]$SetupUiProject
    )
    $version = Assert-PackageVersion $PackageVersion
    $published = Resolve-PublishExecutable -Directory $PublishedDirectory -ExecutableName $EntryPoint
    $publishedVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($published).FileVersion ?? '').Trim()
    if (!$publishedVersion) {
        throw "$EntryPoint has no FileVersion metadata; refusing to package an unversioned executable"
    }
    if ($publishedVersion -ne "$version.0" -and $publishedVersion -ne $version) {
        throw "$EntryPoint FileVersion is '$publishedVersion', expected '$version.0'"
    }
    $publishedFull = [IO.Path]::GetFullPath($PublishedDirectory)
    $outputFull = [IO.Path]::GetFullPath($OutputDirectory)
    if ($outputFull.Equals($publishedFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published and package output directories must be different'
    }
    $output = Clear-PackageOutput -Directory $outputFull -PublishedDirectory $publishedFull
    if (![string]::IsNullOrWhiteSpace($NotesPath)) {
        $notes = Get-Item -LiteralPath $NotesPath -ErrorAction Stop
        if ($notes.PSIsContainer) { throw "Release notes path is a directory: $NotesPath" }
        $NotesPath = $notes.FullName
    }
    Invoke-VpkPack -Command $Command -PackIdValue $PackageIdValue -PackVersion $version -PackDirectory $publishedFull -OutputDirectory $output -EntryPoint $EntryPoint -ChannelName $ChannelName -NotesPath $NotesPath
    # The distributed installer is the setup window with the engine inside it. -NoSetupUi
    # leaves the bare engine in place, for tests that only exercise the packaging contract.
    $wrap = if ($NoSetupUi) { $null } else {
        {
            param([string]$enginePath)
            New-SetupUiInstaller -EnginePath $enginePath -OutputPath $enginePath -ProjectPath $SetupUiProject -Command $Command
        }
    }
    Assert-PackageOutput -Directory $output -PackageVersion $version -PackageId $PackageIdValue -ChannelName $ChannelName -WrapSetup $wrap
}

if (!$LoadOnly -and $MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Usage: pwsh -File ./scripts/Package.ps1 -PublishedDir <dir> -OutputDir <dir> -Version <version>' }
    Invoke-Package -PublishedDirectory $PublishedDir -OutputDirectory $OutputDir -PackageVersion $Version -NotesPath $ReleaseNotesPath -PackageIdValue $PackId -EntryPoint $MainExe -ChannelName $Channel -Command $VpkCommand | Out-Null
}
