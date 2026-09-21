#Requires -Version 7.0
<#
.SYNOPSIS
    Checks and, with explicit approval, prepares the Native AOT setup prerequisites.

.DESCRIPTION
    This file is intentionally separate from the build flow. It never stops CycleArc and it
    never installs anything unless the caller is at a real interactive console and chooses
    Install. All external boundaries are injectable so tests never download or launch an
    installer.
#>
Set-StrictMode -Version Latest

$script:SetupUiBootstrapperUri = 'https://aka.ms/vs/17/release/vs_buildtools.exe'
$script:SetupUiBootstrapperName = 'vs_buildtools.exe'
$script:SetupUiSetupWorkload = 'Microsoft.VisualStudio.Workload.VCTools'
$script:SetupUiSetupComponent = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'

function Get-SetupUiProperty {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Test-SetupUiCiEnvironment {
    foreach ($name in @('CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'BUILD_ID', 'BUILD_NUMBER', 'JENKINS_URL')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -and $value -notmatch '^(0|false|no)$') { return $true }
    }
    $false
}

function Test-SetupUiPrerequisiteInteractive {
    <#
    .SYNOPSIS
        Returns true only when it is safe to ask a person for an install decision.
    #>
    param(
        [switch]$NoPrompt,
        [switch]$SilentInstall
    )
    if ($NoPrompt -or $SilentInstall) { return $false }
    if (Test-SetupUiCiEnvironment) { return $false }
    if (![Environment]::UserInteractive) { return $false }

    $commandLine = @([Environment]::GetCommandLineArgs())
    if ($commandLine -match '^(?i)-NonInteractive$') { return $false }

    try {
        if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) { return $false }
    }
    catch {
        return $false
    }

    # ConsoleHost is the normal PowerShell console and also covers pwsh launched by cmd.
    if ($Host -and $Host.Name -and $Host.Name -notmatch '(?i)ConsoleHost') { return $false }
    $true
}

function Write-SetupUiPrerequisiteManualGuidance {
    param(
        [AllowNull()][string]$Missing,
        [AllowNull()][string]$Reason
    )
    if ($Reason) { Write-Host $Reason }
    Write-Host ''
    Write-Host 'Manual installation:'
    Write-Host '  1. Open the official Visual Studio downloads page or the Visual Studio Installer.'
    Write-Host '  2. Use Visual Studio Build Tools 2022 (VS 2022, version 17.x).'
    Write-Host '  3. Select the "Desktop development with C++" workload.'
    Write-Host '  4. Keep the recommended components, including MSVC x64/x86 tools and a Windows SDK.'
    if ($Missing) { Write-Host ("  Current missing prerequisite: {0}" -f $Missing) }
    Write-Host ("  Official bootstrapper: {0}" -f $script:SetupUiBootstrapperUri)
    Write-Host 'After installation or repair, run build-local.cmd again.'
    Write-Host 'Company-managed PCs may require administrator or IT approval.'
    Write-Host 'No CycleArc files were built, stopped or installed; the existing CycleArc installation is unchanged.'
}

function Write-SetupUiPrerequisiteCancelledGuidance {
    param([AllowNull()][string]$Reason)
    if ($Reason) { Write-Host $Reason }
    Write-Host 'Prerequisite installation was cancelled.'
    Write-Host 'No CycleArc files were built, stopped or installed; the existing CycleArc installation is unchanged.'
}

function Write-SetupUiPrerequisiteRebootGuidance {
    Write-Host 'Visual Studio prerequisite installation requires a reboot before the toolchain can be used.'
    Write-Host 'Reboot Windows, then run build-local.cmd again.'
    Write-Host 'CycleArc was not stopped or installed; the existing CycleArc installation is unchanged.'
}

function Invoke-SetupUiPrerequisitePrompt {
    param(
        [AllowNull()][string]$Missing,
        [scriptblock]$Interaction
    )
    Write-Host ''
    Write-Host 'CycleArc installer build prerequisites are missing.'
    Write-Host ''
    Write-Host 'Required to build CycleArc-Setup.exe:'
    Write-Host '  - Visual Studio Build Tools 2022'
    Write-Host '  - Desktop development with C++'
    Write-Host '  - MSVC x64/x86 tools'
    Write-Host '  - Windows SDK'
    if ($Missing) { Write-Host ("  Detected issue: {0}" -f $Missing) }
    Write-Host ''
    Write-Host 'This is only required to BUILD the installer.'
    Write-Host 'Running an already-built CycleArc-Setup.exe does not require Visual Studio.'
    Write-Host ''
    Write-Host 'Company-managed PCs may require administrator or IT approval.'
    Write-Host ''
    Write-Host '[1] Install required Microsoft build tools'
    Write-Host '[2] Show manual installation instructions'
    Write-Host '[3] Cancel'

    if ($Interaction) {
        $choice = & $Interaction $Missing
    }
    else {
        $choice = Read-Host 'Choose 1, 2 or 3'
    }
    $value = ([string](@($choice) | Select-Object -Last 1)).Trim().ToLowerInvariant()
    switch -Regex ($value) {
        '^(1|install)$' { return 'Install' }
        '^(2|manual|instructions)$' { return 'Manual' }
        default { return 'Cancel' }
    }
}

function Find-SetupUiVisualStudioInstaller {
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (!$base) { continue }
        $candidate = Join-Path $base 'Microsoft Visual Studio/Installer/setup.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $null
}

function Resolve-SetupUiExistingInstallation {
    <#
    Finds an existing VS 2022 installation without requiring the C++ component. JSON is used
    because installationPath alone cannot distinguish Build Tools from an IDE or an incomplete
    instance that must be repaired instead of duplicated.
    #>
    param(
        [scriptblock]$VsWhereLocator,
        [scriptblock]$VsWhereInvoker
    )
    if (!$VsWhereLocator) { $VsWhereLocator = { Find-VsWhere } }
    if (!$VsWhereInvoker) {
        $VsWhereInvoker = {
            param($path, $arguments)
            Invoke-VsWhere -VsWherePath $path -Arguments $arguments
        }
    }

    $vswhere = & $VsWhereLocator
    if (!$vswhere) { return $null }
    $raw = @(& $VsWhereInvoker $vswhere @(
        '-all', '-prerelease', '-products', '*', '-version', '[17.0,18.0)', '-format', 'json'
    ))
    if ($raw.Count -eq 0) { throw 'Visual Studio discovery returned no JSON; use Visual Studio Installer manually before retrying.' }
    try {
        $records = @(($raw -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop)
    }
    catch { throw "Could not read Visual Studio installation metadata: $($_.Exception.Message). Open Visual Studio Installer manually." }
    if (!$records.Count) { return $null }
    # Prefer a complete Build Tools installation; otherwise reuse an IDE. An incomplete
    # installation still counts as existing and routes to manual repair, never a duplicate.
    $record = $records | Sort-Object @{
        Expression = { (Get-SetupUiProperty $_ 'isComplete') -eq $true }; Descending = $true
    }, @{
        Expression = { (Get-SetupUiProperty $_ 'productId') -eq 'Microsoft.VisualStudio.Product.BuildTools' }; Descending = $true
    } | Select-Object -First 1
    $path = [string](Get-SetupUiProperty $record 'installationPath')
    if (!$path) { throw 'Visual Studio discovery returned an installation without a path. Open Visual Studio Installer manually.' }
    [pscustomobject]@{
        InstallationPath = $path.Trim()
        ProductId = [string](Get-SetupUiProperty $record 'productId')
        ChannelId = [string](Get-SetupUiProperty $record 'channelId')
        IsComplete = (Get-SetupUiProperty $record 'isComplete') -eq $true
        IsRebootRequired = (Get-SetupUiProperty $record 'isRebootRequired') -eq $true
    }
}

function Test-SetupUiOfficialUri {
    param([Parameter(Mandatory)][Uri]$Uri)
    if ($Uri.Scheme -ne 'https') { return $false }
    $hostName = $Uri.Host.ToLowerInvariant()
    @(
        'aka.ms',
        'go.microsoft.com',
        'visualstudio.microsoft.com',
        'download.visualstudio.microsoft.com',
        'download.microsoft.com'
    ) -contains $hostName -or $hostName.EndsWith('.visualstudio.microsoft.com')
}

function Invoke-SetupUiOfficialDownload {
    <#
    Downloads the official bootstrapper while allowing only HTTPS Microsoft redirect targets.
    The destination must not already exist, so a stale or user-supplied installer is never reused.
    #>
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [int]$MaximumRedirects = 5,
        [Net.Http.HttpMessageHandler]$Handler
    )
    $current = [Uri]$Uri
    if (!(Test-SetupUiOfficialUri $current)) {
        throw "The Visual Studio bootstrapper URL is not an approved HTTPS Microsoft endpoint: $Uri"
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to reuse an existing prerequisite bootstrapper at $Destination."
    }

    if (!$Handler) { $Handler = [Net.Http.HttpClientHandler]::new() }
    if ($Handler -is [Net.Http.HttpClientHandler]) { $Handler.AllowAutoRedirect = $false }
    $client = [Net.Http.HttpClient]::new($Handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    try {
        for ($redirect = 0; $redirect -le $MaximumRedirects; $redirect++) {
            if (!(Test-SetupUiOfficialUri $current)) {
                throw "The Visual Studio bootstrapper redirect left the approved HTTPS Microsoft endpoints: $current"
            }
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $current)
            try {
                $response = $client.SendAsync(
                    $request,
                    [Net.Http.HttpCompletionOption]::ResponseHeadersRead
                ).GetAwaiter().GetResult()
            }
            finally {
                $request.Dispose()
            }

            $status = [int]$response.StatusCode
            if ($status -ge 300 -and $status -lt 400) {
                $location = $response.Headers.Location
                $response.Dispose()
                if ($null -eq $location) { throw "The Visual Studio bootstrapper returned redirect $status without a Location header." }
                $current = if ($location.IsAbsoluteUri) {
                    [Uri]$location
                }
                else {
                    [Uri]::new($current, $location)
                }
                continue
            }
            if (!$response.IsSuccessStatusCode) {
                $response.Dispose()
                throw "The Visual Studio bootstrapper download returned HTTP $status."
            }

            $stream = $null
            $file = $null
            $copyCancellation = $null
            try {
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $copyCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(10))
                $file = [IO.File]::Open(
                    $Destination,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                $stream.CopyToAsync($file, $copyCancellation.Token).GetAwaiter().GetResult()
            }
            finally {
                if ($file) { $file.Dispose() }
                if ($stream) { $stream.Dispose() }
                if ($copyCancellation) { $copyCancellation.Dispose() }
                $response.Dispose()
            }
            return
        }
        throw "The Visual Studio bootstrapper exceeded the $MaximumRedirects redirect limit."
    }
    finally {
        $client.Dispose()
    }
}

function Test-SetupUiMicrosoftAuthenticode {
    param([Parameter(Mandatory)][string]$Path, [scriptblock]$SignatureReader)
    if (!$SignatureReader) { $SignatureReader = { param($file) Get-AuthenticodeSignature -LiteralPath $file -ErrorAction Stop } }
    try {
        $signature = & $SignatureReader $Path
        if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate) { return $false }
        $certificate = $signature.SignerCertificate
        $subject = [string]$certificate.Subject
        $simpleName = [string]$certificate.GetNameInfo(
            [Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false
        )
        return $subject -match '(?i)(^|,\s*)CN=Microsoft Corporation(,|$)' -or
            $simpleName -eq 'Microsoft Corporation'
    }
    catch {
        return $false
    }
}

function New-SetupUiPrerequisiteTempDirectory {
    $root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $name = 'CycleArc-setup-prerequisite-' + [Guid]::NewGuid().ToString('N')
        $candidate = Join-Path $root $name
        try {
            New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
            return $candidate
        }
        catch {
            if ($attempt -eq 7) { throw }
        }
    }
    throw 'Could not create a unique temporary directory for the Visual Studio bootstrapper.'
}

