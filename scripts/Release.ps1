#Requires -Version 7.0
<#
.SYNOPSIS
    Publish a tested CycleArc Windows artifact as a GitHub draft release.

.DESCRIPTION
    The script deliberately has a small, linear release gate.  It only creates or
    changes a release while the release is a draft.  A public release is checked
    for an exact match and is never edited or overwritten.

    GitHub CLI and Git are the only external tools used.  CI is selected by the
    explicit -FullRunId, exact commit SHA, workflow_dispatch event and Windows
    full workflow, including every required job; a failed or in-progress
    run is an error and is never rerun by this script.

    -Preflight runs local, package and remote verification without creating tags,
    drafts, uploads or public releases. Publish starts only after that gate and
    never starts a new build, test or packaging run.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Release.ps1 -Version 0.5.7 -FullRunId 123456 -NotesPath ./release-notes/0.5.7.md

    Commit defaults to HEAD. An explicit -Commit must resolve to the checked-out
    HEAD; check out an older version before releasing it.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Release.ps1 -Version 0.5.7 -FullRunId 123456 -NotesPath ./release-notes/0.5.7.md -Preflight
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Commit = 'HEAD',
    [string]$NotesPath,
    [string]$Repository = 'frozenvoice/cyclearc',
    [string]$Remote = 'origin',
    [string]$Workflow = '.github/workflows/windows.yml',
    [long]$FullRunId,
    [switch]$DraftOnly,
    [switch]$Preflight,
    [switch]$LoadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = [int]$LASTEXITCODE
    $text = $output -join "`n"
    if ($exitCode -ne 0 -and !$AllowFailure) {
        $display = if ($text) { ": $text" } else { '' }
        throw "$FilePath failed with exit code $exitCode$display"
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $text
    }
}

function Invoke-GhJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowNotFound
    )

    $result = Invoke-NativeCommand -FilePath 'gh' -Arguments $Arguments -AllowFailure
    if ($result.ExitCode -ne 0) {
        if ($AllowNotFound -and $result.Output -match '(?i)(not found|404)') {
            return $null
        }
        $display = if ($result.Output) { ": $($result.Output)" } else { '' }
        throw "gh failed with exit code $($result.ExitCode)$display"
    }
    if ([string]::IsNullOrWhiteSpace($result.Output)) { return $null }
    try {
        return $result.Output | ConvertFrom-Json -Depth 30
    }
    catch {
        throw "gh returned invalid JSON: $($_.Exception.Message)"
    }
}

function Assert-ReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be a plain semantic version such as 0.5.7: '$Value'"
    }
    $Value
}

function Get-ReleaseTagName {
    param([Parameter(Mandatory)][string]$Value)
    "v$(Assert-ReleaseVersion $Value)"
}

function Assert-CleanTrackedTree {
    $result = Invoke-NativeCommand -FilePath 'git' -Arguments @('status', '--porcelain=v1', '--untracked-files=no')
    if (![string]::IsNullOrWhiteSpace($result.Output)) {
        throw "Tracked working-tree changes must be committed before release:`n$($result.Output)"
    }
}

function Assert-SourceVersion {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Value
    )

    $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
    if (!(Test-Path -LiteralPath $propsPath -PathType Leaf)) { return }
    $text = [IO.File]::ReadAllText($propsPath)
    $versionMatch = [regex]::Match($text, '<Version>\s*([^<]+?)\s*</Version>')
    if ($versionMatch.Success -and $versionMatch.Groups[1].Value.Trim() -ne $Value) {
        throw "Directory.Build.props Version is '$($versionMatch.Groups[1].Value.Trim())', expected '$Value'"
    }
    $fileVersionMatch = [regex]::Match($text, '<FileVersion>\s*([^<]+?)\s*</FileVersion>')
    $expectedFileVersion = "$Value.0"
    if ($fileVersionMatch.Success -and $fileVersionMatch.Groups[1].Value.Trim() -ne $expectedFileVersion) {
        throw "Directory.Build.props FileVersion is '$($fileVersionMatch.Groups[1].Value.Trim())', expected '$expectedFileVersion'"
    }
}

function Resolve-CommitSha {
    param([Parameter(Mandatory)][string]$Commitish)
    $result = Invoke-NativeCommand -FilePath 'git' -Arguments @('rev-parse', '--verify', "$Commitish^{commit}")
    $sha = $result.Output.Trim()
    if ($sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Git did not resolve '$Commitish' to a full commit SHA"
    }
    $sha.ToLowerInvariant()
}

function Get-RemoteHeadRecords {
    param([Parameter(Mandatory)][string]$RemoteName)
    $result = Invoke-NativeCommand -FilePath 'git' -Arguments @('ls-remote', '--heads', $RemoteName)
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^([0-9a-fA-F]{40})\s+(refs/heads/\S+)$') {
            [pscustomobject]@{ Sha = $Matches[1].ToLowerInvariant(); Ref = $Matches[2] }
        }
    }
}

function Assert-CommitIsRemoteHead {
    param(
        [object[]]$RemoteHeads,
        [Parameter(Mandatory)][string]$CommitSha
    )
    $matches = @($RemoteHeads | Where-Object { $_.Sha -ieq $CommitSha })
    if ($matches.Count -eq 0) {
        throw "Commit $CommitSha is not the current SHA of any head on the configured remote"
    }
    $matches
}

