using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public class DisplayedAccountIdentitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuotaTimeOrderAndNewAccountChangesWaitForTheNextShow()
    {
        var identities = new DisplayedAccountIdentities();
        var shown = new[] { Account("a", 20), Account("b", 40) };
        identities.Record(shown);

        Assert.False(identities.Withdrawn(shown));
        Assert.False(identities.Withdrawn([Account("b", 90), Account("a", 99) with
        {
            Snapshot = Account("a", 99).Snapshot with { LastSuccessfulRefresh = Now.AddMinutes(5), Status = CodexQuotaStatus.Stale }
        }, Account("c", 10)]));
        Assert.False(identities.Withdrawn([Account("a", 20) with { Profile = Account("a", 20).Profile with { Label = "Renamed" } },
            Account("b", 40)]));
    }

    [Fact]
    public void RemovedAccountOrAnotherLoginEmailIsWithdrawnAtOnce()
    {
        var identities = new DisplayedAccountIdentities();
        identities.Record([Account("a", 20), Account("b", 40)]);

        Assert.True(identities.Withdrawn([Account("a", 20)]));
        Assert.True(identities.Withdrawn([Account("a", 20), Account("b", 40) with { Email = "other@example.invalid" }]));
        Assert.True(identities.Withdrawn([Account("a", 20), Account("b", 40) with { Email = null }]));
        Assert.True(identities.Withdrawn([]));
    }

    [Theory]
    [InlineData("codex-identity-mismatch")]
    [InlineData("codex-identity-conflict")]
    [InlineData("claude-live-identity-mismatch")]
    [InlineData("cursor-live-identity-mismatch")]
    public void NewlyProtectedQuotaIsWithdrawnButAlreadyProtectedQuotaIsNot(string detail)
    {
        var identities = new DisplayedAccountIdentities();
        identities.Record([Account("a", 20)]);
        var protectedAccount = Account("a", 20) with { Snapshot = Account("a", 20).Snapshot with { TechnicalDetail = detail } };

        Assert.True(DisplayedAccountIdentities.IsProtected(protectedAccount.Snapshot));
        Assert.True(identities.Withdrawn([protectedAccount]));
        identities.Record([protectedAccount]);
        Assert.False(identities.Withdrawn([protectedAccount]));
        Assert.False(identities.Withdrawn([Account("a", 30)]));
    }

    [Fact]
    public void SignedOutQuotaIsProtected()
    {
        var identities = new DisplayedAccountIdentities();
        identities.Record([Account("a", 20)]);
        Assert.True(identities.Withdrawn([Account("a", 20) with
        {
            Snapshot = Account("a", 20).Snapshot with { Status = CodexQuotaStatus.SignedOut }
        }]));
    }

    [Fact]
    public void OnlyTheLatestRecordedGenerationCounts()
    {
        var identities = new DisplayedAccountIdentities();
        identities.Record([Account("a", 20), Account("b", 40)]);
        identities.Record([Account("a", 20)]);
        Assert.False(identities.Withdrawn([Account("a", 25)]));
    }

    private static CodexAccountView Account(string id, double used) => new(
        new CodexAccountProfile(id, @"C:\synthetic\" + id, "Account " + id),
        new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-2), Now.AddMinutes(-1), null, null, null,
            [new("codex", used, 10080, Now.AddDays(3), CodexWindowKind.Weekly)], null),
        id + "@example.invalid") { IsConnected = true };
}
