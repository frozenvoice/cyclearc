using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorLifecycleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Fact]
    public async Task RestartRetainsTheLastGoodQuotaAndStaleReceipt()
    {
        using var data = new Data();
        var first = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(first.Failure);

        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(null, "cursor-live-request-failed", Email: data.Email,
            IdentityFingerprint: data.Fingerprint, AttemptedAt: data.Clock.UtcNow);
        var stale = await data.Collector().RefreshAsync(data.Binding, default);

        Assert.Equal("cursor-live-request-failed", stale.Failure);
        Assert.Equal(Now, stale.Sample!.ObservedAt);
        Assert.Equal(Now.AddMinutes(5), stale.AttemptedAt);
        var restarted = data.Collector().ReadCached(data.Binding);
        Assert.Equal(stale.Failure, restarted.Failure);
        Assert.Equal(stale.Sample.ObservedAt, restarted.Sample!.ObservedAt);
        Assert.Equal(stale.Sample.Windows.ToArray(), restarted.Sample.Windows.ToArray());
        Assert.Equal(stale.Sample.BillingCycleEnd, restarted.Sample.BillingCycleEnd);
        Assert.Equal(stale.Sample.MembershipType, restarted.Sample.MembershipType);
        Assert.Equal(stale.AttemptedAt, restarted.AttemptedAt);
    }

    [Fact]
    public async Task IdentityMismatchHidesLastGoodQuotaAndCannotResurfaceFromBackup()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow },
            "cursor-live-identity-mismatch", Email: "other@example.invalid",
            IdentityFingerprint: CursorIdentity.Fingerprint("other@example.invalid", "other")!,
            AttemptedAt: data.Clock.UtcNow);

        var mismatch = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(mismatch.Sample);
        Assert.Equal("cursor-live-identity-mismatch", mismatch.Failure);
        Assert.DoesNotContain("@", File.ReadAllText(data.CachePath));

        // A corrupt primary must not make the pre-mismatch backup visible on restart.
        File.WriteAllText(data.CachePath, "not-json");
        var restarted = data.Collector().ReadCached(data.Binding);
        Assert.Null(restarted.Sample);
        Assert.Equal("cursor-live-identity-mismatch", restarted.Failure);
    }

    [Fact]
    public async Task IdentityMismatchCannotBeReplacedByANewerCache()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);

        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow }, null,
            Email: data.Email, IdentityFingerprint: data.Fingerprint, AttemptedAt: data.Clock.UtcNow);
        await data.Collector().RefreshAsync(data.Binding, default);

        data.Clock.UtcNow = Now.AddMinutes(6);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow },
            "cursor-live-identity-mismatch", Email: "other@example.invalid",
            IdentityFingerprint: CursorIdentity.Fingerprint("other@example.invalid", "other")!,
            // Deliberately older than the cache already on disk.
            AttemptedAt: Now.AddMinutes(1));
        var mismatch = await data.Collector().RefreshAsync(data.Binding, default);

        Assert.Null(mismatch.Sample);
        Assert.Equal("cursor-live-identity-mismatch", mismatch.Failure);
        Assert.Null(data.Collector().ReadCached(data.Binding).Sample);
    }

    [Fact]
    public async Task MismatchFailureAndUnverifiedSampleAreNeverExposed()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);

        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow },
            "cursor-live-identity-mismatch", Email: data.Email,
            IdentityFingerprint: data.Fingerprint, AttemptedAt: data.Clock.UtcNow);
        var explicitMismatch = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(explicitMismatch.Sample);

        // A response carrying a quota without an exact matching fingerprint is equally
        // untrusted, even if its failure field is otherwise empty.
        data.Clock.UtcNow = Now.AddMinutes(6);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow }, null,
            Email: data.Email, IdentityFingerprint: null, AttemptedAt: data.Clock.UtcNow);
        var unverified = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(unverified.Sample);
        Assert.Equal("cursor-live-identity-mismatch", unverified.Failure);
    }

    [Fact]
    public async Task MismatchFailureSurvivesCacheWriteFailure()
    {
        using var data = new Data();
        Directory.CreateDirectory(data.CachePath);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow },
            "cursor-live-identity-mismatch", Email: "other@example.invalid",
            IdentityFingerprint: CursorIdentity.Fingerprint("other@example.invalid", "other")!,
            AttemptedAt: data.Clock.UtcNow);

        var result = await data.Collector().RefreshAsync(data.Binding, default);

        Assert.Null(result.Sample);
        Assert.Equal("cursor-live-identity-mismatch", result.Failure);
    }

    [Fact]
    public async Task ExternalBindingChangeClearsCollectorStateAndEphemeralEmail()
    {
        using var data = new Data();
        var collector = data.Collector();
        var good = await collector.RefreshAsync(data.Binding, default);
        Assert.Equal(data.Email, good.Email);

        var rebound = data.Binding with { Generation = Guid.NewGuid().ToString("N") };
        data.Connections.Save(rebound);
        var changed = collector.ReadCached(data.Binding);

        Assert.Null(changed.Sample);
        Assert.Equal("cursor-live-identity-mismatch", changed.Failure);
        Assert.Null(changed.Email);
        Assert.Null(collector.ReadCached(data.Binding).Sample);
    }

    [Fact]
    public async Task CancellationLeavesCacheAndReceiptUnchanged()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);
        var before = File.ReadAllText(data.CachePath);
        using var cancellation = new CancellationTokenSource();
        data.Client.BeforeFetch = (_, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            data.Collector().RefreshAsync(data.Binding, cancellation.Token));

        Assert.Equal(before, File.ReadAllText(data.CachePath));
        var restarted = data.Collector().ReadCached(data.Binding);
        Assert.Equal(Now, restarted.Sample!.ObservedAt);
        Assert.Null(restarted.Failure);
    }

    [Fact]
    public async Task DisconnectDuringRequestCannotCommitOrResurfaceQuota()
    {
        using var data = new Data();
        data.Client.BeforeFetch = (_, _) =>
        {
            data.Connections.Save(data.Binding with { Disconnected = true });
            return Task.CompletedTask;
        };

        var result = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.False(File.Exists(data.CachePath));
        Assert.Null(data.Collector().ReadCached(data.Binding).Sample);
    }

    [Fact]
    public async Task RetryAfterSurvivesRestartAndDoesNotIssueEarlyRequest()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(null, "cursor-live-rate-limited", Now.AddMinutes(15),
            data.Email, data.Fingerprint, data.Clock.UtcNow);
        var limited = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Equal("cursor-live-rate-limited", limited.Failure);

        var restarted = data.Collector();
        data.Clock.UtcNow = Now.AddMinutes(10);
        var skipped = await restarted.RefreshAsync(data.Binding, default);
        Assert.Equal("cursor-live-rate-limited", skipped.Failure);
        Assert.Equal(2, data.Client.Calls);
        Assert.Equal(Now, skipped.Sample!.ObservedAt);

        data.Clock.UtcNow = Now.AddMinutes(15);
        data.Client.Next = new(data.Sample with { ObservedAt = data.Clock.UtcNow }, null,
            Email: data.Email, IdentityFingerprint: data.Fingerprint,
            AttemptedAt: data.Clock.UtcNow);
        var recovered = await restarted.RefreshAsync(data.Binding, default);
        Assert.Null(recovered.Failure);
        Assert.Equal(3, data.Client.Calls);
    }

    [Fact]
    public async Task SandRateLimitKeepsMonthlySampleAndBackoffAcrossRestart()
    {
        using var data = new Data();
        data.Client.Next = new(data.Sample, "cursor-sand-unavailable", Now.AddMinutes(15),
            data.Email, data.Fingerprint, Now);

        var partial = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Equal("cursor-sand-unavailable", partial.Failure);
        Assert.Equal(data.Sample, partial.Sample);

        data.Clock.UtcNow = Now.AddMinutes(10);
        var restarted = data.Collector();
        var skipped = await restarted.RefreshAsync(data.Binding, default);
        Assert.Equal("cursor-sand-unavailable", skipped.Failure);
        Assert.NotNull(skipped.Sample);
        Assert.Equal(data.Sample.ObservedAt, skipped.Sample.ObservedAt);
        Assert.Equal(data.Sample.Windows.ToArray(), skipped.Sample.Windows.ToArray());
        Assert.Equal(Now.AddMinutes(15), skipped.RetryAfter);
        Assert.Equal(1, data.Client.Calls);
    }

    [Fact]
    public async Task ProviderLiveRefreshHasStableSingleFlightBusyOwner()
    {
        using var data = new Data();
        var collector = data.Collector();
        var service = new CursorQuotaService(data.Accounts, data.Profile, data.Client, collector, data.Clock);
        data.Client.Block = true;
        var first = service.RefreshLiveAsync(default);
        await data.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var busy = await service.RefreshLiveAsync(default);
        Assert.Equal("cursor-live-busy", busy.FailureCategory);
        Assert.True(service.IsRefreshing);

        data.Client.Release.SetResult();
        var completed = await first;
        Assert.Null(completed.FailureCategory);
        Assert.False(service.IsRefreshing);
    }

    [Fact]
    public async Task ProviderLoadsBindingScopedCacheOnConstructionWithoutDiskReadsFromSnapshot()
    {
        using var data = new Data();
        await data.Collector().RefreshAsync(data.Binding, default);
        var service = new CursorQuotaService(data.Accounts, data.Profile, data.Client,
            data.Collector(), data.Clock);

        Assert.Equal(Now, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(data.Sample.Windows, service.Snapshot.Windows);
    }

    private sealed class Data : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-cursor-" + Guid.NewGuid().ToString("N"));
        public MutableClock Clock { get; } = new(Now);
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public CursorConnectionStore Connections { get; }
        public CursorConnectionBinding Binding { get; }
        public string Email { get; } = "cursor@example.invalid";
        public string Fingerprint { get; }
        public CursorUsageSample Sample { get; }
        public FakeClient Client { get; }
        public string CachePath => Path.Combine(Root, "accounts", Profile.Id, "cursor-usage.json");

        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewCursor("Cursor fixture");
            Accounts.Save(initial with { Version = 3, Profiles = initial.Profiles.Append(Profile).ToArray() });
            Fingerprint = CursorIdentity.Fingerprint(Email, "cursor-account")!;
            Binding = new(1, Profile.Id, Fingerprint, Now.AddDays(-1), Guid.NewGuid().ToString("N"));
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
            Sample = new(Now,
            [
                new CodexQuotaWindow("cursor-auto", 25, null, Now.AddDays(29), CodexWindowKind.Other)
                {
                    UsedAmount = 25m, LimitAmount = 100m, RemainingAmount = 75m, Unit = "USD",
                    IsEnabled = true
                },
                new CodexQuotaWindow("cursor-api", null, null, Now.AddDays(29), CodexWindowKind.Other)
                {
                    UsedAmount = 0m, LimitAmount = null, RemainingAmount = null, Unit = "USD",
                    IsEnabled = true
                }
            ], Now, Now.AddDays(30), "pro", "individual");
            Client = new(this);
        }

        public CursorUsageCollector Collector() => new(Accounts, Profile.Id, Client, Clock);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeClient(Data data) : ICursorUsageClient
    {
        public CursorUsageResponse Next { get; set; }
            = new(data.Sample, null, null, data.Email, data.Fingerprint, Now);
        public int Calls { get; private set; }
        public bool Block { get; set; }
        public Func<CursorConnectionBinding, CancellationToken, Task>? BeforeFetch { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CursorConnectionResult> ReadIdentityAsync(CancellationToken token) =>
            Task.FromResult(new CursorConnectionResult(true, IdentityFingerprint: data.Fingerprint,
                Email: data.Email, Binding: data.Binding));

        public async Task<CursorUsageResponse> FetchAsync(CursorConnectionBinding binding, CancellationToken token)
        {
            Calls++;
            Started.TrySetResult();
            if (BeforeFetch is not null) await BeforeFetch(binding, token);
            if (Block)
                await Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return Next;
        }
    }
}