function Get-RemoteTagRecords {
    param([Parameter(Mandatory)][string]$RemoteName)
    $result = Invoke-NativeCommand -FilePath 'git' -Arguments @('ls-remote', '--tags', $RemoteName)
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^([0-9a-fA-F]{40})\s+(refs/tags/\S+)$') {
            [pscustomobject]@{ Sha = $Matches[1].ToLowerInvariant(); Ref = $Matches[2] }
        }
    }
}

function Get-TagTargetFromRecords {
    param(
        [object[]]$Records,
        [Parameter(Mandatory)][string]$TagName
    )
    $direct = @($Records | Where-Object { $_.Ref -eq "refs/tags/$TagName" })
    $peeled = @($Records | Where-Object { $_.Ref -eq "refs/tags/$TagName^{}" })
    if ($peeled.Count -gt 1 -or $direct.Count -gt 1) { throw "Duplicate remote records for tag '$TagName'" }
    if ($peeled.Count -eq 1) { return $peeled[0].Sha }
    if ($direct.Count -eq 1) { return $direct[0].Sha }
    $null
}

function Get-LocalTagTarget {
    param([Parameter(Mandatory)][string]$TagName)
    $result = Invoke-NativeCommand -FilePath 'git' -Arguments @('rev-parse', '--verify', '--quiet', "refs/tags/$TagName^{}") -AllowFailure
    if ($result.ExitCode -ne 0) { return $null }
    $target = $result.Output.Trim()
    if ($target -notmatch '^[0-9a-fA-F]{40}$') { throw "Local tag '$TagName' did not resolve to a commit" }
    $target.ToLowerInvariant()
}

function Assert-TagTargets {
    param(
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$ExpectedSha,
        [string]$LocalSha,
        [string]$RemoteSha
    )
    if ($LocalSha -and $LocalSha -ine $ExpectedSha) {
        throw "Local tag '$TagName' points to $LocalSha, expected $ExpectedSha"
    }
    if ($RemoteSha -and $RemoteSha -ine $ExpectedSha) {
        throw "Remote tag '$TagName' points to $RemoteSha, expected $ExpectedSha"
    }
    if ($LocalSha -and $RemoteSha -and $LocalSha -ine $RemoteSha) {
        throw "Local and remote tag '$TagName' targets differ"
    }
}

function Assert-SuccessfulWindowsFullRun {
    param(
        [Parameter(Mandatory)][object]$Run,
        [Parameter(Mandatory)][long]$RunId,
        [Parameter(Mandatory)][string]$RepositoryName,
        [Parameter(Mandatory)][string]$CommitSha
    )
    if ($RunId -le 0 -or [long]$Run.id -ne $RunId) {
        throw 'An explicit positive -FullRunId matching the returned run is required'
    }
    if ([string]$Run.head_sha -ine $CommitSha -or [string]$Run.head_repository.full_name -ine $RepositoryName) {
        throw "Windows full run $RunId does not match repository $RepositoryName and target SHA $CommitSha"
    }
    if ([string]$Run.event -cne 'workflow_dispatch' -or [string]$Run.path -cne '.github/workflows/windows.yml') {
        throw "Windows full run $RunId must be a windows.yml workflow_dispatch run"
    }
    if ([string]$Run.status -cne 'completed' -or [string]$Run.conclusion -cne 'success' -or [int]$Run.run_attempt -lt 1) {
        throw "Windows full run $RunId is not a completed success; it will not be rerun or waited for"
    }
    $Run
}

function Get-RunApiItems {
    param([Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][string]$Property)
    $items = @()
    $page = 1
    do {
        $response = Invoke-GhJson -Arguments @('api', "${Endpoint}?per_page=100&page=$page")
        $batch = @($response.$Property)
        if ($batch.Count -eq 0 -and $items.Count -lt [int]$response.total_count) {
            throw "Incomplete GitHub $Property response for $Endpoint"
        }
        $items += $batch
        $page++
    } while ($items.Count -lt [int]$response.total_count)
    $items
}

function Assert-WindowsFullJobs {
    param([object[]]$Jobs, [Parameter(Mandatory)][long]$RunId, [Parameter(Mandatory)][int]$Attempt)
    foreach ($name in @('build', 'managed-setup-install', 'setup-shortcut-choices')) {
        $matching = @($Jobs | Where-Object { [string]$_.name -ceq $name })
        if ($matching.Count -ne 1) { throw "Windows full run $RunId must contain exactly one required job '$name'" }
    }
    foreach ($job in $Jobs) {
        if ([long]$job.run_id -ne $RunId -or [int]$job.run_attempt -ne $Attempt -or
            [string]$job.status -cne 'completed' -or [string]$job.conclusion -cne 'success') {
            throw "Windows full run $RunId job '$($job.name)' is not a completed success for attempt $Attempt"
        }
    }
}

