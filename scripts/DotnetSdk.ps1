# Compatible with Windows PowerShell 5.1 so build-local can prepare PowerShell 7.
# Never infer checkout readiness from the installed SDK inventory alone.
function Test-CycleArcSelectedSdk {
    param([string]$SelectedVersion, [string]$MinimumVersion = '10.0.100')
    if ($SelectedVersion -notmatch '^\d+\.\d+\.\d+$') { return $false }
    $selected = [version]$SelectedVersion
    $minimum = [version]$MinimumVersion
    $selected.Major -eq $minimum.Major -and $selected.Minor -eq $minimum.Minor -and $selected -ge $minimum
}

function Get-CycleArcSdkProperty {
    param([AllowNull()][object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { $property.Value }
}

function Get-CycleArcSdkConfiguration {
    param([string]$RepoRoot)
    $configuration = Get-Content -LiteralPath (Join-Path $RepoRoot 'global.json') -Raw | ConvertFrom-Json
    $sdk = Get-CycleArcSdkProperty $configuration 'sdk'
    $minimum = [string](Get-CycleArcSdkProperty $sdk 'version')
    if ($minimum -notmatch '^10\.0\.\d+$' -or (Get-CycleArcSdkProperty $sdk 'rollForward') -ne 'latestFeature' -or
        (Get-CycleArcSdkProperty $sdk 'allowPrerelease') -ne $false) {
        throw 'global.json must select stable .NET 10 SDKs with latestFeature roll-forward.'
    }
    $minimum
}

function Test-CycleArcDotnetX64Host {
    param([string]$Path)
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 60
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 6) { return $false }
        $stream.Position = $peOffset
        return $reader.ReadUInt32() -eq 0x00004550 -and $reader.ReadUInt16() -eq 0x8664
    }
    catch { return $false }
    finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
}

function Resolve-CycleArcDotnetHost {
    # Explicit roots support per-user/isolated SDKs without modifying a system SDK.
    foreach ($root in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
        if (!$root) { continue }
        $candidate = Join-Path $root 'dotnet.exe'
        if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and (Test-CycleArcDotnetX64Host $candidate)) { return $candidate }
    }
    # A Windows x64 SDK installation must not be hidden by an x86 dotnet on PATH.
    $programFiles = $env:ProgramW6432
    if (!$programFiles) { $programFiles = $env:ProgramFiles }
    if ($programFiles) {
        $candidate = Join-Path $programFiles 'dotnet/dotnet.exe'
        if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and (Test-CycleArcDotnetX64Host $candidate)) { return $candidate }
    }
    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and (Test-CycleArcDotnetX64Host $command.Source)) { return $command.Source }
    $null
}

function Enable-CycleArcDotnetHost {
    param([string]$Path)
    if (!$Path) { return }
    $directory = Split-Path -Parent $Path
    if ($directory) { $env:PATH = $directory + [IO.Path]::PathSeparator + $env:PATH }
}

