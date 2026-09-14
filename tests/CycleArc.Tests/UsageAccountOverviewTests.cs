using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public class UsageAccountOverviewTests
{

    [Theory]
    [InlineData("codex-identity-mismatch")]
    [InlineData("codex-identity-conflict")]
    [InlineData("codex-identity-binding-unavailable")]
    public void CodexIdentityProblemsKeepTheSelectedProfileWithoutAnotherAccountsQuota(string detail)
    {
        var problem = Account("main", UsageProviderId.Codex, CodexQuotaStatus.Unavailable, null) with
        {
            Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail),
            IsConnected = false
        };
        var other = Account("other", UsageProviderId.Codex, CodexQuotaStatus.Available, 36);
        var overview = UsageAccountOverview.Create([problem, other], problem.Profile.Id);
        Assert.Equal(problem, overview.Selected);
        Assert.Equal(2, overview.Accounts.Count);
        Assert.Empty(overview.Snapshot.Windows);
        Assert.Null(overview.Snapshot.ResetCreditsAvailable);
        Assert.False(CodexRingPresentation.From(overview.Snapshot).IsAvailable);
        Assert.Empty(CodexDisplayFormatting.Rows(overview.Snapshot));
        Assert.DoesNotContain("36%", overview.Tooltip);
        Assert.DoesNotContain("64%", overview.Tooltip);
        Assert.Contains(CodexIdentityPresentation.Label(problem.Snapshot), overview.Tooltip);
        Assert.InRange(overview.Tooltip.Length, 1, 127);
    }

    [Fact]
    public void ConnectedWaitingAccountKeepsItsPlaceAndSelectedProviderWithoutUsage()
    {
        var waiting = Account("connected", UsageProviderId.Claude, CodexQuotaStatus.Unavailable, null) with
        {
            IsConnected = true, Email = "person@example.invalid",
            Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "claude-connected-waiting") with { Provider = UsageProviderId.Claude }
        };
        var known = Account("known", UsageProviderId.Codex, CodexQuotaStatus.Available, 11);
        var overview = UsageAccountOverview.Create([known, waiting], waiting.Profile.Id);
        Assert.Equal(new[] { known, waiting }, overview.Accounts);
        Assert.Equal(waiting, overview.Selected);
        Assert.True(waiting.IsAwaitingUsage);
        Assert.False(overview.Snapshot.HasUsablePercentages);
        Assert.Contains("Claude", overview.Tooltip);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    public void ProfilesWithoutUsageNeverOccupyTheOverview(UsageProviderId provider)
    {
        var pending = Account("pending", provider, CodexQuotaStatus.Unavailable, null);
        var first = Account("first", UsageProviderId.Codex, CodexQuotaStatus.Available, 99);
        var second = Account("second", UsageProviderId.Codex, CodexQuotaStatus.Available, 11);
        var all = new[] { first, pending, second };
        var overview = UsageAccountOverview.Create(all, second.Profile.Id);
        Assert.Equal(new[] { "first", "second" }, overview.Accounts.Select(account => account.Profile.Id));
        Assert.Equal(second, overview.Selected);
        Assert.Equal(3, all.Length); // Management retains the pending profile.
        Assert.DoesNotContain("pending", overview.Tooltip);
    }

    [Theory]
    [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.CodexNotFound)]
    public void ExplicitlyUnavailableConnectionsStayHiddenEvenDuringARetry(CodexQuotaStatus status)
    {
        var account = Account("old", UsageProviderId.Codex, status, 40);
        Assert.Empty(UsageAccountOverview.Create([account], "old").Accounts);
        Assert.Empty(UsageAccountOverview.Create([account with { Snapshot = account.Snapshot.AsRefreshing() }], "old").Accounts);
        Assert.Equal(40, account.Snapshot.Windows[0].UsedPercent); // Original cached record remains intact.
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Available)]
    [InlineData(CodexQuotaStatus.Refreshing)]
    [InlineData(CodexQuotaStatus.Stale)]
    public void ValidZeroUsageAndStaleUpdatesRemainVisible(CodexQuotaStatus status)
    {
        var account = Account("known", UsageProviderId.Claude, status, 0);
        Assert.Equal(account, Assert.Single(UsageAccountOverview.Create([account], "known").Accounts));
    }

    [Fact]
    public void HiddenSelectionFallsBackConsistentlyAndAppearsWhenFirstUsageArrives()
    {
        var known = Account("known", UsageProviderId.Codex, CodexQuotaStatus.Available, 11);
        var pending = Account("pending", UsageProviderId.Claude, CodexQuotaStatus.Unavailable, null);
        var before = UsageAccountOverview.Create([pending, known], pending.Profile.Id);
        Assert.Equal(known, before.Selected);
        Assert.Same(known.Snapshot, before.Snapshot);
        Assert.DoesNotContain("Claude", before.Tooltip);
        var received = pending with { Snapshot = Account("pending", UsageProviderId.Claude, CodexQuotaStatus.Available, 23.5).Snapshot };
        var after = UsageAccountOverview.Create([received, known], pending.Profile.Id);
        Assert.Equal(new[] { "pending", "known" }, after.Accounts.Select(account => account.Profile.Id));
        Assert.Same(received.Snapshot, after.Snapshot);
        Assert.Equal(received, after.Selected);
        Assert.Contains("Claude", after.Tooltip);
    }

    [Fact]
    public void EmptyOverviewHasNoSelectedAccountOrInventedProvider()
    {
        var pending = Account("pending", UsageProviderId.Claude, CodexQuotaStatus.Unavailable, null);
        var overview = UsageAccountOverview.Create([pending], pending.Profile.Id);
        Assert.Empty(overview.Accounts); Assert.Null(overview.Selected); Assert.Empty(overview.SelectedId);
        Assert.False(overview.Snapshot.HasUsablePercentages);
        Assert.DoesNotContain("Codex", overview.Tooltip);
        Assert.DoesNotContain("Claude", overview.Tooltip);
    }

    [Fact]
    public async Task PassiveFirstClaudeSampleAddsTheProfileWithoutRemovingItFromManagement()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var provider = new ClaudeUsageProvider(data.Accounts, data.Clock);
        var service = provider.Create(data.Profile);
        CodexAccountView View() => new(data.Profile, service.Snapshot);
        Assert.Empty(UsageAccountOverview.Create([View()], data.Profile.Id).Accounts);
        await data.Receive(ClaudeStatusLineTests.Payload());
        await service.RefreshAsync(default);
        Assert.Single(UsageAccountOverview.Create([View()], data.Profile.Id).Accounts);
        var receivedAt = service.Snapshot.LastSuccessfulRefresh;
        data.Clock.UtcNow = data.Clock.UtcNow.AddHours(8);
        await service.RefreshAsync(default);
        var idle = UsageAccountOverview.Create([View()], data.Profile.Id);
        Assert.Equal(CodexQuotaStatus.Available, idle.Snapshot.Status);
        Assert.Equal(receivedAt, idle.Snapshot.LastSuccessfulRefresh);
        Assert.Single(idle.Accounts);

        await data.Receive("{}");
        await service.RefreshAsync(default);
        var failed = UsageAccountOverview.Create([View()], data.Profile.Id);
        Assert.Equal(CodexQuotaStatus.Stale, failed.Snapshot.Status);
        Assert.Equal(receivedAt, failed.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(23.5, failed.Snapshot.Windows[0].UsedPercent);
        Assert.Single(failed.Accounts);
        Assert.True(data.Accounts.ContainsClaude(data.Profile.Id));
    }

    private static CodexAccountView Account(string id, UsageProviderId provider, CodexQuotaStatus status, double? used) =>
        new(new CodexAccountProfile(id, "", id) { Provider = provider },
            CodexQuotaSnapshot.Empty(status) with { Provider = provider, Windows = used is null ? []
                : [new("quota", used, 10080, DateTimeOffset.UtcNow.AddDays(1), CodexWindowKind.Weekly)] });
}