function Select-WindowsFullArtifact {
    param([object[]]$Artifacts, [Parameter(Mandatory)][object]$Run, [Parameter(Mandatory)][object[]]$Jobs)
    $matching = @($Artifacts | Where-Object { [string]$_.name -ceq 'CycleArc-win-x64' })
    if ($matching.Count -ne 1) { throw 'Expected exactly one CycleArc-win-x64 artifact in the selected full run' }
    $artifact = $matching[0]
    if ([long]$artifact.id -le 0 -or [bool]$artifact.expired -or [long]$artifact.size_in_bytes -le 0 -or
        [long]$artifact.workflow_run.id -ne [long]$Run.id -or
        [string]$artifact.workflow_run.head_sha -ine [string]$Run.head_sha -or
        [long]$artifact.workflow_run.head_repository_id -ne [long]$Run.head_repository.id) {
        throw 'Selected CycleArc-win-x64 artifact is expired, empty or does not match the full run and target SHA'
    }
    # Artifacts belong to a run ID, not an attempt. Refuse an earlier attempt's
    # surviving artifact even if the run ID and commit are still identical.
    $build = @($Jobs | Where-Object { [string]$_.name -ceq 'build' })
    if ($build.Count -ne 1) { throw 'Artifact requires exactly one successful build job' }
    try {
        $created = [DateTimeOffset]::Parse([string]$artifact.created_at)
        $started = [DateTimeOffset]::Parse([string]$build[0].started_at)
        $completed = [DateTimeOffset]::Parse([string]$build[0].completed_at)
    }
    catch { throw 'Artifact/build timestamps are missing or invalid; cannot verify the full run attempt' }
    if ($created -lt $started -or $created -gt $completed) {
        throw 'Selected artifact was not created by the successful build in the selected full run attempt'
    }
    $artifact
}

function Get-VerifiedWindowsFullRun {
    param([Parameter(Mandatory)][string]$RepositoryName, [Parameter(Mandatory)][long]$RunId,
        [Parameter(Mandatory)][string]$CommitSha)
    $endpoint = "repos/$RepositoryName/actions/runs/$RunId"
    $run = Invoke-GhJson -Arguments @('api', $endpoint)
    Assert-SuccessfulWindowsFullRun -Run $run -RunId $RunId -RepositoryName $RepositoryName -CommitSha $CommitSha | Out-Null
    $jobs = @(Get-RunApiItems -Endpoint "$endpoint/attempts/$($run.run_attempt)/jobs" -Property 'jobs')
    Assert-WindowsFullJobs -Jobs $jobs -RunId $RunId -Attempt $run.run_attempt
    $artifacts = @(Get-RunApiItems -Endpoint "$endpoint/artifacts" -Property 'artifacts')
    $artifact = Select-WindowsFullArtifact -Artifacts $artifacts -Run $run -Jobs $jobs
    [pscustomobject]@{ Run = $run; Artifact = $artifact }
}

function Assert-OwnedDirectory {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Target
    )
    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release staging path is outside the repository: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        $item = Get-Item -LiteralPath $absolute
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Release staging path is a reparse point: $absolute" }
        $reparse = @(Get-ChildItem -LiteralPath $absolute -Force -Recurse | Where-Object {
            $_.Attributes -band [IO.FileAttributes]::ReparsePoint
        })
        if ($reparse.Count -gt 0) { throw "Release staging path contains a reparse point: $($reparse[0].FullName)" }
    }
}

function Assert-SinglePublishedExecutable {
    param(
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$ExpectedFileVersion
    )
    $files = @(Get-ChildItem -LiteralPath $StagingDirectory -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -cne 'CycleArc.exe') {
        $names = ($files | ForEach-Object { $_.FullName }) -join ', '
        throw "CycleArc-win-x64 must contain exactly CycleArc.exe; found: $names"
    }
    $exe = $files[0]
    $actual = ([string]$exe.VersionInfo.FileVersion).Trim()
    if ($actual -ne $ExpectedFileVersion) {
        throw "CycleArc.exe FileVersion is '$actual', expected '$ExpectedFileVersion'"
    }
    $exe.FullName
}

function Assert-PackagedChecksumManifest {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string[]]$AssetPaths
    )
    $expectedNames = @($AssetPaths | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $lines = @(Get-Content -LiteralPath $ManifestPath)
    $actualNames = @()
    foreach ($line in $lines) {
        if ($line -notmatch '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') {
            throw "SHA256SUMS.txt contains an invalid line: '$line'"
        }
        $name = $Matches[2]
        if ($name -eq 'SHA256SUMS.txt' -or $name -match '[\\/]') {
            throw "SHA256SUMS.txt contains an invalid asset name: '$name'"
        }
        if ($name -in $actualNames) { throw "SHA256SUMS.txt contains duplicate asset '$name'" }
        $actualNames += $name
        $path = Join-Path (Split-Path -Parent $ManifestPath) $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "SHA256SUMS.txt references missing asset '$name'"
        }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actualHash -ine $Matches[1]) {
            throw "SHA256SUMS.txt does not match '$name'"
        }
    }
    $expectedJoined = ($expectedNames -join "`n")
    $actualJoined = (@($actualNames | Sort-Object) -join "`n")
    if ($expectedJoined -cne $actualJoined) {
        throw "SHA256SUMS.txt must contain one SHA-256 entry for every release asset"
    }
    $true
}

