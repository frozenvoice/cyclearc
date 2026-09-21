#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/SetupUiToolchain.ps1')
. (Join-Path $repoRoot 'scripts/SetupUiPrerequisites.ps1')

function Assert-Equal {
    param([AllowNull()][object]$Expected, [AllowNull()][object]$Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "$Message (expected '$Expected', got '$Actual')" }
}
function Assert-True([bool]$Value, [string]$Message) {
    if (!$Value) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Fragment) {
    try { & $Action }
    catch {
        if ($Fragment -and $_.Exception.Message -notlike "*$Fragment*") {
            throw "Expected '$Fragment' in '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected an exception containing '$Fragment'."
}
function New-SequenceResolver([object[]]$Results) {
    $state = [pscustomobject]@{ Results = $Results; Index = 0 }
    { $index = [Math]::Min($state.Index, $state.Results.Count - 1); $state.Index++; $state.Results[$index] }.GetNewClosure()
}
function New-TestTempDirectory {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-prereq-test-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    $path
}
function New-MissingToolchain([string]$Missing = 'vswhere.exe was not found', [string]$Installation = $null) {
    [pscustomobject]@{ Ok = $false; VsWhere = $null; Installation = $Installation; Linker = $null; SdkLibrary = $null; Missing = $Missing }
}
function New-ReadyToolchain {
    [pscustomobject]@{ Ok = $true; VsWhere = 'vswhere.exe'; Installation = 'C:\VS2022'; Linker = 'C:\VS2022\link.exe'; SdkLibrary = 'C:\SDK\kernel32.lib'; Missing = $null }
}

$readyDownload = 0
$readyProcess = 0
$readyPath = 0
$ready = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -NoPrompt -Resolver (New-SequenceResolver @((New-ReadyToolchain), (New-ReadyToolchain))) -PathEnabler { $script:readyPath++; $true } -Downloader { $script:readyDownload++ } -ProcessRunner { $script:readyProcess++ }
Assert-Equal 'Ready' $ready.Status 'ready preflight status'
Assert-Equal 1 $readyPath 'ready preflight PATH enablement'
Assert-Equal 0 $readyDownload 'ready preflight download count'
Assert-Equal 0 $readyProcess 'ready preflight process count'
Assert-True ($null -ne $ready.Toolchain.Linker) 'ready result carries the verified linker'
Write-Host 'PASS: ready prerequisites are re-probed without installation.'

$nonInteractivePrompt = 0
$nonInteractiveDownload = 0
Assert-Throws { Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -NoPrompt -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $false } -Interaction { $script:nonInteractivePrompt++; '1' } -Downloader { $script:nonInteractiveDownload++ } } 'non-interactive invocation'
Assert-Equal 0 $nonInteractivePrompt 'noninteractive prompt count'
Assert-Equal 0 $nonInteractiveDownload 'noninteractive download count'
Write-Host 'PASS: noninteractive preflight fails fast with manual instructions.'

$cancelDownload = 0
$cancelProcess = 0
$cancel = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $true } -Interaction { '3' } -Downloader { $script:cancelDownload++ } -ProcessRunner { $script:cancelProcess++ }
Assert-Equal 'Cancelled' $cancel.Status 'prompt cancellation status'
Assert-Equal 0 $cancelDownload 'cancel download count'
Assert-Equal 0 $cancelProcess 'cancel process count'
Write-Host 'PASS: prompt cancellation leaves the existing installation untouched.'

$installRoot = New-TestTempDirectory
try {
    $downloadedUri = $null
    $downloadedPath = $null
    $signaturePath = $null
    $launchedPath = $null
    $launchedArgs = $null
    $install = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain), (New-ReadyToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { $installRoot } -Downloader { param($uri, $path) $script:downloadedUri = $uri; $script:downloadedPath = $path; Set-Content -LiteralPath $path -Value 'fake bootstrapper' } -SignatureValidator { param($path) $script:signaturePath = $path; $true } -ProcessRunner { param($path, $arguments) $script:launchedPath = $path; $script:launchedArgs = @($arguments); 0 } -PathEnabler { $true }
    Assert-Equal 'Ready' $install.Status 'successful install status'
    Assert-Equal 'https://aka.ms/vs/17/release/vs_buildtools.exe' $downloadedUri 'official bootstrapper URL'
    Assert-Equal $downloadedPath $signaturePath 'signature receives downloaded file'
    Assert-Equal $downloadedPath $launchedPath 'only validated file is launched'
    Assert-True ($launchedArgs -contains 'Microsoft.VisualStudio.Workload.VCTools') 'bootstrapper has VCTools workload'
    Assert-True ($launchedArgs -contains '--includeRecommended') 'bootstrapper includes recommended components'
    Assert-True ($launchedArgs -contains '--passive') 'bootstrapper uses visible passive UI'
    Assert-True ($launchedArgs -contains '--norestart') 'bootstrapper disables automatic restart'
    Assert-True ($launchedArgs -contains '--wait') 'bootstrapper waits for completion'
}
finally { if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force } }
Write-Host 'PASS: approved install downloads, validates, launches and re-probes.'

