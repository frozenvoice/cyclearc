using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Tests;

public class FlyoutCardRedesignTests
{
    [Fact]
    public void NewBadgeAndNoticeStrings_ExistInKoreanAndEnglish()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("History-based estimate", UiText.HistoryBasedEstimateBadge);
            Assert.Equal("Some history is being revalidated.", UiText.PartialRevalidationNotice);
            Assert.Equal("Used", UiText.CodexLegendUsed);
            Assert.Equal("Remaining", UiText.CodexLegendRemaining);
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("기록 기반 추정", UiText.HistoryBasedEstimateBadge);
            Assert.Equal("일부 기록 재검증 중", UiText.PartialRevalidationNotice);
            Assert.Equal("사용", UiText.CodexLegendUsed);
            Assert.Equal("남음", UiText.CodexLegendRemaining);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void CountWithUnit_AddsKoreanCounterSuffixOnlyInKorean()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("5", DisplayFormatting.CountWithUnit(5));
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("5회", DisplayFormatting.CountWithUnit(5));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void HasReliabilityConcern_TrueOnlyWhenUnresolvedOrLegacyEvidenceExists()
    {
        var clean = new QuotaSnapshot { UnresolvedCount = 0, LegacyPendingCount = 0 };
        Assert.False(ProStatusPresentation.HasReliabilityConcern(clean));

        var unresolved = new QuotaSnapshot { UnresolvedCount = 3 };
        Assert.True(ProStatusPresentation.HasReliabilityConcern(unresolved));

        var legacy = new QuotaSnapshot { LegacyPendingCount = 2 };
        Assert.True(ProStatusPresentation.HasReliabilityConcern(legacy));
    }

    [Theory]
    [InlineData(UserFacingHealthKind.Usable, StatusToneKind.Ok)]
    [InlineData(UserFacingHealthKind.Syncing, StatusToneKind.Accent)]
    [InlineData(UserFacingHealthKind.NeedsConnection, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.NeedsSignIn, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.SyncFailed, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.NeedsAttention, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.Stale, StatusToneKind.Muted)]
    public void HealthTone_MapsExpectedSeverity(UserFacingHealthKind kind, StatusToneKind expected)
    {
        Assert.Equal(expected, UserFacingHealthTone.From(kind));
    }

    [Fact]
    public void FlyoutXaml_ContainsOnlyCodexCard()
    {
        var xaml = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml"));
        Assert.Contains("x:Name=\"CodexCard\"", xaml, StringComparison.Ordinal);
        foreach (var retired in new[] { "GptProCard", "ReasoningCard", "StatusCard", "ExactRemainingPanel", "ReasonTodayBar" })
            Assert.DoesNotContain(retired, xaml, StringComparison.Ordinal);
        foreach (var primitive in new[] { "CodexRingTrack", "CodexRingArcPath", "ArcSegment", "CodexRingFullCircle", "CodexRingValueText" })
            Assert.Contains(primitive, xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexBadge", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnSettingsClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("livecharts", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlyoutCodeBehind_UsesCodexOnlyPresentation()
    {
        var code = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("CodexRingPresentation.From(snapshot, UsagePeriod)", code, StringComparison.Ordinal);
        Assert.Contains("RingGeometry.ComputeUsedArc(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexBadge", code, StringComparison.Ordinal);
        Assert.Contains("SettingsRequested?.Invoke()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ProStatusPresentation", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReasoningStats", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Themes_DefineFlyoutCardPaletteAndRuntimeOverridesForBothThemes()
    {
        var themes = File.ReadAllText(Find("src/CycleArc/UI/Themes.xaml"));
        Assert.Contains("x:Key=\"PanelBrush\"", themes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ExtraHighAccentBrush\"", themes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"FlyoutCard\"", themes, StringComparison.Ordinal);

        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.Contains("app.Resources[\"PanelBrush\"]", app, StringComparison.Ordinal);
    }

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
