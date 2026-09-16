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
        using var data = new Data();
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
        using var data = new Data();
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
        using var data = new Data();
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
        using var data = new Data();
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
        using var data = new Data();
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
        using var data = new Data();
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

    private sealed class Data : IDisposable
    {
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

        public CodexAccountManager Manager(IClaudeLiveUsageSource live) =>
            new(Accounts, Root, [new ClaudeUsageProvider(Accounts, Clock, liveFactory: _ => live)]);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
