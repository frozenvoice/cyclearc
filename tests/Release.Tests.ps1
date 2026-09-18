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
            throw "ASSERT FAILED: expected '$MessageFragment' in '$($_.Exception.Message)'"
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
    databaseId = 101; headSha = $shaA; event = 'push'; status = 'completed'; conclusion = 'success'
    createdAt = '2026-09-15T10:00:00Z'; workflowName = 'Windows'
}
Assert-Equal 101 (Select-SuccessfulWindowsPushRun -Runs @($successfulRun) -CommitSha $shaA).databaseId 'completed successful push CI is selected'
$failedRun = $successfulRun.PSObject.Copy()
$failedRun.status = 'completed'
$failedRun.conclusion = 'failure'
Assert-Throws { Select-SuccessfulWindowsPushRun -Runs @($failedRun) -CommitSha $shaA } 'not a completed success'
$pendingRun = $successfulRun.PSObject.Copy()
$pendingRun.status = 'in_progress'
$pendingRun.conclusion = ''
Assert-Throws { Select-SuccessfulWindowsPushRun -Runs @($pendingRun) -CommitSha $shaA } 'not a completed success'
$newerFailed = $failedRun.PSObject.Copy()
$newerFailed.createdAt = '2026-09-15T11:00:00Z'
Assert-Throws {
    Select-SuccessfulWindowsPushRun -Runs @($successfulRun, $newerFailed) -CommitSha $shaA
} 'not a completed success'
$olderFailed = $failedRun.PSObject.Copy()
$olderFailed.createdAt = '2026-09-15T09:00:00Z'
Assert-Throws {
    Select-SuccessfulWindowsPushRun -Runs @($olderFailed, $successfulRun) -CommitSha $shaA
} 'not a completed success'
Assert-Throws {
    Select-SuccessfulWindowsPushRun -Runs @($pendingRun, $successfulRun) -CommitSha $shaA
} 'not a completed success'
Assert-Throws { Select-SuccessfulWindowsPushRun -Runs @($successfulRun) -CommitSha $shaB } 'No Windows push CI run'

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

    # The CI artifact now contains installer, full package, feed, and a
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
        $entry = $archive.CreateEntry('CycleArc.exe')
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
    $assetFiles = @($setup, $fullPath, $feedPath)
    $manifestLines = foreach ($asset in $assetFiles) {
        "$( (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() )  $(Split-Path -Leaf $asset)"
    }
    $packagedManifest = Join-Path $packaged 'SHA256SUMS.txt'
    $manifestLines | Set-Content -LiteralPath $packagedManifest -Encoding utf8
    Assert-PackagedReleaseFeed -FeedPath $feedPath -VersionValue '0.6.0' -PackageName $fullName -PackagePath $fullPath | Out-Null
    $feedText = [IO.File]::ReadAllText($feedPath)
    $fullBytes = [IO.File]::ReadAllBytes($fullPath)
    Assert-Throws {
        Remove-Item -LiteralPath $feedPath -Force
        Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0'
    } 'release feed'
    [IO.File]::WriteAllText($feedPath, $feedText)
    [IO.File]::WriteAllText($feedPath, $feedText.Replace('"0.6.0"', '"0.6.1"'))
    Assert-Throws { Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' } 'Release feed'
    [IO.File]::WriteAllText($feedPath, $feedText)
    Assert-Throws {
        Remove-Item -LiteralPath $fullPath -Force
        Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0'
    } 'full package'
    [IO.File]::WriteAllBytes($fullPath, $fullBytes)
    [IO.File]::WriteAllBytes($fullPath, [byte[]](5, 4, 3, 2))
    Assert-Throws { Get-PackagedArtifact -StagingDirectory $packaged -VersionValue '0.6.0' -ExpectedFileVersion '0.6.0.0' } 'Size'

    $owned = Join-Path $testRoot 'publish/.release-staging/run-1'
    New-Item -ItemType Directory -Path $owned -Force | Out-Null
    Assert-OwnedDirectory -RepoRoot $testRoot -Target $owned
    Assert-Throws { Assert-OwnedDirectory -RepoRoot $testRoot -Target (Join-Path $testRoot '..') } 'outside the repository'

    Assert-Equal 'CycleArc-Setup.exe' (Get-ExpectedReleaseAssetNames -VersionValue '0.6.0')[0] 'setup asset name'
    $expectedAssets = @(Get-ExpectedReleaseAssetNames -VersionValue '0.6.0')
    Assert-True ($expectedAssets -contains 'CycleArc-0.6.0-full.nupkg') 'full package asset name'
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
