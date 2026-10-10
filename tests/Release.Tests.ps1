#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/Release.ps1') -LoadOnly

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "ASSERT FAILED: $Message" }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "ASSERT FAILED: $Message (expected '$Expected', got '$Actual')"
    }
}

function Assert-Throws([scriptblock]$Action, [string]$MessageFragment) {
    $thrown = $false
    try { & $Action }
    catch {
        $thrown = $true
        if ($MessageFragment -and $_.Exception.Message -notlike "*$MessageFragment*") {
            throw "ASSERT FAILED: expected '$MessageFragment' in '$($_.Exception.Message)' at $($_.ScriptStackTrace)"
        }
    }
    if (!$thrown) { throw "ASSERT FAILED: expected an exception containing '$MessageFragment'" }
}

$shaA = 'a' * 40
$shaB = 'b' * 40
$hashA = '1' * 64
$hashB = '2' * 64

Assert-Equal '0.5.7' (Assert-ReleaseVersion '0.5.7') 'plain semantic versions are accepted'
Assert-Equal 'v0.5.7' (Get-ReleaseTagName '0.5.7') 'release tags use the v prefix'
Assert-Throws { Assert-ReleaseVersion 'v0.5.7' } 'plain semantic version'
Assert-Throws { Assert-ReleaseVersion '0.5' } 'plain semantic version'

# Mock only the native boundary to verify the gh JSON contract without network access.
$nativeInvoker = (Get-Command Invoke-NativeCommand -CommandType Function).ScriptBlock
$observedGhArguments = @()
function Invoke-NativeCommand {
    param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)
    $script:observedGhArguments = @($Arguments)
    [pscustomobject]@{
        ExitCode = 0
        Output = '{"isDraft":true,"tagName":"v0.5.7","targetCommitish":"commit","assets":[]}'
    }
}
$snapshot = Get-ReleaseSnapshot -RepositoryName 'frozenvoice/cyclearc' -TagName 'v0.5.7'
Assert-Equal 'v0.5.7' $snapshot.tagName 'mocked gh release snapshot is parsed'
$jsonIndex = [array]::IndexOf([array]$observedGhArguments, '--json')
Assert-True ($jsonIndex -ge 0) 'release inspection requests JSON output'
Assert-True ($observedGhArguments[$jsonIndex + 1] -match 'targetCommitish') 'release inspection requests targetCommitish'
Assert-True ($observedGhArguments[$jsonIndex + 1] -notmatch 'isLatest') 'release inspection avoids unsupported isLatest'
Set-Item -Path Function:\Invoke-NativeCommand -Value $nativeInvoker

$head = [pscustomobject]@{ Sha = $shaA; Ref = 'refs/heads/main' }
Assert-Equal $shaA (Assert-CommitIsRemoteHead -RemoteHeads @($head) -CommitSha $shaA).Sha 'the exact remote head is accepted'
Assert-Throws { Assert-CommitIsRemoteHead -RemoteHeads @($head) -CommitSha $shaB } 'not the current SHA'
Assert-True ($null -eq (Get-TagTargetFromRecords -Records @() -TagName 'v0.5.7')) 'an absent tag has no target'
$tagRecords = @(
    [pscustomobject]@{ Sha = 'c' * 40; Ref = 'refs/tags/v0.5.7' },
    [pscustomobject]@{ Sha = $shaA; Ref = 'refs/tags/v0.5.7^{}' }
)
Assert-Equal $shaA (Get-TagTargetFromRecords -Records $tagRecords -TagName 'v0.5.7') 'annotated tags use their peeled commit'
Assert-Throws { Assert-TagTargets -TagName 'v0.5.7' -ExpectedSha $shaA -RemoteSha $shaB } 'expected'

$successfulRun = [pscustomobject]@{
    id = 101; head_sha = $shaA; event = 'workflow_dispatch'; status = 'completed'; conclusion = 'success'
    path = '.github/workflows/windows.yml'; run_attempt = 1
    head_repository = [pscustomobject]@{ id = 17; full_name = 'frozenvoice/cyclearc' }
}
function Assert-FixtureRun([object]$Run = $successfulRun, [long]$RunId = 101, [string]$Sha = $shaA) {
    Assert-SuccessfulWindowsFullRun -Run $Run -RunId $RunId -RepositoryName 'frozenvoice/cyclearc' -CommitSha $Sha
}
Assert-Equal 101 (Assert-FixtureRun).id 'only the explicitly selected successful manual full run is accepted'
Assert-Throws { Assert-FixtureRun -RunId 0 } 'explicit positive'
Assert-Throws { Assert-FixtureRun -RunId 102 } 'matching the returned run'
Assert-Throws { Assert-FixtureRun -Sha $shaB } 'target SHA'
Assert-Throws { Invoke-Release -VersionValue '0.6.0' -Preflight } 'explicit positive -FullRunId'
Assert-Throws { Invoke-Release -VersionValue '0.6.0' -FullRunId 101 -WorkflowFile '.github/workflows/windows-e2e.yml' -Preflight } 'requires the full'
foreach ($state in @('failure', 'cancelled', 'skipped', 'neutral', 'timed_out', 'action_required')) {
    $badRun = $successfulRun.PSObject.Copy()
    $badRun.conclusion = $state
    Assert-Throws { Assert-FixtureRun -Run $badRun } 'not a completed success'
}
$pendingRun = $successfulRun.PSObject.Copy()
$pendingRun.status = 'in_progress'
Assert-Throws { Assert-FixtureRun -Run $pendingRun } 'not a completed success'
$oldPush = $successfulRun.PSObject.Copy()
$oldPush.event = 'push'
Assert-Throws { Assert-FixtureRun -Run $oldPush } 'workflow_dispatch'
$otherWorkflow = $successfulRun.PSObject.Copy()
$otherWorkflow.path = '.github/workflows/windows-build-local.yml'
Assert-Throws { Assert-FixtureRun -Run $otherWorkflow } 'windows.yml'

