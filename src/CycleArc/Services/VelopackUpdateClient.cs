using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using CycleArc.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

[assembly: InternalsVisibleTo("CycleArc.UiSmoke")]

namespace CycleArc.Services;

/// <summary>Only official, stable, installed releases can update. No account credentials are used.</summary>
public sealed class VelopackUpdateClient : IAppUpdateClient
{
    private readonly UpdateManager? _manager;
    private readonly bool _enabled;
    private readonly string? _packagesDirectory;
    private readonly UpdateDownloader _downloader = new();
    private UpdateInfo? _candidate;
    public bool IsInstalled => _enabled && _manager?.IsInstalled == true;
    public string CurrentVersion => _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public VelopackUpdateClient()
    {
        if (!InstalledApp.SupportsUpdates) return;
        _enabled = true;
        _packagesDirectory = VelopackLocator.Current.PackagesDir;
        _manager = new UpdateManager(
            new GithubSource("https://github.com/frozenvoice/cyclearc", null, prerelease: false, _downloader),
            new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = 0 });
    }

    internal VelopackUpdateClient(UpdateManager manager, string packagesDirectory)
    {
        _manager = manager;
        _packagesDirectory = packagesDirectory;
        _enabled = true;
    }

    public async Task<AppUpdateRelease?> CheckAsync(CancellationToken token)
    {
        if (!IsInstalled) return null;
        token.ThrowIfCancellationRequested();
        _downloader.MetadataToken = token;
        _candidate = null;
        var candidate = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (candidate is null || candidate.IsDowngrade) return null;
        ValidateAsset(candidate.TargetFullRelease);
        _candidate = new UpdateInfo(candidate.TargetFullRelease, isDowngrade: false);
        return new(candidate.TargetFullRelease.Version.ToString(), candidate.TargetFullRelease.NotesMarkdown ?? "");
    }

    public async Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token)
    {
        var candidate = RequireCandidate(release);
        var asset = candidate.TargetFullRelease;
        ValidateAsset(asset);
        var cachedPath = Path.Combine(_packagesDirectory!, asset.FileName);
        if (File.Exists(cachedPath))
        {
            try { await VerifyPackageAsync(asset, token).ConfigureAwait(false); }
            catch (InvalidDataException) { File.Delete(cachedPath); }
        }
        await _manager!.DownloadUpdatesAsync(candidate, progress.Report, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Velopack skips validation when a cached full package already exists.
        // Revalidate even that path, and again immediately before handing off to Update.exe.
        await VerifyPackageAsync(candidate.TargetFullRelease, token).ConfigureAwait(false);
    }

    public void ApplyOnExit(AppUpdateRelease release)
    {
        var candidate = RequireCandidate(release);
        VerifyPackageAsync(candidate.TargetFullRelease, CancellationToken.None).GetAwaiter().GetResult();
        var asset = candidate.TargetFullRelease;
        ManagedUpdateSupervisor.Prepare(VelopackLocator.Current.RootAppDir
            ?? throw new IOException("Installation root unavailable."), asset.FileName, asset.Size,
            asset.SHA256, release.Version, CurrentVersion);
    }

    private UpdateInfo RequireCandidate(AppUpdateRelease release)
    {
        if (!IsInstalled || _candidate is null || _candidate.TargetFullRelease.Version.ToString() != release.Version)
            throw new InvalidOperationException("The update selection is no longer available.");
        return _candidate;
    }

    private static void ValidateAsset(VelopackAsset asset)
    {
        if (asset.PackageId != "CycleArc" || asset.Type != VelopackAssetType.Full
            || string.IsNullOrWhiteSpace(asset.FileName) || Path.GetFileName(asset.FileName) != asset.FileName
            || !asset.FileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase)
            || asset.Size <= 0 || asset.Size > 1024L * 1024 * 1024
            || asset.SHA256 is not { Length: 64 } || !asset.SHA256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release package metadata is invalid.");
    }

    private Task VerifyPackageAsync(VelopackAsset asset, CancellationToken token)
    {
        ValidateAsset(asset);
        var root = _packagesDirectory ?? throw new IOException("Package directory unavailable.");
        return UpdatePackageVerifier.VerifyAsync(root, asset.FileName, asset.Size, asset.SHA256, token);
    }

    private sealed class UpdateDownloader : IFileDownloader
    {
        private readonly HttpClientFileDownloader _files = new();
        public CancellationToken MetadataToken { get; set; }

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30,
            CancellationToken cancelToken = default)
        {
            RequireHttps(url);
            return _files.DownloadFile(url, targetFile, progress, headers, 15 * 60, cancelToken);
        }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            RequireHttps(url);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CycleArc-Updater/0.6");
            if (headers is not null)
                foreach (var header in headers) client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            return await client.GetByteArrayAsync(url, MetadataToken).ConfigureAwait(false);
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

        private static void RequireHttps(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Updates require HTTPS.");
        }
    }
}
