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


# --- Required vs allowed release assets, exercised through the real decision path ---------
#
# These drive Invoke-Release -Preflight with only the external process boundary
# (Invoke-NativeCommand) replaced, so the asset-name, checksum, size and digest decisions
# are the production ones. Assert-FullPackageFileVersion is stubbed because a representative
# fixture cannot carry real PE version metadata; every asset rule under test stays live.
# Nothing here creates, edits or publishes a GitHub release.

$assetTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-release-assets-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $assetTestRoot | Out-Null
$nativeBeforeAssetTests = (Get-Command Invoke-NativeCommand -CommandType Function).ScriptBlock
$fileVersionBeforeAssetTests = (Get-Command Assert-FullPackageFileVersion -CommandType Function).ScriptBlock
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
            [switch]$OmitFeed
        )
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $Directory 'CycleArc-Setup.exe'), [byte[]](4, 5, 6, 7))
        $fullName = "CycleArc-$script:fixtureVersion-full.nupkg"
        $fullPath = Join-Path $Directory $fullName
        $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entryStream = $archive.CreateEntry('CycleArc.exe').Open()
            try { $entryStream.Write([byte[]](1, 2, 3, 4), 0, 4) } finally { $entryStream.Dispose() }
        }
        finally { $archive.Dispose(); $stream.Dispose() }
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
            # Exactly the two extra outputs Package.ps1 already accepts next to the required four.
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
            if ($joined -match '^run list') {
                $run = @(@{
                    databaseId = 4242; headSha = $script:fixtureCommit; event = 'push'; status = 'completed'
                    conclusion = 'success'; createdAt = '2026-09-15T10:00:00Z'; workflowName = 'Windows'
                    url = 'https://example.invalid/run/4242'
                })
                return [pscustomobject]@{ ExitCode = 0; Output = ($run | ConvertTo-Json -Depth 30) }
            }
            if ($joined -match '^run download') {
                $dirIndex = [array]::IndexOf([array]$Arguments, '--dir')
                if ($dirIndex -lt 0) { throw 'gh run download was called without --dir' }
                Copy-Item -Path (Join-Path $script:fixtureArtifact '*') -Destination $Arguments[$dirIndex + 1] -Force
                return [pscustomobject]@{ ExitCode = 0; Output = '' }
            }
            if ($joined -match '^api repos/.+/releases/latest') {
                return [pscustomobject]@{ ExitCode = 0; Output = (@{ tag_name = $script:fixtureTag } | ConvertTo-Json) }
            }
            throw "Unexpected gh call in a preflight test: $joined"
        }
        throw "Unexpected process in a preflight test: $FilePath"
    }

    # A representative fixture cannot carry real PE version metadata; that check has its own
    # coverage above and is not what these cases decide.
    function Assert-FullPackageFileVersion {
        param([string]$PackagePath, [string]$ExpectedFileVersion)
    }

    $script:fixtureVersion = $assetVersion

    function Invoke-PreflightFixture {
        param(
            [Parameter(Mandatory)][string]$Name,
            [switch]$OmitOptional,
            [switch]$AddStrayFile,
            [switch]$OmitFeed,
            [switch]$Public,
            [string]$CorruptName,
            [string]$WrongSizeName,
            [string[]]$ReleaseOnly,
            [object[]]$ExtraReleaseAssets
        )
        $caseRoot = Join-Path $assetTestRoot $Name
        $script:fixtureArtifact = Join-Path $caseRoot 'artifact'
        New-StagedArtifact -Directory $script:fixtureArtifact -OmitOptional:$OmitOptional `
            -AddStrayFile:$AddStrayFile -OmitFeed:$OmitFeed
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
        Invoke-Release -VersionValue $script:fixtureVersion -Commitish 'HEAD' `
            -RepositoryName 'frozenvoice/cyclearc' -RemoteName 'origin' -Preflight -RepositoryRoot $caseRoot
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

    # A normally produced draft carries six files; the name check must not reject it.
    $draft = Invoke-PreflightFixture -Name 'draft-six-files'
    Assert-Equal 'Preflight' $draft.Status 'a six-file draft passes preflight'
    Assert-NoRemoteMutation

    # The same six files, already public and matching, need no further work.
    $complete = Invoke-PreflightFixture -Name 'public-six-files' -Public
    Assert-Equal 'AlreadyComplete' $complete.Status 'a matching public six-file release is already complete'
    Assert-NoRemoteMutation

    # A package without the optional outputs is still a complete package.
    Assert-Equal 'Preflight' (Invoke-PreflightFixture -Name 'draft-four-files' -OmitOptional).Status `
        'a four-file package is handled by the same contract'
    Assert-Equal 'AlreadyComplete' (Invoke-PreflightFixture -Name 'public-four-files' -OmitOptional -Public).Status `
        'a matching public four-file release is already complete'
    Assert-NoRemoteMutation

    # Anything outside required-plus-optional is still refused, in the build output...
    # The release itself is clean here, so the stray reaches the build-output check.
    Assert-Throws {
        Invoke-PreflightFixture -Name 'stray-artifact-file' -AddStrayFile -ReleaseOnly @(
            'CycleArc-Setup.exe', "CycleArc-$assetVersion-full.nupkg", 'releases.win.json',
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

    # Digest and size verification against the uploaded assets stays in force, including for
    # an optional asset.
    Assert-Throws { Invoke-PreflightFixture -Name 'public-bad-digest' -Public -CorruptName 'CycleArc-Setup.exe' } 'digest'
    Assert-Throws { Invoke-PreflightFixture -Name 'public-bad-size' -Public -WrongSizeName 'RELEASES' } 'size'
    Assert-NoRemoteMutation
}
finally {
    Set-Item -Path Function:\Invoke-NativeCommand -Value $nativeBeforeAssetTests
    Set-Item -Path Function:\Assert-FullPackageFileVersion -Value $fileVersionBeforeAssetTests
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
