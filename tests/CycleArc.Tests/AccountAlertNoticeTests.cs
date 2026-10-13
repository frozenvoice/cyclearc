using CycleArc.Monitoring;

namespace CycleArc.Tests;

public sealed class AccountAlertNoticeTests
{
    [Fact]
    public void SimultaneousPoliciesKeepBothAccountsAndOneStableClickTarget()
    {
        var notice = AccountAlertNotice.Combine(("Account A usage", "90% used", "a"),
            ("Account B credits", "2 credits left", "b"))!.Value;
        Assert.Contains("Account A usage: 90% used", notice.Body);
        Assert.Contains("Account B credits: 2 credits left", notice.Body);
        Assert.Equal("a", notice.ProfileId);
        Assert.Null(AccountAlertNotice.Combine(null, null));
        Assert.Equal(("Credits", "Low", "b"), AccountAlertNotice.Combine(null, ("Credits", "Low", "b")));
    }

    [Fact]
    public void LongNamesAndBodiesKeepBothPoliciesInsideTheWindowsBalloonLimit()
    {
        var notice = AccountAlertNotice.Combine(("Account A usage " + new string('A', 100), "90% used " + new string('a', 255), "a"),
            ("Account B credits " + new string('B', 100), "2 credits left " + new string('b', 255), "b"))!.Value;
        Assert.True(notice.Body.Length <= 255);
        Assert.True(notice.Title.Length <= 63);
        Assert.Contains("Account A usage", notice.Body);
        Assert.Contains("90% used", notice.Body);
        Assert.Contains("Account B credits", notice.Body);
        Assert.Contains("2 credits left", notice.Body);
        Assert.Equal("a", notice.ProfileId);
    }
}
