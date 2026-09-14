using System.Text.Json.Nodes;
using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexIdentityBindingTests
{
    [Fact]
    public void StableFingerprintIgnoresPlanChangesAndBindingStoresOnlyHashes()
    {
        var first = new CodexAccountIdentity(CodexQuotaStatus.Available, "User@Example.invalid", "pro");
        var migrated = new CodexAccountIdentity(CodexQuotaStatus.Available, " user@example.invalid ", "team");
        Assert.Equal(first.StableAccountFingerprint, migrated.StableAccountFingerprint);
        Assert.NotEqual(first.Fingerprint, migrated.Fingerprint);

        using var data = new AccountTestDirectory();
        var path = new CodexAccountStore(data.Root).SnapshotPath(new("default", data.Home("existing"), ""));
        var bindings = new CodexIdentityBindingStore(path, "default");
        Assert.Equal(CodexIdentityBindingMatch.FirstSeen, bindings.Match(first));
        var document = File.ReadAllText(bindings.PathName);
        Assert.DoesNotContain("@", document);
        Assert.Equal(CodexIdentityBindingMatch.Matched, new CodexIdentityBindingStore(path, "default").Match(migrated));
    }

    [Fact]
    public async Task ForeignIdentityCannotReplaceExistingQuotaOrAppearAfterRestart()
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "Main");
        var email = "first@example.invalid";
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method == "account/read" ? [AccountTestProtocol.Account(email)] : AccountTestProtocol.Standard(line);
            }
        };
        var service = data.Service(profile, factory);
        var first = await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, first.Snapshot.Status);
        var original = File.ReadAllText(new CodexAccountStore(data.Root).SnapshotPath(profile));

        email = "foreign@example.invalid";
        var mismatch = await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Unavailable, mismatch.Snapshot.Status);
        Assert.Equal("codex-identity-mismatch", mismatch.Snapshot.TechnicalDetail);
        Assert.Empty(mismatch.Snapshot.Windows);
        Assert.Null(service.Identity);
        Assert.Equal(original, File.ReadAllText(new CodexAccountStore(data.Root).SnapshotPath(profile)));

        var restarted = data.Service(profile, factory);
        Assert.Equal(CodexQuotaStatus.Unavailable, restarted.Snapshot.Status);
        Assert.Empty(restarted.Snapshot.Windows);
    }

    [Fact]
    public async Task SameIdentityMustValidateBeforeItsCachedQuotaCanBeUsedAfterRestart()
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "Main");
        var failQuota = false;
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                if (method == "account/rateLimits/read" && failQuota)
                    return ["{\"id\":3,\"error\":{\"code\":-1}}"];
                return AccountTestProtocol.Standard(line);
            }
        };
        var service = data.Service(profile, factory);
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        var restarted = data.Service(profile, factory);
        Assert.Empty(restarted.Snapshot.Windows);
        Assert.Equal(CodexQuotaStatus.Unavailable, restarted.Snapshot.Status);

        failQuota = true;
        var result = await restarted.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, result.Snapshot.Status);
        Assert.Equal(25, result.Snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task InitializeFailureWithoutAccountResultKeepsVerifiedCacheAsStale()
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "Main");
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        var service = data.Service(profile, factory);
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);

        factory.Responder = line => JsonNode.Parse(line)?["method"]?.ToString() == "initialize"
            ? ["{malformed"] : [];
        var result = await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);

        Assert.Equal(CodexQuotaStatus.Stale, result.Snapshot.Status);
        Assert.True(result.UsedCache);
        Assert.Equal(25, result.Snapshot.Windows[0].UsedPercent);
        Assert.Equal("initialize-failed", result.Snapshot.TechnicalDetail);
    }
    [Fact]
    public void CorruptBindingBackupFailsClosedEvenWhenPrimaryIsReadable()
    {
        using var data = new AccountTestDirectory();
        var path = new CodexAccountStore(data.Root).SnapshotPath(new("default", data.Home("existing"), ""));
        var identity = new CodexAccountIdentity(CodexQuotaStatus.Available, "first@example.invalid", "pro");
        var bindings = new CodexIdentityBindingStore(path, "default");
        bindings.Reset(identity);
        bindings.Reset(identity);
        File.WriteAllText(bindings.PathName + ".bak", "{invalid");
        var read = new CodexIdentityBindingStore(path, "default").Read();
        Assert.True(read.Unavailable);
        Assert.Null(read.State);

        // An explicit successful login repairs both atomic copies.
        bindings.Reset(identity);
        var repaired = new CodexIdentityBindingStore(path, "default").Read();
        Assert.False(repaired.Unavailable);
        Assert.Equal(identity.StableAccountFingerprint, repaired.State?.AccountFingerprint);
    }
}