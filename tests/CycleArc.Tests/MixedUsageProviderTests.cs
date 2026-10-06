using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class MixedUsageProviderTests
{
    [Theory]
    [InlineData("cancel", false)]
    [InlineData("exception", false)]
    [InlineData("binding", true)]
    [InlineData("cache", true)]
    [InlineData("unreadable", true)]
    public async Task NewClaudeFlowDiscardsOnlyItsOwnEmptyDraft(string outcome, bool kept)
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("codex"),
            [new CodexUsageProvider(p => data.Service(p, new ScriptedCodexProcessFactory()), () => AccountTestDirectory.Executable),
             new ClaudeUsageProvider(store)]));
        var existing = manager.AddClaude("Previously registered");
        manager.Select(existing.Id);
        string? draftId = null;
        void Configure() => manager.ConfigureNewClaude("Draft", profile =>
        {
            draftId = profile.Id;
            if (outcome == "exception") throw new InvalidOperationException("Synthetic failure");
            if (outcome == "binding") new ClaudeConnectionStore(store, profile.Id).Save(new(1, profile.Id,
                data.Home("claude"), Path.Combine(data.Root, "claude.cmd"), false, new string('A', 64), DateTimeOffset.UtcNow));
            if (outcome == "cache") new ClaudeStatusLineStore(store.ClaudeStatusLinePath(profile.Id), profile.Id)
                .RecordAsync(new(ClaudeInputStatus.Missing), DateTimeOffset.UtcNow, default).GetAwaiter().GetResult();
            if (outcome == "unreadable")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(store.ClaudeConnectionPath(profile.Id))!);
                File.WriteAllText(store.ClaudeConnectionPath(profile.Id), "{broken");
            }
        });
        if (outcome == "exception") Assert.Throws<InvalidOperationException>(Configure); else Configure();
        Assert.Equal(kept, manager.Accounts.Any(account => account.Profile.Id == draftId));
        Assert.Equal(kept, store.ContainsClaude(draftId!));
        Assert.Contains(manager.Accounts, account => account.Profile == existing);
        Assert.Equal(existing.Id, manager.SelectedId);
        Assert.Equal(kept ? 3 : 2, manager.Accounts.Count);
    }

    [Fact]
    public async Task MixedProfilesKeepIndependentValuesAliasesOrderAndSelectedProviderAcrossRestart()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var requested = 0;
        var clock = new MutableClock(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        IUsageProvider[] Providers() => [new CodexUsageProvider(p => data.Service(p,
                new ScriptedCodexProcessFactory { Responder = line => { requested++; return AccountTestProtocol.Standard(line); } }),
            () => AccountTestDirectory.Executable), new ClaudeUsageProvider(store, clock)];
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("codex"), Providers()));
        var first = manager.AddClaude("  개인\n계정  ");
        var second = manager.AddClaude("Work");
        Assert.Equal("개인계정", first.Label);
        Assert.Equal(0, requested); // Adding/renaming/passive intake never starts Codex.
        Assert.Equal("", first.HomePath);
        Assert.False(first.IsManaged);
        await Receive(first, 12);
        await Receive(second, 86);
        await manager.RefreshPassiveAsync(CancellationToken.None);
        Assert.Equal(0, requested);
        Assert.Equal(12, manager.Accounts[1].Snapshot.Windows[0].UsedPercent);
        Assert.Equal(86, manager.Accounts[2].Snapshot.Windows[0].UsedPercent);
        Assert.All(manager.Accounts.Skip(1), account => Assert.Equal(UsageProviderId.Claude, account.Snapshot.Provider));
        await manager.RefreshManuallyAsync(CancellationToken.None);
        Assert.True(requested > 0);
        Assert.Equal(CodexQuotaStatus.Available, manager.Accounts[0].Snapshot.Status);
        Assert.Equal(UsageProviderId.Codex, manager.Accounts[0].Snapshot.Provider);
        Assert.False(manager.Accounts[1].HasMatchingIdentity);
        manager.Rename(first.Id, "");
        Assert.StartsWith("Claude · ", manager.Accounts[1].DisplayName);
        manager.Rename(second.Id, new string('X', 100));
        Assert.Equal(80, manager.Accounts[2].DisplayName.Length);
        manager.Select(second.Id);
        Assert.True(manager.Move(second.Id, -1));
        Assert.Equal(second.Id, manager.SelectedId);
        var restarted = data.TrackManager(new CodexAccountManager(store, data.Root, Providers()));
        Assert.Equal(second.Id, restarted.SelectedId);
        Assert.Equal(new[] { "default", second.Id, first.Id }, restarted.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(86, restarted.Snapshot.Windows[0].UsedPercent);
        Assert.EndsWith("codex-snapshot.json", store.SnapshotPath(restarted.Accounts[0].Profile));
        Assert.Equal(2, store.LoadOrMigrate(data.Root).Version);

        async Task Receive(CodexAccountProfile profile, double used)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(ClaudeStatusLineTests.Payload(used)));
            var exit = await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, profile.Id], input,
                new StringWriter(), store, clock);
            Assert.Equal(0, exit);
        }
    }

    [Fact]
    public async Task ClaudeNeverUsesCodexLoginCreditOrDiscoveryAndRemovalDisablesCollector()
    {
        await using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var codex = new CodexUsageProvider(p => data.Service(p,
            new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard }), () => AccountTestDirectory.Executable);
        var manager = data.TrackManager(new CodexAccountManager(store, data.Home("codex"), [codex, new ClaudeUsageProvider(store)]));
        var claude = manager.AddClaude("Work");
        var browserOpened = false;
        var result = await manager.LoginAsync(claude.Id, "", (_, _) => { browserOpened = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Unavailable, result.Status);
        Assert.False(browserOpened);
        Assert.Equal(CreditRedemptionOutcome.Unavailable, await manager.ConsumeCreditAsync(claude.Id, "synthetic-credit", CancellationToken.None));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(ClaudeStatusLineTests.Payload()));
        await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, claude.Id], input, new StringWriter(), store);
        var cache = File.ReadAllBytes(store.ClaudeStatusLinePath(claude.Id));
        Assert.True(manager.Remove(claude.Id));
        Assert.DoesNotContain("", store.LoadOrMigrate(data.Root).IgnoredHomes);
        input.Position = 0;
        Assert.Equal(2, await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, claude.Id], input, new StringWriter(), store));
        Assert.Equal(cache, File.ReadAllBytes(store.ClaudeStatusLinePath(claude.Id)));
    }

    [Fact]
    public void LegacyRegistryDefaultsToCodexAndRejectsAmbiguousProviderOrIdentityPaths()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var original = store.LoadOrMigrate(data.Home("codex"));
        var path = Path.Combine(data.Root, "codex-accounts.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["Profiles"]![0]!.AsObject().Remove("Provider");
        File.WriteAllText(path, root.ToJsonString());
        Assert.Equal(UsageProviderId.Codex, store.LoadOrMigrate(data.Root).Profiles[0].Provider);
        var claude = store.NewClaude("Personal");
        Assert.Throws<InvalidDataException>(() => store.Save(original with { Profiles = [original.Profiles[0], claude] }));
        Assert.Throws<InvalidDataException>(() => store.Save(original with { Version = 2,
            Profiles = [original.Profiles[0], claude with { HomePath = data.Home("claude-auth") }] }));
        Assert.Throws<InvalidDataException>(() => store.Save(original with { Version = 2,
            Profiles = [original.Profiles[0], claude with { Provider = (UsageProviderId)42 }] }));
        Assert.Equal(original.Profiles[0].HomePath, store.LoadOrMigrate(data.Root).Profiles[0].HomePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaudeMigrationUpgradesBackupTooSoOldBuildCannotSilentlyRestoreCodexOnlyState(bool corruptPrimary)
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var original = store.LoadOrMigrate(data.Home("existing-codex"));
        store.Save(original); // Existing version-1 primary and backup.
        var path = Path.Combine(data.Root, "codex-accounts.json");
        if (corruptPrimary) File.WriteAllText(path, "{broken");
        var recovered = store.LoadOrMigrate(data.Root);
        var claude = store.NewClaude("Personal Claude");
        store.Save(recovered with { Version = 2, Profiles = recovered.Profiles.Append(claude).ToArray() });
        var primary = JsonSerializer.Deserialize<CodexAccountConfiguration>(File.ReadAllText(path))!;
        var backup = JsonSerializer.Deserialize<CodexAccountConfiguration>(File.ReadAllText(path + ".bak"))!;
        Assert.Equal(2, primary.Version);
        Assert.Equal(2, backup.Version);
        Assert.Equal(original.Profiles, backup.Profiles);
        Assert.Contains(primary.Profiles, p => p.Id == claude.Id && p.Provider == UsageProviderId.Claude);
        Assert.Throws<InvalidDataException>(() => store.Save(original));
        Assert.Contains(store.LoadOrMigrate(data.Root).Profiles, p => p.Id == claude.Id);
        File.WriteAllText(path, "{broken");
        var fallback = store.LoadOrMigrate(data.Root);
        Assert.True(store.RecoveredFromBackup);
        Assert.Equal(2, fallback.Version);
        Assert.Equal(original.Profiles, fallback.Profiles);
    }
}
