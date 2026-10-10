using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using Xunit.Abstractions;

namespace CycleArc.Tests;

public sealed class CoalescedUpdateTests(ITestOutputHelper output)
{
    [Fact]
    public void RequestsBeforeARunShareOnePostAndARequestDuringARunSchedulesAnother()
    {
        var queue = new Queue<Action>();
        var state = 0;
        var observed = new List<int>();
        CoalescedUpdate? update = null;
        update = new CoalescedUpdate(queue.Enqueue, () =>
        {
            observed.Add(state);
            if (state == 2) { state = 3; update!.Request(); }
        });

        state = 1; update.Request();
        state = 2; update.Request();
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(new[] { 2 }, observed);

        // The change made while the run was reading state was not lost.
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(new[] { 2, 3 }, observed);
        Assert.Empty(queue);
    }

    [Fact]
    public void FailedPostDoesNotLeaveTheUpdateStuck()
    {
        var fail = true;
        var queue = new Queue<Action>();
        var update = new CoalescedUpdate(run =>
        {
            if (fail) throw new InvalidOperationException("dispatcher unavailable");
            queue.Enqueue(run);
        }, () => { });

        Assert.Throws<InvalidOperationException>(update.Request);
        fail = false;
        update.Request();
        Assert.Single(queue);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverLoseTheLastState()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var state = 0;
        var lastSeen = -1;
        var update = new CoalescedUpdate(queue.Enqueue, () => lastSeen = Volatile.Read(ref state));
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested || !queue.IsEmpty)
                if (queue.TryDequeue(out var run)) run();
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++) { Interlocked.Increment(ref state); update.Request(); }
        })));
        stop.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(20000, lastSeen);
    }

    // Mirrors the desktop wiring: the manager forwards each refresh-state change as Changed,
    // and the desktop also listens to the refresh state directly.
    [Fact]
    public async Task RefreshStateChangeSchedulesOneProjectionInsteadOfTwo()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("codex"), [new StubProvider()]));
        var legacyPosts = 0;
        manager.Changed += () => legacyPosts++;
        manager.Refresh.StateChanged += () => legacyPosts++;

        var queue = new Queue<Action>();
        var projections = 0;
        var update = new CoalescedUpdate(queue.Enqueue, () => projections++);
        manager.Changed += update.Request;
        manager.Refresh.StateChanged += update.Request;
        var stateChanges = 0;
        manager.Refresh.StateChanged += () =>
        {
            stateChanges++;
            while (queue.TryDequeue(out var run)) run();
        };

        await manager.RefreshManuallyAsync(default);
        while (queue.TryDequeue(out var run)) run();

        output.WriteLine($"refresh-state changes={stateChanges} legacy projections={legacyPosts} coalesced projections={projections}");
        Assert.Equal(2, stateChanges);
        Assert.Equal(2 * stateChanges, legacyPosts);
        Assert.Equal(stateChanges, projections);
    }

    private sealed class StubProvider : IUsageProvider
    {
        public UsageProviderId Id => UsageProviderId.Codex;
        public IUsageAccountService Create(CodexAccountProfile profile) => new StubService();
    }

    private sealed class StubService : IUsageAccountService
    {
        public CodexQuotaSnapshot Snapshot => CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
        public string? Email => null;
        public string? IdentityFingerprint => null;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => false;
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) =>
            Task.FromResult(new CodexRefreshResult(Snapshot, true, null));
    }
}