function Invoke-CycleArcDotnetProbe {
    param([string]$Path, [string[]]$Arguments)
    # PS5 treats redirected native stderr as ErrorRecord. Capture it without terminating
    # SDK resolution; restore the caller's error preference after this local invocation.
    $ErrorActionPreference = 'Continue'
    $output = @(& $Path @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Get-CycleArcDotnetSdkState {
    param(
        [string]$RepoRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))),
        [scriptblock]$DotnetResolver,
        [scriptblock]$DotnetInvoker
    )
    $minimum = Get-CycleArcSdkConfiguration -RepoRoot $RepoRoot
    if (!$DotnetResolver) { $DotnetResolver = { Resolve-CycleArcDotnetHost } }
    if (!$DotnetInvoker) { $DotnetInvoker = { param($path, $arguments) Invoke-CycleArcDotnetProbe -Path $path -Arguments $arguments } }
    $installed = @()
    $selected = $null
    $diagnostic = $null
    $ready = $false
    $path = $null
    Push-Location -LiteralPath $RepoRoot
    try {
        $path = & $DotnetResolver
        if (!$path) { $diagnostic = 'No Windows x64 dotnet host was found.' }
        else {
            $inventory = & $DotnetInvoker $path @('--list-sdks')
            $selection = & $DotnetInvoker $path @('--version')
            $listOutput = @((Get-CycleArcSdkProperty $inventory 'Output') | ForEach-Object { [string]$_ })
            foreach ($line in $listOutput) {
                if ($line -match '^\s*(\d+\.\d+\.\d+(?:-[^\s]+)?)\s+\[') { $installed += $Matches[1] }
            }
            $rawSelected = (@((Get-CycleArcSdkProperty $selection 'Output')) -join [Environment]::NewLine).Trim()
            # Keep native diagnostics available to the separate log, never in Selected.
            if ($rawSelected -match '^\d+\.\d+\.\d+(?:-[^\s]+)?$') { $selected = $rawSelected }
            $listExit = Get-CycleArcSdkProperty $inventory 'ExitCode'
            $selectedExit = Get-CycleArcSdkProperty $selection 'ExitCode'
            $ready = $null -ne $listExit -and $listExit -eq 0 -and $null -ne $selectedExit -and $selectedExit -eq 0 -and
                (Test-CycleArcSelectedSdk -SelectedVersion $selected -MinimumVersion $minimum) -and $installed -contains $selected
            if (!$ready) {
                $diagnostic = 'SDK inventory exit: {0}; checkout selection exit: {1}.{2}{3}{2}{4}' -f
                    $listExit, $selectedExit, [Environment]::NewLine, ($listOutput -join [Environment]::NewLine), $rawSelected
            }
        }
    }
    catch { $diagnostic = $_.Exception.Message }
    finally { Pop-Location }
    [pscustomobject]@{
        Status = if ($ready) { 'Ready' } else { 'Missing' }
        Selected = $selected
        Installed = @($installed | Select-Object -Unique)
        MinimumVersion = $minimum
        Diagnostic = $diagnostic
        DotnetPath = $path
    }
}

function Assert-CycleArcDotnetSdk {
    param(
        [string]$RepoRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))),
        [scriptblock]$DotnetResolver,
        [scriptblock]$DotnetInvoker
    )
    $state = Get-CycleArcDotnetSdkState -RepoRoot $RepoRoot -DotnetResolver $DotnetResolver -DotnetInvoker $DotnetInvoker
    if ($state.Status -ne 'Ready') {
        $inventory = if ($state.Installed.Count) { $state.Installed -join ', ' } else { 'none detected' }
        throw "A stable .NET 10 SDK selected by global.json is required (minimum $($state.MinimumVersion); installed: $inventory). Run build-local.cmd to prepare prerequisites, or install the Windows x64 .NET 10 SDK."
    }
    Enable-CycleArcDotnetHost -Path $state.DotnetPath
    Write-Host "Selected .NET SDK: $($state.Selected) (global.json)"
}

