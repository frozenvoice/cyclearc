#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/BuildLocalFailure.ps1') -LoadOnly
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message (expected '$Expected', got '$Actual')" }
}
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-error-dialog-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$marker = Join-Path $testRoot 'last-failure.txt'
$savedEnvironment = @{}
$environmentNames = @('CYCLEARC_NO_PAUSE', 'CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'BUILD_ID', 'BUILD_NUMBER', 'JENKINS_URL')
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
$script:dialogCount = 0
$script:dialogMessage = $null
$presenter = { param($text) $script:dialogCount++; $script:dialogMessage = $text }
try {
    Set-Content -LiteralPath $marker -Value "Failed at: preflight`r`nCause: SDK Authenticode validation failed.`r`nLog: $testRoot\prerequisites.log"
    Show-BuildLocalPrerequisiteFailure -FailureMarker $marker -InteractiveProbe { $true } -DialogPresenter $presenter
    Assert-Equal 0 $dialogCount 'generic signature/preflight failure displays no acknowledgement'
    foreach ($status in @('PolicyBlocked', 'RebootRequired')) {
        $script:dialogCount = 0
        Set-Content -LiteralPath $marker -Value "Failed at: prerequisite bootstrap`r`nPrerequisite status: $status`r`nReason: $status`r`nDiagnostic log: $testRoot\bootstrap.log"
        Show-BuildLocalPrerequisiteFailure -FailureMarker $marker -InteractiveProbe { $true } -DialogPresenter $presenter
        Assert-Equal 1 $dialogCount "$status bootstrap marker receives one acknowledgement"
        Assert-True ($dialogMessage.Contains($status) -and $dialogMessage.Contains("$testRoot\bootstrap.log") -and $dialogMessage.Contains($marker)) 'typed policy/reboot acknowledgement preserves actual reason and log/marker paths'
    }
    foreach ($suppression in @(@{NoPrompt=$true}, @{SilentInstall=$true}, @{ManualPrerequisites=$true})) {
        $script:dialogCount = 0
        Show-BuildLocalPrerequisiteFailure -FailureMarker $marker @suppression -InteractiveProbe { throw 'suppressed run must not probe or ask input' } -DialogPresenter $presenter
        Assert-Equal 0 $dialogCount 'explicit suppression displays no acknowledgement'
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, '1')
        $script:dialogCount = 0
        Show-BuildLocalPrerequisiteFailure -FailureMarker $marker -InteractiveProbe { throw 'CI/suppressed run must not probe or ask input' } -DialogPresenter $presenter
        Assert-Equal 0 $dialogCount "$name displays no acknowledgement"
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    $script:dialogCount = 0
    Show-BuildLocalPrerequisiteFailure -FailureMarker $marker -InteractiveProbe { $false } -DialogPresenter $presenter
    Assert-Equal 0 $dialogCount 'redirected/noninteractive console displays no acknowledgement'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $start.Arguments = '-NoProfile -File "' + (Join-Path $repoRoot 'scripts/BuildLocalFailure.ps1') + '" -FailureMarker "' + $marker + '"'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $child = [Diagnostics.Process]::Start($start)
    try {
        $child.StandardInput.Close()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        if (!$child.WaitForExit(15000)) { $child.Kill(); throw 'Real redirected helper waited for acknowledgement input' }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Assert-Equal 0 $child.ExitCode "real redirected helper preserves no-input exit; $output"
    }
    finally { $child.Dispose() }
    Set-Content -LiteralPath $marker -Value 'Failed at: install'
    Show-BuildLocalPrerequisiteFailure -FailureMarker $marker -InteractiveProbe { $true } -DialogPresenter $presenter
    Assert-Equal 0 $dialogCount 'build/install-stage failure is not acknowledged twice'
    Show-BuildLocalPrerequisiteFailure -FailureMarker (Join-Path $testRoot 'missing.txt') -InteractiveProbe { $true } -DialogPresenter $presenter
    Assert-Equal 0 $dialogCount 'absent marker is not treated as a prerequisite failure'
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts/BuildLocalFailure.ps1'), [ref]$null, [ref]$parseErrors)
    Assert-Equal 0 $parseErrors.Count 'acknowledgement helper parses in Windows PowerShell'
    $inputs = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Read-Host' }, $true))
    Assert-Equal 0 $inputs.Count 'native acknowledgement does not reintroduce a console choice/input menu'
    # Drive real CMD token handling with a fake failing build and fake presenter.
    # The latter only writes a marker, so these subprocesses can never display UI.
    $cmdRoot = Join-Path $testRoot 'cmd token fixture'
    $cmdScripts = Join-Path $cmdRoot 'scripts'
    New-Item -ItemType Directory -Path $cmdScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'build-local.cmd') -Destination $cmdRoot
    Set-Content -LiteralPath (Join-Path $cmdScripts 'Build-Local.ps1') -Encoding UTF8 -Value @'
param([switch]$NoPrerequisitePrompt, [switch]$SilentInstall, [switch]$ManualPrerequisites)
$logs = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/build-local'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Set-Content -LiteralPath (Join-Path $logs 'last-failure.txt') -Value 'Failed at: preflight'
exit 1
'@
    Set-Content -LiteralPath (Join-Path $cmdScripts 'BuildLocalFailure.ps1') -Encoding UTF8 -Value @'
param([string]$FailureMarker)
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'presenter-called') -Value 'called'
'@
    foreach ($name in @('NoPrerequisitePrompt', 'SilentInstall', 'ManualPrerequisites')) {
        foreach ($value in @('$true', '$false')) {
            $called = Join-Path $cmdScripts 'presenter-called'
            if (Test-Path -LiteralPath $called) { Remove-Item -LiteralPath $called }
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = $env:ComSpec
            $start.Arguments = '/d /c ""' + (Join-Path $cmdRoot 'build-local.cmd') + '" -' + $name + ':' + $value + '"'
            $start.UseShellExecute = $false; $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
            $child = [Diagnostics.Process]::Start($start)
            try {
                $child.StandardInput.Close()
                $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
                if (!$child.WaitForExit(15000)) { $child.Kill(); throw 'CMD suppression fixture waited for input' }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                Assert-Equal 1 $child.ExitCode "CMD -${name}:$value preserves build failure exit; $output"
                Assert-Equal ($value -eq '$false') (Test-Path -LiteralPath $called) "CMD -${name}:$value respects explicit switch value"
            }
            finally { $child.Dispose() }
        }
    }
    Write-Host 'PASS: real CMD :$true suppression skips acknowledgement and :$false preserves default behavior.'
    Write-Host 'PASS: typed policy/reboot outcomes get one native acknowledgement; generic failures, automation and suppression display none.'
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Remove-SetupUiPrerequisiteTempDirectory -Path $testRoot
}
