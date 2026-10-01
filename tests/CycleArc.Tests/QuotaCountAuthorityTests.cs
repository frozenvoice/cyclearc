using CycleArc.Services;

namespace CycleArc.Tests;

public class QuotaCountAuthorityTests
{
    [Fact]
    public void WeeklyAuthoritative_SolDailyReconstructed_ShowsLowerBoundAndNoSolToast()
    {
        var settings = Pro200();
        EnableToasts(settings);
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [ProEvent("sol", "GPT-5.6 Sol Pro", "gpt-5-6-pro", now.AddHours(-1))],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet
            {
                Gpt6ProWeekly = Window(11, 200, now.AddDays(4), "gpt-6-pro-weekly"),
                SolProDaily = new QuotaWindow { Found = true, Limit = 170, FeatureName = "gpt-5-6-pro-daily" }
            },
            AppSyncStatus.UpToDate);

        Assert.True(snapshot.UsesServerWeeklyCount);
        Assert.True(snapshot.UsesServerCount);
        Assert.False(snapshot.UsesServerSolDailyCount);
        Assert.Equal(11, snapshot.Used);
        Assert.Equal(200, snapshot.Limit);
        Assert.Equal("11 / 200", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("11 / 200", DisplayFormatting.WindowUsage(snapshot.Used, snapshot.Limit, snapshot.UsesServerWeeklyCount));
        Assert.Equal(UiText.ReconstructedCount(1), DisplayFormatting.WindowUsage(snapshot.TodaySolPro, snapshot.SolProDailyLimit, snapshot.UsesServerSolDailyCount));
        Assert.True(ProStatusPresentation.From(snapshot).ExactRemainingAvailable);
        Assert.DoesNotContain(
            QuotaNotificationPolicy.Evaluate(snapshot, settings),
            item => item.Body == UiText.ToastSol10 || item.Body == UiText.ToastSol20 || item.Body == UiText.ToastSolExhausted);
    }

    [Fact]
    public void WeeklyAuthoritative_CombinedDailyReconstructed_ShowsLowerBoundAndNoCombinedToast()
    {
        var settings = Pro200();
        EnableToasts(settings);
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [ProEvent("g6", "GPT-6 Pro", "gpt-6-pro", now.AddHours(-1))],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet
            {
                Gpt6ProWeekly = Window(11, 200, now.AddDays(4), "gpt-6-pro-weekly"),
                CombinedProDaily = new QuotaWindow { Found = true, Limit = 200, FeatureName = "combined_pro_daily" }
            },
            AppSyncStatus.UpToDate);

        Assert.True(snapshot.UsesServerWeeklyCount);
        Assert.False(snapshot.UsesServerCombinedDailyCount);
        Assert.Equal(UiText.ReconstructedCount(1), DisplayFormatting.WindowUsage(snapshot.CombinedToday, snapshot.CombinedDailyLimit, snapshot.UsesServerCombinedDailyCount));
        Assert.DoesNotContain(
            QuotaNotificationPolicy.Evaluate(snapshot, settings),
            item => item.Body == UiText.ToastCombined10 || item.Body == UiText.ToastCombined20 || item.Body == UiText.ToastCombinedExhausted);
    }

    [Fact]
    public void SolDailyAuthoritative_ShowsExactDailyAndAllowsToast()
    {
        var settings = Pro200();
        EnableToasts(settings);
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet
            {
                SolProDaily = Window(90, 100, now.AddHours(8), "gpt-5-6-pro-daily")
            },
            AppSyncStatus.UpToDate);

        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.True(snapshot.UsesServerSolDailyCount);
        Assert.Equal(90, snapshot.TodaySolPro);
        Assert.Equal(100, snapshot.SolProDailyLimit);
        Assert.Equal("90 / 100", DisplayFormatting.WindowUsage(snapshot.TodaySolPro, snapshot.SolProDailyLimit, snapshot.UsesServerSolDailyCount));
        Assert.False(ProStatusPresentation.From(snapshot).ExactRemainingAvailable);
        Assert.Contains(
            QuotaNotificationPolicy.Evaluate(snapshot, settings),
            item => item.Kind == QuotaNotificationKind.Threshold && item.Body == UiText.ToastSol10);
    }

    [Fact]
    public void CombinedDailyAuthoritative_ShowsExactDailyAndAllowsToast()
    {
        var settings = Pro200();
        EnableToasts(settings);
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet
            {
                CombinedProDaily = Window(180, 200, now.AddHours(8), "combined_pro_daily")
            },
            AppSyncStatus.UpToDate);

        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.True(snapshot.UsesServerCombinedDailyCount);
        Assert.Equal("180 / 200", DisplayFormatting.WindowUsage(snapshot.CombinedToday, snapshot.CombinedDailyLimit, snapshot.UsesServerCombinedDailyCount));
        Assert.Contains(
            QuotaNotificationPolicy.Evaluate(snapshot, settings),
            item => item.Kind == QuotaNotificationKind.Threshold && item.Body == UiText.ToastCombined10);
    }

    [Fact]
    public void ReconstructedWeeklyOnly_HasNoExactRemaining()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro100);
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [ProEvent("p", "GPT-5.4 Pro", "gpt-5-4-pro", now.AddHours(-1))],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet
            {
                ProServerStatus = new ProServerStatus { LastConfirmedResetAt = now.AddDays(-1) }
            },
            AppSyncStatus.UpToDate);

        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.False(snapshot.UsesServerSolDailyCount);
        Assert.False(snapshot.UsesServerCombinedDailyCount);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.Equal(UiText.ReconstructedCount(1), DisplayFormatting.UsageLabel(snapshot));
        Assert.False(ProStatusPresentation.From(snapshot).ExactRemainingAvailable);
        Assert.Equal(UiText.ExactRemainingUnavailable, ProStatusPresentation.From(snapshot).ExactRemainingText);
    }

    [Fact]
    public void DesktopDoesNotDisplayRetiredChatQuotaCounters()
    {
        var source = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        Assert.DoesNotContain("UsesServerWeeklyCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProCountText", source, StringComparison.Ordinal);
        // The popup splits CodexDisplayFormatting.Rows(..., includeResetCredits: false) beside and below its ring.
        Assert.Contains("CodexDisplayFormatting.DetailSections(detailSnapshot,", source, StringComparison.Ordinal);
        var formatting = File.ReadAllText(Find("src/CycleArc.Core/Codex/CodexDisplayFormatting.cs"));
        Assert.Contains("var rows = Rows(snapshot, now, includeResetCredits: false);", formatting, StringComparison.Ordinal);
    }

    private static AppSettings Pro200()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro200);
        settings.ResetTimeZoneId = "UTC";
        return settings;
    }

    private static void EnableToasts(AppSettings settings)
    {
        settings.NotifyAt20 = true;
        settings.NotifyAt10 = true;
        settings.NotifyExhausted = true;
        settings.NotifyReset = true;
    }

    private static QuotaWindow Window(int used, int limit, DateTimeOffset reset, string name) => new()
    {
        Found = true,
        Used = used,
        Limit = limit,
        ResetAt = reset,
        FeatureName = name
    };

    private static UsageEvent ProEvent(string id, string display, string raw, DateTimeOffset created) => new()
    {
        Id = id,
        RequestId = id,
        ConversationId = "c-" + id,
        MessageId = id,
        CreatedAt = created,
        NormalizedModel = display,
        RawModel = raw,
        Source = UsageSource.ConversationSync,
        QuotaFamily = QuotaFamily.GptPro
    };

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