$downloadProcess = 0
Assert-Throws { Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { New-TestTempDirectory } -Downloader { throw 'network unavailable' } -SignatureValidator { throw 'signature should not run' } -ProcessRunner { $script:downloadProcess++ } } 'bootstrapper download failed'
Assert-Equal 0 $downloadProcess 'download failure process count'
Write-Host 'PASS: download failures stop before execution.'

$signatureProcess = 0
Assert-Throws { Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { New-TestTempDirectory } -Downloader { param($uri, $path) Set-Content -LiteralPath $path -Value 'unsigned' } -SignatureValidator { $false } -ProcessRunner { $script:signatureProcess++ } } 'failed Authenticode validation'
Assert-Equal 0 $signatureProcess 'signature failure process count'
Write-Host 'PASS: unsigned or wrong-signer bootstrapper is never executed.'

foreach ($code in @(1223, 1602, 5004, -1073741510)) {
    $cancelledInstall = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { New-TestTempDirectory } -Downloader { param($uri, $path) Set-Content -LiteralPath $path -Value 'signed' } -SignatureValidator { $true } -ProcessRunner { $code }
    Assert-Equal 'Cancelled' $cancelledInstall.Status "installer cancellation $code"
    Assert-Equal $code $cancelledInstall.ExitCode "installer cancellation exit code $code"
}
$reboot = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { New-TestTempDirectory } -Downloader { param($uri, $path) Set-Content -LiteralPath $path -Value 'signed' } -SignatureValidator { $true } -ProcessRunner { 3010 }
Assert-Equal 'RebootRequired' $reboot.Status 'installer reboot status'
Assert-Equal 3010 $reboot.ExitCode 'installer reboot exit code'
Write-Host 'PASS: cancellation and reboot outcomes are reported separately.'

$linkProcess = 0
$linkManual = Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain 'the MSVC linker (link.exe) is missing'))) -InteractiveProbe { $true } -Interaction { '1' } -ProcessRunner { $script:linkProcess++; 0 }
Assert-Equal 'Manual' $linkManual.Status 'missing linker manual status'
Assert-Equal 0 $linkProcess 'missing linker process count'
Assert-Equal 'RepairRequired' $linkManual.Reason 'missing linker reason'
Write-Host 'PASS: a reported VC workload without link.exe gets repair guidance.'

Assert-Throws { Invoke-SetupUiPrerequisitePreflight -ExistingInstallationResolver { $null } -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain 'no Windows SDK x64 import library (um\x64\kernel32.lib) was found'), (New-MissingToolchain 'no Windows SDK x64 import library (um\x64\kernel32.lib) was found'))) -InteractiveProbe { $true } -Interaction { '1' } -TemporaryDirectoryFactory { New-TestTempDirectory } -Downloader { param($uri, $path) Set-Content -LiteralPath $path -Value 'signed' } -SignatureValidator { $true } -ProcessRunner { 0 } -PathEnabler { $true } } 'prerequisites are still missing'
Write-Host 'PASS: post-install SDK re-probe reports the exact remaining gap.'