function Assert-PackagedReleaseFeed {
    param(
        [Parameter(Mandatory)][string]$FeedPath,
        [Parameter(Mandatory)][string]$VersionValue,
        [string]$PackageId = 'CycleArc',
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$PackagePath
    )
    try { $feed = Get-Content -LiteralPath $FeedPath -Raw | ConvertFrom-Json -Depth 30 }
    catch { throw "releases.win.json is invalid JSON: $($_.Exception.Message)" }
    $assets = @($feed.Assets)
    if ($assets.Count -ne 1) { throw "releases.win.json must contain exactly one stable $VersionValue asset" }
    $full = $assets[0]
    if ([string]$full.PackageId -cne $PackageId -or
        [string]$full.Version -cne $VersionValue -or
        [string]$full.Type -cne 'Full' -or
        [string]$full.FileName -cne $PackageName) {
        throw 'Release feed contains an unapproved asset or version'
    }
    if ([string]$full.FileName -match '(?i)-delta\.nupkg$') { throw 'Stable CycleArc releases cannot contain delta assets' }
    $packageItem = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
    $sha1 = (Get-FileHash -LiteralPath $packageItem.FullName -Algorithm SHA1).Hash.ToUpperInvariant()
    $sha256 = (Get-FileHash -LiteralPath $packageItem.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    if ([int64]$full.Size -ne [int64]$packageItem.Length) { throw 'Release feed package Size does not match the full package' }
    if ([string]$full.SHA1 -ine $sha1) { throw 'Release feed package SHA1 does not match the full package' }
    if ([string]::IsNullOrWhiteSpace([string]$full.SHA256) -or [string]$full.SHA256 -ine $sha256) {
        throw 'Release feed package SHA256 does not match the full package'
    }
    $true
}

function Assert-FullPackageFileVersion {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$ExpectedFileVersion
    )
    $checkRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-package-check-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $checkRoot -Force | Out-Null
    $extracted = $null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
        try {
            $entry = @($archive.Entries | Where-Object { $_.Name -ceq 'CycleArc.exe' })
            if ($entry.Count -ne 1) { throw 'Full package does not contain exactly one CycleArc.exe' }
            $extracted = Join-Path $checkRoot 'CycleArc.exe'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $extracted, $true)
        }
        finally { $archive.Dispose() }
        $actual = ([Diagnostics.FileVersionInfo]::GetVersionInfo($extracted).FileVersion ?? '').Trim()
        if (!$actual) { throw 'Full package CycleArc.exe has no FileVersion metadata' }
        if ($actual -ne $ExpectedFileVersion) {
            throw "Full package CycleArc.exe FileVersion is '$actual', expected '$ExpectedFileVersion'"
        }
    }
    finally {
        if (Test-Path -LiteralPath $checkRoot) { Remove-Item -LiteralPath $checkRoot -Recurse -Force }
    }
}

function Get-PackagedArtifact {
    param(
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$VersionValue,
        [Parameter(Mandatory)][string]$ExpectedFileVersion,
        [string]$PackageId = 'CycleArc',
        [string]$Channel = 'win'
    )
    $files = @(Get-ChildItem -LiteralPath $StagingDirectory -File -Recurse)
    if ($files.Count -eq 0) { throw 'CycleArc-win-x64 artifact is empty' }
    $setup = @($files | Where-Object { $_.Name -ceq "$PackageId-Setup.exe" })
    $packageName = "$PackageId-$VersionValue-full.nupkg"
    $full = @($files | Where-Object { $_.Name -ceq $packageName })
    $feedName = "releases.$Channel.json"
    $feed = @($files | Where-Object { $_.Name -ceq $feedName })
    $manifest = @($files | Where-Object { $_.Name -ceq 'SHA256SUMS.txt' })
    foreach ($record in @(@{ Name = 'Setup'; Items = $setup }, @{ Name = 'full package'; Items = $full }, @{ Name = 'release feed'; Items = $feed }, @{ Name = 'checksum manifest'; Items = $manifest })) {
        if ($record.Items.Count -ne 1) { throw "Expected exactly one $($record.Name) in CI artifact" }
    }
    $deltas = @($files | Where-Object { $_.Name -match '(?i)-delta\.nupkg$' })
    if ($deltas.Count -gt 0) { throw "CI artifact contains forbidden delta package(s): $($deltas.Name -join ', ')" }
    # Same rule Package.ps1 applies to its own output directory: the four required files
    # plus the optional Velopack asset list and legacy index, and nothing else. Anything
    # else in the artifact is rejected here rather than being uploaded and then allowed.
    $permitted = @(Get-ExpectedReleaseAssetNames -VersionValue $VersionValue -PackageId $PackageId -Channel $Channel) +
        @(Get-OptionalReleaseAssetNames -Channel $Channel)
    $unexpected = @($files | Where-Object { $_.Name -cnotin $permitted })
    if ($unexpected.Count -gt 0) {
        throw "CI artifact contains unexpected file(s): $($unexpected.Name -join ', ')"
    }
    Assert-PackagedReleaseFeed -FeedPath $feed[0].FullName -VersionValue $VersionValue -PackageId $PackageId -PackageName $packageName -PackagePath $full[0].FullName | Out-Null
    $assetPaths = @($files | Where-Object { $_.Name -cne 'SHA256SUMS.txt' } | ForEach-Object { $_.FullName })
    Assert-PackagedChecksumManifest -ManifestPath $manifest[0].FullName -AssetPaths $assetPaths | Out-Null
    Assert-FullPackageFileVersion -PackagePath $full[0].FullName -ExpectedFileVersion $ExpectedFileVersion
    Get-LocalAssetMap -Paths @($files | ForEach-Object { $_.FullName })
}

function Normalize-Sha256 {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $normalized = $Value.Trim()
    if ($normalized -match '(?i)^sha256:([0-9a-f]{64})$') { $normalized = $Matches[1] }
    if ($normalized -notmatch '^[0-9a-fA-F]{64}$') { return $null }
    $normalized.ToLowerInvariant()
}