$fullJobs = @('build', 'managed-setup-install', 'setup-shortcut-choices', 'portable-distribution', 'managed-startup') | ForEach-Object {
    [pscustomobject]@{ name = $_; run_id = 101; run_attempt = 1; status = 'completed'; conclusion = 'success'
        started_at = '2026-09-15T10:00:00Z'; completed_at = '2026-09-15T10:10:00Z' }
}
Assert-WindowsFullJobs -Jobs $fullJobs -RunId 101 -Attempt 1
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0], $fullJobs[1]) -RunId 101 -Attempt 1 } 'required job'
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs + $fullJobs[0]) -RunId 101 -Attempt 1 } 'exactly one'
Assert-Throws { Assert-WindowsFullJobs -Jobs $fullJobs -RunId 101 -Attempt 2 } 'not a completed success'
foreach ($state in @('failure', 'cancelled', 'skipped', 'neutral')) {
    $badJob = $fullJobs[2].PSObject.Copy()
    $badJob.conclusion = $state
    Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0], $fullJobs[1], $badJob, $fullJobs[3], $fullJobs[4]) -RunId 101 -Attempt 1 } 'not a completed success'
}
# Actual execution of the registered managed startup command is a mandatory,
# independently successful job from this exact run attempt.
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0..3]) -RunId 101 -Attempt 1 } "required job 'managed-startup'"
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs + $fullJobs[4]) -RunId 101 -Attempt 1 } "required job 'managed-startup'"
foreach ($state in @('failure', 'cancelled', 'skipped', 'neutral')) {
    $badStartup = $fullJobs[4].PSObject.Copy()
    $badStartup.conclusion = $state
    Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0..3] + $badStartup) -RunId 101 -Attempt 1 } "job 'managed-startup' is not a completed success"
}
$previousStartup = $fullJobs[4].PSObject.Copy()
$previousStartup.run_attempt = 2
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0..3] + $previousStartup) -RunId 101 -Attempt 1 } "job 'managed-startup' is not a completed success"
$otherRunStartup = $fullJobs[4].PSObject.Copy()
$otherRunStartup.run_id = 102
Assert-Throws { Assert-WindowsFullJobs -Jobs @($fullJobs[0..3] + $otherRunStartup) -RunId 101 -Attempt 1 } "job 'managed-startup' is not a completed success"

$fullArtifact = [pscustomobject]@{
    id = 555; name = 'CycleArc-win-x64'; expired = $false; size_in_bytes = 123; created_at = '2026-09-15T10:05:00Z'
    workflow_run = [pscustomobject]@{ id = 101; head_sha = $shaA; head_repository_id = 17 }
}
Assert-Equal 555 (Select-WindowsFullArtifact -Artifacts @($fullArtifact) -Run $successfulRun -Jobs $fullJobs).id 'exact artifact identity is retained'
Assert-Throws { Select-WindowsFullArtifact -Artifacts @() -Run $successfulRun -Jobs $fullJobs } 'exactly one'
Assert-Throws { Select-WindowsFullArtifact -Artifacts @($fullArtifact, $fullArtifact) -Run $successfulRun -Jobs $fullJobs } 'exactly one'
$expiredArtifact = $fullArtifact.PSObject.Copy()
$expiredArtifact.expired = $true
Assert-Throws { Select-WindowsFullArtifact -Artifacts @($expiredArtifact) -Run $successfulRun -Jobs $fullJobs } 'expired, empty'
$wrongArtifact = $fullArtifact.PSObject.Copy()
$wrongArtifact.workflow_run = [pscustomobject]@{ id = 101; head_sha = $shaB; head_repository_id = 17 }
Assert-Throws { Select-WindowsFullArtifact -Artifacts @($wrongArtifact) -Run $successfulRun -Jobs $fullJobs } 'target SHA'
$emptyArtifact = $fullArtifact.PSObject.Copy()
$emptyArtifact.size_in_bytes = 0
Assert-Throws { Select-WindowsFullArtifact -Artifacts @($emptyArtifact) -Run $successfulRun -Jobs $fullJobs } 'expired, empty'
$staleArtifact = $fullArtifact.PSObject.Copy()
$staleArtifact.created_at = '2026-09-15T09:55:00Z'
Assert-Throws { Select-WindowsFullArtifact -Artifacts @($staleArtifact) -Run $successfulRun -Jobs $fullJobs } 'selected full run attempt'

