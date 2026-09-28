using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class DetailSectionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    public DetailSectionsTests() => UiText.SetLanguage(UiLanguage.English);

    [Theory]
    [InlineData(UsagePeriodPreference.FiveHour, 0)]
    [InlineData(UsagePeriodPreference.Weekly, 1)]
    public void RingLimitSitsBesideTheRingAndEveryOtherRowStaysInOrderBelow(UsagePeriodPreference period, int ringIndex)
    {
        var snapshot = Snapshot(UsageProviderId.Claude,
            new CodexQuotaWindow("five_hour", 31, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(2), CodexWindowKind.FiveHour),
            new("seven_day", 17, CodexWindowClassifier.WeeklyMinutes, Now.AddDays(3), CodexWindowKind.Weekly));
        var ring = CodexRingPresentation.FromDetail(snapshot, period);
        var all = CodexDisplayFormatting.Rows(snapshot, Now, includeResetCredits: false);

        var (primary, secondary, start) = CodexDisplayFormatting.DetailSections(snapshot, ring.Window, Now);

        Assert.Same(snapshot.Windows[ringIndex], ring.Window);
        Assert.Equal(ringIndex * 2, start);
        Assert.Equal(all.Skip(start).Take(2), primary);
        // Nothing is dropped, and putting the ring rows back at their start restores the source order.
        Assert.Equal(all, secondary.Take(start).Concat(primary).Concat(secondary.Skip(start)));
        Assert.Equal(all.Last(), secondary.Last());
    }

    [Fact]
    public void CursorKeepsOneRowPerLimitWithTheRingLimitBesideTheRing()
    {
        var snapshot = Snapshot(UsageProviderId.Cursor,
            new CodexQuotaWindow("cursor-auto", 76.91, null, Now.AddDays(10), CodexWindowKind.Other) { IsEnabled = true },
            new("cursor-api", 0, null, Now.AddDays(10), CodexWindowKind.Other) { IsEnabled = true },
            new("cursor-on-demand", null, null, Now.AddDays(10), CodexWindowKind.Other) { IsEnabled = false });
        var ring = CodexRingPresentation.FromDetail(snapshot);
        var all = CodexDisplayFormatting.Rows(snapshot, Now, includeResetCredits: false);

        var (primary, secondary, start) = CodexDisplayFormatting.DetailSections(snapshot, ring.Window, Now);

        Assert.Equal(0, start);
        Assert.Equal(all[0], Assert.Single(primary));
        Assert.Equal(all.Skip(1), secondary);
    }

    [Fact]
    public void WithoutARingLimitEveryRowStaysBesideTheRing()
    {
        var snapshot = Snapshot(UsageProviderId.Codex,
            new CodexQuotaWindow("five", 12, CodexWindowClassifier.FiveHourMinutes, Now.AddHours(1), CodexWindowKind.FiveHour));
        var all = CodexDisplayFormatting.Rows(snapshot, Now, includeResetCredits: false);

        var (primary, secondary, start) = CodexDisplayFormatting.DetailSections(snapshot, null, Now);

        Assert.Equal(all, primary);
        Assert.Empty(secondary);
        Assert.Equal(0, start);
    }

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, params CodexQuotaWindow[] windows) =>
        new(CodexQuotaStatus.Available, "pro", Now.AddMinutes(-1), Now.AddMinutes(-1), null, null, null, windows, null)
        {
            Provider = provider
        };
}