function New-ChecksumManifest {
    param([Parameter(Mandatory)][string]$ExecutablePath)
    $hash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestPath = Join-Path (Split-Path -Parent $ExecutablePath) 'SHA256SUMS.txt'
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($manifestPath, "$hash  CycleArc.exe`r`n", $utf8NoBom)
    $manifestPath
}

function Assert-ChecksumManifest {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$ExecutablePath
    )
    $lines = @(Get-Content -LiteralPath $ManifestPath)
    if ($lines.Count -ne 1 -or $lines[0] -notmatch '^\s*([0-9a-fA-F]{64})\s+\*?CycleArc\.exe\s*$') {
        throw 'SHA256SUMS.txt must contain one SHA-256 entry for CycleArc.exe'
    }
    $expected = $Matches[1].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw 'SHA256SUMS.txt does not match CycleArc.exe' }
}

function Get-LocalAssetMap {
    param([Parameter(Mandatory)][string[]]$Paths)
    $map = @{}
    foreach ($path in $Paths) {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $map[$item.Name] = [pscustomobject]@{
            Name   = $item.Name
            Path   = $item.FullName
            Size   = [int64]$item.Length
            Sha256 = $hash
            Digest = "sha256:$hash"
        }
    }
    $map
}

function Assert-ReleaseAssetNames {
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string[]]$AllowedNames
    )
    $assets = @($Release.assets)
    $names = @($assets | ForEach-Object { [string]$_.name })
    $duplicates = @($names | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "Release contains duplicate assets: $($duplicates.Name -join ', ')" }
    $extra = @($names | Where-Object { $_ -cnotin $AllowedNames })
    if ($extra.Count -gt 0) {
        throw "Release contains unexpected assets; refusing to delete them: $($extra -join ', ')"
    }
}

function Assert-ReleaseAssets {
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][hashtable]$ExpectedAssets,
        [switch]$RequireComplete
    )
    $expectedNames = @($ExpectedAssets.Keys | ForEach-Object { [string]$_ })
    Assert-ReleaseAssetNames -Release $Release -AllowedNames $expectedNames
    $assets = @($Release.assets)
    foreach ($name in $expectedNames) {
        $remote = @($assets | Where-Object { [string]$_.name -ceq $name })
        if ($remote.Count -gt 1) { throw "Release contains duplicate asset '$name'" }
        if ($remote.Count -eq 0) {
            if ($RequireComplete) { throw "Release is missing expected asset '$name'" }
            continue
        }
        $local = $ExpectedAssets[$name]
        if (!$local -or !($local.PSObject.Properties.Name -contains 'Size') -or $null -eq $local.Size) {
            throw "Local asset '$name' has no valid size"
        }
        if (!($remote[0].PSObject.Properties.Name -contains 'size') -or $null -eq $remote[0].size) {
            throw "Asset '$name' has no valid size"
        }
        $remoteSize = 0L
        try { $remoteSize = [int64]$remote[0].size } catch { throw "Asset '$name' has no valid size" }
        if ($remoteSize -ne [int64]$local.Size) {
            throw "Asset '$name' size is $remoteSize, expected $($local.Size)"
        }
        $hasDigest = $remote[0].PSObject.Properties.Name -contains 'digest'
        $remoteHash = if ($hasDigest) { Normalize-Sha256 ([string]$remote[0].digest) } else { $null }
        if (!$remoteHash) { throw "Asset '$name' has no GitHub SHA-256 digest" }
        $localHash = if ($local.PSObject.Properties.Name -contains 'Sha256') {
            Normalize-Sha256 ([string]$local.Sha256)
        }
        elseif ($local.PSObject.Properties.Name -contains 'Digest') {
            Normalize-Sha256 ([string]$local.Digest)
        }
        else {
            $null
        }
        if (!$localHash) { throw "Local asset '$name' has no valid SHA-256 digest" }
        if ($remoteHash -ine $localHash) {
            throw "Asset '$name' digest is $remoteHash, expected $localHash"
        }
    }
    $true
}

function Get-ReleaseSnapshot {
    param(
        [Parameter(Mandatory)][string]$RepositoryName,
        [Parameter(Mandatory)][string]$TagName
    )
    $fields = 'isDraft,isPrerelease,isImmutable,tagName,targetCommitish,name,body,assets,createdAt,publishedAt'
    Invoke-GhJson -Arguments @('release', 'view', $TagName, '--repo', $RepositoryName, '--json', $fields) -AllowNotFound
}

function Get-LatestReleaseTag {
    param([Parameter(Mandatory)][string]$RepositoryName)
    $latest = Invoke-GhJson -Arguments @('api', "repos/$RepositoryName/releases/latest") -AllowNotFound
    if (!$latest) { return $null }
    [string]$latest.tag_name
}

function Assert-ReleaseTools {
    foreach ($name in @('git', 'gh')) {
        if (!(Get-Command $name -ErrorAction SilentlyContinue)) {
            throw "Missing required tool '$name'. Install Git and the GitHub CLI, then retry. No GitHub release was created."
        }
    }
}

function Assert-GitHubCliAuth {
    $result = Invoke-NativeCommand -FilePath 'gh' -Arguments @('auth', 'status') -AllowFailure
    if ($result.ExitCode -ne 0) {
        $detail = if ($result.Output) { ": $($result.Output)" } else { '' }
        throw "GitHub CLI is not authenticated$detail"
    }
    $true
}

