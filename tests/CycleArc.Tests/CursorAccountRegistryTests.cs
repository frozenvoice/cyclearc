using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public class CursorAccountRegistryTests
{
    [Fact]
    public async Task RepeatedCurrentLoginReusesBoundProfileAndFailureDiscardsOnlyEmptyDraft()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var fail = false;
        var provider = new CursorStubProvider(profile => new CursorStub(store, profile, () => fail));
        var manager = new CodexAccountManager(store, data.Home("codex"), [new FakeProvider(UsageProviderId.Codex), provider]);
        Assert.True((await manager.ConnectCursorAsync(null, "Work", default)).Success);
        var cursor = Assert.Single(manager.Accounts, a => a.Profile.Provider == UsageProviderId.Cursor);
        var firstId = cursor.Profile.Id;
        manager.Rename(firstId, "My Cursor");
        Assert.True((await manager.ConnectCursorAsync(null, "Ignored new label", default)).Success);
        Assert.Equal(firstId, Assert.Single(manager.Accounts, a => a.Profile.Provider == UsageProviderId.Cursor).Profile.Id);
        Assert.Equal("My Cursor", manager.Selected!.Profile.Label);
        fail = true;
        Assert.False((await manager.ConnectCursorAsync(null, "Failed draft", default)).Success);
        Assert.Equal(2, manager.Accounts.Count);
        Assert.Equal(firstId, manager.SelectedId);
        Assert.False((await manager.ConnectCursorAsync(firstId, "Work", default)).Success);
        Assert.Equal(2, manager.Accounts.Count);
        Assert.False(manager.IsSigningIn);
        await manager.DisconnectCursorAsync(firstId, default);
        Assert.False(manager.Selected!.IsConnected);
        Assert.True(new CursorConnectionStore(store, firstId).Read().Binding!.Disconnected);
    }

    [Fact]
    public async Task CancelledNewConnectionReleasesLoginGateAndKeepsExistingSelection()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var cancel = true;
        var provider = new CursorStubProvider(profile => new CursorStub(store, profile, () =>
        {
            if (cancel) throw new OperationCanceledException();
            return false;
        }));
        var manager = new CodexAccountManager(store, data.Home("codex"), [new FakeProvider(UsageProviderId.Codex), provider]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ConnectCursorAsync(null, "Draft", default));
        Assert.Single(manager.Accounts);
        Assert.Equal("default", manager.SelectedId);
        Assert.False(manager.IsSigningIn);
        cancel = false;
        Assert.True((await manager.ConnectCursorAsync(null, "Retry", default)).Success);
    }

    [Fact]
    public void CursorAdditionPreservesOtherProvidersSelectionOrderAndAliasesAcrossRestart()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        IUsageProvider[] providers = [new FakeProvider(UsageProviderId.Codex),
            new FakeProvider(UsageProviderId.Claude), new FakeProvider(UsageProviderId.Cursor)];
        var manager = new CodexAccountManager(store, data.Home("codex"), providers);
        manager.Rename("default", "Existing Codex");
        var claude = manager.AddClaude("Existing Claude");
        manager.Select(claude.Id);
        var cursor = manager.AddCursor("Cursor work");
        Assert.Equal(claude.Id, manager.SelectedId);
        Assert.Equal("", cursor.HomePath);
        Assert.False(cursor.IsManaged);
        Assert.True(manager.Move(cursor.Id, -1));
        var secondClaude = manager.AddClaude("Another Claude");
        var restored = new CodexAccountManager(store, data.Home("unused"), providers);
        Assert.Equal(claude.Id, restored.SelectedId);
        Assert.Equal(new[] { "default", cursor.Id, claude.Id, secondClaude.Id }, restored.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(new[] { "Existing Codex", "Cursor work", "Existing Claude", "Another Claude" },
            restored.Accounts.Select(a => a.Profile.Label));
        Assert.Equal(3, store.LoadOrMigrate(data.Root).Version);
        Assert.True(restored.Remove(cursor.Id));
        Assert.Equal(3, store.LoadOrMigrate(data.Root).Version);
        Assert.Equal(claude.Id, restored.SelectedId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UpgradeProtectsBackupFromOlderBuildsAndRejectsDowngrade(int originalVersion)
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var original = store.LoadOrMigrate(data.Home("codex")) with { Version = originalVersion };
        store.Save(original);
        var cursor = store.NewCursor("Cursor");
        store.Save(original with { Version = 3, Profiles = original.Profiles.Append(cursor).ToArray() });
        using var backup = JsonDocument.Parse(File.ReadAllText(Path.Combine(data.Root, "codex-accounts.json.bak")));
        Assert.Equal(3, backup.RootElement.GetProperty("Version").GetInt32());
        Assert.Throws<InvalidDataException>(() => store.Save(original));
        Assert.Equal(2, store.LoadOrMigrate(data.Root).Profiles.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CursorRequiresVersionThree(int version)
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var original = store.LoadOrMigrate(data.Home("codex"));
        Assert.Throws<InvalidDataException>(() => store.Save(original with
        {
            Version = version, Profiles = original.Profiles.Append(store.NewCursor("Cursor")).ToArray()
        }));
        Assert.Single(store.LoadOrMigrate(data.Root).Profiles);
    }

    private sealed class FakeProvider(UsageProviderId id) : IUsageProvider
    {
        public UsageProviderId Id => id;
        public IUsageAccountService Create(CodexAccountProfile profile) => new FakeService(id);
    }

    private sealed class CursorStubProvider(Func<CodexAccountProfile, IUsageAccountService> create) : IUsageProvider
    {
        public UsageProviderId Id => UsageProviderId.Cursor;
        public IUsageAccountService Create(CodexAccountProfile profile) => create(profile);
    }

    private sealed class CursorStub(CodexAccountStore store, CodexAccountProfile profile, Func<bool> fail)
        : IUsageAccountService, ICursorAccountOperations
    {
        private readonly CursorConnectionStore _connection = new(store, profile.Id);
        public string? BoundIdentityFingerprint => _connection.Read().Binding?.IdentityFingerprint;
        public CodexQuotaSnapshot Snapshot => CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = UsageProviderId.Cursor };
        public string? Email => null;
        public string? IdentityFingerprint => BoundIdentityFingerprint;
        public bool IsConnected => _connection.Read().Binding is { Disconnected: false };
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => false;
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) => Task.FromResult(new CodexRefreshResult(Snapshot, true, null));
        public Task<CursorConnectionResult> ConnectCurrentAsync(CancellationToken token)
        {
            if (fail()) return Task.FromResult(new CursorConnectionResult(false, "cursor-live-auth-required"));
            var binding = new CursorConnectionBinding(1, profile.Id, new string('A', 64), DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            _connection.Save(binding);
            return Task.FromResult(new CursorConnectionResult(true, IdentityFingerprint: binding.IdentityFingerprint, Binding: binding));
        }
        public Task DisconnectAsync(CancellationToken token)
        {
            if (_connection.Read().Binding is { } binding) _connection.Save(binding with { Disconnected = true });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeService(UsageProviderId id) : IUsageAccountService
    {
        public CodexQuotaSnapshot Snapshot => CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = id };
        public string? Email => null;
        public string? IdentityFingerprint => null;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => false;
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed { add { } remove { } }
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) => Task.FromResult(new CodexRefreshResult(Snapshot, true, null));
    }
}
