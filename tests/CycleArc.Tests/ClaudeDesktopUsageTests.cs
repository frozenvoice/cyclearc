using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeDesktopUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");
    private const string Organization = "73ea74ee-7e58-427a-8593-7bc000000001";

    [Fact]
    public async Task DesktopUsageReplacesTheOldCliSampleWithoutAResponseOrInventedResets()
    {
        using var data = new Data();
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(4, Now.AddHours(5).ToUnixTimeSeconds()),
            new(3, Now.AddDays(7).ToUnixTimeSeconds())), Now.AddHours(-1), default);
        var service = data.Service(data.Collector());
        var result = await service.RefreshAsync(default);
        Assert.Equal(CodexQuotaStatus.Available, result.Snapshot.Status);
        Assert.Equal("claude-desktop-history", result.Snapshot.TechnicalDetail);
        Assert.Equal(new double?[] { 15, 7 }, result.Snapshot.Windows.Select(window => window.UsedPercent));
        Assert.All(result.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
        Assert.Equal(Now, result.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(Now.AddHours(-1), data.Inbox.Read().State!.LastReceivedAt);
    }

    [Fact]
    public async Task RepeatedPollingDoesNotRenewTheReceiptOrRunAuthenticationAgain()
    {
        using var data = new Data();
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        var receipt = service.Snapshot;
        var notifications = 0;
        service.Changed += _ => notifications++;
        data.Clock.UtcNow = Now.AddDays(1);
        await service.RefreshAsync(default);
        Assert.Same(receipt, service.Snapshot);
        Assert.Equal(0, notifications);
        Assert.Equal(1, data.AuthCalls);
    }

    [Fact]
    public async Task ANewDesktopSampleIsVerifiedAndReceivedWithItsOwnTimestamp()
    {
        using var data = new Data();
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(15);
        data.Read = new(new(data.Clock.UtcNow, 18.5, 7.2));
        await service.RefreshAsync(default);
        Assert.Equal(18.5, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(2, data.AuthCalls);
    }

    [Fact]
    public async Task NewCliSampleWinsButAnOlderCliCallbackCannotHideDesktopUsage()
    {
        using var data = new Data();
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(12, Now.ToUnixTimeSeconds()), null), Now.AddSeconds(-1), default);
        await service.RefreshAsync(default);
        Assert.Equal(15, service.Snapshot.Windows[0].UsedPercent);
        data.Clock.UtcNow = Now.AddSeconds(1);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(19, Now.AddHours(5).ToUnixTimeSeconds()), null), data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Equal(19, service.Snapshot.Windows[0].UsedPercent);
        Assert.NotNull(service.Snapshot.Windows[0].ResetsAt);
        Assert.Single(service.Snapshot.Windows);
    }

    [Fact]
    public async Task MalformedHistoryRetainsTheLastGoodValuesAndReceiptAcrossRestart()
    {
        using var data = new Data();
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        data.Read = new(null, true);
        data.Clock.UtcNow = Now.AddMinutes(1);
        var restarted = data.Service(data.Collector());
        Assert.False(restarted.Snapshot.HasUsablePercentages);
        await restarted.RefreshAsync(default);
        Assert.Equal(CodexQuotaStatus.Stale, restarted.Snapshot.Status);
        Assert.Equal(15, restarted.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(Now, restarted.Snapshot.LastSuccessfulRefresh);
        Assert.Equal("claude-desktop-unavailable", restarted.Snapshot.TechnicalDetail);
    }

    [Fact]
    public async Task MissingDesktopWindowStaysUnknownAndNeverBorrowsTheCliWindow()
    {
        using var data = new Data();
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(3, Now.ToUnixTimeSeconds()),
            new(20, Now.AddDays(7).ToUnixTimeSeconds())), Now.AddMinutes(-1), default);
        data.Read = new(new(Now, 0, null));
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        Assert.Single(service.Snapshot.Windows);
        Assert.Equal(0, service.Snapshot.Windows[0].UsedPercent);
        Assert.Null(service.Snapshot.Windows[0].ResetsAt);
    }

    [Fact]
    public async Task OrganizationOrIdentityMismatchCannotImportDesktopUsage()
    {
        using var data = new Data();
        data.Identity = data.Identity with { OrganizationId = "73ea74ee-7e58-427a-8593-7bc000000002" };
        var collector = data.Collector();
        var result = await collector.RefreshAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.Equal("claude-desktop-identity-unverified", result.Failure);
        Assert.Equal(0, data.ReadCalls);
        Assert.False(File.Exists(data.CachePath));
        await collector.RefreshAsync(data.Binding, default);
        Assert.Equal(1, data.AuthCalls);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("enterprise")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task SharedOrganizationPlansCannotBeAttributedByOrganizationAlone(string? plan)
    {
        using var data = new Data();
        data.Identity = data.Identity with { Plan = plan };
        var result = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.Equal(0, data.ReadCalls);
    }

    [Fact]
    public async Task ANewSampleCannotBeAttributedAfterTheCliAccountChanges()
    {
        using var data = new Data();
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        data.Identity = data.Identity with { Email = "different@example.invalid" };
        data.Clock.UtcNow = Now.AddMinutes(15);
        data.Read = new(new(data.Clock.UtcNow, 44, 21));
        await service.RefreshAsync(default);
        Assert.Equal(15, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal(Now, service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task DisconnectDuringIdentityVerificationRejectsTheLateResultAndDoesNotWrite()
    {
        using var data = new Data();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = data.Collector(async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return data.Identity;
        });
        var service = data.Service(collector);
        var pending = service.RefreshAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        data.Connections.Save(data.Binding with { Disconnected = true });
        release.SetResult();
        await pending;
        Assert.Equal(CodexQuotaStatus.SignedOut, service.Snapshot.Status);
        Assert.False(service.Snapshot.HasUsablePercentages);
        Assert.False(File.Exists(data.CachePath));
    }

    [Fact]
    public async Task GenerationRotationDuringReadCannotCommitOrDisplayTheOldGeneration()
    {
        using var data = new Data();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var collector = new ClaudeDesktopUsageCollector(data.Accounts, data.Profile.Id,
            (_, _) => Task.FromResult(data.Identity), data.Clock, (_, _) =>
            {
                started.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
                return data.Read;
            });
        var pending = collector.RefreshAsync(data.Binding, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        release.Set();
        Assert.Null((await pending).Sample);
        Assert.False(File.Exists(data.CachePath));
    }

    [Fact]
    public async Task CancellationPreservesTheCacheAndReleasesTheRefreshGate()
    {
        using var data = new Data();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = data.Collector(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return data.Identity;
        });
        var service = data.Service(collector);
        using var cancel = new CancellationTokenSource();
        var pending = service.RefreshAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(service.IsRefreshing);
        Assert.False(File.Exists(data.CachePath));
        data.Connections.Save(data.Binding with { Disconnected = true });
        await service.RefreshAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CodexQuotaStatus.SignedOut, service.Snapshot.Status);
    }

    [Fact]
    public async Task DesktopReceiptsCannotClearAnExplicitAuthenticationFailure()
    {
        using var data = new Data();
        await new ClaudeFailureStore(data.Accounts).RecordAsync(data.Profile.Id, data.Binding.BindingGeneration!,
            ClaudeFailureKind.AuthRequired, Now.AddMinutes(-1));
        var service = data.Service(data.Collector());
        await service.RefreshAsync(default);
        Assert.Equal(15, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
    }

    [Fact]
    public async Task CacheContainsOnlyProjectedUsageAndBindingMetadataAndRecoversItsBackup()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(15);
        data.Read = new(new(data.Clock.UtcNow, 17, 8));
        await collector.RefreshAsync(data.Binding, default);
        var json = File.ReadAllText(data.CachePath);
        Assert.DoesNotContain(Organization, json);
        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("resetsAt", json);
        Assert.DoesNotContain("samples", json);
        File.WriteAllText(data.CachePath, "partial{");
        data.Read = new(null, true);
        var recovered = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Equal(Now, recovered.Sample!.ObservedAt);
        Assert.Equal(15, recovered.Sample.FiveHour);
        Assert.Equal("claude-desktop-unavailable", recovered.Failure);
    }

    [Fact]
    public async Task ReadOnlyUsageIdentityCheckNeverInstallsStatusLineOrChangesSettings()
    {
        using var data = new Data();
        var cli = new AuthCli(data.Identity);
        var connections = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var before = File.ReadAllText(data.Connections.PathName);
        var result = await connections.VerifyUsageIdentityAsync(data.Binding, default);
        Assert.Equal(data.Identity, result);
        Assert.False(cli.Login);
        Assert.Equal(data.Binding.ConfigDirectory, cli.ConfigDirectory);
        Assert.Equal(before, File.ReadAllText(data.Connections.PathName));
        Assert.False(File.Exists(Path.Combine(data.Binding.ConfigDirectory, "settings.json")));
    }

    private sealed class AuthCli(ClaudeAuthentication identity) : IClaudeCli
    {
        public bool Login { get; private set; }
        public string? ConfigDirectory { get; private set; }
        public string? FindExecutable() => null;
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
        { Login = login; ConfigDirectory = configDirectory; return Task.FromResult(identity); }
    }

    private sealed class Data : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-desktop-" + Guid.NewGuid().ToString("N"));
        public MutableClock Clock { get; } = new(Now);
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public ClaudeConnectionStore Connections { get; }
        public ClaudeConnectionBinding Binding { get; }
        public ClaudeStatusLineStore Inbox { get; }
        public string CachePath => Path.Combine(Root, "accounts", Profile.Id, "claude-desktop-usage.json");
        public ClaudeAuthentication Identity { get; set; } = new(ClaudeAuthStatus.SignedIn,
            "desktop@example.invalid", "pro", OrganizationId: Organization);
        public ClaudeDesktopUsageRead Read { get; set; } = new(new(Now, 15, 7));
        public int AuthCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Desktop fixture");
            Accounts.Save(initial with { Version = 2, Profiles = initial.Profiles.Append(Profile).ToArray() });
            Binding = new(2, Profile.Id, Path.Combine(Root, "claude-home"), Path.Combine(Root, "claude.exe"),
                false, Identity.StableFingerprint!, Now.AddDays(-1),
                BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro");
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
            Inbox = new(Accounts.ClaudeStatusLinePath(Profile.Id), Profile.Id);
        }
        public ClaudeDesktopUsageCollector Collector(Func<ClaudeConnectionBinding, CancellationToken, Task<ClaudeAuthentication>>? auth = null) =>
            new(Accounts, Profile.Id, auth ?? ((_, _) => { AuthCalls++; return Task.FromResult(Identity); }), Clock,
                (org, _) => { Assert.Equal(Organization, org); ReadCalls++; return Read; });
        public ClaudeQuotaService Service(IClaudeDesktopUsageSource desktop) =>
            new(Inbox, Clock, Connections.Read, failureStore: new(Accounts), desktop: desktop);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
