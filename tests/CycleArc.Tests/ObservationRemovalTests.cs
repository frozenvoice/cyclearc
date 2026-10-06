using System.Reflection;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public sealed class ObservationRemovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:10:00Z");
    private static readonly string[] LegacyNames =
    [
        "quota-observations.json", "quota-observations.json.bak",
        "quota-observation-epoch.json", "quota-observation-epoch.json.bak"
    ];

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public async Task CurrentQuotaRefreshNeverCreatesObservationFiles(UsageProviderId provider)
    {
        await using var fixture = new Fixture(provider);
        AssertLegacyAbsent(fixture);
        fixture.Service.NextRefresh = Snapshot(provider, Now, 23);
        await fixture.Manager.RefreshManuallyAsync(default);
        await fixture.Manager.RefreshAutomaticallyAsync(TimeSpan.FromMinutes(1), default);
        await fixture.Manager.RefreshPassiveAsync(default);
        var selected = fixture.Manager.Selected!;
        Assert.Equal(fixture.Profile.Id, selected.Profile.Id);
        Assert.True(selected.IsConnected);
        Assert.Equal(CodexQuotaStatus.Available, selected.Snapshot.Status);
        Assert.Equal(23, selected.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(Now, selected.Snapshot.LastSuccessfulRefresh);
        AssertLegacyAbsent(fixture);
        var restarted = fixture.Restart();
        Assert.Equal(23, restarted.Selected!.Snapshot.Windows[0].UsedPercent);
        AssertLegacyAbsent(fixture);
        Assert.True(restarted.Remove(fixture.Profile.Id));
        AssertLegacyAbsent(fixture);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public async Task ExistingObservationFilesAreInertThroughRefreshRestartAndRemoval(UsageProviderId provider)
    {
        // Invalid JSON and exclusive locks distinguish an inert old file from a
        // successfully loaded, rewritten or repaired legacy history.
        await using var fixture = new Fixture(provider, seedLegacy: true);
        var locks = fixture.LegacyBytes.Keys.Select(path => new FileStream(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None)).ToArray();
        try
        {
            fixture.Service.NextRefresh = Snapshot(provider, Now, 12);
            await fixture.Manager.RefreshManuallyAsync(default);
            await fixture.Manager.RefreshAutomaticallyAsync(TimeSpan.FromMinutes(1), default);
            await fixture.Manager.RefreshPassiveAsync(default);
            fixture.Manager.Rename(fixture.Profile.Id, "Current quota");
            var registry = File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json"));
            var restarted = fixture.Restart();
            Assert.Equal(CodexQuotaStatus.Available, restarted.Selected!.Snapshot.Status);
            Assert.Equal(12, restarted.Selected.Snapshot.Windows[0].UsedPercent);
            Assert.Equal(Now, restarted.Selected.Snapshot.LastSuccessfulRefresh);
            Assert.Equal("Current quota", restarted.Selected.DisplayName);
            Assert.Equal(registry, File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json")));
            Assert.Equal("synthetic settings sentinel", File.ReadAllText(fixture.SettingsPath));
            Assert.True(restarted.Remove(fixture.Profile.Id));
            Assert.Empty(restarted.Accounts);
        }
        finally { foreach (var stream in locks) stream.Dispose(); }
        AssertLegacyUnchanged(fixture);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Cursor)]
    public async Task ReconnectKeepsOldObservationFilesAndOnlyRefreshesAfterSuccess(UsageProviderId provider)
    {
        await using var fixture = new Fixture(provider, seedLegacy: true);
        var original = fixture.Manager.Selected!.Snapshot;
        fixture.Service.LoginStatus = CodexQuotaStatus.Cancelled;
        fixture.Service.ConnectSuccess = false;
        if (provider == UsageProviderId.Codex)
            Assert.Equal(CodexQuotaStatus.Cancelled,
                (await fixture.Manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default)).Status);
        else Assert.False((await fixture.Manager.ConnectCursorAsync(fixture.Profile.Id, "", default)).Success);
        Assert.Same(original, fixture.Manager.Selected!.Snapshot);
        Assert.Equal(0, fixture.Service.RefreshCalls);
        AssertLegacyUnchanged(fixture);
        fixture.Service.LoginStatus = CodexQuotaStatus.Available;
        fixture.Service.ConnectSuccess = true;
        fixture.Service.NextRefresh = Snapshot(provider, Now.AddMinutes(2), 26);
        if (provider == UsageProviderId.Codex)
            Assert.Equal(CodexQuotaStatus.Available,
                (await fixture.Manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default)).Status);
        else Assert.True((await fixture.Manager.ConnectCursorAsync(fixture.Profile.Id, "", default)).Success);
        Assert.Equal(1, fixture.Service.RefreshCalls);
        Assert.Equal(26, fixture.Manager.Selected!.Snapshot.Windows[0].UsedPercent);
        AssertLegacyUnchanged(fixture);
        var restarted = fixture.Restart();
        Assert.Equal(26, restarted.Selected!.Snapshot.Windows[0].UsedPercent);
        AssertLegacyUnchanged(fixture);
    }

    [Fact]
    public async Task ImportedReplacementKeepsAcceptedQuotaOldCacheAndOldObservationFiles()
    {
        await using var fixture = new Fixture(UsageProviderId.Codex, seedLegacy: true);
        var imported = fixture.Profile with { IsManaged = false };
        fixture.Store.Save(new(3, imported.Id, [imported]));
        var oldCache = fixture.Store.SnapshotPath(imported);
        File.WriteAllText(oldCache, "synthetic imported quota cache");
        var identity = new CodexAccountIdentity(CodexQuotaStatus.Available, "new-managed@example.invalid", "pro");
        fixture.Provider.Configure = (profile, service) =>
        {
            if (profile.Id == imported.Id) return;
            service.LoginIdentity = identity;
            service.NextRefresh = Snapshot(UsageProviderId.Codex, Now.AddMinutes(1), 33)
                with { IdentityFingerprint = identity.StableAccountFingerprint };
        };
        var restarted = fixture.Restart();
        var result = await restarted.LoginAsync(imported.Id, "", (_, _) => Task.CompletedTask, default);
        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        var replacement = Assert.Single(restarted.Accounts);
        Assert.NotEqual(imported.Id, replacement.Profile.Id);
        Assert.True(replacement.Profile.IsManaged);
        Assert.Equal(33, replacement.Snapshot.Windows[0].UsedPercent);
        Assert.Equal("synthetic imported quota cache", File.ReadAllText(oldCache));
        AssertLegacyUnchanged(fixture);
    }

    [Fact]
    public async Task RemovalRejectsCapturedServiceCallbackAndKeepsCurrentCacheAndOldFiles()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude, seedLegacy: true);
        var late = fixture.Service.CaptureCallbacks();
        var cache = fixture.Store.SnapshotPath(fixture.Profile);
        File.WriteAllText(cache, "synthetic current quota cache");
        Assert.True(fixture.Manager.Remove(fixture.Profile.Id));
        var changes = 0;
        fixture.Manager.Changed += () => changes++;
        late?.Invoke(Snapshot(UsageProviderId.Claude, Now.AddMinutes(2), 99));
        Assert.Empty(fixture.Manager.Accounts);
        Assert.Equal(0, changes);
        Assert.Equal("synthetic current quota cache", File.ReadAllText(cache));
        AssertLegacyUnchanged(fixture);
    }

    [Fact]
    public async Task PassivePollingPreservesOriginalSourceTimeAndNeverIssuesRemoteRequests()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        var original = fixture.Service.Snapshot;
        for (var index = 0; index < 20; index++)
        {
            await fixture.Manager.RefreshPassiveAsync(default);
            fixture.Manager.Select(fixture.Profile.Id);
            Assert.Same(original, fixture.Manager.Selected!.Snapshot);
        }
        Assert.Equal(20, fixture.Service.RefreshCalls);
        Assert.Equal(0, fixture.Service.RemoteRequests);
        Assert.Equal(original.LastSuccessfulRefresh, fixture.Manager.Snapshot.LastSuccessfulRefresh);
        fixture.Service.Emit(Snapshot(UsageProviderId.Claude, Now.AddMinutes(1), 0));
        Assert.Equal(0, fixture.Manager.Snapshot.Windows[0].UsedPercent);
        AssertLegacyAbsent(fixture);
    }

    [Fact]
    public async Task RepeatedAccountEventsKeepLatestQuotaAndAccountsIndependent()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        var otherProfile = fixture.Manager.AddClaude("Other synthetic account");
        var other = fixture.Provider.Services[otherProfile.Id];
        for (var index = 1; index <= 40; index++)
        {
            fixture.Service.Emit(Snapshot(UsageProviderId.Claude, Now.AddSeconds(index), index));
            other.Emit(Snapshot(UsageProviderId.Claude, Now.AddSeconds(index), 80 + index / 10.0));
        }
        var accounts = fixture.Manager.Accounts;
        Assert.Equal(40, accounts[0].Snapshot.Windows[0].UsedPercent);
        Assert.Equal(84, accounts[1].Snapshot.Windows[0].UsedPercent);
        Assert.Equal(fixture.Profile.Id, fixture.Manager.SelectedId);
        Assert.Equal(0, fixture.Service.RemoteRequests + other.RemoteRequests);
        AssertLegacyAbsent(fixture);
    }

    [Fact]
    public void ProductionManagerAndProjectionHaveNoObservationWorkerOrHistoryApi()
    {
        var core = typeof(CodexAccountManager).Assembly;
        Assert.DoesNotContain(core.GetTypes(), type => type.Namespace == "CycleArc.Observations");
        Assert.DoesNotContain(typeof(CodexAccountManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name.Contains("observ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(CodexAccountManager).GetMembers(),
            member => member.Name.Contains("observ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(CodexAccountView).GetProperties(),
            property => property.Name.Contains("observ", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertLegacyAbsent(Fixture fixture) => Assert.False(Directory.Exists(Path.Combine(fixture.Root, "accounts"))
        && Directory.EnumerateFiles(Path.Combine(fixture.Root, "accounts"), "quota-observ*", SearchOption.AllDirectories).Any());

    private static void AssertLegacyUnchanged(Fixture fixture) => Assert.All(fixture.LegacyBytes,
        entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, DateTimeOffset at, double value) =>
        new(CodexQuotaStatus.Available, "pro", at, at, true, null, null,
            [new("synthetic-five-hour", value, 300, Now.AddHours(4), CodexWindowKind.FiveHour)], null)
        { Provider = provider, IdentityFingerprint = new string('A', 64) };

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-observation-removal-" + Guid.NewGuid().ToString("N"));
        public CodexAccountStore Store { get; }
        public CodexAccountProfile Profile { get; }
        public FakeProvider Provider { get; }
        public CodexAccountManager Manager { get; }
        public FakeService Service => Provider.Services[Profile.Id];
        public string SettingsPath => Path.Combine(Root, "settings.json");
        public Dictionary<string, byte[]> LegacyBytes { get; } = [];
        private readonly List<CodexAccountManager> _managers = [];

        public Fixture(UsageProviderId provider, bool seedLegacy = false)
        {
            Directory.CreateDirectory(Root);
            Store = new(Root);
            var id = Guid.NewGuid().ToString("N");
            Profile = new(id, provider == UsageProviderId.Codex ? Path.Combine(Root, "accounts", id, "codex-home") : "", "Synthetic",
                provider == UsageProviderId.Codex) { Provider = provider };
            Store.Save(new(3, Profile.Id, [Profile]));
            Provider = new(provider);
            if (seedLegacy)
            {
                var directory = Path.Combine(Root, "accounts", id);
                Directory.CreateDirectory(directory);
                foreach (var name in LegacyNames.Concat(["quota-observations.json." + Guid.NewGuid().ToString("N") + ".tmp"]))
                {
                    var path = Path.Combine(directory, name);
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{damaged rollback sentinel " + name);
                    File.WriteAllBytes(path, bytes);
                    LegacyBytes.Add(path, bytes);
                }
                File.WriteAllText(SettingsPath, "synthetic settings sentinel");
            }
            Manager = Restart();
        }

        public CodexAccountManager Restart()
        {
            var manager = new CodexAccountManager(Store, Path.Combine(Root, "unused-default-home"), [Provider]);
            _managers.Add(manager);
            return manager;
        }

        public ValueTask DisposeAsync()
        {
            Assert.All(_managers, manager => Assert.False(manager.Refresh.IsRefreshing));
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider(UsageProviderId id) : IUsageProvider
    {
        public UsageProviderId Id => id;
        public Dictionary<string, FakeService> Services { get; } = new(StringComparer.Ordinal);
        public Action<CodexAccountProfile, FakeService>? Configure { get; set; }
        public IUsageAccountService Create(CodexAccountProfile profile)
        {
            var previous = Services.GetValueOrDefault(profile.Id);
            var service = new FakeService(profile.Provider, previous?.Snapshot);
            Configure?.Invoke(profile, service);
            Services[profile.Id] = service;
            return service;
        }
    }

    private sealed class FakeService(UsageProviderId provider, CodexQuotaSnapshot? cached)
        : IUsageAccountService, ICodexAccountOperations, ICursorAccountOperations
    {
        public CodexQuotaSnapshot Snapshot { get; private set; } = cached ?? ObservationRemovalTests.Snapshot(provider, Now.AddMinutes(-1), 3);
        public string? Email => null;
        public string? IdentityFingerprint => Snapshot.IdentityFingerprint;
        public bool ConnectSuccess { get; set; } = true;
        public string? BoundIdentityFingerprint => IdentityFingerprint;
        public bool IsConnected { get; private set; } = true;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => provider == UsageProviderId.Claude;
        public int RemoteRequests { get; private set; }
        public int RefreshCalls { get; private set; }
        public CodexQuotaStatus LoginStatus { get; set; } = CodexQuotaStatus.Available;
        public CodexAccountIdentity? LoginIdentity { get; set; }
        public CodexQuotaSnapshot? NextRefresh { get; set; }
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => true;
        public event Action<CodexQuotaSnapshot>? Changed;
        public void Emit(CodexQuotaSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(snapshot); }
        public Action<CodexQuotaSnapshot>? CaptureCallbacks() => Changed;
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (provider != UsageProviderId.Claude) RemoteRequests++;
            Emit(NextRefresh ?? Snapshot);
            return Task.FromResult(new CodexRefreshResult(Snapshot, false, null));
        }
        public Task<CodexAccountIdentity> ProbeAccountAsync(CancellationToken token)
        {
            RemoteRequests++;
            return Task.FromResult(new CodexAccountIdentity(CodexQuotaStatus.Available, "synthetic@example.invalid", "pro"));
        }
        public Task<CodexLoginResult> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token)
        {
            RemoteRequests++;
            if (LoginStatus == CodexQuotaStatus.Available)
                Emit(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = provider,
                    IdentityFingerprint = LoginIdentity?.StableAccountFingerprint ?? new string('A', 64) });
            return Task.FromResult(new CodexLoginResult(LoginStatus, LoginIdentity));
        }
        public Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, CancellationToken token) => Task.FromResult(CreditRedemptionOutcome.Unavailable);
        public Task<CursorConnectionResult> ConnectCurrentAsync(CancellationToken token)
        {
            RemoteRequests++;
            return Task.FromResult(new CursorConnectionResult(ConnectSuccess, IdentityFingerprint: IdentityFingerprint));
        }
        public Task DisconnectAsync(CancellationToken token) { IsConnected = false; return Task.CompletedTask; }
    }
}
