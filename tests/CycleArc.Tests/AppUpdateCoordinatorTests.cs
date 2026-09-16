using CycleArc.Updates;
using System.Security.Cryptography;

namespace CycleArc.Tests;

public sealed class AppUpdateCoordinatorTests
{
    [Fact]
    public async Task UninstalledBuildDoesNotContactUpdateSource()
    {
        var client = new FakeClient { IsInstalled = false };
        var coordinator = new AppUpdateCoordinator(client);

        await coordinator.CheckAsync();

        Assert.Equal(AppUpdateState.Disabled, coordinator.State);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public async Task RepeatedCheckClicksShareNoSecondOperation()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AppUpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            CheckHandler = async token =>
            {
                entered.SetResult(null);
                return await release.Task.WaitAsync(token);
            },
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);

        var first = coordinator.CheckAsync();
        await entered.Task;
        var second = coordinator.CheckAsync();
        release.SetResult(new AppUpdateRelease("1.1.0", "new"));

        Assert.True(second.IsCompletedSuccessfully);
        await first;
        Assert.Equal(1, client.CheckCalls);
        Assert.Equal(AppUpdateState.Available, coordinator.State);
    }

    [Fact]
    public async Task CheckRetriesAndReportsSafeCheckFailureCategory()
    {
        var client = new FakeClient();
        client.CheckHandler = _ =>
        {
            if (client.CheckCalls == 1)
                throw new IOException("transport detail must not escape");
            return Task.FromResult<AppUpdateRelease?>(new("1.1.0", "new"));
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);

        await coordinator.CheckAsync();

        Assert.Equal(2, client.CheckCalls);
        Assert.Equal(AppUpdateState.Available, coordinator.State);
        Assert.Null(coordinator.Error);

        client.CheckHandler = _ => throw new InvalidOperationException("offline");
        await coordinator.CheckAsync();

        Assert.Equal(4, client.CheckCalls);
        Assert.Equal(AppUpdateState.Failed, coordinator.State);
        Assert.Equal(AppUpdateError.CheckFailed, coordinator.Error);
        Assert.Null(coordinator.Release);
    }

    [Fact]
    public async Task DownloadRetriesAndOnlyCompletesAfterSuccessfulAttempt()
    {
        var client = new FakeClient { CheckResult = new AppUpdateRelease("1.1.0", "new") };
        client.DownloadHandler = (release, progress, _) =>
        {
            Assert.Equal("1.1.0", release.Version);
            client.LastProgress = progress;
            if (client.DownloadCalls == 1)
            {
                progress.Report(28);
                throw new IOException("first package fetch failed");
            }
            progress.Report(84);
            return Task.CompletedTask;
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);
        await coordinator.CheckAsync();

        await coordinator.DownloadAsync();

        Assert.Equal(2, client.DownloadCalls);
        Assert.Equal(AppUpdateState.Ready, coordinator.State);
        Assert.Equal(100, coordinator.Progress);
        Assert.Null(coordinator.Error);
    }

    [Fact]
    public async Task RepeatedDownloadClicksDoNotStartAnotherDownload()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            CheckResult = new AppUpdateRelease("1.1.0", "new"),
            DownloadHandler = async (_, _, token) =>
            {
                entered.SetResult(null);
                await release.Task.WaitAsync(token);
            },
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);
        await coordinator.CheckAsync();

        var first = coordinator.DownloadAsync();
        await entered.Task;
        var second = coordinator.DownloadAsync();
        release.SetResult(null);

        Assert.True(second.IsCompletedSuccessfully);
        await first;
        Assert.Equal(1, client.DownloadCalls);
        Assert.Equal(AppUpdateState.Ready, coordinator.State);
    }

    [Fact]
    public async Task CancellationCannotLeaveDownloadReadyOrAcceptStaleProgress()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { CheckResult = new AppUpdateRelease("1.1.0", "new") };
        client.DownloadHandler = async (_, progress, token) =>
        {
            client.LastProgress = progress;
            entered.SetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);
        await coordinator.CheckAsync();
        using var cts = new CancellationTokenSource();
        var download = coordinator.DownloadAsync(cts.Token);
        await entered.Task;

        cts.Cancel();
        await download;
        client.LastProgress!.Report(100);

        Assert.Equal(AppUpdateState.Failed, coordinator.State);
        Assert.Equal(AppUpdateError.Canceled, coordinator.Error);
        Assert.NotEqual(AppUpdateState.Ready, coordinator.State);
        Assert.Equal(0, coordinator.Progress);
        Assert.False(coordinator.ApplyOnExit());

        client.DownloadHandler = (_, _, _) => Task.CompletedTask;
        await coordinator.DownloadAsync();
        Assert.Equal(AppUpdateState.Ready, coordinator.State);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("1.0.0", "2.0.0-beta.1")]
    [InlineData("1.0.0", "2.0")]
    public async Task EqualDowngradePrereleaseAndMalformedReleasesAreIgnored(
        string currentVersion, string advertisedVersion)
    {
        var client = new FakeClient
        {
            CurrentVersion = currentVersion,
            CheckResult = new AppUpdateRelease(advertisedVersion, "candidate"),
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);

        await coordinator.CheckAsync();

        Assert.Equal(AppUpdateState.Idle, coordinator.State);
        Assert.Null(coordinator.Release);
        Assert.Null(coordinator.Error);
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task StableReleaseCanUpdateAnEqualNumericPrereleaseBuild()
    {
        var client = new FakeClient
        {
            CurrentVersion = "1.2.3-rc.1",
            CheckResult = new AppUpdateRelease("1.2.3", "stable"),
        };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);

        await coordinator.CheckAsync();

        Assert.Equal(AppUpdateState.Available, coordinator.State);
        Assert.Equal("1.2.3", coordinator.Release!.Version);
    }

    [Fact]
    public async Task ApplyIsPossibleOnlyAfterCompletedDownload()
    {
        var client = new FakeClient { CheckResult = new AppUpdateRelease("1.1.0", "new") };
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);

        Assert.False(coordinator.ApplyOnExit());
        await coordinator.CheckAsync();
        Assert.False(coordinator.ApplyOnExit());
        await coordinator.DownloadAsync();

        Assert.True(coordinator.ApplyOnExit());
        Assert.Equal(AppUpdateState.Applying, coordinator.State);
        Assert.Equal(1, client.ApplyCalls);
        Assert.False(coordinator.ApplyOnExit());
    }

    [Fact]
    public async Task ApplyFailureAllowsDownloadRevalidationBeforeRetry()
    {
        var client = new FakeClient { CheckResult = new AppUpdateRelease("1.1.0", "new") };
        client.ApplyHandler = () => throw new IOException("staging failed");
        var coordinator = new AppUpdateCoordinator(client, retryDelay: TimeSpan.Zero);
        await coordinator.CheckAsync();
        await coordinator.DownloadAsync();

        Assert.False(coordinator.ApplyOnExit());
        Assert.Equal(AppUpdateError.ApplyFailed, coordinator.Error);

        await coordinator.DownloadAsync();
        Assert.Equal(AppUpdateState.Ready, coordinator.State);
    }

    [Fact]
    public async Task InvalidCurrentVersionStopsBeforeNetworkCheck()
    {
        var client = new FakeClient { CurrentVersion = "1.2" };
        var coordinator = new AppUpdateCoordinator(client);

        await coordinator.CheckAsync();

        Assert.Equal(AppUpdateState.Failed, coordinator.State);
        Assert.Equal(AppUpdateError.InvalidCurrentVersion, coordinator.Error);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public async Task PackageVerifierAcceptsMatchingFreshAndCachedPackage()
    {
        using var directory = new TemporaryDirectory();
        var bytes = new byte[4096];
        RandomNumberGenerator.Fill(bytes);
        var fileName = "CycleArc-1.1.0-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, fileName), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        await UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, bytes.Length, hash);
        // A cached package follows the same path and integrity checks as a fresh download.
        await UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, bytes.Length, hash);
    }

    [Fact]
    public async Task PackageVerifierRejectsTamperingBeforeApply()
    {
        using var directory = new TemporaryDirectory();
        var bytes = new byte[512];
        var fileName = "CycleArc-1.1.0-full.nupkg";
        var path = Path.Combine(directory.FullName, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        bytes[0] = 1;
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, bytes.Length, hash));
    }

    [Theory]
    [InlineData("..\\CycleArc.nupkg")]
    [InlineData("nested/CycleArc.nupkg")]
    [InlineData("CycleArc.nupkg")]
    [InlineData("CycleArc.zip")]
    [InlineData("CycleArc.nupkg.sha512")]
    [InlineData("CycleArc:stream-full.nupkg")]
    public async Task PackageVerifierRejectsTraversalAndNonFullPackageNames(string fileName)
    {
        using var directory = new TemporaryDirectory();
        var hash = new string('A', 64);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, 1, hash));
    }

    [Fact]
    public async Task PackageVerifierRejectsUnsafeManifestMetadata()
    {
        using var directory = new TemporaryDirectory();
        var fileName = "CycleArc-1.1.0-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, fileName), [1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, UpdatePackageVerifier.MaximumPackageSize + 1, new string('A', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyAsync(directory.FullName, fileName, 1, "not-a-sha256"));
    }

    private sealed class FakeClient : IAppUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion { get; set; } = "1.0.0";
        public AppUpdateRelease? CheckResult { get; set; }
        public Func<CancellationToken, Task<AppUpdateRelease?>>? CheckHandler { get; set; }
        public Func<AppUpdateRelease, IProgress<int>, CancellationToken, Task>? DownloadHandler { get; set; }
        public Action? ApplyHandler { get; set; }
        public IProgress<int>? LastProgress { get; set; }
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int ApplyCalls { get; private set; }

        public Task<AppUpdateRelease?> CheckAsync(CancellationToken token)
        {
            CheckCalls++;
            return CheckHandler?.Invoke(token) ?? Task.FromResult(CheckResult);
        }

        public Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            DownloadCalls++;
            LastProgress = progress;
            return DownloadHandler?.Invoke(release, progress, token) ?? Task.CompletedTask;
        }

        public void ApplyOnExit(AppUpdateRelease release)
        {
            ApplyCalls++;
            ApplyHandler?.Invoke();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("cyclearc-update-");

        public string FullName => _directory.FullName;

        public void Dispose()
        {
            if (_directory.Exists)
                _directory.Delete(recursive: true);
        }
    }
}
