using System.Collections.Concurrent;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public sealed class CursorHookSynchronizerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A hooks.json stand-in whose every write waits until the test releases it.</summary>
    private sealed class HeldHooks
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _releases = new();
        private readonly SemaphoreSlim _started = new(0);
        public volatile bool Desired;
        public volatile bool Installed;
        public ConcurrentQueue<bool> Applied { get; } = new();

        public async Task Apply(bool enable, CancellationToken cancellation)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases.Enqueue(release);
            Applied.Enqueue(enable);
            _started.Release();
            await release.Task.WaitAsync(cancellation);
            Installed = enable;
        }

        public async Task WaitStartedAsync()
        {
            if (!await _started.WaitAsync(Timeout)) throw new TimeoutException("No apply started.");
        }

        public void ReleaseNext()
        {
            Assert.True(_releases.TryDequeue(out var release));
            release.SetResult();
        }
    }

    private static CursorHookSynchronizer Create(HeldHooks hooks, CancellationToken cancellation = default) =>
        new(() => hooks.Desired, hooks.Apply, cancellation);

    [Fact]
    public async Task TurningOffWhileTheInstallRunsRemovesTheHooksAfterIt()
    {
        var hooks = new HeldHooks { Desired = true };
        var sync = Create(hooks);
        var run = sync.Request();
        await hooks.WaitStartedAsync();

        hooks.Desired = false;
        Assert.Same(run, sync.Request());
        hooks.ReleaseNext();
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await run.WaitAsync(Timeout);

        Assert.False(hooks.Installed);
        Assert.Equal([true, false], hooks.Applied);
        Assert.True(sync.Completion.IsCompleted);
    }

    [Fact]
    public async Task TurningOnWhileTheRemovalRunsInstallsTheHooksAfterIt()
    {
        var hooks = new HeldHooks { Desired = false, Installed = true };
        var sync = Create(hooks);
        var run = sync.Request();
        await hooks.WaitStartedAsync();

        hooks.Desired = true;
        _ = sync.Request();
        hooks.ReleaseNext();
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await run.WaitAsync(Timeout);

        Assert.True(hooks.Installed);
        Assert.Equal([false, true], hooks.Applied);
    }

    [Fact]
    public async Task ManyChangesDuringOneApplyEndOnTheLatestWithOneFollowUp()
    {
        var hooks = new HeldHooks { Desired = true };
        var sync = Create(hooks);
        var run = sync.Request();
        await hooks.WaitStartedAsync();

        foreach (var value in new[] { false, true, false })
        {
            hooks.Desired = value;
            _ = sync.Request();
        }
        hooks.ReleaseNext();
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await run.WaitAsync(Timeout);

        Assert.False(hooks.Installed);
        Assert.Equal([true, false], hooks.Applied);
    }

    [Fact]
    public async Task OnOffOnDuringTheInstallStillEndsInstalled()
    {
        var hooks = new HeldHooks { Desired = true };
        var sync = Create(hooks);
        var run = sync.Request();
        await hooks.WaitStartedAsync();

        hooks.Desired = false;
        _ = sync.Request();
        hooks.Desired = true;
        _ = sync.Request();
        hooks.ReleaseNext();
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await run.WaitAsync(Timeout);

        Assert.True(hooks.Installed);
        Assert.Equal([true, true], hooks.Applied);
    }

    [Fact]
    public async Task ARequestAfterTheRunFinishedStartsANewRun()
    {
        var hooks = new HeldHooks { Desired = true };
        var sync = Create(hooks);
        var first = sync.Request();
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await first.WaitAsync(Timeout);

        hooks.Desired = false;
        var second = sync.Request();
        Assert.NotSame(first, second);
        await hooks.WaitStartedAsync();
        hooks.ReleaseNext();
        await second.WaitAsync(Timeout);

        Assert.False(hooks.Installed);
        Assert.Equal([true, false], hooks.Applied);
    }

    [Fact]
    public async Task ExitCancelsWithoutAFollowUpApply()
    {
        using var lifetime = new CancellationTokenSource();
        var hooks = new HeldHooks { Desired = true };
        var sync = Create(hooks, lifetime.Token);
        var run = sync.Request();
        await hooks.WaitStartedAsync();

        hooks.Desired = false;
        _ = sync.Request();
        lifetime.Cancel();
        await run.WaitAsync(Timeout);

        Assert.Equal([true], hooks.Applied);
        Assert.False(hooks.Installed);
    }

    [Fact]
    public async Task AnUnexpectedFailureDoesNotLeaveTheSynchronizerStuck()
    {
        var calls = 0;
        var sync = new CursorHookSynchronizer(() => true, (_, _) =>
            ++calls == 1 ? throw new InvalidOperationException("synthetic") : Task.CompletedTask, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.Request().WaitAsync(Timeout));
        await sync.Request().WaitAsync(Timeout);

        Assert.Equal(2, calls);
    }
}