# API timestamps must keep their original instant across DateKind defaults, offsets,
# and release-machine culture. Only the native command boundary is mocked.
$nativeBeforeTimestampTests = (Get-Command Invoke-NativeCommand -CommandType Function).ScriptBlock
$cultureBeforeTimestampTests = [Globalization.CultureInfo]::CurrentCulture
$converterBeforeTimestampTests = Get-Command ConvertFrom-Json
try {
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $script:timestampJson = ''
    function Invoke-NativeCommand {
        param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)
        [pscustomobject]@{ ExitCode = 0; Output = $script:timestampJson }
    }
    $offsetArtifact = $fullArtifact.PSObject.Copy()
    $offsetArtifact.created_at = '2026-09-15T19:05:00.1234567+09:00'
    $offsetJobs = @($fullJobs | ForEach-Object { $_.PSObject.Copy() })
    # The job timestamps deliberately use two other offsets for the same UTC window.
    $offsetJobs[0].started_at = '2026-09-15T06:00:00-04:00'
    $offsetJobs[0].completed_at = '2026-09-15T10:10:00Z'
    $script:timestampJson = @{ artifact = $offsetArtifact; jobs = $offsetJobs } | ConvertTo-Json -Depth 10
    $apiEvidence = Invoke-GhJson -Arguments @('api', 'synthetic-timestamp-evidence')
    if ($converterBeforeTimestampTests.Parameters.ContainsKey('DateKind')) {
        Assert-True ($apiEvidence.artifact.created_at -is [string]) 'modern JSON conversion preserves timestamp strings'
        Assert-Equal $offsetArtifact.created_at $apiEvidence.artifact.created_at 'API offset and fractional precision survive JSON conversion'
    }
    Assert-Equal 555 (Select-WindowsFullArtifact -Artifacts @($apiEvidence.artifact) -Run $successfulRun -Jobs $apiEvidence.jobs).id `
        'mixed original offsets pass within the successful build window under a non-US culture'
    Assert-Equal ([DateTimeOffset]::Parse('2026-09-15T10:05:00.1234567Z').UtcTicks) `
        (ConvertTo-ReleaseTimestamp -Value $apiEvidence.artifact.created_at).UtcTicks 'timestamp normalization preserves the exact UTC instant'
    $apiEvidence.artifact.created_at = '2026-09-15T18:59:59.9999999+09:00'
    Assert-Throws { Select-WindowsFullArtifact -Artifacts @($apiEvidence.artifact) -Run $successfulRun -Jobs $apiEvidence.jobs } 'selected full run attempt'
    $apiEvidence.artifact.created_at = '2026-09-15T19:10:00.0000001+09:00'
    Assert-Throws { Select-WindowsFullArtifact -Artifacts @($apiEvidence.artifact) -Run $successfulRun -Jobs $apiEvidence.jobs } 'selected full run attempt'
    $apiEvidence.artifact.created_at = '2026-09-15T10:05:00'
    Assert-Throws { Select-WindowsFullArtifact -Artifacts @($apiEvidence.artifact) -Run $successfulRun -Jobs $apiEvidence.jobs } 'timestamps are missing or invalid'
    Assert-Throws { ConvertTo-ReleaseTimestamp -Value ([DateTime]::SpecifyKind([DateTime]::Now, [DateTimeKind]::Unspecified)) } 'no timezone'
    $typedOffset = [DateTimeOffset]::Parse('2026-09-15T19:05:00.1234567+09:00', [Globalization.CultureInfo]::InvariantCulture)
    Assert-Equal $typedOffset.UtcTicks (ConvertTo-ReleaseTimestamp -Value $typedOffset).UtcTicks 'typed DateTimeOffset keeps its offset and fractional precision'
    Assert-Equal $typedOffset.UtcTicks (ConvertTo-ReleaseTimestamp -Value $typedOffset.UtcDateTime).UtcTicks 'legacy typed UTC DateTime preserves its instant'
    Assert-Equal $typedOffset.UtcTicks (ConvertTo-ReleaseTimestamp -Value $typedOffset.LocalDateTime).UtcTicks 'legacy typed local DateTime preserves its instant'

    # Simulate a PowerShell 7.0-7.4 command surface without DateKind. The production
    # capability branch must omit that parameter and correctly handle typed dates.
    $script:legacyJsonCalls = 0
    function ConvertFrom-Json {
        param([Parameter(ValueFromPipeline)][string]$InputObject, [int]$Depth = 30)
        process {
            $script:legacyJsonCalls++
            Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $InputObject -Depth $Depth
        }
    }
    $legacyEvidence = Invoke-GhJson -Arguments @('api', 'synthetic-legacy-timestamp-evidence')
    Assert-Equal 1 $script:legacyJsonCalls 'older command surface is invoked without unsupported DateKind'
    Assert-True ($legacyEvidence.artifact.created_at -is [DateTime]) 'legacy JSON conversion returns typed dates'
    Assert-Equal 555 (Select-WindowsFullArtifact -Artifacts @($legacyEvidence.artifact) -Run $successfulRun -Jobs $legacyEvidence.jobs).id `
        'legacy typed dates retain their different original offsets under a non-US culture'
    Assert-Equal $typedOffset.UtcTicks (ConvertTo-ReleaseTimestamp -Value $legacyEvidence.artifact.created_at).UtcTicks `
        'legacy JSON conversion retains the original UTC instant and subsecond precision'
    $legacyEvidence.artifact.created_at = $typedOffset.UtcDateTime.AddMinutes(-10)
    Assert-Throws { Select-WindowsFullArtifact -Artifacts @($legacyEvidence.artifact) -Run $successfulRun -Jobs $legacyEvidence.jobs } 'selected full run attempt'
}
finally {
    [Globalization.CultureInfo]::CurrentCulture = $cultureBeforeTimestampTests
    Set-Item -Path Function:\Invoke-NativeCommand -Value $nativeBeforeTimestampTests
    if ($converterBeforeTimestampTests.CommandType -eq 'Function') {
        Set-Item -Path Function:\ConvertFrom-Json -Value $converterBeforeTimestampTests.ScriptBlock
    }
    else { Remove-Item -Path Function:\ConvertFrom-Json -ErrorAction SilentlyContinue }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-release-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $exePath = Join-Path $testRoot 'CycleArc.exe'
    [IO.File]::WriteAllBytes($exePath, [byte[]](1, 2, 3, 4))
    $manifestPath = New-ChecksumManifest -ExecutablePath $exePath
    Assert-ChecksumManifest -ManifestPath $manifestPath -ExecutablePath $exePath
    $localAssets = Get-LocalAssetMap -Paths @($exePath, $manifestPath)
    Assert-Equal 2 $localAssets.Count 'local asset map includes the executable and manifest'

    $release = [pscustomobject]@{
        isDraft = $true
        tagName = 'v0.5.7'
        assets = @(
            [pscustomobject]@{ name = 'CycleArc.exe'; size = $localAssets['CycleArc.exe'].Size; digest = $localAssets['CycleArc.exe'].Digest },
            [pscustomobject]@{ name = 'SHA256SUMS.txt'; size = $localAssets['SHA256SUMS.txt'].Size; digest = $localAssets['SHA256SUMS.txt'].Digest }
        )
    }
    Assert-True (Assert-ReleaseAssets -Release $release -ExpectedAssets $localAssets -RequireComplete) 'matching GitHub asset sizes and digests pass'
    $missing = [pscustomobject]@{ isDraft = $true; assets = @($release.assets[0]) }
    Assert-True (Assert-ReleaseAssets -Release $missing -ExpectedAssets $localAssets) 'a draft may be missing an expected asset before upload'
    Assert-Throws { Assert-ReleaseAssets -Release $missing -ExpectedAssets $localAssets -RequireComplete } 'missing expected asset'
    $extra = [pscustomobject]@{ isDraft = $true; assets = @($release.assets + [pscustomobject]@{ name = 'old.zip'; size = 1; digest = "sha256:$hashA" }) }
    Assert-Throws { Assert-ReleaseAssetNames -Release $extra -AllowedNames @('CycleArc.exe', 'SHA256SUMS.txt') } 'unexpected assets'
    $badDigest = [pscustomobject]@{ isDraft = $true; assets = @($release.assets[0], [pscustomobject]@{ name = 'SHA256SUMS.txt'; size = $localAssets['SHA256SUMS.txt'].Size; digest = "sha256:$hashB" }) }
    Assert-Throws { Assert-ReleaseAssets -Release $badDigest -ExpectedAssets $localAssets -RequireComplete } 'digest'
    $missingDigest = [pscustomobject]@{ isDraft = $true; assets = @($release.assets[0], [pscustomobject]@{ name = 'SHA256SUMS.txt'; size = $localAssets['SHA256SUMS.txt'].Size }) }
    Assert-Throws { Assert-ReleaseAssets -Release $missingDigest -ExpectedAssets $localAssets -RequireComplete } 'no GitHub SHA-256 digest'
    Set-Content -LiteralPath $manifestPath -Value ((Get-Content -Raw $manifestPath) + 'tampered') -NoNewline
    Assert-Throws { Assert-ChecksumManifest -ManifestPath $manifestPath -ExecutablePath $exePath } 'one SHA-256 entry'

function New-PortableFixtureArchive {
    param([string]$Path, [string[]]$EntryNames = @('CycleArc.exe'), [byte[]]$Bytes = [byte[]](1, 2, 3, 4))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $EntryNames | ForEach-Object {
            $entryStream = $zip.CreateEntry($_).Open()
            try { $entryStream.Write($Bytes, 0, $Bytes.Length) }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $zip.Dispose(); $stream.Dispose() }
}

    # The CI artifact now contains installer, full package, portable ZIP, feed, and a
    # checksum entry for every shipped asset. Exercise the feed and package
    # checks in isolation so a malformed or missing artifact fails before gh.
    $packaged = Join-Path $testRoot 'packaged'
    New-Item -ItemType Directory -Path $packaged -Force | Out-Null
    $setup = Join-Path $packaged 'CycleArc-Setup.exe'
    [IO.File]::WriteAllBytes($setup, [byte[]](9, 8, 7))
    $fullName = 'CycleArc-0.6.0-full.nupkg'
    $fullPath = Join-Path $packaged $fullName
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('lib/net10.0/CycleArc.exe')
        $entryStream = $entry.Open()
        try { $entryStream.Write([byte[]](1, 2, 3, 4), 0, 4) }
        finally { $entryStream.Dispose() }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
    $feedPath = Join-Path $packaged 'releases.win.json'
    $fullSha1 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA1).Hash
    $fullSha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    @{ Assets = @(@{ PackageId = 'CycleArc'; Version = '0.6.0'; Type = 'Full'; FileName = $fullName; SHA1 = $fullSha1; SHA256 = $fullSha256; Size = (Get-Item $fullPath).Length }) } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $feedPath -Encoding utf8
    # Read actual Windows PE metadata without compiling or launching a test program.
    $realExe = @(Get-Command pwsh -CommandType Application)[0].Source
    $realVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($realExe).FileVersion ?? '').Trim()
    Assert-True (![string]::IsNullOrWhiteSpace($realVersion)) 'Windows PowerShell executable carries real PE version metadata'
    Assert-ReleaseExecutableVersion -Path $realExe -ExpectedFileVersion $realVersion
    Assert-Throws { Assert-ReleaseExecutableVersion -Path $realExe -ExpectedFileVersion '99.0.0.0' } 'FileVersion'
    $realBytes = [IO.File]::ReadAllBytes($realExe)
    $realHash = (Get-FileHash -LiteralPath $realExe -Algorithm SHA256).Hash
    $realZip = Join-Path $testRoot 'versioned-portable.zip'
    New-PortableFixtureArchive -Path $realZip -Bytes $realBytes
    Assert-Throws { Get-PortableArchiveExeSha256 -ArchivePath $realZip -ExpectedFileVersion '99.0.0.0' -ExpectedSha256 $realHash } 'FileVersion'
    Assert-Throws { Get-PortableArchiveExeSha256 -ArchivePath $realZip -ExpectedSha256 ('f' * 64) } 'does not match original publish'
    $realPackage = Join-Path $testRoot 'versioned-full.nupkg'
    New-PortableFixtureArchive -Path $realPackage -EntryNames @('lib/net10.0/CycleArc.exe') -Bytes $realBytes
    Assert-Throws { Assert-FullPackageFileVersion -PackagePath $realPackage -ExpectedFileVersion '99.0.0.0' -ExpectedSha256 $realHash } 'FileVersion'
    Assert-Throws { Assert-FullPackageFileVersion -PackagePath $realPackage -ExpectedFileVersion $realVersion -ExpectedSha256 ('f' * 64) } 'does not match original publish'
    # PowerShell's external-runtime host must never pass as a self-contained CycleArc portable.
    Assert-Throws { Get-PortableArchiveExeSha256 -ArchivePath $realZip -ExpectedFileVersion $realVersion -ExpectedSha256 $realHash } 'bundle'
    Assert-Throws { Assert-FullPackageFileVersion -PackagePath $realPackage -ExpectedFileVersion $realVersion -ExpectedSha256 $realHash } 'bundle'

    $portable = Join-Path $packaged 'CycleArc-0.6.0-win-x64-portable.zip'
    New-PortableFixtureArchive -Path $portable
    $assetFiles = @($setup, $fullPath, $portable, $feedPath)
    $manifestLines = foreach ($asset in $assetFiles) {
        "$( (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() )  $(Split-Path -Leaf $asset)"
    }
    $packagedManifest = Join-Path $packaged 'SHA256SUMS.txt'
    $manifestLines | Set-Content -LiteralPath $packagedManifest -Encoding utf8
    Assert-PackagedReleaseFeed -FeedPath $feedPath -VersionValue '0.6.0' -PackageName $fullName -PackagePath $fullPath | Out-Null
    $feedText = [IO.File]::ReadAllText($feedPath)
    $fullBytes = [IO.File]::ReadAllBytes($fullPath)
    foreach ($field in @('SHA1', 'SHA256')) {
        $badFeed = $feedText | ConvertFrom-Json
        $badFeed.Assets[0].$field = if ($field -eq 'SHA1') { '0' * 40 } else { '0' * 64 }
        $badFeed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $feedPath -Encoding utf8
        Assert-Throws { Assert-PackagedReleaseFeed -FeedPath $feedPath -VersionValue '0.6.0' -PackageName $fullName -PackagePath $fullPath } $field
    }
    [IO.File]::WriteAllText($feedPath, $feedText)
    Assert-Throws {
        Remove-Item -LiteralPath $feedPath -Force
        Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' -PublishedExecutablePath $exePath
    } 'release feed'
    [IO.File]::WriteAllText($feedPath, $feedText)
    [IO.File]::WriteAllText($feedPath, $feedText.Replace('"0.6.0"', '"0.6.1"'))
    Assert-Throws { Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' -PublishedExecutablePath $exePath } 'Release feed'
    [IO.File]::WriteAllText($feedPath, $feedText)
    Assert-Throws {
        Remove-Item -LiteralPath $fullPath -Force
        Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' -PublishedExecutablePath $exePath
    } 'full package'
    [IO.File]::WriteAllBytes($fullPath, $fullBytes)
    [IO.File]::WriteAllBytes($fullPath, [byte[]](5, 4, 3, 2))
    Assert-Throws { Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' -PublishedExecutablePath $exePath } 'Size'

    $owned = Join-Path $testRoot 'publish/.release-staging/run-1'
    New-Item -ItemType Directory -Path $owned -Force | Out-Null
    Assert-OwnedDirectory -RepoRoot $testRoot -Target $owned
    Assert-Throws { Assert-OwnedDirectory -RepoRoot $testRoot -Target (Join-Path $testRoot '..') } 'outside the repository'

    Assert-Equal 'CycleArc-Setup.exe' (Get-ExpectedReleaseAssetNames -VersionValue '0.6.0')[0] 'setup asset name'
    $expectedAssets = @(Get-ExpectedReleaseAssetNames -VersionValue '0.6.0')
    Assert-True ($expectedAssets -contains 'CycleArc-0.6.0-full.nupkg') 'full package asset name'
    Assert-True ($expectedAssets -contains 'CycleArc-0.6.0-win-x64-portable.zip') 'portable asset name'
    Assert-True ($expectedAssets -contains 'releases.win.json') 'feed asset name'
    Assert-True ($expectedAssets -contains 'SHA256SUMS.txt') 'checksum asset name'

    $notesPath = Join-Path $testRoot 'notes.md'
    Set-Content -LiteralPath $notesPath -Value 'notes'
    Assert-Equal (Get-Item -LiteralPath $notesPath).FullName (Assert-ReleaseNotesForNewDraft -Release $null -NotesFile $notesPath -TagName 'v0.6.0') 'new drafts require a notes file'
    Assert-Throws { Assert-ReleaseNotesForNewDraft -Release $null -NotesFile '' -TagName 'v0.6.0' } 'notes file is required'
    Assert-True ([string]::IsNullOrWhiteSpace((Assert-ReleaseNotesForNewDraft -Release $release -NotesFile '' -TagName 'v0.6.0'))) 'existing releases do not require notes'

    $authInvoker = (Get-Command Invoke-NativeCommand -CommandType Function).ScriptBlock
    function Invoke-NativeCommand {
        param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)
        [pscustomobject]@{ ExitCode = 1; Output = 'not logged in' }
    }
    Assert-Throws { Assert-GitHubCliAuth } 'not authenticated'
    Set-Item -Path Function:\Invoke-NativeCommand -Value $authInvoker
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}