function Test-SetupUiTempChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $pathFull = [IO.Path]::GetFullPath($Path)
    $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-SetupUiPrerequisiteTempDirectory {
    param([AllowNull()][string]$Path)
    if (!$Path -or !(Test-Path -LiteralPath $Path)) { return }
    try {
        $full = [IO.Path]::GetFullPath($Path)
        $root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
        if ($full -eq $root -or !$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch {
        # Cleanup is best effort; a failed cleanup must not hide the installer result.
    }
}

function Get-SetupUiNativeErrorCode {
    param([AllowNull()][Exception]$Exception)
    $current = $Exception
    while ($current) {
        if ($current -is [ComponentModel.Win32Exception]) { return [int]$current.NativeErrorCode }
        $current = $current.InnerException
    }
    $null
}

function New-SetupUiPrerequisiteProcessStartInfo {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $true
    $startInfo.WorkingDirectory = [IO.Path]::GetTempPath()
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $startInfo
}

function Invoke-SetupUiPrerequisiteInstaller {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-SetupUiPrerequisiteProcessStartInfo -FilePath $FilePath -ArgumentList $ArgumentList
    try {
        if (!$process.Start()) { throw "The Visual Studio prerequisite installer did not start." }
        $process.WaitForExit()
        [int]$process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}
function Complete-SetupUiPrerequisiteReady {
    param(
        [Parameter(Mandatory)][scriptblock]$Resolver,
        [Parameter(Mandatory)][scriptblock]$PathEnabler
    )
    $verified = & $Resolver
    if (!$verified -or !(Get-SetupUiProperty $verified 'Ok')) {
        $missing = [string](Get-SetupUiProperty $verified 'Missing')
        if (!$missing) { $missing = 'the MSVC linker or Windows SDK could not be verified' }
        Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason 'The required Native AOT files could not be verified.'
        throw "Native AOT prerequisites are still missing: $missing"
    }
    $pathReady = & $PathEnabler
    if (!$pathReady) {
        throw 'vswhere.exe could not be put on PATH for the Native AOT build.'
    }
    [pscustomobject]@{
        Status = 'Ready'
        Toolchain = $verified
        Missing = $null
        Reason = $null
        ExitCode = 0
    }
}

function Invoke-SetupUiPrerequisitePreflight {
    <#
    .SYNOPSIS
        Resolve prerequisites and, only after an explicit interactive approval, offer installation.

    .OUTPUTS
        Status is Ready, Cancelled, Manual or RebootRequired. Toolchain is populated only
        when a verified Resolve-SetupUiToolchain result is available.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [switch]$NoPrompt,
        [switch]$SilentInstall,
        [scriptblock]$Resolver,
        [scriptblock]$Interaction,
        [scriptblock]$Downloader,
        [scriptblock]$SignatureValidator,
        [scriptblock]$ProcessRunner,
        [scriptblock]$PathEnabler,
        [scriptblock]$ExistingInstallationResolver,
        [scriptblock]$InteractiveProbe,
        [scriptblock]$InstallerPathResolver,
        [scriptblock]$TemporaryDirectoryFactory
    )

    $setupProject = Join-Path $RepoRoot 'src/CycleArc.Setup/CycleArc.Setup.csproj'
    if (!(Test-Path -LiteralPath $setupProject -PathType Leaf)) {
        throw "The setup UI project is missing at $setupProject. Nothing was built, stopped or installed."
    }
    if (!$Resolver) { $Resolver = { Resolve-SetupUiToolchain } }
    if (!$Interaction) { $Interaction = { param($missing) Read-Host 'Choose 1, 2 or 3' } }
    if (!$Downloader) { $Downloader = { param($uri, $destination) Invoke-SetupUiOfficialDownload -Uri $uri -Destination $destination } }
    if (!$SignatureValidator) { $SignatureValidator = { param($path) Test-SetupUiMicrosoftAuthenticode -Path $path } }
    if (!$ProcessRunner) { $ProcessRunner = { param($path, $arguments) Invoke-SetupUiPrerequisiteInstaller -FilePath $path -ArgumentList $arguments } }
    if (!$PathEnabler) { $PathEnabler = { Add-VsWhereToPath } }
    if (!$ExistingInstallationResolver) { $ExistingInstallationResolver = { param($toolchain) Resolve-SetupUiExistingInstallation } }
    if (!$InteractiveProbe) { $InteractiveProbe = { Test-SetupUiPrerequisiteInteractive -NoPrompt:$NoPrompt -SilentInstall:$SilentInstall } }
    if (!$InstallerPathResolver) { $InstallerPathResolver = { Find-SetupUiVisualStudioInstaller } }
    if (!$TemporaryDirectoryFactory) { $TemporaryDirectoryFactory = { New-SetupUiPrerequisiteTempDirectory } }

    $initial = & $Resolver
    if (!$initial) { throw 'The Native AOT prerequisite resolver returned no result.' }
    if (Get-SetupUiProperty $initial 'Ok') {
        # Even an initially ready machine must pass through the same PATH enablement and
        # resolver re-probe used after installation.
        return Complete-SetupUiPrerequisiteReady -Resolver $Resolver -PathEnabler $PathEnabler
    }

    $missing = [string](Get-SetupUiProperty $initial 'Missing')
    if (!$missing) { $missing = 'the Visual Studio 2022 C++ toolchain could not be verified' }

    if ($NoPrompt -or $SilentInstall -or !(& $InteractiveProbe)) {
        Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason 'Interactive prerequisite installation is unavailable in this invocation.'
        throw "Visual Studio 2022 build prerequisites are missing in a non-interactive invocation: $missing. Nothing was built, stopped or installed."
    }

    $choice = Invoke-SetupUiPrerequisitePrompt -Missing $missing -Interaction $Interaction
    if ($choice -eq 'Cancel') {
        Write-SetupUiPrerequisiteCancelledGuidance -Reason 'No Microsoft installer was started.'
        return [pscustomobject]@{
            Status = 'Cancelled'; Toolchain = $initial; Missing = $missing
            Reason = 'UserCancelled'; ExitCode = $null
        }
    }
    if ($choice -eq 'Manual') {
        Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason 'No Microsoft installer was started.'
        return [pscustomobject]@{
            Status = 'Manual'; Toolchain = $initial; Missing = $missing
            Reason = 'ManualInstructions'; ExitCode = $null
        }
    }

    # A reported VC component with no actual linker is a damaged/incomplete installation.
    # Running --add against it would falsely suggest that a repair happened.
    if ($missing -match '(?i)link\.exe') {
        Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason 'The C++ workload is reported, but the MSVC linker is absent. Repair or modify that exact Visual Studio 2022 installation.'
        return [pscustomobject]@{
            Status = 'Manual'; Toolchain = $initial; Missing = $missing
            Reason = 'RepairRequired'; ExitCode = $null
        }
    }

    $temporaryDirectory = $null
    try {
        try {
            $temporaryDirectory = & $TemporaryDirectoryFactory
            if (!$temporaryDirectory) { throw 'The temporary directory factory returned no path.' }
            $temporaryDirectory = [IO.Path]::GetFullPath([string]$temporaryDirectory)
            if (!(Test-Path -LiteralPath $temporaryDirectory -PathType Container)) {
                throw "The temporary directory does not exist: $temporaryDirectory"
            }
        }
        catch {
            throw "Could not create a unique temporary directory for the Visual Studio prerequisite installer: $($_.Exception.Message)"
        }

        $existing = & $ExistingInstallationResolver $initial
        $existingPath = [string](Get-SetupUiProperty $existing 'InstallationPath')
        if (!$existingPath) { $existingPath = [string](Get-SetupUiProperty $existing 'Path') }
        if (!$existingPath -and $existing -is [string]) { $existingPath = [string]$existing }

        $installer = $null
        $arguments = @()
        if ($existingPath) {
            $product = [string](Get-SetupUiProperty $existing 'ProductId')
            $channel = [string](Get-SetupUiProperty $existing 'ChannelId')
            $supported = @('Microsoft.VisualStudio.Product.BuildTools', 'Microsoft.VisualStudio.Product.Community',
                'Microsoft.VisualStudio.Product.Professional', 'Microsoft.VisualStudio.Product.Enterprise')
            if ((Get-SetupUiProperty $existing 'IsComplete') -ne $true -or
                (Get-SetupUiProperty $existing 'IsRebootRequired') -eq $true -or $product -notin $supported -or !$channel) {
                Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason "Existing Visual Studio at $existingPath needs manual repair/reboot or its edition could not be verified. No installer was started."
                return [pscustomobject]@{ Status = 'Manual'; Toolchain = $initial; Reason = 'ExistingInstallationNeedsAttention' }
            }
            $installer = & $InstallerPathResolver
            if (!$installer) {
                Write-SetupUiPrerequisiteManualGuidance -Missing $missing -Reason 'An existing Visual Studio 2022 installation was found, but its Visual Studio Installer could not be located. No installer was started.'
                return [pscustomobject]@{ Status = 'Manual'; Toolchain = $initial; Reason = 'InstallerUnavailable' }
            }
            $workload = if ($product -eq 'Microsoft.VisualStudio.Product.BuildTools') {
                $script:SetupUiSetupWorkload
            } else { 'Microsoft.VisualStudio.Workload.NativeDesktop' }
            $arguments = @('modify', '--installPath', $existingPath, '--channelId', $channel, '--productId', $product, '--add', $workload,
                '--add', $script:SetupUiSetupComponent, '--includeRecommended', '--passive', '--norestart')
            Write-Host ("Modifying the existing Visual Studio 2022 installation at {0} ({1})." -f $existingPath, $workload)
        }
        else {
            $destination = Join-Path $temporaryDirectory $script:SetupUiBootstrapperName
            if (!(Test-SetupUiTempChildPath -Root $temporaryDirectory -Path $destination)) {
                throw "The Visual Studio bootstrapper destination escaped its unique temporary directory: $destination"
            }
            Write-Host ("Downloading the official Microsoft Visual Studio Build Tools bootstrapper from {0}." -f $script:SetupUiBootstrapperUri)
            try {
                & $Downloader $script:SetupUiBootstrapperUri $destination
            }
            catch {
                throw "Visual Studio Build Tools bootstrapper download failed: $($_.Exception.Message)"
            }
            if (!(Test-Path -LiteralPath $destination -PathType Leaf)) {
                throw "Visual Studio Build Tools bootstrapper download did not create $destination."
            }

            $installer = $destination
            $arguments = @(
                '--add', $script:SetupUiSetupWorkload,
                '--add', $script:SetupUiSetupComponent,
                '--includeRecommended',
                '--passive',
                '--norestart',
                '--wait'
            )
        }

        if (!(& $SignatureValidator $installer)) {
            throw 'The Visual Studio prerequisite installer failed Authenticode validation for Microsoft Corporation. It was not executed.'
        }
        Write-Host 'Verified a valid Microsoft Corporation Authenticode signature.'
        Write-Host ("Launching the Visual Studio prerequisite installer with visible passive UI: {0}" -f $installer)
        try {
            $processResult = & $ProcessRunner $installer $arguments
        }
        catch {
            $nativeCode = Get-SetupUiNativeErrorCode $_.Exception
            if ($nativeCode -eq 1223) {
                Write-SetupUiPrerequisiteCancelledGuidance -Reason 'Windows UAC approval was cancelled.'
                return [pscustomobject]@{ Status = 'Cancelled'; Toolchain = $initial; Reason = 'UacCancelled'; ExitCode = 1223 }
            }
            throw "Could not launch the Visual Studio prerequisite installer: $($_.Exception.Message). Administrator or company IT approval may be required; no policy bypass was attempted."
        }
        $processValue = @($processResult) | Select-Object -Last 1
        $exitCode = if ($processValue -is [int] -or $processValue -is [long]) {
            [int]$processValue
        }
        else {
            $candidate = Get-SetupUiProperty $processValue 'ExitCode'
            if ($null -eq $candidate -or [string]$candidate -eq '') {
                throw 'The Visual Studio prerequisite installer returned no exit code.'
            }
            [int]$candidate
        }

        if ($exitCode -in @(1223, 1602, 5004, -1073741510)) {
            Write-SetupUiPrerequisiteCancelledGuidance ("The Visual Studio installer returned cancellation code {0} (UAC or user cancellation)." -f $exitCode)
            return [pscustomobject]@{
                Status = 'Cancelled'; Toolchain = $initial; Missing = $missing
                Reason = if ($exitCode -eq 1223) { 'UacCancelled' } else { 'InstallerCancelled' }
                ExitCode = $exitCode
            }
        }
        if ($exitCode -in @(3010, 1641)) {
            Write-SetupUiPrerequisiteRebootGuidance
            return [pscustomobject]@{
                Status = 'RebootRequired'; Toolchain = $initial; Missing = $missing
                Reason = 'InstallerRequestedReboot'; ExitCode = $exitCode
            }
        }
        if ($exitCode -in @(1001, 1003, 1618, 8006)) {
            throw "Visual Studio prerequisite installer is busy because another installation is already running (exit code $exitCode). Wait for it to finish, then retry; company IT policy may also require approval."
        }
        if ($exitCode -eq 5003) { throw 'The Visual Studio installer could not download its payload (exit 5003). Check network access or company IT policy, then retry.' }
        if ($exitCode -ne 0) {
            throw "Visual Studio prerequisite installer failed with exit code $exitCode. Company IT approval or policy may be required."
        }

        Write-Host 'Visual Studio prerequisite installation completed; rechecking the linker and Windows SDK.'
        return Complete-SetupUiPrerequisiteReady -Resolver $Resolver -PathEnabler $PathEnabler
    }
    finally {
        Remove-SetupUiPrerequisiteTempDirectory -Path $temporaryDirectory
    }
}
