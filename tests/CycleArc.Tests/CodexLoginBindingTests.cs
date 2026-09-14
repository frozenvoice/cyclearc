using System.Text.Json.Nodes;
using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexLoginBindingTests
{
    [Fact]
    public async Task ExplicitLoginRebindsButFailedNewQuotaNeverRestoresPreviousAccountsCache()
    {
        using var fixture = new LoginFixture();
        await fixture.Refresh();
        var saved = File.ReadAllText(fixture.CachePath);
        var result = await fixture.Service.LoginAsync(AccountTestDirectory.Executable, (_, _) =>
        {
            fixture.Email = "second@example.invalid";
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        Assert.Equal(fixture.Identity.StableAccountFingerprint, fixture.Binding.Read().State!.AccountFingerprint);
        fixture.FailQuota = true;
        var failed = await fixture.Refresh();
        Assert.False(failed.Snapshot.HasUsablePercentages);
        Assert.Empty(failed.Snapshot.RedeemableCredits);
        Assert.Equal(saved, File.ReadAllText(fixture.CachePath));
        fixture.FailQuota = false;
        fixture.Used = 73;
        var ready = await fixture.Refresh();
        Assert.Equal(CodexQuotaStatus.Available, ready.Snapshot.Status);
        Assert.Equal(73, ready.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(fixture.Identity.StableAccountFingerprint, ready.Snapshot.IdentityFingerprint);
    }

    [Fact]
    public async Task CancelledLoginKeepsBindingAndLastGoodCache()
    {
        using var fixture = new LoginFixture();
        await fixture.Refresh();
        var saved = File.ReadAllText(fixture.CachePath);
        var binding = File.ReadAllText(fixture.Binding.PathName);
        using var cancellation = new CancellationTokenSource();
        var result = await fixture.Service.LoginAsync(AccountTestDirectory.Executable, (_, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        Assert.Equal(CodexQuotaStatus.Cancelled, result.Status);
        Assert.Equal(saved, File.ReadAllText(fixture.CachePath));
        Assert.Equal(binding, File.ReadAllText(fixture.Binding.PathName));
        Assert.False(fixture.Service.IsSigningIn);
        Assert.Equal(CodexQuotaStatus.Available, (await fixture.Refresh()).Snapshot.Status);
    }

    [Fact]
    public async Task SignedOutThenAnotherLoginCannotReplaceBoundAccount()
    {
        using var fixture = new LoginFixture();
        await fixture.Refresh();
        var saved = File.ReadAllText(fixture.CachePath);
        var binding = File.ReadAllText(fixture.Binding.PathName);
        fixture.SignedOut = true;
        var signedOut = await fixture.Refresh();
        Assert.Equal(CodexQuotaStatus.SignedOut, signedOut.Snapshot.Status);
        Assert.Empty(signedOut.Snapshot.Windows);
        Assert.Equal(saved, File.ReadAllText(fixture.CachePath));
        Assert.Equal(binding, File.ReadAllText(fixture.Binding.PathName));
        fixture.SignedOut = false;
        fixture.Email = "foreign@example.invalid";
        var foreign = await fixture.Refresh();
        Assert.Equal("codex-identity-mismatch", foreign.Snapshot.TechnicalDetail);
        Assert.Empty(foreign.Snapshot.Windows);
        fixture.FailInitialize = true;
        Assert.Empty((await fixture.Refresh()).Snapshot.Windows);
        Assert.Equal(saved, File.ReadAllText(fixture.CachePath));
        Assert.Equal(binding, File.ReadAllText(fixture.Binding.PathName));
    }

    [Fact]
    public async Task UnrelatedCacheCannotBecomeStaleQuotaAfterVerifiedIdentity()
    {
        using var fixture = new LoginFixture();
        await fixture.Refresh();
        var foreign = fixture.Service.Snapshot with
        {
            IdentityFingerprint = new CodexAccountIdentity(CodexQuotaStatus.Available, "foreign@example.invalid", "pro").StableAccountFingerprint
        };
        new CodexSnapshotStore(fixture.CachePath).Save(foreign);
        fixture.Service = fixture.Data.Service(fixture.Profile, fixture.Factory);
        fixture.FailQuota = true;
        var failed = await fixture.Refresh();
        Assert.Empty(failed.Snapshot.Windows);
        Assert.Null(failed.Snapshot.ResetCreditsAvailable);
    }

    private sealed class LoginFixture : IDisposable
    {
        public AccountTestDirectory Data { get; } = new();
        public CodexAccountProfile Profile { get; }
        public ScriptedCodexProcessFactory Factory { get; } = new();
        public CodexQuotaService Service { get; set; }
        public string Email { get; set; } = "first@example.invalid";
        public bool SignedOut { get; set; }
        public bool FailQuota { get; set; }
        public bool FailInitialize { get; set; }
        public int Used { get; set; } = 25;
        public string CachePath { get; }
        public CodexIdentityBindingStore Binding { get; }
        public CodexAccountIdentity Identity => new(CodexQuotaStatus.Available, Email, "pro");

        public LoginFixture()
        {
            var store = new CodexAccountStore(Data.Root);
            Profile = store.NewManaged("Main");
            CachePath = store.SnapshotPath(Profile);
            Binding = new CodexIdentityBindingStore(CachePath, Profile.Id);
            Factory.Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                if (method == "initialize" && FailInitialize) return ["{\"id\":1,\"error\":{\"code\":-1}}"];
                if (method == "account/read") return SignedOut
                    ? ["{\"id\":2,\"result\":{\"account\":null,\"requiresOpenaiAuth\":true}}"]
                    : [AccountTestProtocol.Account(Email)];
                if (method == "account/login/start")
                    return AccountTestProtocol.Standard(line).Append(AccountTestProtocol.Completed).ToArray();
                if (method == "account/rateLimits/read")
                {
                    if (FailQuota) return ["{\"id\":3,\"error\":{\"code\":-1}}"];
                    var node = JsonNode.Parse(AccountTestProtocol.Standard(line).Single())!;
                    node["result"]!["rateLimits"]!["primary"]!["usedPercent"] = Used;
                    return [node.ToJsonString()];
                }
                return AccountTestProtocol.Standard(line);
            };
            Service = Data.Service(Profile, Factory);
        }
        public Task<CodexRefreshResult> Refresh() => Service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        public void Dispose() => Data.Dispose();
    }
}
