using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorServiceLifetimeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");
    private const string Email = "cursor@example.invalid";

    [Fact]
    public async Task ServiceReleasesItsOwnClientOnlyAfterTheInFlightRequestFinishes()
    {
        await using var data = new AccountTestDirectory();
        var fixture = new Fixture(data);
        var service = (CursorQuotaService)fixture.OwningProvider().Create(fixture.Bound);
        var client = Assert.Single(fixture.Created);
        client.Block = true;

        var refresh = service.RefreshLiveAsync(default);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        Assert.Equal(0, client.Disposals);

        client.Release.SetResult();
        var completed = await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(completed.FailureCategory);
        Assert.Equal(CodexQuotaStatus.Available, completed.Snapshot.Status);
        Assert.Equal(1, client.Disposals);

        service.Dispose();
        Assert.Equal(1, client.Disposals);
        var released = await service.RefreshLiveAsync(default);
        Assert.Equal("cursor-service-released", released.FailureCategory);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.False((await service.ConnectCurrentAsync(default)).Success);
        Assert.Equal(1, client.Fetches);
        Assert.Equal(0, client.IdentityReads);
    }

    [Fact]
    public async Task InjectedSharedClientIsNeverReleasedByServicesOrFailedCreation()
    {
        await using var data = new AccountTestDirectory();
        var fixture = new Fixture(data);
        var shared = new LifetimeClient(fixture.Fingerprint);
        var provider = new CursorUsageProvider(fixture.Store, fixture.Clock, clientFactory: _ => shared);
        var first = (CursorQuotaService)provider.Create(fixture.Bound);
        var second = (CursorQuotaService)provider.Create(fixture.Bound);
        await first.RefreshLiveAsync(default);
        first.Dispose();
        second.Dispose();

        var failing = new CursorUsageProvider(fixture.Store, fixture.Clock, clientFactory: _ => shared,
            sourceFactory: _ => throw new IOException("source unavailable"));
        Assert.Throws<IOException>(() => failing.Create(fixture.Bound));
        Assert.Equal(0, shared.Disposals);
    }

    [Fact]
    public async Task FailedServiceCreationReleasesTheClientTheProviderCreated()
    {
        await using var data = new AccountTestDirectory();
        var fixture = new Fixture(data);
        var provider = fixture.OwningProvider(sourceFactory: _ => throw new IOException("source unavailable"));

        Assert.Throws<IOException>(() => provider.Create(fixture.Bound));
        Assert.Equal(1, Assert.Single(fixture.Created).Disposals);
    }

    [Fact]
    public async Task RemovalAndShutdownReleaseOwnedClientsWithoutInterruptingARequest()
    {
        await using var data = new AccountTestDirectory();
        var fixture = new Fixture(data);
        var manager = data.TrackManager(new CodexAccountManager(fixture.Store, data.Home("codex"),
            [new CodexStubProvider(), fixture.OwningProvider()]));
        var bound = Assert.Single(fixture.Created);
        var draft = manager.AddCursor("Second");
        var draftClient = fixture.Created[1];

        Assert.True(manager.Remove(draft.Id));
        Assert.Equal(1, draftClient.Disposals);
        Assert.Equal(0, bound.Disposals);

        bound.Block = true;
        var refresh = manager.RefreshManuallyAsync(default);
        await bound.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manager.Remove(fixture.Bound.Id));
        manager.Dispose();
        Assert.Equal(0, bound.Disposals);

        bound.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, bound.Disposals);
        Assert.Equal(CodexQuotaStatus.Available,
            manager.Accounts.Single(account => account.Profile.Id == fixture.Bound.Id).Snapshot.Status);
        manager.Dispose();
        Assert.Equal(1, bound.Disposals);
        Assert.Equal(1, draftClient.Disposals);
    }

    private sealed class Fixture
    {
        public CodexAccountStore Store { get; }
        public CodexAccountProfile Bound { get; }
        public string Fingerprint { get; } = CursorIdentity.Fingerprint(Email, "cursor-account")!;
        public MutableClock Clock { get; } = new(Now);
        public List<LifetimeClient> Created { get; } = [];

        public Fixture(AccountTestDirectory data)
        {
            Store = new CodexAccountStore(data.Root);
            var initial = Store.LoadOrMigrate(data.Home("codex"));
            Bound = Store.NewCursor("Cursor");
            Store.Save(initial with { Version = 3, Profiles = initial.Profiles.Append(Bound).ToArray() });
            new CursorConnectionStore(Store, Bound.Id).Save(new(1, Bound.Id, Fingerprint, Now.AddDays(-1),
                Guid.NewGuid().ToString("N")));
        }

        public CursorUsageProvider OwningProvider(Func<CodexAccountProfile, ICursorUsageSource>? sourceFactory = null) =>
            new(Store, Clock, clientFactory: null, authFactory: _ => new NoAuth(), sourceFactory,
                ownedClientFactory: _ =>
                {
                    var client = new LifetimeClient(Fingerprint);
                    Created.Add(client);
                    return client;
                });
    }

    private sealed class LifetimeClient(string fingerprint) : ICursorUsageClient, IDisposable
    {
        public int Disposals;
        public int Fetches;
        public int IdentityReads;
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CursorConnectionResult> ReadIdentityAsync(CancellationToken token)
        {
            Interlocked.Increment(ref IdentityReads);
            return Task.FromResult(new CursorConnectionResult(true, IdentityFingerprint: fingerprint, Email: Email));
        }

        public async Task<CursorUsageResponse> FetchAsync(CursorConnectionBinding binding, CancellationToken token,
            bool includeSand = true)
        {
            Assert.Equal(0, Volatile.Read(ref Disposals));
            Interlocked.Increment(ref Fetches);
            Started.TrySetResult();
            if (Block) await Release.Task.WaitAsync(token);
            Assert.Equal(0, Volatile.Read(ref Disposals));
            var sample = new CursorUsageSample(Now,
                [new CodexQuotaWindow("cursor-auto", 25, null, Now.AddDays(29), CodexWindowKind.Other)
                    { UsedAmount = 25m, LimitAmount = 100m, RemainingAmount = 75m, Unit = "USD", IsEnabled = true }],
                Now, Now.AddDays(30), "pro", "individual");
            return new(sample, null, null, Email, fingerprint, Now);
        }

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class NoAuth : ICursorAuthSource
    {
        public CursorAuthRead Read() => throw new InvalidOperationException("The fake client never reads credentials.");
    }

    private sealed class CodexStubProvider : IUsageProvider
    {
        public UsageProviderId Id => UsageProviderId.Codex;
        public IUsageAccountService Create(CodexAccountProfile profile) => new CodexStub();
    }

    private sealed class CodexStub : IUsageAccountService
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
