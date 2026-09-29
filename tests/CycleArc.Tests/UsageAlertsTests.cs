using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public class UsageAlertsTests
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 3, 0, 39, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(84.99, UsageAlertLevel.None)]
    [InlineData(85, UsageAlertLevel.NearLimit)]
    [InlineData(99.6, UsageAlertLevel.NearLimit)]
    [InlineData(100, UsageAlertLevel.LimitReached)]
    public void LevelsFollowTheRingBandsWithoutRounding(double used, UsageAlertLevel expected) =>
        Assert.Equal(expected, UsageAlerts.LevelFor(used));

    [Fact]
    public void EachLevelAlertsOncePerPeriodAcrossRefreshesAndRestarts()
    {
        var (first, marks) = UsageAlerts.Evaluate([Account("work", 86, Reset)], null);
        Assert.Equal(UsageAlertLevel.NearLimit, Assert.Single(first).Level);

        var (again, marks2) = UsageAlerts.Evaluate([Account("work", 90, Reset.AddSeconds(40))], marks);
        Assert.Empty(again);

        var (reached, marks3) = UsageAlerts.Evaluate([Account("work", 100, Reset)], marks2);
        Assert.Equal(UsageAlertLevel.LimitReached, Assert.Single(reached).Level);

        var (restarted, _) = UsageAlerts.Evaluate([Account("work", 100, Reset)], new Dictionary<string, string>(marks3));
        Assert.Empty(restarted);
    }

    [Fact]
    public void ANewPeriodAlertsAgain()
    {
        var (_, marks) = UsageAlerts.Evaluate([Account("work", 100, Reset)], null);
        var (next, _) = UsageAlerts.Evaluate([Account("work", 88, Reset.AddDays(7))], marks);
        Assert.Equal(UsageAlertLevel.NearLimit, Assert.Single(next).Level);
    }

    [Fact]
    public void UsageThatFallsWithoutAResetTimeRearmsTheAlert()
    {
        var (_, marks) = UsageAlerts.Evaluate([Account("claude", 92, null)], null);
        var (_, lowered) = UsageAlerts.Evaluate([Account("claude", 10, null)], marks);
        var (rise, _) = UsageAlerts.Evaluate([Account("claude", 87, null)], lowered);
        Assert.Equal(UsageAlertLevel.NearLimit, Assert.Single(rise).Level);
    }

    [Fact]
    public void StaleUnknownAndHiddenValuesNeverAlertAndKeepTheirMarks()
    {
        var (_, marks) = UsageAlerts.Evaluate([Account("work", 95, Reset)], null);
        var stale = Account("work", 99, Reset) with
        {
            Snapshot = Account("work", 99, Reset).Snapshot with { Status = CodexQuotaStatus.Stale }
        };
        var (none, kept) = UsageAlerts.Evaluate([stale, Account("unknown", null, Reset)], marks);
        Assert.Empty(none);
        Assert.Equal(marks, kept);

        var (recovered, _) = UsageAlerts.Evaluate([Account("work", 95, Reset)], kept);
        Assert.Empty(recovered);
    }

    [Fact]
    public void AccountsAreNeverCombinedAndRemovedAccountsForgetTheirMarks()
    {
        var (alerts, marks) = UsageAlerts.Evaluate([Account("a", 90, Reset), Account("b", 100, Reset)], null);
        Assert.Equal(["a", "b"], alerts.Select(alert => alert.ProfileId).Order());
        var (_, remaining) = UsageAlerts.Evaluate([Account("a", 90, Reset)], marks);
        Assert.All(remaining.Keys, key => Assert.StartsWith("a|", key));
    }

    [Fact]
    public void WindowsSharingALimitIdKeepSeparateMarks()
    {
        var account = Account("work", 90, Reset) with
        {
            Snapshot = Account("work", 90, Reset).Snapshot with
            {
                Windows = [new("codex", 90, 300, Reset.AddHours(-70), CodexWindowKind.FiveHour),
                    new("codex", 88, 10080, Reset, CodexWindowKind.Weekly)]
            }
        };
        var (first, marks) = UsageAlerts.Evaluate([account], null);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, marks.Count);
        var (again, _) = UsageAlerts.Evaluate([account], marks);
        Assert.Empty(again);
    }

    [Fact]
    public void TheMessageNamesTheAccountAndLimit()
    {
        var alert = Assert.Single(UsageAlerts.Evaluate([Account("work", 100, Reset)], null).Alerts);
        Assert.Contains("work", alert.Title);
        Assert.Contains(CodexDisplayFormatting.DurationLabel(10080), alert.Title);
    }

    private static CodexAccountView Account(string id, double? used, DateTimeOffset? reset) =>
        new(new CodexAccountProfile(id, "", id) { Provider = UsageProviderId.Codex },
            CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
            {
                Provider = UsageProviderId.Codex,
                LastSuccessfulRefresh = DateTimeOffset.UtcNow,
                Windows = [new("weekly", used, 10080, reset, CodexWindowKind.Weekly)]
            }) { IsConnected = true };
}
