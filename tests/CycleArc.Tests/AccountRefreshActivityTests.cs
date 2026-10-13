using System.Collections.Concurrent;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class AccountRefreshActivityTests
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActivityIncludesQueuedAccountsAndClearsEachCompletionWithoutChangingSnapshots()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var profiles = Enum.GetValues<UsageProviderId>().Select(provider => Profile(data, provider)).ToArray();
        store.Save(new(3, profiles[0].Id, profiles));
        var services = profiles.ToDictionary(profile => profile.Id, profile => new GatedService(Snapshot(profile.Provider)));
        var providers = profiles.Select(profile => new FakeProvider(profile.Provider, services[profile.Id])).ToArray();
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("unused"), providers));
        var transitions = new ConcurrentQueue<(string Id, bool Busy)>();
        manager.Changed += () =>
        {
            foreach (var account in manager.Accounts) transitions.Enqueue((account.Profile.Id, account.IsRefreshing));
        };

        var refresh = manager.RefreshManuallyAsync(CancellationToken.None);
        try
        {
            await services[profiles[0].Id].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await services[profiles[1].Id].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { true, true, true }, manager.Accounts.Select(account => account.IsRefreshing));
            Assert.False(services[profiles[2].Id].Started.Task.IsCompleted);
            Assert.False(manager.Remove(profiles[0].Id));
            Assert.False(manager.Remove(profiles[2].Id));
            AssertSnapshotsUnchanged(manager, services);

            services[profiles[0].Id].Release.SetResult();
            await services[profiles[2].Id].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { false, true, true }, manager.Accounts.Select(account => account.IsRefreshing));
            AssertSnapshotsUnchanged(manager, services);
        }
        finally
        {
            foreach (var service in services.Values) service.Release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.All(manager.Accounts, account => Assert.False(account.IsRefreshing));
        foreach (var profile in profiles)
        {
            Assert.Contains((profile.Id, true), transitions);
            Assert.Contains((profile.Id, false), transitions);
        }
        AssertSnapshotsUnchanged(manager, services);
        Assert.True(manager.Remove(profiles[2].Id));
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, false)]
    [InlineData(UsageProviderId.Claude, false)]
    [InlineData(UsageProviderId.Cursor, false)]
    [InlineData(UsageProviderId.Codex, true)]
    [InlineData(UsageProviderId.Claude, true)]
    [InlineData(UsageProviderId.Cursor, true)]
    public async Task ActivityClearsAfterFailureOrCancellationAndRetainsPreviousWarning(UsageProviderId provider, bool cancel)
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var profile = Profile(data, provider);
        store.Save(new(3, profile.Id, [profile]));
        var previous = Snapshot(provider) with { Status = CodexQuotaStatus.Stale,
            TechnicalDetail = provider == UsageProviderId.Claude ? "claude-live-request-failed"
                : provider == UsageProviderId.Cursor ? "cursor-live-request-failed" : "timed-out" };
        var service = new GatedService(previous) { Fail = !cancel };
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("unused"), [new FakeProvider(provider, service)]));
        var transitions = new ConcurrentQueue<bool>();
        manager.Changed += () => transitions.Enqueue(manager.Accounts.Single().IsRefreshing);
        using var cancellation = new CancellationTokenSource();
        var refresh = manager.RefreshManuallyAsync(cancellation.Token);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var account = manager.Accounts.Single();
        Assert.True(account.IsRefreshing);
        Assert.Same(previous, account.Snapshot);
        var before = WidgetAccountModel.From(account with { IsRefreshing = false }, false, now: Now);
        var busy = WidgetAccountModel.From(account, false, now: Now);
        Assert.True(busy.IsRefreshing);
        Assert.True(busy.StatusPresentation!.IsWarning);
        Assert.Equal(before.StatusText, busy.StatusText);
        Assert.Equal(before.StatusPresentation!.ObservationTime, busy.StatusPresentation.ObservationTime);
        Assert.Equal(before.Ring, busy.Ring);
        Assert.Equal(before.Periods, busy.Periods);
        Assert.Contains(UiText.T("Checking…", "확인 중…"), busy.Tooltip);
        Assert.Contains(before.StatusPresentation.DetailText, busy.Tooltip);

        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        else
        {
            service.Release.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        }
        Assert.False(manager.Accounts.Single().IsRefreshing);
        Assert.Same(previous, manager.Accounts.Single().Snapshot);
        Assert.Contains(true, transitions);
        Assert.False(transitions.Last());
    }

    [Fact]
    public async Task CancellingAQueuedAccountClearsItsActivityWithoutReleasingAnExecutingSlot()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var profiles = Enum.GetValues<UsageProviderId>().Select(provider => Profile(data, provider)).ToArray();
        store.Save(new(3, profiles[0].Id, profiles));
        var services = profiles.Select(profile => new GatedService(Snapshot(profile.Provider))
            { IgnoreCancellation = profile != profiles[2] }).ToArray();
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("unused"),
            profiles.Select((profile, index) => new FakeProvider(profile.Provider, services[index]))));
        var queuedCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasQueued = false;
        manager.Changed += () =>
        {
            var queued = manager.Accounts.FirstOrDefault(account => account.Profile.Id == profiles[2].Id);
            if (queued is null) return;
            if (queued.IsRefreshing) wasQueued = true;
            else if (wasQueued) queuedCleared.TrySetResult();
        };
        using var cancellation = new CancellationTokenSource();
        var refresh = manager.RefreshManuallyAsync(cancellation.Token);
        try
        {
            await services[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await services[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(manager.Accounts, account => Assert.True(account.IsRefreshing));
            Assert.False(manager.Remove(profiles[0].Id));
            Assert.False(manager.Remove(profiles[2].Id));
            cancellation.Cancel();
            await queuedCleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { true, true, false }, manager.Accounts.Select(account => account.IsRefreshing));
            Assert.False(services[2].Started.Task.IsCompleted);
            Assert.False(refresh.IsCompleted);
            Assert.True(manager.Remove(profiles[2].Id));
            Assert.False(manager.Remove(profiles[0].Id));
            // The cancelled waiter never owned a slot. Both executing requests still do.
            var slots = (SemaphoreSlim)typeof(CodexAccountManager).GetField("_processSlots",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            Assert.Equal(0, slots.CurrentCount);
        }
        finally
        {
            foreach (var service in services) service.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        Assert.All(manager.Accounts, account => Assert.False(account.IsRefreshing));
    }

    [Fact]
    public async Task AutomaticRefreshDoesNotMarkSkippedAccountsBusy()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var profile = Profile(data, UsageProviderId.Cursor);
        store.Save(new(3, profile.Id, [profile]));
        var service = new GatedService(Snapshot(profile.Provider)) { Due = false };
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("unused"), [new FakeProvider(profile.Provider, service)]));
        var transitions = new List<bool>();
        manager.Changed += () => transitions.Add(manager.Accounts.Single().IsRefreshing);
        await manager.RefreshAutomaticallyAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.False(service.Started.Task.IsCompleted);
        Assert.All(transitions, busy => Assert.False(busy));
        Assert.False(manager.Accounts.Single().IsRefreshing);
    }

    private static void AssertSnapshotsUnchanged(CodexAccountManager manager, Dictionary<string, GatedService> services)
    {
        foreach (var account in manager.Accounts)
        {
            Assert.Same(services[account.Profile.Id].Snapshot, account.Snapshot);
            if (!account.IsRefreshing) continue;
            var model = WidgetAccountModel.From(account, false, now: Now);
            Assert.True(model.IsRefreshing);
            Assert.True(model.ShowStatusRow);
            Assert.Equal(UiText.T("Checking…", "확인 중…"), model.StatusText);
            Assert.Equal(account.Snapshot.LastSuccessfulRefresh, model.StatusPresentation!.ObservationTime);
        }
    }

    private static CodexAccountProfile Profile(AccountTestDirectory data, UsageProviderId provider) =>
        new(Guid.NewGuid().ToString("N"), provider == UsageProviderId.Codex ? data.Home("codex") : "",
            "Synthetic " + provider) { Provider = provider };

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null,
            [new(provider == UsageProviderId.Cursor ? "cursor-auto" : "five", 23.09,
                CodexWindowClassifier.FiveHourMinutes, Now.AddHours(2), CodexWindowKind.FiveHour)],
            provider == UsageProviderId.Claude ? ClaudeUsagePresentation.LiveDetail
                : provider == UsageProviderId.Cursor ? CursorUsagePresentation.LiveDetail : null) { Provider = provider };

    private sealed class FakeProvider(UsageProviderId id, GatedService service) : IUsageProvider
    {
        public UsageProviderId Id => id;
        public IUsageAccountService Create(CodexAccountProfile profile) => service;
    }

    // Deliberately never publishes AsRefreshing: Claude and Cursor retain their previous
    // source snapshot while their asynchronous live request is running.
    private sealed class GatedService(CodexQuotaSnapshot snapshot) : IUsageAccountService, ILiveUsageAccountService
    {
        public CodexQuotaSnapshot Snapshot => snapshot;
        public string? Email => null;
        public string? IdentityFingerprint => null;
        public bool IsConnected => true;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => false;
        public bool Due { get; init; } = true;
        public bool Fail { get; init; }
        public bool IgnoreCancellation { get; init; }
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => Due;
        public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) => Task.FromResult(new CodexRefreshResult(snapshot, true, null));
        public async Task<CodexRefreshResult> RefreshLiveAsync(CancellationToken token)
        {
            Started.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(token);
            if (Fail) throw new InvalidOperationException("Synthetic live failure");
            return new(snapshot, false, null);
        }
    }
}
