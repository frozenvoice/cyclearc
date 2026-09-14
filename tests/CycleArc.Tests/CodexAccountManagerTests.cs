using CycleArc.Codex;
using System.Text;
using System.Threading.Channels;

namespace CycleArc.Tests;

public class CodexAccountManagerTests
{
    [Fact]
    public async Task ManyAccountsShareBoundedRefreshAndCancelledWaiterDoesNotReleaseOwner()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var state = store.LoadOrMigrate(data.Home("first"));
        var profiles = state.Profiles.Concat(Enumerable.Range(1, 5).Select(i =>
            new CodexAccountProfile(Guid.NewGuid().ToString("N"), data.Home("account" + i), "Account " + i))).ToArray();
        store.Save(state with { Profiles = profiles });
        var factory = new GatedAccountFactory();
        var manager = new CodexAccountManager(store, profiles[0].HomePath, p => data.Service(p, factory), () => AccountTestDirectory.Executable);
        var first = manager.RefreshManuallyAsync(CancellationToken.None);
        var initial = await factory.NextAsync();
        var other = await factory.NextAsync();
        using var waiter = new CancellationTokenSource();
        var joined = manager.RefreshManuallyAsync(waiter.Token);
        waiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
        Assert.True(manager.Refresh.IsRefreshing);
        Assert.False(first.IsCompleted);
        Assert.Equal(2, factory.Started);
        initial.ReleaseQuota(11);
        var third = await factory.NextAsync();
        Assert.True(manager.Refresh.IsRefreshing);
        other.ReleaseQuota(22);
        third.ReleaseQuota(33);
        for (var i = 3; i < profiles.Length; i++) (await factory.NextAsync()).ReleaseQuota((i + 1) * 11);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manager.Refresh.IsRefreshing);
        Assert.Equal(2, factory.MaximumActive);
        Assert.Equal(0, factory.Active);
        Assert.Equal(6, manager.Accounts.Count);
        Assert.Equal(new[] { 11d, 22, 33, 44, 55, 66 }, manager.Accounts.Select(a => a.Snapshot.Windows[0].UsedPercent!.Value).Order().ToArray());
        foreach (var account in manager.Accounts)
        {
            var cached = new CodexSnapshotStore(store.SnapshotPath(account.Profile)).Load()!;
            Assert.Equal(account.Snapshot.Windows[0].UsedPercent, cached.Windows[0].UsedPercent);
        }
    }

    [Fact]
    public async Task OneSignedOutAccountDoesNotEraseOthersAndSelectionSurvivesRestart()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var state = store.LoadOrMigrate(data.Home("first"));
        var second = new CodexAccountProfile(Guid.NewGuid().ToString("N"), data.Home("second"), "Work");
        store.Save(state with { Profiles = [state.Profiles[0], second] });
        var signedOut = false;
        CodexQuotaService Service(CodexAccountProfile profile) => data.Service(profile,
            new ScriptedCodexProcessFactory { Responder = line => signedOut && profile.Id == second.Id
                && JsonNode.Parse(line)?["method"]?.ToString() == "account/read"
                ? ["""{"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}"""] : AccountTestProtocol.Standard(line) });
        var manager = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        await manager.RefreshManuallyAsync(CancellationToken.None);
        Assert.All(manager.Accounts, account => Assert.True(account.HasMatchingIdentity));
        manager.Select(second.Id);
        signedOut = true;
        await manager.RefreshManuallyAsync(CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, manager.Accounts[0].Snapshot.Status);
        Assert.Equal(25, manager.Accounts[0].Snapshot.Windows[0].UsedPercent);
        Assert.Equal(CodexQuotaStatus.SignedOut, manager.Selected!.Snapshot.Status);
        Assert.All(manager.Accounts, account => Assert.False(account.HasMatchingIdentity));
        Assert.Empty(manager.Selected.Snapshot.Windows);
        var restarted = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        Assert.Equal(second.Id, restarted.SelectedId);
        Assert.All(restarted.Accounts, account => Assert.Empty(account.Snapshot.Windows));
        await restarted.RefreshManuallyAsync(CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.SignedOut, restarted.Snapshot.Status);
        Assert.Equal(CodexQuotaStatus.Available, restarted.Accounts[0].Snapshot.Status);
    }

    [Fact]
    public async Task DiscoveryImportsVerifiedAccountReferencesWithoutCloningOrDuplicates()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var existing = data.Home("existing");
        var next = data.Home("custom-codex");
        var empty = data.Home("unsigned");
        var malformed = data.Home("malformed");
        var requested = new List<string>();
        CodexQuotaService Service(CodexAccountProfile profile) => data.Service(profile, new ScriptedCodexProcessFactory { Responder = line =>
        {
            lock (requested) requested.Add(JsonNode.Parse(line)!["method"]!.ToString());
            if (JsonNode.Parse(line)?["method"]?.ToString() == "account/read")
            {
                if (profile.HomePath == empty) return ["""{"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}"""];
                if (profile.HomePath == malformed) return ["""{"id":2,"result":{"unexpected":true}}"""];
            }
            return AccountTestProtocol.Standard(line);
        }});
        var manager = new CodexAccountManager(store, existing, Service, () => AccountTestDirectory.Executable);
        var result = await manager.DiscoverAsync([existing, next, next, empty, malformed], true, CancellationToken.None);
        Assert.Equal(new CodexDiscoveryResult(1, 1, 1), result);
        Assert.Equal(2, manager.Accounts.Count);
        Assert.Equal(next, manager.Accounts[1].Profile.HomePath);
        Assert.False(manager.Accounts[1].Profile.IsManaged);
        Assert.DoesNotContain("account/rateLimits/read", requested);
        Assert.DoesNotContain("account/login/start", requested);
        Assert.Empty(Directory.EnumerateFiles(next));
        Assert.Equal(0, (await manager.DiscoverAsync([next], false, CancellationToken.None)).Added);
        var id = manager.Accounts[1].Profile.Id;
        Assert.True(manager.Remove(id));
        Assert.True(Directory.Exists(next));
        Assert.Equal(0, (await manager.DiscoverAsync([next], true, CancellationToken.None)).Added);
        Assert.Equal(1, (await manager.DiscoverAsync([next], false, CancellationToken.None)).Added);
    }

    [Fact]
    public async Task LoginDoesNotBlockOtherAccountsOrAllowAnotherInteractiveLogin()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var factory = new GatedAccountFactory();
        var loggedInFactory = new ScriptedCodexProcessFactory { Responder = line =>
            JsonNode.Parse(line)?["method"]?.ToString() == "account/login/start"
                ? AccountTestProtocol.Standard(line).Append(AccountTestProtocol.Completed).ToArray()
                : AccountTestProtocol.Standard(line) };
        var manager = new CodexAccountManager(store, data.Home("existing"), profile =>
            data.Service(profile, profile.IsManaged ? loggedInFactory : factory), () => AccountTestDirectory.Executable);
        var browserReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = manager.LoginAsync(null, "New", (_, _) => { browserReached.TrySetResult(); return browserRelease.Task; }, CancellationToken.None);
        try
        {
            await browserReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newProfile = manager.Accounts.Single(a => a.Profile.IsManaged);
            Assert.True(newProfile.IsSigningIn);
            Assert.False(manager.Remove(newProfile.Profile.Id));
            Assert.Equal(CodexQuotaStatus.Unavailable, (await manager.LoginAsync(null, "Duplicate", (_, _) => Task.CompletedTask, CancellationToken.None)).Status);
            Assert.Equal(2, manager.Accounts.Count);
            var refresh = manager.RefreshManuallyAsync(CancellationToken.None);
            (await factory.NextAsync()).ReleaseQuota(17);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(17, manager.Accounts[0].Snapshot.Windows[0].UsedPercent);
            Assert.False(login.IsCompleted);
            Assert.True(manager.IsSigningIn);
        }
        finally { browserRelease.TrySetResult(); }
        var outcome = await login.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CodexQuotaStatus.Available, outcome.Status);
        Assert.False(manager.IsSigningIn);
        Assert.Equal(25, manager.Accounts[1].Snapshot.Windows[0].UsedPercent);
        Assert.NotEqual(manager.Accounts[0].Profile.HomePath, loggedInFactory.LastCommand!.CodexHome);
        Assert.True(loggedInFactory.LastCommand.ManagedHome);
    }

    [Fact]
    public async Task FailedImportedRecoveryOnlyAttemptsLoginInAnIsolatedHome()
    {
        using var data = new AccountTestDirectory();
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        var manager = new CodexAccountManager(new(data.Root), data.Home("existing"), profile => data.Service(profile, factory), () => AccountTestDirectory.Executable);
        var result = await manager.LoginAsync("default", "", (_, _) => throw new InvalidOperationException("Must not open browser"), CancellationToken.None);
        Assert.NotEqual(CodexQuotaStatus.Available, result.Status);
        Assert.Equal(1, factory.StartCount);
        Assert.True(factory.LastCommand!.ManagedHome);
        Assert.NotEqual(manager.Accounts.Single().Profile.HomePath, factory.LastCommand.CodexHome);
        Assert.Equal("default", manager.Accounts.Single().Profile.Id);
    }

    [Fact]
    public void RemovingSelectedAccountChoosesRemainingProfileAndNeverDeletesQuota()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("existing"));
        var next = store.NewManaged("Other");
        store.Save(initial with { Profiles = [initial.Profiles[0], next], SelectedId = next.Id });
        Directory.CreateDirectory(Path.GetDirectoryName(store.SnapshotPath(next))!);
        File.WriteAllText(store.SnapshotPath(next), "preserved synthetic cache");
        var manager = new CodexAccountManager(store, data.Root, p => data.Service(p, new ScriptedCodexProcessFactory()), () => null);
        Assert.True(manager.Remove(next.Id));
        Assert.Equal("default", manager.SelectedId);
        Assert.Equal("preserved synthetic cache", File.ReadAllText(store.SnapshotPath(next)));
        Assert.True(manager.Remove("default"));
        Assert.Empty(manager.Accounts);
        Assert.Equal("", manager.SelectedId);
        Assert.Equal(CodexQuotaStatus.SignedOut, manager.Snapshot.Status);
        Assert.Empty(store.LoadOrMigrate(data.Root).Profiles);
    }

    [Fact]
    public async Task AccountOrderPersistsWithoutChangingSelectionQuotasOrStartingRefresh()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("existing"));
        var profiles = initial.Profiles.Concat(new[] { store.NewManaged("Work"), store.NewManaged("Other") }).ToArray();
        store.Save(initial with { Profiles = profiles });
        var requests = 0;
        CodexQuotaService Service(CodexAccountProfile profile) => data.Service(profile,
            new ScriptedCodexProcessFactory { Responder = line =>
            {
                Interlocked.Increment(ref requests);
                if (JsonNode.Parse(line)?["method"]?.ToString() == "account/read")
                    return [AccountTestProtocol.Account(profile.Id + "@example.invalid")];
                var responses = AccountTestProtocol.Standard(line);
                if (JsonNode.Parse(line)?["method"]?.ToString() != "account/rateLimits/read") return responses;
                var response = JsonNode.Parse(responses.Single())!;
                response["result"]!["rateLimits"]!["primary"]!["usedPercent"] = (Array.IndexOf(profiles, profile) + 1) * 10;
                return [response.ToJsonString()];
            } });
        var manager = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        await manager.RefreshManuallyAsync(CancellationToken.None);
        manager.Select(profiles[1].Id);
        var completedRequests = requests;
        var selectedSnapshot = manager.Snapshot;
        var caches = manager.Accounts.ToDictionary(a => a.Profile.Id, a => File.ReadAllText(store.SnapshotPath(a.Profile)));
        Assert.True(manager.Move(profiles[1].Id, -1));
        Assert.True(manager.Move(profiles[0].Id, 1));
        var expected = new[] { profiles[1].Id, profiles[2].Id, profiles[0].Id };
        Assert.Equal(expected, manager.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(profiles[1].Id, manager.SelectedId);
        Assert.Same(selectedSnapshot, manager.Snapshot);
        Assert.Equal(completedRequests, requests);
        Assert.All(profiles, profile => Assert.Equal(caches[profile.Id], File.ReadAllText(store.SnapshotPath(profile))));
        var restarted = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        Assert.Equal(expected, restarted.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(profiles[1].Id, restarted.SelectedId);
        Assert.All(restarted.Accounts, account => Assert.Empty(account.Snapshot.Windows));
        await restarted.RefreshManuallyAsync(CancellationToken.None);
        Assert.Equal(new double?[] { 20, 30, 10 }, restarted.Accounts.Select(a => a.Snapshot.Windows.Single().UsedPercent));
    }

    [Fact]
    public void AccountOrderIgnoresBoundariesAndInvalidRequests()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var manager = new CodexAccountManager(store, data.Home("existing"),
            profile => data.Service(profile, new ScriptedCodexProcessFactory()), () => null);
        var changed = 0;
        manager.Changed += () => changed++;
        Assert.False(manager.Move("default", -1));
        Assert.False(manager.Move("default", 1));
        Assert.False(manager.Move("missing", 1));
        Assert.False(manager.Move("default", 0));
        Assert.False(manager.Move("default", 2));
        Assert.Equal(0, changed);
        Assert.Equal("default", manager.SelectedId);
        Assert.Single(store.LoadOrMigrate(data.Root).Profiles);
    }

    [Fact]
    public async Task FirstSuccessfulNewLoginSelectsUsableAccountAndKeepsEveryProfile()
    {
        using var data = new AccountTestDirectory();
        var manager = new CodexAccountManager(new(data.Root), data.Home("unused-default"), profile =>
            data.Service(profile, new ScriptedCodexProcessFactory { Responder = line =>
                JsonNode.Parse(line)?["method"]?.ToString() == "account/login/start"
                    ? AccountTestProtocol.Standard(line).Append(AccountTestProtocol.Completed).ToArray()
                    : AccountTestProtocol.Standard(line) }), () => AccountTestDirectory.Executable);
        var result = await manager.LoginAsync(null, "First", (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        Assert.Equal("First", manager.Selected!.DisplayName);
        Assert.True(manager.Selected.Profile.IsManaged);
        Assert.Equal(CodexQuotaStatus.Available, manager.Snapshot.Status);
        Assert.Equal(2, manager.Accounts.Count);
    }
}

internal sealed class GatedAccountFactory : ICodexProcessFactory
{
    private readonly Channel<GatedAccountProcess> _requests = Channel.CreateUnbounded<GatedAccountProcess>();
    private int _active, _started, _maximum;
    public int Started => Volatile.Read(ref _started);
    public int Active => Volatile.Read(ref _active);
    public int MaximumActive => Volatile.Read(ref _maximum);
    public async Task<GatedAccountProcess> NextAsync() => await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public ICodexProcess Start(CodexLaunchCommand command)
    {
        Interlocked.Increment(ref _started);
        var active = Interlocked.Increment(ref _active);
        int old;
        do { old = Volatile.Read(ref _maximum); if (old >= active) break; }
        while (Interlocked.CompareExchange(ref _maximum, active, old) != old);
        return new GatedAccountProcess(command, process => _requests.Writer.TryWrite(process), () => Interlocked.Decrement(ref _active));
    }
}

internal sealed class GatedAccountProcess(CodexLaunchCommand command, Action<GatedAccountProcess> requested, Action disposed) : ICodexProcess
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    public Task WriteLineAsync(string line, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (JsonNode.Parse(line)?["method"]?.ToString() == "account/rateLimits/read") requested(this);
        else foreach (var response in AccountTestProtocol.Standard(line)) _output.Writer.TryWrite(response);
        return Task.CompletedTask;
    }
    public void ReleaseQuota(int used) => _output.Writer.TryWrite(new JsonObject { ["id"] = 3,
        ["result"] = new JsonObject { ["rateLimits"] = new JsonObject { ["primary"] = new JsonObject
            { ["usedPercent"] = used, ["windowDurationMins"] = 10080 }, ["secondary"] = null } } }.ToJsonString());
    public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken token) => await _output.Reader.ReadAsync(token);
    public Task DrainStderrAsync(StringBuilder sink, int maxBytes, CancellationToken token) => Task.CompletedTask;
    public bool HasExited { get; private set; }
    public bool KillCalled { get; private set; }
    public int? ProcessId => null;
    public string FileName => command.FileName;
    public string Arguments => command.Arguments;
    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token) => Task.FromResult(HasExited);
    public void KillTree() { KillCalled = true; HasExited = true; }
    public ValueTask DisposeAsync() { HasExited = true; disposed(); return ValueTask.CompletedTask; }
}