# --- Required vs allowed release assets, exercised through the real decision path ---------
#
# These drive Invoke-Release -Preflight with only the external process boundary
# (Invoke-NativeCommand) replaced, so the asset-name, checksum, size and digest decisions
# are the production ones. Only PE version/dependency parsing is stubbed for the four-byte
# executable fixture; archive topology, all byte identity and asset checks remain live.
# Nothing here creates, edits or publishes a GitHub release.

$assetTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-release-assets-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $assetTestRoot | Out-Null
$nativeBeforeAssetTests = (Get-Command Invoke-NativeCommand -CommandType Function).ScriptBlock
$fileVersionBeforeAssetTests = (Get-Command Assert-ReleaseExecutableVersion -CommandType Function).ScriptBlock
$dependenciesBeforeAssetTests = (Get-Command Assert-SelfContainedReleaseApp -CommandType Function).ScriptBlock
try {
    $assetVersion = '0.6.0'
    $assetTag = "v$assetVersion"
    $script:fixtureCommit = 'd' * 40
    $script:fixtureTag = $assetTag
    $script:ghCommands = @()

    function New-StagedArtifact {
        param(
            [Parameter(Mandatory)][string]$Directory,
            [switch]$OmitOptional,
            [switch]$AddStrayFile,
            [switch]$OmitFeed,
            [switch]$OmitPortable
        )
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $Directory 'CycleArc-Setup.exe'), [byte[]](4, 5, 6, 7))
        $fullName = "CycleArc-$script:fixtureVersion-full.nupkg"
        $fullPath = Join-Path $Directory $fullName
        $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entryStream = $archive.CreateEntry('lib/net10.0/CycleArc.exe').Open()
            try { $entryStream.Write([byte[]](1, 2, 3, 4), 0, 4) } finally { $entryStream.Dispose() }
        }
        finally { $archive.Dispose(); $stream.Dispose() }
        if (!$OmitPortable) {
            New-PortableFixtureArchive -Path (Join-Path $Directory "CycleArc-$script:fixtureVersion-win-x64-portable.zip")
        }
        if (!$OmitFeed) {
            $feed = @{ Assets = @(@{
                PackageId = 'CycleArc'
                Version   = $script:fixtureVersion
                Type      = 'Full'
                FileName  = $fullName
                SHA1      = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA1).Hash
                SHA256    = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
                Size      = (Get-Item $fullPath).Length
            }) }
            $feed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Directory 'releases.win.json') -Encoding utf8
        }
        if (!$OmitOptional) {
            # Exactly the two extra outputs Package.ps1 already accepts next to the required five.
            @(@{ RelativeFileName = 'CycleArc-Setup.exe'; Type = 'Setup' }) |
                ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Directory 'assets.win.json') -Encoding utf8
            Set-Content -LiteralPath (Join-Path $Directory 'RELEASES') -Value "0000 $fullName 4" -Encoding utf8
        }
        if ($AddStrayFile) {
            Set-Content -LiteralPath (Join-Path $Directory 'leftover.zip') -Value 'stray' -Encoding utf8
        }
        $targets = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Name -cne 'SHA256SUMS.txt' })
        $lines = foreach ($asset in $targets) {
            "$((Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($asset.Name)"
        }
        $lines | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt') -Encoding utf8
    }

    function New-ReleaseAssetsFrom {
        param(
            [Parameter(Mandatory)][string]$Directory,
            [string[]]$Only,
            [string]$CorruptName,
            [string]$WrongSizeName
        )
        foreach ($file in Get-ChildItem -LiteralPath $Directory -File) {
            if ($Only -and $file.Name -cnotin $Only) { continue }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($file.Name -ceq $CorruptName) { $hash = 'f' * 64 }
            $size = [int64]$file.Length
            if ($file.Name -ceq $WrongSizeName) { $size = $size + 1 }
            [pscustomobject]@{ name = $file.Name; size = $size; digest = "sha256:$hash" }
        }
    }

    # The only boundary that leaves this process. Every call is recorded so a test can assert
    # that Preflight performed no remote change at all.
    function Invoke-NativeCommand {
        param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)
        $script:ghCommands += , (@($FilePath) + $Arguments)
        $joined = $Arguments -join ' '
        if ($FilePath -eq 'git') {
            if ($joined -match '^status ') { return [pscustomobject]@{ ExitCode = 0; Output = '' } }
            if ($joined -match '^rev-parse ') { return [pscustomobject]@{ ExitCode = 0; Output = $script:fixtureCommit } }
            if ($joined -match '^ls-remote --heads') {
                return [pscustomobject]@{ ExitCode = 0; Output = ($script:fixtureCommit + "`t" + 'refs/heads/main') }
            }
            if ($joined -match '^ls-remote --tags') {
                return [pscustomobject]@{ ExitCode = 0; Output = ($script:fixtureCommit + "`t" + "refs/tags/$script:fixtureTag") }
            }
            throw "Unexpected git call in a preflight test: $joined"
        }
        if ($FilePath -eq 'gh') {
            if ($joined -match '^auth status') { return [pscustomobject]@{ ExitCode = 0; Output = 'Logged in to github.com' } }
            if ($joined -match '^release view') {
                return [pscustomobject]@{ ExitCode = 0; Output = ($script:fixtureRelease | ConvertTo-Json -Depth 30) }
            }
            if ($joined -match '^api repos/.+/actions/runs/4242$') {
                $script:runReads++
                $run = @{
                    id = 4242; head_sha = $script:fixtureCommit; event = 'workflow_dispatch'; status = 'completed'
                    conclusion = 'success'; path = '.github/workflows/windows.yml'; run_attempt = 1
                    head_repository = @{ id = 17; full_name = 'frozenvoice/cyclearc' }
                }
                if ($script:fixtureGate -eq 'wrong-sha') { $run.head_sha = 'e' * 40 }
                if ($script:fixtureGate -eq 'old-push') { $run.event = 'push' }
                if ($script:fixtureGate -eq 'cancelled') { $run.conclusion = 'cancelled' }
                if ($script:fixtureGate -eq 'rerun-after-download' -and $script:runReads -gt 1) { $run.run_attempt = 2 }
                return [pscustomobject]@{ ExitCode = 0; Output = ($run | ConvertTo-Json -Depth 30) }
            }
            if ($joined -match '^api repos/.+/actions/runs/4242/attempts/(\d+)/jobs\?') {
                $attempt = [int]$Matches[1]
                $jobs = @(@('build', 'managed-setup-install', 'setup-shortcut-choices', 'portable-distribution', 'managed-startup') | ForEach-Object {
                    @{ name = $_; run_id = 4242; run_attempt = $attempt; status = 'completed'; conclusion = 'success'
                        started_at = '2026-09-15T10:00:00Z'; completed_at = '2026-09-15T10:10:00Z' }
                })
                if ($script:fixtureGate -eq 'missing-install-job') { $jobs = @($jobs[0], $jobs[2]) }
                if ($script:fixtureGate -eq 'failed-install-job') { $jobs[1].conclusion = 'failure' }
                if ($script:fixtureGate -eq 'skipped-shortcut-job') { $jobs[2].conclusion = 'skipped' }
                if ($script:fixtureGate -eq 'missing-portable-job') { $jobs = @($jobs | Where-Object { $_.name -cne 'portable-distribution' }) }
                if ($script:fixtureGate -eq 'failed-portable-job') { $jobs[3].conclusion = 'failure' }
                if ($script:fixtureGate -eq 'missing-startup-job') { $jobs = @($jobs[0..3]) }
                if ($script:fixtureGate -eq 'failed-startup-job') { $jobs[4].conclusion = 'failure' }
                if ($script:fixtureGate -eq 'skipped-startup-job') { $jobs[4].conclusion = 'skipped' }
                if ($script:fixtureGate -eq 'pending-startup-job') { $jobs[4].status = 'in_progress' }
                if ($script:fixtureGate -eq 'wrong-attempt-startup-job') { $jobs[4].run_attempt = $attempt + 1 }
                if ($script:fixtureGate -eq 'wrong-run-startup-job') { $jobs[4].run_id = 4243 }
                return [pscustomobject]@{ ExitCode = 0; Output = (@{ total_count = $jobs.Count; jobs = $jobs } | ConvertTo-Json -Depth 30) }
            }
            if ($joined -match '^api repos/.+/actions/runs/4242/artifacts\?') {
                $artifact = @{
                    id = 555; name = 'CycleArc-win-x64'; expired = $false; size_in_bytes = 123; created_at = '2026-09-15T10:05:00Z'
                    workflow_run = @{ id = 4242; head_sha = $script:fixtureCommit; head_repository_id = 17 }
                }
                if ($script:fixtureGate -eq 'wrong-artifact-sha') { $artifact.workflow_run.head_sha = 'e' * 40 }
                if ($script:fixtureGate -eq 'expired-artifact') { $artifact.expired = $true }
                if ($script:fixtureGate -eq 'stale-attempt-artifact') { $artifact.created_at = '2026-09-15T09:55:00Z' }
                if ($script:fixtureGate -eq 'artifact-replaced' -and $script:runReads -gt 1) { $artifact.id = 556 }
                $published = @{} + $artifact
                $published.id = 666
                $published.name = 'CycleArc-published-win-x64'
                if ($script:fixtureGate -eq 'published-replaced' -and $script:runReads -gt 1) { $published.id = 667 }
                if ($script:fixtureGate -eq 'published-expired') { $published.expired = $true }
                if ($script:fixtureGate -eq 'published-wrong-sha') {
                    $published.workflow_run = @{ id = 4242; head_sha = 'e' * 40; head_repository_id = 17 }
                }
                if ($script:fixtureGate -eq 'published-stale') { $published.created_at = '2026-09-15T09:55:00Z' }
                $artifacts = @($artifact, $published)
                if ($script:fixtureGate -eq 'published-missing') { $artifacts = @($artifact) }
                if ($script:fixtureGate -eq 'published-duplicate') { $artifacts += $published }
                if ($script:fixtureGate -eq 'missing-artifact') { $artifacts = @() }
                if ($script:fixtureGate -eq 'duplicate-artifact') { $artifacts = @($artifact, $artifact) }
                return [pscustomobject]@{ ExitCode = 0; Output = (@{ total_count = $artifacts.Count; artifacts = $artifacts } | ConvertTo-Json -Depth 30) }
            }
            if ($joined -match '^run download') {
                Assert-Equal '4242' $Arguments[2] 'download is pinned to the explicitly selected full run'
                $name = $Arguments[[array]::IndexOf([array]$Arguments, '--name') + 1]
                Assert-True ($name -cin @('CycleArc-win-x64', 'CycleArc-published-win-x64')) 'download is pinned to a uniquely checked artifact name'
                $dirIndex = [array]::IndexOf([array]$Arguments, '--dir')
                if ($dirIndex -lt 0) { throw 'gh run download was called without --dir' }
                if ($name -ceq 'CycleArc-win-x64') {
                    Copy-Item -Path (Join-Path $script:fixtureArtifact '*') -Destination $Arguments[$dirIndex + 1] -Force -Recurse
                }
                else {
                    Copy-Item -Path (Join-Path $script:fixturePublished '*') -Destination $Arguments[$dirIndex + 1] -Force -Recurse
                }
                return [pscustomobject]@{ ExitCode = 0; Output = '' }
            }
            if ($joined -match '^api repos/.+/releases/latest') {
                return [pscustomobject]@{ ExitCode = 0; Output = (@{ tag_name = $script:fixtureTag } | ConvertTo-Json) }
            }
            throw "Unexpected gh call in a preflight test: $joined"
        }
        throw "Unexpected process in a preflight test: $FilePath"
    }

    # Version and dependency readers have direct coverage; every archive/hash decision stays live.
    function Assert-ReleaseExecutableVersion {
        param([string]$Path, [string]$ExpectedFileVersion)
        if ($ExpectedFileVersion -cne '0.6.0.0') { throw 'Unexpected fixture FileVersion' }
    }
    function Assert-SelfContainedReleaseApp { param([string]$Path) }

    $script:fixtureVersion = $assetVersion

    function Invoke-PreflightFixture {
        param(
            [Parameter(Mandatory)][string]$Name,
            [switch]$OmitOptional,
            [switch]$AddStrayFile,
            [switch]$OmitFeed,
            [switch]$OmitPortable,
            [switch]$Public,
            [string]$CorruptName,
            [string]$WrongSizeName,
            [string[]]$ReleaseOnly,
            [object[]]$ExtraReleaseAssets,
            [string]$GateFailure
        )
        $caseRoot = Join-Path $assetTestRoot $Name
        $script:fixtureArtifact = Join-Path $caseRoot 'artifact'
        New-StagedArtifact -Directory $script:fixtureArtifact -OmitOptional:$OmitOptional `
            -AddStrayFile:$AddStrayFile -OmitFeed:$OmitFeed -OmitPortable:$OmitPortable
        $script:fixturePublished = Join-Path $caseRoot 'published'
        New-Item -ItemType Directory -Path $script:fixturePublished -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $script:fixturePublished 'CycleArc.exe'), [byte[]](1, 2, 3, 4))
        if ($GateFailure -eq 'published-exe-tampered') {
            [IO.File]::WriteAllBytes((Join-Path $script:fixturePublished 'CycleArc.exe'), [byte[]](5, 6, 7, 8))
        }
        if ($GateFailure -eq 'published-extra-file') {
            Set-Content -LiteralPath (Join-Path $script:fixturePublished 'unexpected.dll') -Value 'stray'
        }
        if ($GateFailure -in @('portable-tampered', 'portable-nested', 'portable-duplicate', 'portable-corrupt')) {
            $portablePath = Join-Path $script:fixtureArtifact "CycleArc-$script:fixtureVersion-win-x64-portable.zip"
            Remove-Item -LiteralPath $portablePath -Force
            switch ($GateFailure) {
                'portable-tampered' { New-PortableFixtureArchive -Path $portablePath -Bytes ([byte[]](4, 3, 2, 1)) }
                'portable-nested' { New-PortableFixtureArchive -Path $portablePath -EntryNames @('nested/CycleArc.exe') }
                'portable-duplicate' { New-PortableFixtureArchive -Path $portablePath -EntryNames @('CycleArc.exe', 'CycleArc.exe') }
                'portable-corrupt' { [IO.File]::WriteAllText($portablePath, 'corrupted zip') }
            }
            # Recompute the outer checksum: the internal EXE validation must still refuse it.
            $targets = @(Get-ChildItem -LiteralPath $script:fixtureArtifact -File | Where-Object Name -cne 'SHA256SUMS.txt')
            $targets | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
                Set-Content -LiteralPath (Join-Path $script:fixtureArtifact 'SHA256SUMS.txt') -Encoding utf8
        }
        if ($GateFailure -in @('full-tampered', 'full-root-exe', 'full-case-duplicate', 'full-corrupt')) {
            $packagePath = Join-Path $script:fixtureArtifact "CycleArc-$script:fixtureVersion-full.nupkg"
            Remove-Item -LiteralPath $packagePath -Force
            switch ($GateFailure) {
                'full-tampered' { New-PortableFixtureArchive -Path $packagePath -EntryNames @('lib/net10.0/CycleArc.exe') -Bytes ([byte[]](4, 3, 2, 1)) }
                'full-root-exe' { New-PortableFixtureArchive -Path $packagePath }
                'full-case-duplicate' { New-PortableFixtureArchive -Path $packagePath -EntryNames @('lib/net10.0/CycleArc.exe', 'lib/net10.0/cyclearc.exe') }
                'full-corrupt' { [IO.File]::WriteAllText($packagePath, 'corrupted package') }
            }
            # Both outer checksums are valid; reject the incompatible/corrupt executable payload.
            $feedPath = Join-Path $script:fixtureArtifact 'releases.win.json'
            $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
            $feed.Assets[0].SHA1 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash
            $feed.Assets[0].SHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
            $feed.Assets[0].Size = (Get-Item -LiteralPath $packagePath).Length
            $feed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $feedPath -Encoding utf8
            Get-ChildItem -LiteralPath $script:fixtureArtifact -File | Where-Object Name -cne 'SHA256SUMS.txt' |
                ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
                Set-Content -LiteralPath (Join-Path $script:fixtureArtifact 'SHA256SUMS.txt') -Encoding utf8
        }
        if ($GateFailure -eq 'portable-nested-asset') {
            $nested = Join-Path $script:fixtureArtifact 'nested'
            New-Item -ItemType Directory -Path $nested | Out-Null
            $portablePath = Join-Path $script:fixtureArtifact "CycleArc-$script:fixtureVersion-win-x64-portable.zip"
            Copy-Item -LiteralPath $portablePath -Destination (Join-Path $nested (Split-Path -Leaf $portablePath))
            Remove-Item -LiteralPath $portablePath -Force
        }
        if ($GateFailure -eq 'portable-missing-checksum') {
            $manifest = Join-Path $script:fixtureArtifact 'SHA256SUMS.txt'
            @(Get-Content -LiteralPath $manifest) | Where-Object { $_ -notmatch 'portable.zip$' } |
                Set-Content -LiteralPath $manifest -Encoding utf8
        }
        if ($GateFailure -eq 'portable-wrong-checksum') {
            $manifest = Join-Path $script:fixtureArtifact 'SHA256SUMS.txt'
            $lines = @(Get-Content -LiteralPath $manifest)
            $lines | ForEach-Object { if ($_ -match 'portable.zip$') { ('f' * 64) + '  ' + ($_ -split '  ')[1] } else { $_ } } |
                Set-Content -LiteralPath $manifest -Encoding utf8
        }
        $assets = @(New-ReleaseAssetsFrom -Directory $script:fixtureArtifact -Only $ReleaseOnly `
            -CorruptName $CorruptName -WrongSizeName $WrongSizeName)
        if ($ExtraReleaseAssets) { $assets = @($assets + $ExtraReleaseAssets) }
        $script:fixtureRelease = [pscustomobject]@{
            isDraft         = !$Public
            isPrerelease    = $false
            tagName         = $script:fixtureTag
            targetCommitish = $script:fixtureCommit
            name            = "CycleArc $script:fixtureVersion"
            body            = 'notes'
            assets          = $assets
        }
        $script:ghCommands = @()
        $script:runReads = 0
        $script:fixtureGate = $GateFailure
        Invoke-Release -VersionValue $script:fixtureVersion -Commitish 'HEAD' `
            -RepositoryName 'frozenvoice/cyclearc' -RemoteName 'origin' -FullRunId 4242 -Preflight -RepositoryRoot $caseRoot
    }

    function Assert-NoRemoteMutation {
        $mutations = @($script:ghCommands | Where-Object {
            $text = $_ -join ' '
            $text -match 'release (create|edit|upload|delete)' -or
            $text -match 'git (tag|push)' -or
            $text -match '--draft=false'
        })
        $shown = ($mutations | ForEach-Object { $_ -join ' ' }) -join '; '
        Assert-Equal 0 $mutations.Count "Preflight must not change anything on GitHub (saw: $shown)"
    }

    # A normally produced draft carries seven files; the name check must not reject it.
    $draft = Invoke-PreflightFixture -Name 'draft-six-files'
    Assert-Equal 'Preflight' $draft.Status 'a seven-file draft passes preflight'
    Assert-Equal 4242 $draft.FullRunId 'preflight records the selected run ID'
    Assert-Equal 555 $draft.ArtifactId 'preflight records the selected exact artifact ID'
    Assert-Equal 666 $draft.PublishedArtifactId 'preflight records the original publish artifact identity'
    Assert-NoRemoteMutation

    foreach ($case in @(
        @{ Gate = 'wrong-sha'; Error = 'target SHA' },
        @{ Gate = 'old-push'; Error = 'workflow_dispatch' },
        @{ Gate = 'cancelled'; Error = 'not a completed success' },
        @{ Gate = 'missing-install-job'; Error = 'required job' },
        @{ Gate = 'failed-install-job'; Error = 'not a completed success' },
        @{ Gate = 'skipped-shortcut-job'; Error = 'not a completed success' },
        @{ Gate = 'missing-portable-job'; Error = 'required job' },
        @{ Gate = 'failed-portable-job'; Error = 'not a completed success' },
        @{ Gate = 'missing-startup-job'; Error = "required job 'managed-startup'" },
        @{ Gate = 'failed-startup-job'; Error = "job 'managed-startup' is not a completed success" },
        @{ Gate = 'skipped-startup-job'; Error = "job 'managed-startup' is not a completed success" },
        @{ Gate = 'pending-startup-job'; Error = "job 'managed-startup' is not a completed success" },
        @{ Gate = 'wrong-attempt-startup-job'; Error = "job 'managed-startup' is not a completed success" },
        @{ Gate = 'wrong-run-startup-job'; Error = "job 'managed-startup' is not a completed success" },
        @{ Gate = 'missing-artifact'; Error = 'exactly one' },
        @{ Gate = 'duplicate-artifact'; Error = 'exactly one' },
        @{ Gate = 'expired-artifact'; Error = 'expired, empty' },
        @{ Gate = 'wrong-artifact-sha'; Error = 'target SHA' },
        @{ Gate = 'stale-attempt-artifact'; Error = 'selected full run attempt' },
        @{ Gate = 'rerun-after-download'; Error = 'identity changed' },
        @{ Gate = 'artifact-replaced'; Error = 'identity changed' },
        @{ Gate = 'published-replaced'; Error = 'identity changed' },
        @{ Gate = 'published-expired'; Error = 'expired, empty' },
        @{ Gate = 'published-wrong-sha'; Error = 'target SHA' },
        @{ Gate = 'published-stale'; Error = 'selected full run attempt' },
        @{ Gate = 'published-missing'; Error = 'CycleArc-published-win-x64 artifact' },
        @{ Gate = 'published-duplicate'; Error = 'CycleArc-published-win-x64 artifact' },
        @{ Gate = 'published-exe-tampered'; Error = 'does not match original publish' },
        @{ Gate = 'published-extra-file'; Error = 'exactly root CycleArc.exe' },
        @{ Gate = 'portable-tampered'; Error = 'does not match original publish' },
        @{ Gate = 'portable-nested'; Error = 'exactly one root CycleArc.exe' },
        @{ Gate = 'portable-duplicate'; Error = 'exactly one root CycleArc.exe' },
        @{ Gate = 'portable-corrupt'; Error = '' },
        @{ Gate = 'portable-wrong-checksum'; Error = 'SHA256SUMS.txt does not match' },
        @{ Gate = 'portable-missing-checksum'; Error = 'one SHA-256 entry for every release asset' },
        @{ Gate = 'portable-nested-asset'; Error = 'must all be root files' },
        @{ Gate = 'full-tampered'; Error = 'does not match original publish' },
        @{ Gate = 'full-root-exe'; Error = 'exactly one lib/*/CycleArc.exe' },
        @{ Gate = 'full-case-duplicate'; Error = 'exactly one lib/*/CycleArc.exe' },
        @{ Gate = 'full-corrupt'; Error = '' }
    )) {
        Assert-Throws { Invoke-PreflightFixture -Name $case.Gate -GateFailure $case.Gate } $case.Error
        Assert-NoRemoteMutation
    }

    # The same seven files, already public and matching, need no further work.
    $complete = Invoke-PreflightFixture -Name 'public-six-files' -Public
    Assert-Equal 'AlreadyComplete' $complete.Status 'a matching public seven-file release is already complete'
    Assert-NoRemoteMutation

    # A package without the optional outputs is still a complete package.
    Assert-Equal 'Preflight' (Invoke-PreflightFixture -Name 'draft-four-files' -OmitOptional).Status `
        'a five-file package is handled by the same contract'
    Assert-Equal 'AlreadyComplete' (Invoke-PreflightFixture -Name 'public-four-files' -OmitOptional -Public).Status `
        'a matching public five-file release is already complete'
    Assert-NoRemoteMutation

    # Anything outside required-plus-optional is still refused, in the build output...
    # The release itself is clean here, so the stray reaches the build-output check.
    Assert-Throws {
        Invoke-PreflightFixture -Name 'stray-artifact-file' -AddStrayFile -ReleaseOnly @(
            'CycleArc-Setup.exe', "CycleArc-$assetVersion-full.nupkg", "CycleArc-$assetVersion-win-x64-portable.zip", 'releases.win.json',
            'SHA256SUMS.txt', 'assets.win.json', 'RELEASES')
    } 'unexpected file'
    # ...and on the release being inspected.
    Assert-Throws {
        Invoke-PreflightFixture -Name 'stray-release-asset' -ExtraReleaseAssets @(
            [pscustomobject]@{ name = 'old-installer.zip'; size = 10; digest = "sha256:$hashA" })
    } 'unexpected assets'

    # A public release missing a required file is not complete.
    Assert-Throws {
        Invoke-PreflightFixture -Name 'public-missing-required' -Public -ReleaseOnly @(
            'CycleArc-Setup.exe', 'releases.win.json', 'SHA256SUMS.txt', 'assets.win.json', 'RELEASES')
    } 'missing expected asset'

    # A build output missing a required file never reaches a release at all.
    Assert-Throws { Invoke-PreflightFixture -Name 'artifact-missing-required' -OmitFeed } 'release feed'
    Assert-Throws { Invoke-PreflightFixture -Name 'artifact-missing-portable' -OmitPortable } 'portable archive'

    # Digest and size verification against the uploaded assets stays in force, including for
    # an optional asset.
    Assert-Throws { Invoke-PreflightFixture -Name 'public-bad-digest' -Public -CorruptName 'CycleArc-Setup.exe' } 'digest'
    Assert-Throws { Invoke-PreflightFixture -Name 'public-bad-size' -Public -WrongSizeName 'RELEASES' } 'size'
    Assert-NoRemoteMutation
}
finally {
    Set-Item -Path Function:\Invoke-NativeCommand -Value $nativeBeforeAssetTests
    Set-Item -Path Function:\Assert-ReleaseExecutableVersion -Value $fileVersionBeforeAssetTests
    Set-Item -Path Function:\Assert-SelfContainedReleaseApp -Value $dependenciesBeforeAssetTests
    if (Test-Path -LiteralPath $assetTestRoot) { Remove-Item -LiteralPath $assetTestRoot -Recurse -Force }
}

$releaseScript = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts/Release.ps1') -Raw
if ($releaseScript -notmatch '\[switch\]\$Preflight') { throw 'Release.ps1 must expose -Preflight' }
$invoke = $releaseScript.IndexOf('function Invoke-Release')
if ($invoke -lt 0) { throw 'Release.ps1 is missing Invoke-Release' }
$invokeBody = $releaseScript.Substring($invoke)
$preflightReturn = $invokeBody.IndexOf("Status = 'Preflight'")
$ensureTag = $invokeBody.IndexOf('Ensure-TagPublished -TagName')
$create = $invokeBody.IndexOf("'release', 'create'")
$upload = $invokeBody.IndexOf("'release', 'upload'")
$publish = $invokeBody.IndexOf("'--draft=false'")
if ($preflightReturn -lt 0) { throw 'Release.ps1 must return a Preflight status without mutating GitHub' }
if ($ensureTag -lt $preflightReturn -or $create -lt $preflightReturn -or $upload -lt $preflightReturn -or $publish -lt $preflightReturn) {
    throw 'Release.ps1 must not create tags, drafts or uploads before the Preflight return'
}
if ($invokeBody -match 'dotnet (build|test|publish)' -or $invokeBody -match 'Package\.ps1') {
    throw 'Release publish must not start a new build, test or packaging run'
}
if ($invokeBody -notmatch 'release-preflight' -or $invokeBody -notmatch 'package-verify' -or $invokeBody -notmatch 'remote-state-verify') {
    throw 'Release.ps1 must expose preflight, package-verify and remote-state-verify phases'
}

Write-Host 'PASS: isolated release validation, CI-state, checksum, asset, staging-ownership and preflight-boundary tests.'
