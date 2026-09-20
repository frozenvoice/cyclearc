[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This is the last pre-Cursor Core revision. Keep the full hash here so the check
# cannot silently drift to a different historical reader.
$HistoricalCommit = '36dcf1fdee0cd05381b323886744d999c8f1f567'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$ArtifactRoot = Join-Path $RepoRoot ('artifacts\cursor-registry-compat-' + [Guid]::NewGuid().ToString('N'))
$HarnessRoot = Join-Path $ArtifactRoot 'historical-reader'

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

try {
    New-Item -ItemType Directory -Path $HarnessRoot -Force | Out-Null

    $historicalPath = "${HistoricalCommit}:src/CycleArc.Core/Codex/CodexAccountStore.cs"
    $historicalStore = & git -C $RepoRoot show $historicalPath
    if ($LASTEXITCODE -ne 0 -or $historicalStore.Count -eq 0) {
        throw "Could not extract the historical account store at $HistoricalCommit."
    }
    Write-Utf8NoBom (Join-Path $HarnessRoot 'CodexAccountStore.cs') (($historicalStore -join [Environment]::NewLine) + [Environment]::NewLine)

    $historicalUsagePath = "${HistoricalCommit}:src/CycleArc.Core/Providers/Usage/IUsageProvider.cs"
    $historicalUsage = & git -C $RepoRoot show $historicalUsagePath
    if ($LASTEXITCODE -ne 0 -or ($historicalUsage -join "`n") -notmatch 'enum\s+UsageProviderId\s*\{\s*Codex\s*,\s*Claude\s*\}') {
        throw "Could not verify the historical Codex/Claude provider enum at $HistoricalCommit."
    }

    # The historical store is compiled unchanged. These are only the small types it
    # references; no current provider enum or current account-store implementation is
    # copied into the harness.
    Write-Utf8NoBom (Join-Path $HarnessRoot 'CompatibilityTypes.cs') @'
namespace CycleArc.Providers.Usage
{
    public enum UsageProviderId { Codex, Claude }
}

namespace CycleArc.Codex
{
    using CycleArc.Providers.Usage;

    public sealed record CodexAccountProfile(string Id, string HomePath, string Label, bool IsManaged = false)
    {
        public UsageProviderId Provider { get; init; } = UsageProviderId.Codex;
    }

    public static class CodexHomeDiscovery
    {
        public static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
    }
}

namespace CycleArc.Services
{
    public static class AppPaths
    {
        public static string Root => throw new InvalidOperationException(
            "The historical compatibility harness must construct CodexAccountStore with its isolated root.");
    }
}
'@

    Write-Utf8NoBom (Join-Path $HarnessRoot 'CompatibilityHarness.csproj') @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="CodexAccountStore.cs" />
    <Compile Include="CompatibilityTypes.cs" />
    <Compile Include="CompatibilityHarness.cs" />
  </ItemGroup>
</Project>
'@

    Write-Utf8NoBom (Join-Path $HarnessRoot 'CompatibilityHarness.cs') @'
using System.Security.Cryptography;
using System.Text.Json;
using CycleArc.Codex;

if (args.Length != 1)
    throw new ArgumentException("Expected one isolated fixture directory.");

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var accountDirectory = Directory.CreateDirectory(Path.Combine(root, "accounts", "11111111111111111111111111111111"));
var cursorDirectory = Directory.CreateDirectory(Path.Combine(root, "accounts", "22222222222222222222222222222222"));
var sentinelPaths = new[]
{
    Path.Combine(root, "settings.json"),
    Path.Combine(root, "codex-snapshot.json"),
    Path.Combine(accountDirectory.FullName, "claude-connection.json"),
    Path.Combine(cursorDirectory.FullName, "cursor-connection.json"),
    Path.Combine(cursorDirectory.FullName, "cursor-usage.json")
};
var sentinelBytes = new[]
{
    "settings-before-cursor"u8.ToArray(),
    "snapshot-before-cursor"u8.ToArray(),
    "binding-before-cursor"u8.ToArray(),
    "cursor-binding-fixture"u8.ToArray(),
    "cursor-usage-fixture"u8.ToArray()
};
for (var i = 0; i < sentinelPaths.Length; i++)
    File.WriteAllBytes(sentinelPaths[i], sentinelBytes[i]);

var legacyProfiles = new[]
{
    new { Id = "default", HomePath = Path.Combine(root, "codex-home"), Label = "Main", IsManaged = false, Provider = 0 },
    new { Id = "33333333333333333333333333333333", HomePath = "", Label = "Claude", IsManaged = false, Provider = 1 }
};
var validV2 = JsonSerializer.Serialize(new
{
    Version = 2,
    SelectedId = "default",
    Profiles = legacyProfiles,
    IgnoredHomes = Array.Empty<string>()
});
var primaryPath = Path.Combine(root, "codex-accounts.json");
var backupPath = primaryPath + ".bak";
File.WriteAllText(primaryPath, validV2);
File.WriteAllText(backupPath, validV2);

// First prove the extracted reader accepts a genuine pre-Cursor v2 registry. This
// prevents a malformed fixture from being mistaken for a compatibility failure.
var baselineSnapshot = Snapshot(root);
var baselineStore = new CodexAccountStore(root);
var baseline = baselineStore.LoadOrMigrate(Path.Combine(root, "codex-home"));
Require(baseline.Version == 2 && baseline.Profiles.Count == 2,
    "Historical reader rejected the valid v2 Codex/Claude baseline.");
RequireSnapshotUnchanged(root, baselineSnapshot);

// A real first v2-to-v3 transition leaves the previous-good profile set in the backup,
// with only its version raised. The primary has the new Cursor profile. Both documents
// are deliberately numeric, matching the historical enum's member values.
var v3Primary = JsonSerializer.Serialize(new
{
    Version = 3,
    SelectedId = "default",
    Profiles = new[]
    {
        legacyProfiles[0], legacyProfiles[1],
        new { Id = "22222222222222222222222222222222", HomePath = "", Label = "Cursor", IsManaged = false, Provider = 2 }
    },
    IgnoredHomes = Array.Empty<string>()
});
var v3Backup = JsonSerializer.Serialize(new
{
    Version = 3,
    SelectedId = "default",
    Profiles = legacyProfiles,
    IgnoredHomes = Array.Empty<string>()
});
File.WriteAllText(primaryPath, v3Primary);
File.WriteAllText(backupPath, v3Backup);

var v3Snapshot = Snapshot(root);
var store = new CodexAccountStore(root);
Exception? failure = null;
try
{
    _ = store.LoadOrMigrate(Path.Combine(root, "codex-home"));
}
catch (Exception ex)
{
    failure = ex;
}

Require(failure is InvalidDataException,
    $"Historical reader did not fail closed for v3 registry: {failure?.GetType().FullName ?? "no exception"}.");
RequireSnapshotUnchanged(root, v3Snapshot);
Require(File.ReadAllText(primaryPath) == v3Primary, "Historical reader changed the v3 primary registry.");
Require(File.ReadAllText(backupPath) == v3Backup, "Historical reader changed the v3 backup registry.");

// If the new primary is damaged, the old reader still tries the v3 backup. It must
// reject that rollback candidate too and leave both bytes untouched.
File.WriteAllText(primaryPath, "{ damaged primary }");
var corruptPrimarySnapshot = Snapshot(root);
failure = null;
try
{
    _ = store.LoadOrMigrate(Path.Combine(root, "codex-home"));
}
catch (Exception ex)
{
    failure = ex;
}
Require(failure is InvalidDataException,
    $"Historical reader did not fail closed when the v3 primary was damaged: {failure?.GetType().FullName ?? "no exception"}.");
RequireSnapshotUnchanged(root, corruptPrimarySnapshot);
Require(File.ReadAllText(backupPath) == v3Backup, "Historical reader changed the v3 backup during fallback.");

Console.WriteLine("PASS: pre-Cursor reader accepted v2, rejected v3 primary and backup fallback, and preserved registry, settings, snapshot, and binding bytes.");

static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(path => path, Sha256, StringComparer.OrdinalIgnoreCase);
static void RequireSnapshotUnchanged(string root, IReadOnlyDictionary<string, string> before)
{
    var after = Snapshot(root);
    Require(before.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
        "Historical reader changed the isolated fixture's file set.");
    foreach (var entry in before)
        Require(after[entry.Key] == entry.Value, $"Historical reader changed {entry.Key}.");
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'@

    $fixtureRoot = Join-Path $ArtifactRoot 'fixture'
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    $projectPath = Join-Path $HarnessRoot 'CompatibilityHarness.csproj'
    & dotnet run --project $projectPath -c Release -- $fixtureRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Historical Cursor registry compatibility harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    $artifactsParent = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
    $artifactCandidate = [IO.Path]::GetFullPath($ArtifactRoot)
    $artifactPrefix = $artifactsParent + [IO.Path]::DirectorySeparatorChar
    $safeName = [IO.Path]::GetFileName($artifactCandidate).StartsWith('cursor-registry-compat-', [StringComparison]::Ordinal)
    if ($artifactCandidate.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $artifactCandidate -ne $artifactsParent -and $safeName -and
        (Test-Path -LiteralPath $artifactCandidate -PathType Container)) {
        Remove-Item -LiteralPath $artifactCandidate -Recurse -Force -ErrorAction SilentlyContinue
    }
}
