using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class ClaudeLiveIntegrationTests
{
    [Fact]
    public async Task PassiveRefreshDoesNotCallLiveAndActiveRefreshPublishesResetTimes()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow,
                new(46, data.Clock.UtcNow.AddHours(5)),
                new(11, data.Clock.UtcNow.AddDays(7))))
        };
        var service = data.Service(live);

        await service.RefreshAsync(default);
        Assert.Equal(0, live.RefreshCalls);
        await service.RefreshLiveAsync(default);

        Assert.Equal(1, live.RefreshCalls);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Equal("claude-live", service.Snapshot.TechnicalDetail);
        Assert.Equal(new double?[] { 46, 11 }, service.Snapshot.Windows.Select(w => w.UsedPercent));
        Assert.Equal(data.Clock.UtcNow.AddHours(5), service.Snapshot.Windows[0].ResetsAt);
        Assert.Equal(data.Clock.UtcNow.AddDays(7), service.Snapshot.Windows[1].ResetsAt);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task LiveFailureKeepsLastGoodValuesAndRemainsVisibleAfterPassiveRead()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow, new(46, data.Clock.UtcNow.AddHours(5)), null))
        };
        var service = data.Service(live);
        await service.RefreshLiveAsync(default);

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5);
        live.Next = new(null, "claude-live-request-failed", data.Clock.UtcNow);
        var failed = await service.RefreshLiveAsync(default);

        Assert.Equal(CodexQuotaStatus.Stale, failed.Snapshot.Status);
        Assert.Equal("claude-live-request-failed", failed.Snapshot.TechnicalDetail);
        Assert.Equal(46, failed.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(data.Clock.UtcNow.AddMinutes(-5), failed.Snapshot.LastSuccessfulRefresh);

        await service.RefreshAsync(default);
        Assert.Equal(2, live.RefreshCalls);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-live-request-failed", service.Snapshot.TechnicalDetail);
        Assert.Equal(46, service.Snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task LiveAttemptSchedulingUsesTheConfiguredInterval()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow, new(46, data.Clock.UtcNow.AddHours(5)), null))
        };
        var service = data.Service(live);

        Assert.True(service.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));
        await service.RefreshLiveAsync(default);
        Assert.False(service.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(4).AddSeconds(59);
        Assert.False(service.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        Assert.True(service.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task BindingRotationBeforeAnActiveReadCannotReuseThePreviousLiveSample()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow, new(46, data.Clock.UtcNow.AddHours(5)), null))
        };
        var service = data.Service(live);
        await service.RefreshLiveAsync(default);

        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        live.Next = new(null, "claude-live-request-failed", data.Clock.UtcNow);
        var result = await service.RefreshLiveAsync(default);

        Assert.Equal(CodexQuotaStatus.Unavailable, result.Snapshot.Status);
        Assert.Equal("claude-live-request-failed", result.Snapshot.TechnicalDetail);
        Assert.Empty(result.Snapshot.Windows);
    }

    [Fact]
    public async Task BindingRotationDuringDesktopFallbackHidesTheOldLiveResult()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow, new(46, data.Clock.UtcNow.AddHours(5)), null))
        };
        var desktopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDesktop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new BlockingDesktopSource(desktopStarted, releaseDesktop);
        var service = data.Service(live, desktop);
        await service.RefreshLiveAsync(default);

        desktop.BlockNext = true;
        live.Next = new(new(data.Clock.UtcNow, new(51, data.Clock.UtcNow.AddHours(5)), null));
        var pending = service.RefreshLiveAsync(default);
        await desktopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rotated = data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") };
        data.Connections.Save(rotated);
        releaseDesktop.SetResult();
        var result = await pending;

        Assert.Equal(CodexQuotaStatus.Unavailable, result.Snapshot.Status);
        Assert.Equal("claude-live-identity-mismatch", result.Snapshot.TechnicalDetail);
        Assert.Empty(result.Snapshot.Windows);
    }

    [Fact]
    public async Task ManagerUsesLiveCapabilityForAutomaticAndManualRefreshButPassiveUsesLocalPath()
    {
        await using var data = new Data();
        var live = new FakeLiveSource
        {
            Next = new(new(data.Clock.UtcNow, new(46, data.Clock.UtcNow.AddHours(5)), null))
        };
        var manager = data.Manager(live);

        await manager.RefreshPassiveAsync(default);
        Assert.Equal(0, live.RefreshCalls);

        await manager.RefreshAutomaticallyAsync(TimeSpan.FromMinutes(5), default);
        Assert.Equal(1, live.RefreshCalls);
        await manager.RefreshAutomaticallyAsync(TimeSpan.FromMinutes(5), default);
        Assert.Equal(1, live.RefreshCalls);

        await manager.RefreshManuallyAsync(default);
        Assert.Equal(2, live.RefreshCalls);
    }

    [Fact]
    public async Task NewLocalReceiptDoesNotRenewExtraUsageOrHideItsFailedServerCheck()
    {
        await using var data = new Data();
        var observed = data.Clock.UtcNow;
        var extra = new ClaudeExtraUsage(true, 3.2m, 10m, false, "USD", 32, observed);
        var live = new FakeLiveSource { Next = new(new(observed, new(46, observed.AddHours(5)), null)
            { ExtraUsage = extra }) };
        var service = data.Service(live);
        await service.RefreshLiveAsync(default);
        Assert.Equal(extra, service.Snapshot.ExtraUsage);

        data.Clock.UtcNow = observed.AddMinutes(5);
        live.Next = new(null, "claude-live-request-failed", data.Clock.UtcNow);
        await service.RefreshLiveAsync(default);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(51, observed.AddHours(5).ToUnixTimeSeconds()), null),
            data.Clock.UtcNow, default);
        await service.RefreshAsync(default);
        Assert.Equal(51, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(extra, service.Snapshot.ExtraUsage);
        Assert.Equal(observed, service.Snapshot.ExtraUsage!.ObservedAt);
        Assert.Equal("claude-live-request-failed", service.Snapshot.ExtraUsageFailure);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);

        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        await service.RefreshAsync(default);
        Assert.Null(service.Snapshot.ExtraUsage);
    }

    [Fact]
    public async Task OptionalExtraFailureDoesNotChangeValidQuotaStatusAndSuccessClearsOnlyItsOwnFailure()
    {
        await using var data = new Data();
        var observed = data.Clock.UtcNow;
        var extra = new ClaudeExtraUsage(true, 3.2m, 10m, false, "USD", 32, observed);
        var live = new FakeLiveSource { Next = new(new(observed, new(46, observed.AddHours(5)), null)
            { ExtraUsage = extra }) };
        var service = data.Service(live);
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = observed.AddMinutes(5);
        live.Next = new(new(data.Clock.UtcNow, new(51, observed.AddHours(5)), null)
            { ExtraUsageFailure = "claude-extra-usage-unavailable" });
        await service.RefreshLiveAsync(default);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Equal(extra, service.Snapshot.ExtraUsage);
        Assert.Equal("claude-extra-usage-unavailable", service.Snapshot.ExtraUsageFailure);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);

        var freshExtra = extra with { UsedAmount = 4m, ObservedAt = data.Clock.UtcNow };
        live.Next = new(live.Next.Sample! with { ExtraUsage = freshExtra, ExtraUsageFailure = null });
        await service.RefreshLiveAsync(default);
        Assert.Equal(freshExtra, service.Snapshot.ExtraUsage);
        Assert.Null(service.Snapshot.ExtraUsageFailure);
        live.Next = new(null, "claude-live-identity-mismatch", data.Clock.UtcNow);
        await service.RefreshLiveAsync(default);
        Assert.Null(service.Snapshot.ExtraUsage);
        Assert.Empty(service.Snapshot.Windows);
    }

    [Fact]
    public async Task ActiveIdentityFailureHidesAllQuotaAndMoneyAcrossLocalReceiptsRestartAndNewLiveQuota()
    {
        await using var data = new Data();
        var observed = data.Clock.UtcNow;
        var extra = new ClaudeExtraUsage(true, 3.2m, 10m, false, "USD", 32, observed);
        var live = new FakeLiveSource { Next = new(new(observed, new(46, observed.AddHours(5)), null)
            { ExtraUsage = extra }) };
        var service = data.Service(live);
        await service.RefreshLiveAsync(default);
        Assert.NotEmpty(service.Snapshot.Windows);
        Assert.Equal(extra, service.Snapshot.ExtraUsage);

        data.Clock.UtcNow = observed.AddMinutes(1);
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Binding.BindingGeneration!, ClaudeFailureKind.IdentityMismatch,
            data.Clock.UtcNow);
        await service.RefreshAsync(default);
        Hidden(service.Snapshot);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastAttemptedRefresh);
        Assert.Equal(observed, service.Snapshot.LastSuccessfulRefresh);

        data.Clock.UtcNow = observed.AddMinutes(2);
        await data.Inbox.RecordAsync(new(ClaudeInputStatus.Available, new(51, observed.AddHours(5).ToUnixTimeSeconds()), null),
            data.Clock.UtcNow, default);
        var legitimateCache = File.ReadAllText(data.Accounts.ClaudeStatusLinePath(data.Profile.Id));
        await service.RefreshAsync(default);
        Hidden(service.Snapshot);
        Assert.Equal(observed, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(ClaudeFailureKind.IdentityMismatch, failures.Read(data.Profile.Id).State!.Kind);
        Assert.False(service.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));

        // Constructor reconstruction must not expose the now-newer local cache.
        var restarted = data.Service(live);
        Hidden(restarted.Snapshot);
        Assert.Equal(legitimateCache, File.ReadAllText(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));
        Assert.NotNull(data.Inbox.Read().State!.LastGood);

        data.Clock.UtcNow = observed.AddMinutes(3);
        live.Next = new(new(data.Clock.UtcNow, new(53, observed.AddHours(5)), null)
            { ExtraUsage = extra with { ObservedAt = data.Clock.UtcNow } });
        await restarted.RefreshLiveAsync(default);
        Hidden(restarted.Snapshot);
        Assert.False(restarted.ShouldRefresh(data.Clock.UtcNow, TimeSpan.FromMinutes(5)));

        // Only the explicit recovery generation changes the persistent failure's
        // attribution. It does not erase saved receipts to implement hiding.
        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        await restarted.RefreshAsync(default);
        Assert.Equal(ClaudeFailureKind.IdentityMismatch, failures.Read(data.Profile.Id).State!.Kind);
        Assert.Equal(legitimateCache, File.ReadAllText(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));
        Assert.Null(restarted.Snapshot.ExtraUsage);
        Assert.NotEqual("claude-identity-mismatch", restarted.Snapshot.TechnicalDetail);

        static void Hidden(CodexQuotaSnapshot snapshot)
        {
            Assert.Equal(CodexQuotaStatus.Unavailable, snapshot.Status);
            Assert.Equal("claude-identity-mismatch", snapshot.TechnicalDetail);
            Assert.Empty(snapshot.Windows);
            Assert.False(snapshot.HasUsablePercentages);
            Assert.Null(snapshot.UsageCredits);
            Assert.Null(snapshot.ExtraUsage);
            Assert.Null(snapshot.ResetCreditsAvailable);
            Assert.Null(snapshot.ResetCreditExpirations);
            Assert.Empty(snapshot.RedeemableCredits);
        }
    }

    private sealed class FakeLiveSource : IClaudeLiveUsageSource
    {
        public ClaudeLiveUsageUpdate Next { get; set; } = new(null, "claude-live-unavailable");
        public int RefreshCalls { get; private set; }

        public ClaudeLiveUsageUpdate ReadCached(ClaudeConnectionBinding? binding) => new(null);

        public Task<ClaudeLiveUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token)
        {
            RefreshCalls++;
            return Task.FromResult(Next);
        }
    }

    private sealed class BlockingDesktopSource(
        TaskCompletionSource started, TaskCompletionSource release) : IClaudeDesktopUsageSource
    {
        public bool BlockNext { get; set; }

        public async Task<ClaudeDesktopUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token)
        {
            if (!BlockNext) return new(null);
            BlockNext = false;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(null);
        }
    }

    private sealed class Data : IAsyncDisposable
    {
        private readonly List<CodexAccountManager> _managers = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-live-integration-" + Guid.NewGuid().ToString("N"));
        public MutableClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public ClaudeConnectionStore Connections { get; }
        public ClaudeConnectionBinding Binding { get; }
        public ClaudeStatusLineStore Inbox { get; }

        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Live integration");
            Accounts.Save(initial with { Version = 2, Profiles = [Profile], SelectedId = Profile.Id });
            Binding = new(2, Profile.Id, Path.Combine(Root, "claude-home"), Path.Combine(Root, "claude.exe"),
                false, ClaudeIdentity.StableFingerprint("live@example.invalid", "org-live"),
                Clock.UtcNow.AddDays(-1), BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro");
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
            Inbox = new(Accounts.ClaudeStatusLinePath(Profile.Id), Profile.Id);
        }

        public ClaudeQuotaService Service(IClaudeLiveUsageSource live, IClaudeDesktopUsageSource? desktop = null) =>
            new(Inbox, Clock, Connections.Read, email: _ => "live@example.invalid",
                failureStore: new ClaudeFailureStore(Accounts), desktop: desktop, live: live);

        public CodexAccountManager Manager(IClaudeLiveUsageSource live)
        {
            var manager = new CodexAccountManager(Accounts, Root,
                [new ClaudeUsageProvider(Accounts, Clock, liveFactory: _ => live)]);
            _managers.Add(manager);
            return manager;
        }

        public ValueTask DisposeAsync()
        {
            Assert.All(_managers, manager => Assert.False(manager.Refresh.IsRefreshing));
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
