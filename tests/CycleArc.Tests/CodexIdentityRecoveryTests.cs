using System.Text.Json.Nodes;
using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexIdentityRecoveryTests
{
    [Fact]
    public async Task ImportedRecoveryReplacesProfileAfterVerifiedQuotaAndPreservesLatestOrderAndSelection()
    {
        using var scenario = new RecoveryScenario("managed@example.invalid");
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var oldCache = scenario.ReadImportedCache();
        var oldBinding = scenario.ReadImportedBinding();
        var opened = false;

        var result = await scenario.Manager.LoginAsync(
            scenario.Imported.Id,
            "ignored",
            (uri, _) =>
            {
                Assert.Equal("auth.openai.com", uri.Host);
                scenario.Manager.Rename(scenario.Imported.Id, "Renamed Main");
                Assert.True(scenario.Manager.Move(scenario.Imported.Id, 1));
                opened = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        Assert.True(opened);

        var state = scenario.Store.LoadOrMigrate(scenario.Data.Root);
        Assert.Equal(scenario.Managed.Id, state.Profiles[0].Id);
        Assert.DoesNotContain(state.Profiles, profile => profile.Id == scenario.Imported.Id);

        var replacement = state.Profiles[1];
        Assert.True(replacement.IsManaged);
        Assert.NotEqual(scenario.Imported.Id, replacement.Id);
        Assert.Equal("Renamed Main", replacement.Label);
        Assert.Equal(Path.Combine(scenario.Data.Root, "accounts", replacement.Id, "codex-home"),
            replacement.HomePath);
        Assert.Equal(replacement.Id, state.SelectedId);
        Assert.Contains(scenario.Imported.HomePath, state.IgnoredHomes, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(oldCache, scenario.ReadImportedCache());
        Assert.Equal(oldBinding, scenario.ReadImportedBinding());
        Assert.DoesNotContain(scenario.Manager.Accounts, account => account.Profile.Id == scenario.Imported.Id);
        Assert.Equal(CodexQuotaStatus.Available,
            scenario.Manager.Accounts.Single(account => account.Profile.Id == replacement.Id).Snapshot.Status);
    }

    [Fact]
    public async Task ImportedRecoveryKeepsSelectionWhenAnotherProfileWasSelected()
    {
        using var scenario = new RecoveryScenario("managed@example.invalid");
        scenario.Manager.Select(scenario.Managed.Id);
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var result = await scenario.Manager.LoginAsync(
            scenario.Imported.Id,
            "",
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        var state = scenario.Store.LoadOrMigrate(scenario.Data.Root);
        Assert.Equal(scenario.Managed.Id, state.SelectedId);
        Assert.Equal(scenario.Managed.Id, state.Profiles[1].Id);
        Assert.True(state.Profiles[0].IsManaged);
        Assert.Equal("Imported", state.Profiles[0].Label);
        Assert.Equal(state.SelectedId, scenario.Manager.SelectedId);
    }

    [Fact]
    public async Task ImportedRecoveryQuotaFailurePreservesRegistryProfileSelectionAndCache()
    {
        using var scenario = new RecoveryScenario("managed@example.invalid", candidateQuotaFailure: true);
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var oldRegistry = scenario.ReadRegistry();
        var oldCache = scenario.ReadImportedCache();
        var oldBinding = scenario.ReadImportedBinding();
        var result = await scenario.Manager.LoginAsync(
            scenario.Imported.Id,
            "",
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(CodexQuotaStatus.Unavailable, result.Status);
        Assert.Equal("codex-reconnect-quota-failed", result.Detail);
        Assert.Equal(oldRegistry, scenario.ReadRegistry());
        Assert.Equal(oldCache, scenario.ReadImportedCache());
        Assert.Equal(oldBinding, scenario.ReadImportedBinding());

        var state = scenario.Store.LoadOrMigrate(scenario.Data.Root);
        Assert.Equal(new[] { scenario.Imported.Id, scenario.Managed.Id }, state.Profiles.Select(profile => profile.Id));
        Assert.Equal(scenario.Imported.Id, state.SelectedId);
        Assert.False(state.Profiles.Single(profile => profile.Id == scenario.Imported.Id).IsManaged);
        Assert.DoesNotContain(state.Profiles, profile => profile.IsManaged && profile.Id != scenario.Managed.Id);
    }

    [Fact]
    public async Task ImportedRecoveryCancellationPreservesRegistryProfileSelectionAndCache()
    {
        using var scenario = new RecoveryScenario("managed@example.invalid", candidateDelay: TimeSpan.FromSeconds(30));
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var oldRegistry = scenario.ReadRegistry();
        var oldCache = scenario.ReadImportedCache();
        var oldBinding = scenario.ReadImportedBinding();
        using var cancellation = new CancellationTokenSource();
        var login = scenario.Manager.LoginAsync(
            scenario.Imported.Id,
            "",
            (_, _) => Task.CompletedTask,
            cancellation.Token);

        Assert.True(SpinWait.SpinUntil(
            () => scenario.CandidateFactory.LastProcess is not null,
            TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        var result = await login.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CodexQuotaStatus.Cancelled, result.Status);
        Assert.True(scenario.CandidateFactory.LastProcess!.KillCalled);
        Assert.Equal(oldRegistry, scenario.ReadRegistry());
        Assert.Equal(oldCache, scenario.ReadImportedCache());
        Assert.Equal(oldBinding, scenario.ReadImportedBinding());

        var state = scenario.Store.LoadOrMigrate(scenario.Data.Root);
        Assert.Equal(new[] { scenario.Imported.Id, scenario.Managed.Id }, state.Profiles.Select(profile => profile.Id));
        Assert.Equal(scenario.Imported.Id, state.SelectedId);
        Assert.DoesNotContain(state.Profiles, profile => profile.IsManaged && profile.Id != scenario.Managed.Id);
    }


    [Fact]
    public async Task ImportedRecoveryRejectsExistingManagedIdentityWithoutChangingState()
    {
        using var scenario = new RecoveryScenario("main@example.invalid");
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var oldRegistry = scenario.ReadRegistry();
        var oldCache = scenario.ReadImportedCache();
        var oldBinding = scenario.ReadImportedBinding();
        var result = await scenario.Manager.LoginAsync(
            scenario.Imported.Id,
            "",
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(CodexQuotaStatus.Unavailable, result.Status);
        Assert.Equal("codex-identity-conflict", result.Detail);
        Assert.Equal(oldRegistry, scenario.ReadRegistry());
        Assert.Equal(oldCache, scenario.ReadImportedCache());
        Assert.Equal(oldBinding, scenario.ReadImportedBinding());

        var state = scenario.Store.LoadOrMigrate(scenario.Data.Root);
        Assert.Equal(new[] { scenario.Imported.Id, scenario.Managed.Id }, state.Profiles.Select(profile => profile.Id));
        Assert.Equal(scenario.Imported.Id, state.SelectedId);
        Assert.False(state.Profiles.Single(profile => profile.Id == scenario.Imported.Id).IsManaged);
        Assert.True(state.Profiles.Single(profile => profile.Id == scenario.Managed.Id).IsManaged);
    }

    [Fact]
    public async Task ImportedDuplicateProjectionSuppressesQuotaAndCreditsButManagedProfileRemainsUsable()
    {
        using var scenario = new RecoveryScenario("main@example.invalid");
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);

        var imported = scenario.Manager.Accounts.Single(account => account.Profile.Id == scenario.Imported.Id);
        var managed = scenario.Manager.Accounts.Single(account => account.Profile.Id == scenario.Managed.Id);

        Assert.Equal(CodexQuotaStatus.Unavailable, imported.Snapshot.Status);
        Assert.Equal("codex-identity-conflict", imported.Snapshot.TechnicalDetail);
        Assert.False(imported.Snapshot.HasUsablePercentages);
        Assert.Null(imported.Email);
        Assert.False(imported.IsConnected);
        var importedStarts = scenario.ImportedFactory.StartCount;

        var credit = await scenario.Manager.ConsumeCreditAsync(
            scenario.Imported.Id, "synthetic-credit", CancellationToken.None);

        Assert.Equal(CreditRedemptionOutcome.Unavailable, credit);
        Assert.Equal(importedStarts, scenario.ImportedFactory.StartCount);
        Assert.Equal(CodexQuotaStatus.Available, managed.Snapshot.Status);
        Assert.True(managed.Snapshot.HasUsablePercentages);
        Assert.True(managed.IsConnected);
        Assert.Equal("main@example.invalid", managed.Email);
    }


    [Fact]
    public async Task RemovingOtherProfileAndRestartingDoesNotClearRequiredIdentityRecovery()
    {
        using var scenario = new RecoveryScenario("main@example.invalid");
        await scenario.Manager.RefreshManuallyAsync(CancellationToken.None);
        Assert.Equal("codex-identity-conflict", scenario.Manager.Accounts[0].Snapshot.TechnicalDetail);
        Assert.True(scenario.Manager.Remove(scenario.Managed.Id));
        var restarted = new CodexAccountManager(scenario.Store, scenario.Data.Root,
            profile => scenario.Data.Service(profile, scenario.ImportedFactory), () => AccountTestDirectory.Executable);
        Assert.Equal("codex-identity-conflict", restarted.Accounts.Single().Snapshot.TechnicalDetail);
        await restarted.RefreshManuallyAsync(CancellationToken.None);
        Assert.Empty(restarted.Accounts.Single().Snapshot.Windows);
        Assert.Equal("codex-identity-conflict", restarted.Snapshot.TechnicalDetail);
    }

    private static IReadOnlyList<string> Respond(string line, string email, bool quotaFailure)
    {
        var method = JsonNode.Parse(line)?["method"]?.ToString();
        if (method == "account/read")
            return [AccountTestProtocol.Account(email)];
        if (method == "account/rateLimits/read" && quotaFailure)
            return ["""{"id":3,"error":{"code":-1}}"""];
        if (method == "account/login/start")
            return AccountTestProtocol.Standard(line).Append(AccountTestProtocol.Completed).ToArray();
        return AccountTestProtocol.Standard(line);
    }

    private sealed class RecoveryScenario : IDisposable
    {
        public AccountTestDirectory Data { get; }
        public CodexAccountStore Store { get; }
        public CodexAccountProfile Imported { get; }
        public CodexAccountProfile Managed { get; }
        public ScriptedCodexProcessFactory ImportedFactory { get; }
        public ScriptedCodexProcessFactory ManagedFactory { get; }
        public ScriptedCodexProcessFactory CandidateFactory { get; }
        public CodexAccountManager Manager { get; }

        public RecoveryScenario(string managedEmail, bool candidateQuotaFailure = false,
            TimeSpan candidateDelay = default)
        {
            Data = new AccountTestDirectory();
            Store = new CodexAccountStore(Data.Root);
            var initial = Store.LoadOrMigrate(Data.Home("imported"));
            Imported = initial.Profiles.Single() with { Label = "Imported" };
            Managed = Store.NewManaged("Managed");
            Store.Save(initial with
            {
                Profiles = [Imported, Managed],
                SelectedId = Imported.Id
            });

            ImportedFactory = new ScriptedCodexProcessFactory
            {
                Responder = line => Respond(line, "main@example.invalid", quotaFailure: false)
            };
            ManagedFactory = new ScriptedCodexProcessFactory
            {
                Responder = line => Respond(line, managedEmail, quotaFailure: false)
            };
            CandidateFactory = new ScriptedCodexProcessFactory
            {
                Responder = line => Respond(line, "main@example.invalid", candidateQuotaFailure),
                ResponseDelay = candidateDelay
            };

            Manager = new CodexAccountManager(
                Store,
                Data.Root,
                profile => profile.Id == Imported.Id
                    ? Data.Service(profile, ImportedFactory)
                    : profile.Id == Managed.Id
                        ? Data.Service(profile, ManagedFactory)
                        : Data.Service(profile, CandidateFactory),
                () => AccountTestDirectory.Executable);
        }

        public string ReadRegistry() =>
            File.ReadAllText(Path.Combine(Data.Root, "codex-accounts.json"));

        public byte[] ReadImportedCache() =>
            File.ReadAllBytes(Store.SnapshotPath(Imported));

        public byte[] ReadImportedBinding() =>
            File.ReadAllBytes(Store.SnapshotPath(Imported) + ".identity.json");

        public void Dispose() => Data.Dispose();
    }
}