function Get-ExpectedReleaseAssetNames {
    param(
        [Parameter(Mandatory)][string]$VersionValue,
        [string]$PackageId = 'CycleArc',
        [string]$Channel = 'win'
    )
    @(
        "$PackageId-Setup.exe",
        "$PackageId-$VersionValue-full.nupkg",
        "releases.$Channel.json",
        'SHA256SUMS.txt'
    )
}

# Velopack also emits an asset list and a legacy RELEASES index. Package.ps1 accepts both as
# ordinary outputs, so a normally produced release carries six files, not four. They are
# optional rather than required: a package that predates them, or a channel that does not
# emit them, is still complete.
function Get-OptionalReleaseAssetNames {
    param([string]$Channel = 'win')
    @(
        "assets.$Channel.json",
        'RELEASES'
    )
}

# The names a release may carry: everything required plus everything optional. Used wherever
# an existing draft or public release is inspected, so that a release built by this pipeline
# is never rejected as carrying "unexpected assets".
function Get-AllowedReleaseAssetNames {
    param(
        [Parameter(Mandatory)][string]$VersionValue,
        [string]$PackageId = 'CycleArc',
        [string]$Channel = 'win'
    )
    @(Get-ExpectedReleaseAssetNames -VersionValue $VersionValue -PackageId $PackageId -Channel $Channel) +
    @(Get-OptionalReleaseAssetNames -Channel $Channel)
}

function Assert-ReleaseNotesForNewDraft {
    param(
        $Release,
        [string]$NotesFile,
        [Parameter(Mandatory)][string]$TagName
    )
    if ($Release) { return $NotesFile }
    if ([string]::IsNullOrWhiteSpace($NotesFile)) {
        throw "A notes file is required when creating a new draft release ($TagName)"
    }
    $notes = Get-Item -LiteralPath $NotesFile -ErrorAction Stop
    if ($notes.PSIsContainer) { throw "Notes path is a directory: $NotesFile" }
    $notes.FullName
}

function Ensure-TagPublished {
    param(
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$VersionValue,
        [Parameter(Mandatory)][string]$RemoteName
    )
    $remoteRecords = @(Get-RemoteTagRecords -RemoteName $RemoteName)
    $remoteSha = Get-TagTargetFromRecords -Records $remoteRecords -TagName $TagName
    $localSha = Get-LocalTagTarget -TagName $TagName
    Assert-TagTargets -TagName $TagName -ExpectedSha $CommitSha -LocalSha $localSha -RemoteSha $remoteSha
    if (!$remoteSha) {
        if (!$localSha) {
            Invoke-NativeCommand -FilePath 'git' -Arguments @('tag', '--annotate', $TagName, $CommitSha, '--message', "CycleArc $VersionValue") | Out-Null
            $localSha = Get-LocalTagTarget -TagName $TagName
            Assert-TagTargets -TagName $TagName -ExpectedSha $CommitSha -LocalSha $localSha
        }
        # A plain push is intentional: an existing remote tag can never be moved.
        Invoke-NativeCommand -FilePath 'git' -Arguments @('push', $RemoteName, "refs/tags/$TagName") | Out-Null
        $remoteRecords = @(Get-RemoteTagRecords -RemoteName $RemoteName)
        $remoteSha = Get-TagTargetFromRecords -Records $remoteRecords -TagName $TagName
        Assert-TagTargets -TagName $TagName -ExpectedSha $CommitSha -LocalSha $localSha -RemoteSha $remoteSha
    }
    $remoteSha
}

