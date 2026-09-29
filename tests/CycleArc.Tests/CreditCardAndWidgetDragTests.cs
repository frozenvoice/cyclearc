using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CreditCardAndWidgetDragTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Drag_UsesScreenDeltaWithoutFeedingMovedWindowPositionBack()
    {
        var drag = new WidgetDragSession(40, 40, 100, 100);
        Assert.Equal((40d, 40d), drag.Move(102, 102));
        Assert.False(drag.IsDragging);
        Assert.Equal((60d, 70d), drag.Move(120, 130));
        Assert.Equal((100d, 90d), drag.Move(160, 150));
        Assert.True(drag.IsDragging);
    }

    [Fact]
    public void Drag_AcrossNegativeMonitorCoordinatesAndReturningToOriginIsStillADrag()
    {
        var drag = new WidgetDragSession(-300, 60, -200, 100);
        Assert.Equal((-400d, 10d), drag.Move(-300, 50));
        Assert.Equal((-300d, 60d), drag.Move(-200, 100));
        Assert.True(drag.IsDragging);
    }

    [Fact]
    public void TinyMotion_RemainsClick()
    {
        var drag = new WidgetDragSession(50, 80, 120, 130);
        Assert.Equal((50d, 80d), drag.Move(121, 133));
        Assert.False(drag.IsDragging);
    }

    [Fact]
    public void CreditCard_ShowsEachCreditTimeWithoutRedundantCount()
    {
        var early = Now.AddDays(1);
        var late = early.AddHours(1);
        var card = CodexCreditCard.From(Snapshot(3, [late, early, early]), Now);
        Assert.Equal(3, card.Rows.Count);
        Assert.Equal(UiText.T("3", "3개"), card.CountText);
        var date = CodexDeadlineFormatting.DateStamp(early, Now);
        var earlyTime = early.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var lateTime = late.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(UiText.T($"Expires {date} {earlyTime}", $"{date} {earlyTime} 만료"), card.Rows[0].Text);
        Assert.Equal(card.Rows[0], card.Rows[1]);
        Assert.Equal(UiText.T($"Expires {date} {lateTime}", $"{date} {lateTime} 만료"), card.Rows[2].Text);
        Assert.All(card.Rows, row => Assert.DoesNotContain("·", row.Text));
        Assert.Null(card.Notice);
    }
    [Fact]
    public void CreditCard_PartialExpiryAndStaleDataRemainExplicit()
    {
        var card = CodexCreditCard.From(Snapshot(3, [Now.AddDays(1), null]) with { Status = CodexQuotaStatus.Stale }, Now);
        Assert.Single(card.Rows);
        Assert.Contains(UiText.T("Some expiries unavailable", "일부 만료일 미제공"), card.Notice);
        Assert.Contains(UiText.CodexDataStale, card.Notice);
    }

    [Fact]
    public void CreditCard_ZeroDoesNotShowRetainedExpiredDates()
    {
        var card = CodexCreditCard.From(Snapshot(0, [Now.AddDays(-1)]), Now);
        Assert.Empty(card.Rows);
        Assert.NotNull(card.Notice);
    }

    [Theory]
    [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.CodexNotFound)]
    public void CreditCard_IdentityFailureDoesNotExposeCachedCreditsAsAvailable(CodexQuotaStatus status)
    {
        var card = CodexCreditCard.From(Snapshot(3, [Now.AddDays(1)]) with { Status = status }, Now);
        Assert.Equal("—", card.CountText);
        Assert.Empty(card.Rows);
    }

    [Fact]
    public void CreditCard_UnknownIsNotZero()
    {
        var card = CodexCreditCard.From(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable), Now);
        Assert.Equal("—", card.CountText);
        Assert.Empty(card.Rows);
        Assert.NotNull(card.Notice);
    }

    [Fact]
    public void CreditCard_ExpiredTimestampRequestsRefresh()
    {
        var card = CodexCreditCard.From(Snapshot(1, [Now.AddMinutes(-1)]), Now);
        Assert.Single(card.Rows);
        Assert.NotNull(card.Notice);
    }

    [Fact]
    public void UsageRows_CanExcludeCreditsWithoutLosingWindowOrCheckTime()
    {
        var snapshot = Snapshot(3, []) with
        {
            LastSuccessfulRefresh = Now,
            Windows = [new("codex", 15, 10080, Now.AddDays(7), CodexWindowKind.Weekly)]
        };
        var rows = CodexDisplayFormatting.Rows(snapshot, Now, includeResetCredits: false);
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, row => row.Label == UiText.ResetCredits);
        Assert.Equal(UiText.LastChecked, rows[^1].Label);
    }

    private static CodexQuotaSnapshot Snapshot(int count, IReadOnlyList<DateTimeOffset?> dates) =>
        CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with { ResetCreditsAvailable = count, ResetCreditExpirations = dates };
}
