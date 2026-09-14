using System.Diagnostics;
using System.Text.Json.Nodes;
using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexAccountProjectionTests
{
    [Fact]
    public async Task ImportedConflictIsPersistedDuringRefresh_WhileProjectionStaysPure()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("imported"));
        var managed = store.NewManaged("Managed");
        store.Save(initial with { Profiles = [initial.Profiles[0], managed] });

        CodexQuotaService Service(CodexAccountProfile profile) => data.Service(profile,
            new ScriptedCodexProcessFactory { Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method == "account/read"
                    ? [AccountTestProtocol.Account("shared@example.invalid")]
                    : AccountTestProtocol.Standard(line);
            } });

        var manager = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        await manager.RefreshManuallyAsync(CancellationToken.None);

        var marker = store.SnapshotPath(initial.Profiles[0]) + ".identity-conflict";
        Assert.True(File.Exists(marker));
        var imported = manager.Accounts.Single(account => account.Profile.Id == initial.Profiles[0].Id);
        var available = manager.Accounts.Single(account => account.Profile.Id == managed.Id);
        Assert.Equal("codex-identity-conflict", imported.Snapshot.TechnicalDetail);
        Assert.Equal(CodexQuotaStatus.Available, available.Snapshot.Status);

        var binding = new CodexIdentityBindingStore(store.SnapshotPath(initial.Profiles[0]), initial.Profiles[0].Id);
        using var held = new FileStream(binding.PathName + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var projection = Task.Run(() => manager.Accounts);
        Assert.True(await Task.WhenAny(projection, Task.Delay(250)) == projection);
        Assert.Equal(2, (await projection).Count);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task ProjectionDoesNotWaitForBindingLock_AndRefreshRevalidatesChangedBinding()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("first"));
        var second = new CodexAccountProfile(Guid.NewGuid().ToString("N"), data.Home("second"), "Second");
        store.Save(initial with { Profiles = [initial.Profiles[0], second] });

        var emails = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [initial.Profiles[0].Id] = "shared@example.invalid",
            [second.Id] = "shared@example.invalid",
        };
        CodexQuotaService Service(CodexAccountProfile profile) => data.Service(profile,
            new ScriptedCodexProcessFactory { Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method == "account/read"
                    ? [AccountTestProtocol.Account(emails[profile.Id])]
                    : AccountTestProtocol.Standard(line);
            } });

        var manager = new CodexAccountManager(store, data.Root, Service, () => AccountTestDirectory.Executable);
        await manager.RefreshManuallyAsync(CancellationToken.None);
        Assert.All(manager.Accounts, account =>
        {
            Assert.Equal(CodexQuotaStatus.Available, account.Snapshot.Status);
            Assert.True(account.HasMatchingIdentity);
        });

        var firstProfile = initial.Profiles[0];
        var firstBinding = new CodexIdentityBindingStore(store.SnapshotPath(firstProfile), firstProfile.Id);
        var lockPath = firstBinding.PathName + ".lock";
        IReadOnlyList<CodexAccountView> projected;
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var stopwatch = Stopwatch.StartNew();
            var projection = Task.Run(() => manager.Accounts);
            var completed = await Task.WhenAny(projection, Task.Delay(250)) == projection;
            stopwatch.Stop();
            if (!completed)
            {
                held.Dispose();
                await projection;
            }

            Assert.True(completed, $"Account projection waited on the binding lock for {stopwatch.Elapsed}.");
            projected = await projection;
        }

        Assert.Equal(2, projected.Count);
        Assert.All(projected, account => Assert.True(account.HasMatchingIdentity));

        firstBinding.Reset(new CodexAccountIdentity(CodexQuotaStatus.Available, "changed@example.invalid", "pro"));
        await manager.RefreshManuallyAsync(CancellationToken.None);

        var changed = manager.Accounts.Single(account => account.Profile.Id == firstProfile.Id);
        var unaffected = manager.Accounts.Single(account => account.Profile.Id == second.Id);
        Assert.Equal("codex-identity-mismatch", changed.Snapshot.TechnicalDetail);
        Assert.False(changed.Snapshot.HasUsablePercentages);
        Assert.False(changed.HasMatchingIdentity);
        Assert.Equal(CodexQuotaStatus.Available, unaffected.Snapshot.Status);
        Assert.True(unaffected.Snapshot.HasUsablePercentages);
        Assert.False(unaffected.HasMatchingIdentity);
    }
}