function Invoke-Release {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VersionValue,
        [string]$Commitish = 'HEAD',
        [string]$NotesFile,
        [string]$RepositoryName = 'frozenvoice/cyclearc',
        [string]$RemoteName = 'origin',
        [string]$WorkflowFile = '.github/workflows/windows.yml',
        [long]$FullRunId,
        [switch]$DraftOnly,
        [switch]$Preflight,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
    )

    if ($FullRunId -le 0) { throw 'An explicit positive -FullRunId is required; no latest successful artifact is selected' }
    if ($WorkflowFile -cne '.github/workflows/windows.yml') { throw 'Release requires the full .github/workflows/windows.yml workflow' }
    $version = Assert-ReleaseVersion $VersionValue
    $tag = Get-ReleaseTagName $version
    $expectedFileVersion = "$version.0"
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $started = [Diagnostics.Stopwatch]::StartNew()
    $script:ReleaseCurrentPhase = 'release-preflight'
    function Get-ReleaseElapsedStamp {
        $elapsed = $started.Elapsed
        '{0:00}:{1:00}.{2}' -f [int][math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds, [int][math]::Floor($elapsed.Milliseconds / 100.0)
    }
    function Write-ReleasePhase([string]$Phase, [string]$Status) {
        $script:ReleaseCurrentPhase = $Phase
        Write-Host ('[{0}] {1} {2}' -f (Get-ReleaseElapsedStamp), $Phase, $Status)
    }

    Push-Location -LiteralPath $root
    try {
        Write-ReleasePhase 'release-preflight' 'start'
        Assert-ReleaseTools
        Assert-GitHubCliAuth | Out-Null
        if ([string]::IsNullOrWhiteSpace($NotesFile)) {
            $defaultNotes = Join-Path $root "release-notes/$version.md"
            if (Test-Path -LiteralPath $defaultNotes -PathType Leaf) { $NotesFile = $defaultNotes }
        }
        Assert-CleanTrackedTree
        Assert-SourceVersion -RepoRoot $root -Value $version
        $commitSha = Resolve-CommitSha -Commitish $Commitish
        $headSha = Resolve-CommitSha -Commitish 'HEAD'
        if ($commitSha -ine $headSha) {
            throw "Selected commit $commitSha is not the checked-out HEAD $headSha; check out the intended commit before releasing"
        }
        $remoteHeads = @(Get-RemoteHeadRecords -RemoteName $RemoteName)
        Assert-CommitIsRemoteHead -RemoteHeads $remoteHeads -CommitSha $commitSha | Out-Null

        $remoteTags = @(Get-RemoteTagRecords -RemoteName $RemoteName)
        $remoteTagSha = Get-TagTargetFromRecords -Records $remoteTags -TagName $tag
        $localTagSha = Get-LocalTagTarget -TagName $tag
        Assert-TagTargets -TagName $tag -ExpectedSha $commitSha -LocalSha $localTagSha -RemoteSha $remoteTagSha

        $release = Get-ReleaseSnapshot -RepositoryName $RepositoryName -TagName $tag
        if ($release -and ([string]$release.tagName -ne $tag)) {
            throw "GitHub returned release tag '$($release.tagName)' while querying '$tag'"
        }
        $isPublic = $null -ne $release -and ![bool]$release.isDraft
        if ($isPublic -and !$remoteTagSha) {
            throw "Public release '$tag' has no matching remote tag; refusing to repair it"
        }

        $NotesFile = Assert-ReleaseNotesForNewDraft -Release $release -NotesFile $NotesFile -TagName $tag
        $expectedAssetNames = @(Get-ExpectedReleaseAssetNames -VersionValue $version)
        # Name-check an existing release against everything the pipeline may legitimately
        # upload. The narrower required list is what the CI artifact must satisfy below;
        # using it here would reject a normally built release for carrying its own
        # optional outputs.
        $allowedAssetNames = @(Get-AllowedReleaseAssetNames -VersionValue $version)
        if ($release) { Assert-ReleaseAssetNames -Release $release -AllowedNames $allowedAssetNames }

        $full = Get-VerifiedWindowsFullRun -RepositoryName $RepositoryName -RunId $FullRunId -CommitSha $commitSha
        $run = $full.Run
        $artifact = $full.Artifact
        Write-Host "Selected full run $FullRunId attempt $($run.run_attempt), SHA $commitSha, artifact $($artifact.id) ($($artifact.name))."
        Write-ReleasePhase 'release-preflight' 'passed'

        Write-ReleasePhase 'package-verify' 'start'
        $stagingRoot = Join-Path $root 'publish/.release-staging'
        Assert-OwnedDirectory -RepoRoot $root -Target $stagingRoot
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        $staging = Join-Path $stagingRoot ("$tag-" + [guid]::NewGuid().ToString('N'))
        Assert-OwnedDirectory -RepoRoot $root -Target $staging
        New-Item -ItemType Directory -Path $staging | Out-Null
        Invoke-NativeCommand -FilePath 'gh' -Arguments @(
            'run', 'download', [string]$FullRunId, '--repo', $RepositoryName,
            '--name', [string]$artifact.name, '--dir', $staging
        ) | Out-Null
        $assets = Get-PackagedArtifact -StagingDirectory $staging -VersionValue $version -ExpectedFileVersion $expectedFileVersion
        $assetPaths = @($assets.Values | ForEach-Object { $_.Path })
        $allowedAssets = @($assets.Keys | ForEach-Object { [string]$_ })
        $missingExpected = @($expectedAssetNames | Where-Object { $_ -cnotin $allowedAssets })
        if ($missingExpected.Count -gt 0) {
            throw "CI artifact is missing expected release asset(s): $($missingExpected -join ', ')"
        }
        Write-ReleasePhase 'package-verify' 'passed'

        Write-ReleasePhase 'remote-state-verify' 'start'
        # gh downloads by exact run/name, after the API proved that name has one
        # artifact ID. Re-read all gates after download; a rerun or replacement
        # artifact must never silently change the reviewed release source.
        $rechecked = Get-VerifiedWindowsFullRun -RepositoryName $RepositoryName -RunId $FullRunId -CommitSha $commitSha
        if ([int]$rechecked.Run.run_attempt -ne [int]$run.run_attempt -or
            [long]$rechecked.Artifact.id -ne [long]$artifact.id) {
            throw 'Selected Windows full run attempt or artifact identity changed during release verification'
        }
        if ($release) { Assert-ReleaseAssetNames -Release $release -AllowedNames $allowedAssets }

        if ($isPublic) {
            Assert-ReleaseAssets -Release $release -ExpectedAssets $assets -RequireComplete | Out-Null
            $latestTag = Get-LatestReleaseTag -RepositoryName $RepositoryName
            if ($latestTag -cne $tag) {
                throw "Public release '$tag' is not GitHub's latest release (latest is '$latestTag')"
            }
            Write-ReleasePhase 'remote-state-verify' 'passed'
            Write-Host "Already complete: public release $tag matches commit $commitSha and uploaded asset digests."
            return [pscustomobject]@{ Status = 'AlreadyComplete'; Tag = $tag; Commit = $commitSha; FullRunId = $FullRunId; ArtifactId = $artifact.id; Staging = $staging }
        }
        Write-ReleasePhase 'remote-state-verify' 'passed'

        if ($Preflight) {
            Write-Host "Preflight passed: $tag for $commitSha. No GitHub tags, drafts, uploads or publishes were performed."
            return [pscustomobject]@{ Status = 'Preflight'; Tag = $tag; Commit = $commitSha; FullRunId = $FullRunId; ArtifactId = $artifact.id; Staging = $staging }
        }

        # Publish uses the already-verified SHA and CI artifacts. It does not
        # start a new build, test or packaging run.
        Write-ReleasePhase 'publish' 'start'
        Ensure-TagPublished -TagName $tag -CommitSha $commitSha -VersionValue $version -RemoteName $RemoteName | Out-Null

        if (!$release) {
            Invoke-NativeCommand -FilePath 'gh' -Arguments @(
                'release', 'create', $tag, '--repo', $RepositoryName, '--draft',
                '--title', "CycleArc $version", '--notes-file', $NotesFile,
                '--target', $commitSha, '--verify-tag'
            ) | Out-Null
            $release = Get-ReleaseSnapshot -RepositoryName $RepositoryName -TagName $tag
            if (!$release -or !$release.isDraft) { throw "New release '$tag' was not created as a draft" }
            Assert-ReleaseAssetNames -Release $release -AllowedNames $allowedAssets
        }
        else {
            # Draft metadata is repairable.  Pin it to the validated commit while
            # retaining the existing notes and refusing any public-release edit.
            Invoke-NativeCommand -FilePath 'gh' -Arguments @(
                'release', 'edit', $tag, '--repo', $RepositoryName, '--target', $commitSha, '--verify-tag'
            ) | Out-Null
        }

        # Re-read immediately before upload so a concurrent draft publication
        # cannot turn --clobber into a public-release mutation.
        $release = Get-ReleaseSnapshot -RepositoryName $RepositoryName -TagName $tag
        if (!$release -or !$release.isDraft) { throw "Release '$tag' is no longer an unpublished draft" }
        if ([string]$release.targetCommitish -and [string]$release.targetCommitish -ine $commitSha) {
            throw "Draft release '$tag' target is '$($release.targetCommitish)', expected $commitSha"
        }
        Assert-ReleaseAssetNames -Release $release -AllowedNames $allowedAssets
        Invoke-NativeCommand -FilePath 'gh' -Arguments (@(
            'release', 'upload', $tag
        ) + $assetPaths + @('--repo', $RepositoryName, '--clobber')) | Out-Null
        $release = Get-ReleaseSnapshot -RepositoryName $RepositoryName -TagName $tag
        if (!$release -or !$release.isDraft) { throw "Release '$tag' became public before asset validation" }
        Assert-ReleaseAssets -Release $release -ExpectedAssets $assets -RequireComplete | Out-Null
        if ($DraftOnly) {
            Write-ReleasePhase 'publish' 'passed'
            Write-Host "Draft ready: $tag for $commitSha with verified installer assets."
            return [pscustomobject]@{ Status = 'DraftReady'; Tag = $tag; Commit = $commitSha; FullRunId = $FullRunId; ArtifactId = $artifact.id; Staging = $staging }
        }

        # Publishing remains the final mutation, after exact GitHub digests are
        # verified. Callers that need review time can pass -DraftOnly.
        $beforePublish = Get-VerifiedWindowsFullRun -RepositoryName $RepositoryName -RunId $FullRunId -CommitSha $commitSha
        if ([int]$beforePublish.Run.run_attempt -ne [int]$run.run_attempt -or
            [long]$beforePublish.Artifact.id -ne [long]$artifact.id) {
            throw 'Selected Windows full run attempt or artifact identity changed before publication'
        }
        Invoke-NativeCommand -FilePath 'gh' -Arguments @(
            'release', 'edit', $tag, '--repo', $RepositoryName, '--draft=false', '--latest', '--verify-tag'
        ) | Out-Null
        $published = Get-ReleaseSnapshot -RepositoryName $RepositoryName -TagName $tag
        if (!$published -or [bool]$published.isDraft) { throw "Release '$tag' did not become public" }
        Assert-ReleaseAssets -Release $published -ExpectedAssets $assets -RequireComplete | Out-Null
        $latestTag = Get-LatestReleaseTag -RepositoryName $RepositoryName
        if ($latestTag -cne $tag) {
            throw "Published release '$tag' is not GitHub's latest release (latest is '$latestTag')"
        }
        Write-ReleasePhase 'publish' 'passed'
        Write-Host "Published $tag for $commitSha with verified installer assets."
        [pscustomobject]@{ Status = 'Published'; Tag = $tag; Commit = $commitSha; FullRunId = $FullRunId; ArtifactId = $artifact.id; Staging = $staging }
    }
    catch {
        Write-Host ("Failed at: {0}" -f $script:ReleaseCurrentPhase)
        Write-Host ("Elapsed: {0}" -f $started.Elapsed.ToString('mm\:ss\.fff'))
        throw
    }
    finally {
        Pop-Location
    }
}

if (!$LoadOnly -and $MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Usage: pwsh -NoProfile -File ./scripts/Release.ps1 -Version <version> -FullRunId <run-id> [-Commit <sha-or-ref>] [-NotesPath <file>] [-DraftOnly] [-Preflight]'
    }
    Invoke-Release -VersionValue $Version -Commitish $Commit -NotesFile $NotesPath -RepositoryName $Repository -RemoteName $Remote -WorkflowFile $Workflow -FullRunId $FullRunId -DraftOnly:$DraftOnly -Preflight:$Preflight
}
