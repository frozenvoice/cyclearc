#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $repoRoot 'scripts/Verify-InstalledUpdate.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors | Out-String) }
# Extract only pure planning/evidence functions. Never execute the installer script or its preconditions.
foreach ($functionName in @('Get-InstalledVerificationBuilds', 'Get-InstalledVerificationSourceRoot', 'Test-FailedStartRecoveryEvidence')) {
    $functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if (!$functionAst) { throw "Missing installed verification function $functionName" }
    . ([scriptblock]::Create($functionAst.Extent.Text))
}
function Assert-InstalledScript([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "Installed update script regression failed: $Message" }
}
$installation = 'C:\Disposable\CycleArc'
$failingBuild = @{ Version = '0.10.1'; Sha256 = ('F' * 64) }
foreach ($previousVersion in @('0.9.1', '0.10.0')) {
    $previousBuild = @{ Version = $previousVersion; Sha256 = ('A' * 64) }
    $outcome = [pscustomobject]@{ Marker = 'completed'; Text = 'restored'; FailureText = 'System.IO.IOException: CycleArc exited before becoming ready.' }
    $job = [pscustomobject]@{
        TargetVersion = $failingBuild.Version
        TargetExecutableSha256 = $failingBuild.Sha256
        Snapshot = [pscustomobject]@{ Version = $previousBuild.Version; ExecutableSha256 = $previousBuild.Sha256; InstallationRoot = $installation }
    }
    Assert-InstalledScript (Test-FailedStartRecoveryEvidence $outcome $job $previousBuild $failingBuild $installation) "valid restoration to $previousVersion was rejected"
    Assert-InstalledScript (!(Test-FailedStartRecoveryEvidence $null $job $previousBuild $failingBuild $installation)) 'a still-present previous executable before replacement must not count as recovery'
    foreach ($invalid in @(
        @{ Marker = 'completed'; Text = 'updated'; FailureText = $outcome.FailureText },
        @{ Marker = 'failed'; Text = 'recovery-required'; FailureText = $outcome.FailureText },
        @{ Marker = 'completed'; Text = 'restored'; FailureText = '' },
        @{ Marker = 'completed'; Text = 'restored'; FailureText = 'The updater could not replace the application.' },
        @{ Marker = 'completed'; Text = 'restored'; FailureText = $null }
    )) {
        Assert-InstalledScript (!(Test-FailedStartRecoveryEvidence ([pscustomobject]$invalid) $job $previousBuild $failingBuild $installation)) 'update/apply failure or incomplete evidence must not prove an actual new-desktop startup rollback'
    }
    foreach ($change in @('targetVersion', 'targetHash', 'snapshotVersion', 'snapshotHash', 'installationRoot')) {
        $wrongJob = $job | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        switch ($change) {
            'targetVersion' { $wrongJob.TargetVersion = '0.10.2' }
            'targetHash' { $wrongJob.TargetExecutableSha256 = 'B' * 64 }
            'snapshotVersion' { $wrongJob.Snapshot.Version = '0.8.0' }
            'snapshotHash' { $wrongJob.Snapshot.ExecutableSha256 = 'B' * 64 }
            'installationRoot' { $wrongJob.Snapshot.InstallationRoot = 'C:\OtherInstallation' }
        }
        Assert-InstalledScript (!(Test-FailedStartRecoveryEvidence $outcome $wrongJob $previousBuild $failingBuild $installation)) "an unrelated recovery job with wrong $change was accepted"
    }
    Assert-InstalledScript (!(Test-FailedStartRecoveryEvidence $outcome ([pscustomobject]@{}) $previousBuild $failingBuild $installation)) 'missing recovery job metadata was accepted'
}
$default = Get-InstalledVerificationBuilds
Assert-InstalledScript ($default.A.Version -eq '9.9.1' -and $default.B.Version -eq '9.9.2' -and $default.Fail.Version -eq '9.9.3') 'the default current-runtime fixture changed'
$migration = Get-InstalledVerificationBuilds -CrossRuntime -MigrationVersion '0.10.0'
Assert-InstalledScript ($migration.A.Version -eq '0.9.1' -and $migration.B.Version -eq '0.10.0' -and $migration.Fail.Version -eq '0.10.1') 'the cross-runtime fixture must use actual baseline and migrated versions'
Assert-InstalledScript ($migration.A.Constants -contains 'CYCLEARC_TEST_E2E' -and $migration.Fail.Constants -contains 'CYCLEARC_TEST_FAIL_STARTUP') 'baseline must drive the real update; failure must exercise recovery'
foreach ($invalid in @('0.9.1', '0.8.9', '0.10.0-preview.1', 'bad')) {
    $rejected = $false
    try { Get-InstalledVerificationBuilds -CrossRuntime -MigrationVersion $invalid | Out-Null } catch { $rejected = $true }
    Assert-InstalledScript $rejected "invalid migration version $invalid was accepted"
}
foreach ($name in @('A', 'B', 'Fail')) {
    $selected = Get-InstalledVerificationSourceRoot -BuildName $name -CurrentRepoRoot 'current-checkout' -HistoricalRepoRoot 'net8-checkout'
    $expected = if ($name -eq 'A') { 'net8-checkout' } else { 'current-checkout' }
    Assert-InstalledScript ($selected -eq $expected) "build $name selected the wrong source runtime"
    Assert-InstalledScript ((Get-InstalledVerificationSourceRoot -BuildName $name -CurrentRepoRoot 'current-checkout') -eq 'current-checkout') "default build $name selected historical source"
}
$text = Get-Content -LiteralPath $scriptPath -Raw
Assert-InstalledScript ($text.Contains("`$BuildRoot = Join-Path `$RepoRoot 'publish/.installed-e2e'")) 'payload outputs must remain under repository publish'
Assert-InstalledScript ($text.Contains('Push-Location -LiteralPath $sourceRoot') -and $text.Contains('finally { Pop-Location }')) 'publishing must evaluate the correct source global.json and restore cwd'
Assert-InstalledScript ($text.Contains("-MinimumVersion '8.0.100'")) 'historical source must select stable .NET 8'
Assert-InstalledScript ($text.Contains('if (!$ConfirmDisposableEnvironment)') -and $text.Contains('CycleArc data already exists')) 'disposable-environment safeguards must remain'
Assert-InstalledScript ($text.Contains("Invoke-FailedStartRecovery -PreviousBuild `$Builds.A -EvidencePrefix 'crossRuntimeRecovery'") -and
    $text.IndexOf("Invoke-FailedStartRecovery -PreviousBuild `$Builds.A") -lt $text.IndexOf('Starting the installed app and driving the real in-app update to build B')) 'cross-runtime rollback must precede the successful baseline upgrade'
Assert-InstalledScript ($text.Contains("Invoke-FailedStartRecovery -PreviousBuild `$Builds.B -EvidencePrefix 'recovery'")) 'current-runtime rollback must use the same strong recovery verification'
Assert-InstalledScript ($text.Contains("@('--desktop-shutdown')")) 'phase transitions must stop the recovered installed desktop gracefully'
Write-Host 'PASS: installed E2E version planning, .NET 8 baseline routing, bound failed-start recovery evidence, publish scope and disposable guards.'