$modifyArgs = $null
$modifyDownload = 0
$modify = Invoke-SetupUiPrerequisitePreflight -RepoRoot $repoRoot -Resolver (New-SequenceResolver @((New-MissingToolchain 'no Visual Studio installation reports the C++ component'), (New-ReadyToolchain))) -InteractiveProbe { $true } -Interaction { '1' } -ExistingInstallationResolver { [pscustomobject]@{ InstallationPath = 'C:\Program Files\VS2022\Community'; ProductId = 'Microsoft.VisualStudio.Product.Community'; ChannelId = 'VisualStudio.17.Release'; IsComplete = $true; IsRebootRequired = $false } } -InstallerPathResolver { 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe' } -SignatureValidator { $true } -Downloader { $script:modifyDownload++ } -ProcessRunner { param($path, $arguments) $script:modifyArgs = @($arguments); 0 } -PathEnabler { $true } -TemporaryDirectoryFactory { New-TestTempDirectory }
Assert-Equal 'Ready' $modify.Status 'existing installation modify status'
Assert-Equal 0 $modifyDownload 'existing installation download count'
Assert-True ($modifyArgs -contains 'modify') 'existing installation uses modify'
Assert-True ($modifyArgs -contains '--installPath') 'existing installation uses exact installPath'
Assert-True ($modifyArgs -contains 'C:\Program Files\VS2022\Community') 'existing installation preserves edition path'
Write-Host 'PASS: existing VS 2022 installations are modified in place.'

$vswhereArgs = $null
$versionResult = Resolve-SetupUiToolchain -VsWhereLocator { 'fake-vswhere.exe' } -VsWhereInvoker { param($path, $arguments) $script:vswhereArgs = @($arguments); 'C:\VS2022' } -LinkerProbe { param($installation) 'C:\VS2022\link.exe' } -SdkProbe { 'C:\SDK\kernel32.lib' }
Assert-True $versionResult.Ok 'version constrained resolver result'
Assert-True ($vswhereArgs -contains '-version') 'resolver has a VS version filter'
Assert-True ($vswhereArgs -contains '[17.0,18.0)') 'resolver limits itself to VS 2022'
Assert-True ($vswhereArgs -contains 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64') 'resolver retains VC component probe'
Write-Host 'PASS: resolver remains component/linker/SDK accurate and VS 2022 bounded.'



# Exercise the real boundary functions as well as the orchestration fakes.
Assert-True ($modifyArgs -contains 'Microsoft.VisualStudio.Workload.NativeDesktop') 'IDE uses NativeDesktop'
Assert-True ($modifyArgs -contains '--channelId' -and $modifyArgs -contains 'VisualStudio.17.Release') 'modify targets discovered channel'
Assert-True ($modifyArgs -contains '--productId' -and $modifyArgs -contains 'Microsoft.VisualStudio.Product.Community') 'modify targets discovered product'
Assert-True ($modifyArgs -notcontains 'Microsoft.VisualStudio.Workload.VCTools') 'IDE must not receive Build Tools workload'
Assert-True ($modifyArgs -notcontains '--wait') 'installed setup.exe must not receive bootstrapper-only wait'
Assert-True ($modifyArgs -contains 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64') 'modify explicitly adds the VC component'
$processInfo = New-SetupUiPrerequisiteProcessStartInfo -FilePath 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe' -ArgumentList @('modify', '--installPath', 'C:\Program Files\Visual Studio\2022')
Assert-Equal 'C:\Program Files\Visual Studio\2022' $processInfo.ArgumentList[2] 'spaces remain one argument'
Assert-True $processInfo.UseShellExecute 'Windows/Installer owns elevation'
Assert-True (!$processInfo.Verb) 'build-local does not elevate itself'
Assert-True ($processInfo.WorkingDirectory -ne (Split-Path -Parent $processInfo.FileName)) 'installer starts outside its own directory'
Write-Host 'PASS: IDE workload, bootstrapper-only wait and spaced installer arguments follow Microsoft contracts.'

# Every optional operation is isolated even when these tests run on a real CI machine.
$baseFlow = @{
    RepoRoot = $repoRoot
    Resolver = { New-MissingToolchain }
    InteractiveProbe = { $true }
    Interaction = { '1' }
    ExistingInstallationResolver = { $null }
    TemporaryDirectoryFactory = { New-TestTempDirectory }
    Downloader = { param($uri, $path) Set-Content -LiteralPath $path -Value 'fake bootstrapper' }
    SignatureValidator = { $true }
    ProcessRunner = { throw 'installer must not run' }
    PathEnabler = { $true }
}
foreach ($suppression in @(@{ NoPrompt = $true }, @{ SilentInstall = $true })) {
    Assert-Throws { Invoke-SetupUiPrerequisitePreflight @baseFlow @suppression } 'non-interactive invocation'
}
$previousCi = $env:CI
try {
    $env:CI = 'true'
    Assert-True (!(Test-SetupUiPrerequisiteInteractive)) 'CI cannot prompt'
    $ciFlow = $baseFlow.Clone()
    $ciFlow.Remove('InteractiveProbe')
    Assert-Throws { Invoke-SetupUiPrerequisitePreflight @ciFlow } 'non-interactive invocation'
}
finally { $env:CI = $previousCi }
Assert-True (!(Test-SetupUiPrerequisiteInteractive -NoPrompt)) 'explicit prompt opt-out'
Assert-True (!(Test-SetupUiPrerequisiteInteractive -SilentInstall)) 'silent install opt-out'
$manualFlow = $baseFlow.Clone()
$manualFlow.Interaction = { '2' }
$manualFlow.Downloader = { throw 'manual selection must not download' }
Assert-Equal 'Manual' (Invoke-SetupUiPrerequisitePreflight @manualFlow).Status 'manual selection status'
Write-Host 'PASS: explicit opt-outs and CI cannot prompt; manual guidance cannot install.'

$uacFlow = $baseFlow.Clone()
$uacFlow.ProcessRunner = { throw [ComponentModel.Win32Exception]::new(1223) }
Assert-Equal 'Cancelled' (Invoke-SetupUiPrerequisitePreflight @uacFlow).Status 'real UAC launch exception classification'
$policyFlow = $baseFlow.Clone()
$policyFlow.ProcessRunner = { throw [ComponentModel.Win32Exception]::new(1260) }
Assert-Throws { Invoke-SetupUiPrerequisitePreflight @policyFlow } 'company IT approval'
foreach ($failedCode in @(1603, 740, 5003, 1001, 1003, 1618, 8006)) {
    $failedFlow = $baseFlow.Clone()
    $failedFlow.ProcessRunner = { $failedCode }
    Assert-Throws { Invoke-SetupUiPrerequisitePreflight @failedFlow } ''
}
$missingAfterInstall = $baseFlow.Clone()
$missingAfterInstall.Resolver = { New-MissingToolchain 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64 is absent' }
$missingAfterInstall.ProcessRunner = { 0 }
Assert-Throws { Invoke-SetupUiPrerequisitePreflight @missingAfterInstall } 'VC.Tools.x86.x64'
Write-Host 'PASS: native UAC cancellation, policy/busy/download failures and missing post-install VC block the build.'

$buildToolsFlow = $baseFlow.Clone()
$buildToolsFlow.ExistingInstallationResolver = {
    [pscustomobject]@{ InstallationPath = 'C:\Build Tools'; ProductId = 'Microsoft.VisualStudio.Product.BuildTools'; ChannelId = 'VisualStudio.17.Release'; IsComplete = $true; IsRebootRequired = $false }
}
$buildToolsFlow.InstallerPathResolver = { 'C:\VS Installer\setup.exe' }
$buildToolsFlow.Downloader = { throw 'existing Build Tools must not download' }
$buildToolsFlow.Resolver = New-SequenceResolver @((New-MissingToolchain), (New-ReadyToolchain))
$buildToolsFlow.ProcessRunner = {
    param($path, $arguments)
    Assert-True ($arguments -contains 'Microsoft.VisualStudio.Workload.VCTools') 'BuildTools uses VCTools'
    Assert-True ($arguments -notcontains '--wait') 'installed BuildTools setup is not a bootstrapper'
    0
}
Assert-Equal 'Ready' (Invoke-SetupUiPrerequisitePreflight @buildToolsFlow).Status 'Build Tools modify status'
$incompleteFlow = $baseFlow.Clone()
$incompleteFlow.ExistingInstallationResolver = {
    [pscustomobject]@{ InstallationPath = 'C:\VS incomplete'; ProductId = 'Microsoft.VisualStudio.Product.Community'; IsComplete = $false }
}
$incompleteFlow.Downloader = { throw 'incomplete VS must not be duplicated' }
Assert-Equal 'Manual' (Invoke-SetupUiPrerequisitePreflight @incompleteFlow).Status 'incomplete VS requires manual repair'
$script:discoveryArgs = $null
$existingRecord = Resolve-SetupUiExistingInstallation -VsWhereLocator { 'fake-vswhere' } -VsWhereInvoker {
    param($path, $arguments)
    $script:discoveryArgs = $arguments
    '[{"installationPath":"C:\\IDE","productId":"Microsoft.VisualStudio.Product.Community","channelId":"VisualStudio.17.Release","isComplete":true},{"installationPath":"C:\\BuildTools","productId":"Microsoft.VisualStudio.Product.BuildTools","channelId":"VisualStudio.17.Release","isComplete":true}]'
}
Assert-Equal 'C:\BuildTools' $existingRecord.InstallationPath 'reuse existing complete Build Tools before an IDE'
Assert-Equal 'VisualStudio.17.Release' $existingRecord.ChannelId 'discovery retains exact channel'
Assert-True ($discoveryArgs -contains '-all') 'incomplete installations are discovered'
Assert-Throws { Resolve-SetupUiExistingInstallation -VsWhereLocator { 'fake' } -VsWhereInvoker { 'malformed' } } 'metadata'
Write-Host 'PASS: existing Build Tools, incomplete instances and malformed discovery never duplicate an IDE.'

$fakeSigner = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation'; SimpleName = 'Microsoft Corporation' }
$fakeSigner | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($type, $issuer) $this.SimpleName }
$fakeSignature = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $fakeSigner }
Assert-True (Test-SetupUiMicrosoftAuthenticode -Path 'fake.exe' -SignatureReader { $fakeSignature }) 'valid Microsoft certificate accepted'
foreach ($invalidStatus in @('NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError')) {
    $fakeSignature.Status = $invalidStatus
    Assert-True (!(Test-SetupUiMicrosoftAuthenticode -Path 'fake.exe' -SignatureReader { $fakeSignature })) "reject $invalidStatus"
}
$fakeSignature.Status = 'Valid'
$fakeSigner.Subject = 'CN=Microsoft Corporation Impostor'
$fakeSigner.SimpleName = 'Microsoft Corporation Impostor'
Assert-True (!(Test-SetupUiMicrosoftAuthenticode -Path 'fake.exe' -SignatureReader { $fakeSignature })) 'reject wrong signer'
Write-Host 'PASS: real signature policy rejects unsigned, altered, untrusted and wrong-publisher files.'

# A fake HttpMessageHandler exercises redirect and destination checks without a socket.
if (!('CycleArcPrerequisiteHttpFixture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class CycleArcPrerequisiteHttpFixture : HttpMessageHandler {
    public string Mode = "Success";
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        Calls++;
        if (Mode == "Success") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[]{1,2,3}) });
        if (Mode == "Failure") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(Mode == "Downgrade" ? "http://download.microsoft.com/tool.exe" : Mode == "Loop" ? "https://aka.ms/loop" : "https://example.invalid/tool.exe");
        return Task.FromResult(response);
    }
}
'@
}
$httpRoot = New-TestTempDirectory
try {
    $downloadFile = Join-Path $httpRoot 'bootstrapper.exe'
    $httpSuccess = [CycleArcPrerequisiteHttpFixture]::new()
    Invoke-SetupUiOfficialDownload -Uri 'https://aka.ms/vs/17/release/vs_buildtools.exe' -Destination $downloadFile -Handler $httpSuccess
    Assert-Equal 3 (Get-Item -LiteralPath $downloadFile).Length 'download writes expected fake payload'
    Assert-Throws { Invoke-SetupUiOfficialDownload -Uri 'https://aka.ms/test' -Destination $downloadFile } 'reuse'
    foreach ($mode in @('External', 'Downgrade', 'Failure', 'Loop')) {
        $httpFake = [CycleArcPrerequisiteHttpFixture]::new()
        $httpFake.Mode = $mode
        Assert-Throws { Invoke-SetupUiOfficialDownload -Uri 'https://aka.ms/test' -Destination (Join-Path $httpRoot ($mode + '.exe')) -Handler $httpFake -MaximumRedirects 1 } ''
        Assert-Equal $(if ($mode -eq 'Loop') { 2 } else { 1 }) $httpFake.Calls "bounded requests for $mode"
    }
}
finally { Remove-SetupUiPrerequisiteTempDirectory $httpRoot }
Write-Host 'PASS: real download boundary rejects HTTPS downgrade, foreign redirects, stale files and HTTP failures without network.'

$helperAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts/SetupUiPrerequisites.ps1'), [ref]$null, [ref]$null)
$duplicateFunctions = @($helperAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Group-Object Name | Where-Object Count -gt 1)
Assert-Equal 0 $duplicateFunctions.Count 'helper functions must not be shadowed by duplicated definitions'
Write-Host 'SetupUiPrerequisites.Tests.ps1 passed.'

$script:postInstallReprobed = $false
Assert-Throws {
    Complete-SetupUiPrerequisiteReady -Resolver {
        $script:postInstallReprobed = $true
        New-MissingToolchain 'vswhere.exe was not found after setup'
    } -PathEnabler { throw 'PATH must not hide the resolver diagnostic' }
} 'vswhere.exe was not found after setup'
Assert-True $postInstallReprobed 'post-install resolve precedes PATH setup even when vswhere is absent'