function Read-CycleArcDotnetReleaseMetadata {
    param([string]$Uri)
    $temporaryDirectory = New-SetupUiPrerequisiteTempDirectory
    try {
        $path = Join-Path $temporaryDirectory 'dotnet-releases.json'
        Invoke-SetupUiOfficialDownload -Uri $Uri -Destination $path
        if ((Get-Item -LiteralPath $path).Length -gt 16MB) { throw 'The .NET release metadata exceeded its size limit.' }
        Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    finally { Remove-SetupUiPrerequisiteTempDirectory -Path $temporaryDirectory }
}

function Resolve-CycleArcDotnetSdkInstaller {
    param([object]$Metadata, [string]$MinimumVersion)
    if ((Get-CycleArcSdkProperty $Metadata 'channel-version') -ne '10.0') { throw 'The official SDK metadata did not describe .NET 10.0.' }
    $candidates = @()
    foreach ($release in @(Get-CycleArcSdkProperty $Metadata 'releases')) {
        $sdks = @(Get-CycleArcSdkProperty $release 'sdks') + @(Get-CycleArcSdkProperty $release 'sdk')
        foreach ($sdk in $sdks) {
            $version = [string](Get-CycleArcSdkProperty $sdk 'version')
            if (!(Test-CycleArcSelectedSdk -SelectedVersion $version -MinimumVersion $MinimumVersion)) { continue }
            foreach ($file in @(Get-CycleArcSdkProperty $sdk 'files')) {
                if ((Get-CycleArcSdkProperty $file 'rid') -ne 'win-x64' -or
                    (Get-CycleArcSdkProperty $file 'name') -ne 'dotnet-sdk-win-x64.exe') { continue }
                $uri = [Uri]([string](Get-CycleArcSdkProperty $file 'url'))
                $hash = [string](Get-CycleArcSdkProperty $file 'hash')
                if (!(Test-SetupUiOfficialUri $uri) -or $uri.AbsolutePath -notmatch '/dotnet-sdk-[0-9.]+-win-x64\.exe$' -or
                    $uri.AbsolutePath -notlike "*/dotnet-sdk-$version-win-x64.exe" -or $hash -notmatch '^[a-fA-F0-9]{128}$') {
                    throw 'The official SDK metadata contained an invalid Windows x64 installer URL or SHA-512 hash.'
                }
                $candidates += [pscustomobject]@{ Version = $version; Uri = $uri.AbsoluteUri; Hash = $hash }
            }
        }
    }
    $installer = $candidates | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
    if (!$installer) { throw 'No stable Windows x64 .NET 10 SDK matching global.json was found in official release metadata.' }
    $installer
}

function Invoke-CycleArcDotnetSdkInstall {
    # The coordinator owns prerequisite preparation. This function never prompts,
    # bypasses policy or removes an existing SDK; Microsoft's installer adds alongside it.
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [scriptblock]$Resolver,
        [scriptblock]$Downloader,
        [scriptblock]$SignatureValidator,
        [scriptblock]$ProcessRunner,
        [scriptblock]$MetadataReader,
        [scriptblock]$TemporaryDirectoryFactory,
        [scriptblock]$TemporaryDirectoryRemover
    )
    if (!$Resolver) { $Resolver = { param($root) Get-CycleArcDotnetSdkState -RepoRoot $root } }
    if (!$Downloader) { $Downloader = { param($uri, $destination) Invoke-SetupUiOfficialDownload -Uri $uri -Destination $destination } }
    if (!$SignatureValidator) { $SignatureValidator = { param($path) Test-SetupUiMicrosoftAuthenticode -Path $path -AllowDotnetPublisher } }
    if (!$ProcessRunner) { $ProcessRunner = { param($path, $arguments) Invoke-SetupUiPrerequisiteInstaller -FilePath $path -ArgumentList $arguments } }
    if (!$MetadataReader) { $MetadataReader = { param($uri) Read-CycleArcDotnetReleaseMetadata -Uri $uri } }
    if (!$TemporaryDirectoryFactory) { $TemporaryDirectoryFactory = { New-SetupUiPrerequisiteTempDirectory } }
    if (!$TemporaryDirectoryRemover) { $TemporaryDirectoryRemover = { param($path) Remove-SetupUiPrerequisiteTempDirectory -Path $path } }
    $initial = & $Resolver $RepoRoot
    if (!$initial) { throw 'The .NET SDK resolver returned no result.' }
    if ($initial.Status -eq 'Ready') {
        Enable-CycleArcDotnetHost -Path (Get-CycleArcSdkProperty $initial 'DotnetPath')
        return [pscustomobject]@{ Status = 'Ready'; State = $initial; Reason = $null; ExitCode = 0 }
    }
    $minimum = Get-CycleArcSdkConfiguration -RepoRoot $RepoRoot
    try { $metadata = & $MetadataReader 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' }
    catch { throw 'Could not download official .NET 10 SDK release metadata. Check network access or company IT policy, then retry.' }
    $release = Resolve-CycleArcDotnetSdkInstaller -Metadata $metadata -MinimumVersion $minimum
    $temporaryDirectory = $null
    try {
        $temporaryDirectory = & $TemporaryDirectoryFactory
        if (!$temporaryDirectory -or !(Test-Path -LiteralPath $temporaryDirectory -PathType Container)) { throw 'Could not create a unique temporary directory for the .NET SDK installer.' }
        $installer = Join-Path $temporaryDirectory ("dotnet-sdk-{0}-win-x64.exe" -f $release.Version)
        if (!(Test-SetupUiTempChildPath -Root $temporaryDirectory -Path $installer) -or (Test-Path -LiteralPath $installer)) { throw 'Refusing to reuse an existing .NET SDK installer.' }
        Write-Host 'Downloading official Microsoft .NET SDK installer...'
        try { & $Downloader $release.Uri $installer }
        catch { throw 'The official .NET SDK installer download failed. Check network access or company IT policy, then retry.' }
        if (!(Test-Path -LiteralPath $installer -PathType Leaf) -or (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ne $release.Hash) {
            throw 'The .NET SDK installer failed its official SHA-512 check. It was not executed.'
        }
        if (!(& $SignatureValidator $installer)) { throw 'The .NET SDK installer failed Microsoft Corporation Authenticode validation. It was not executed.' }
        Write-Host 'Signature verified: Microsoft Corporation'
        Write-Host 'Installing .NET SDK...'
        try { $result = & $ProcessRunner $installer @('/install', '/passive', '/norestart') }
        catch {
            $nativeCode = Get-SetupUiNativeErrorCode $_.Exception
            if ($nativeCode -eq 1223) { return [pscustomobject]@{ Status = 'Cancelled'; State = $initial; Reason = 'UacCancelled'; ExitCode = 1223 } }
            if ($nativeCode -in @(5, 1260, 1625)) { return [pscustomobject]@{ Status = 'PolicyBlocked'; State = $initial; Reason = 'Administrator or company IT approval is required.'; ExitCode = $nativeCode } }
            throw 'Could not start the .NET SDK installer. Administrator or company IT approval may be required; no policy bypass was attempted.'
        }
        $last = @($result) | Select-Object -Last 1
        $exit = if ($last -is [int] -or $last -is [long]) { $last } else { Get-CycleArcSdkProperty $last 'ExitCode' }
        if ($null -eq $exit -or [string]$exit -eq '') { throw 'The .NET SDK installer returned no exit code.' }
        $exit = [long]$exit
        # Burn wraps Windows Installer errors in HRESULT_FROM_WIN32.
        $code = if ($exit -lt 0 -or $exit -gt 65535) { $exit -band 65535 } else { $exit }
        if ($code -in @(1223, 1602)) { return [pscustomobject]@{ Status = 'Cancelled'; State = $initial; Reason = 'InstallerCancelled'; ExitCode = $exit } }
        if ($code -in @(3010, 1641)) { return [pscustomobject]@{ Status = 'RebootRequired'; State = $initial; Reason = 'Reboot Windows before retrying.'; ExitCode = $exit } }
        if ($code -in @(5, 1260, 1625)) { return [pscustomobject]@{ Status = 'PolicyBlocked'; State = $initial; Reason = 'Administrator or company IT approval is required.'; ExitCode = $exit } }
        if ($exit -ne 0) { throw "The .NET SDK installer failed (exit $exit). Check network access, other running installers or company IT policy." }
        Enable-CycleArcDotnetHost -Path (Resolve-CycleArcDotnetHost)
        Write-Host 'Rechecking .NET SDK...'
        $verified = & $Resolver $RepoRoot
        if (!$verified -or $verified.Status -ne 'Ready') { throw 'The installer completed, but this checkout still cannot select a stable .NET 10 SDK. No build was started. Ask company IT to repair the SDK installation.' }
        Enable-CycleArcDotnetHost -Path (Get-CycleArcSdkProperty $verified 'DotnetPath')
        [pscustomobject]@{ Status = 'Ready'; State = $verified; Reason = $null; ExitCode = 0 }
    }
    finally { if ($temporaryDirectory) { & $TemporaryDirectoryRemover $temporaryDirectory } }
}
