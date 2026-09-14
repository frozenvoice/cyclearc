using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexBindingRepairTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VerifiedLoginRepairsDamagedBindingAndRejectsTheOldAccount(bool primary, bool backup)
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "Main");
        var store = new CodexIdentityBindingStore(new CodexAccountStore(data.Root).SnapshotPath(profile), profile.Id);
        var original = new CodexAccountIdentity(CodexQuotaStatus.Available, "original@example.invalid", "pro");
        var verified = new CodexAccountIdentity(CodexQuotaStatus.Available, "verified@example.invalid", "pro");
        store.Reset(original);
        if (primary) File.WriteAllText(store.PathName, "{invalid");
        if (backup) File.WriteAllText(store.PathName + ".bak", "{invalid");
        Assert.True(store.Read().Unavailable);
        store.Reset(verified);
        var read = store.Read();
        Assert.False(read.Unavailable);
        Assert.Equal(verified.StableAccountFingerprint, read.State!.AccountFingerprint);
        Assert.Equal(CodexIdentityBindingMatch.Matched, store.Match(verified));
        Assert.Equal(CodexIdentityBindingMatch.Mismatch, store.Match(original));
    }
}
