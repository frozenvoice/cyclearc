using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeResetPresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Theory]
    [InlineData(UiLanguage.English, "in 4h 32m", "Next reset unconfirmed", "Reset unconfirmed", "Reset not provided", "Refresh needed")]
    [InlineData(UiLanguage.Korean, "4시간 32분 후", "다음 리셋 시각 미확인", "리셋 미확인", "리셋 미제공", "새로고침 필요")]
    public void ClaudeResetStatesUseDistinctCopyWithoutInventingACountdown(
        UiLanguage language, string countdown, string elapsedDetail, string elapsedWidget, string missing, string stale)
    {
        var previous = UiText.Language;
        UiText.SetLanguage(language);
        try
        {
            var future = Window(ClaudeResetObservationState.Direct, Now.AddHours(4).AddMinutes(32), Now.AddHours(4).AddMinutes(32));
            Assert.Equal(countdown, ClaudeResetPresentation.Countdown(future, Now));
            var preserved = Window(ClaudeResetObservationState.Preserved, Now.AddHours(4).AddMinutes(32), Now.AddHours(4).AddMinutes(32));
            Assert.Contains(language == UiLanguage.Korean ? "이전" : "Prior", ClaudeResetPresentation.Countdown(preserved, Now));
            Assert.Contains(countdown, ClaudeResetPresentation.Countdown(preserved, Now));
            var elapsed = Window(ClaudeResetObservationState.Elapsed, null, Now.AddMinutes(-1));
            Assert.Equal(elapsedWidget, ClaudeResetPresentation.Countdown(elapsed, Now));
            Assert.Equal(elapsedDetail, ClaudeResetPresentation.Stamp(elapsed, Now));
            Assert.NotEqual(CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(-1), Now), ClaudeResetPresentation.Countdown(elapsed, Now));
            var never = Window(ClaudeResetObservationState.NotProvided, null, null);
            Assert.Equal(missing, ClaudeResetPresentation.Countdown(never, Now));
            Assert.Equal(missing, ClaudeResetPresentation.Stamp(never, Now));
            var expired = Window(ClaudeResetObservationState.Stale, null, Now.AddHours(1));
            Assert.Equal(stale, ClaudeResetPresentation.Countdown(expired, Now));
            Assert.Equal(stale, ClaudeResetPresentation.Stamp(expired, Now));
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Fact]
    public void ElapsedTooltipSeparatesUsageAndResetEvidenceAndDoesNotPresentTheOldTimeAsNext()
    {
        var previous = UiText.Language;
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var origin = Now.AddMinutes(-4);
            var reset = Now.AddMinutes(-1);
            var window = Window(ClaudeResetObservationState.Elapsed, null, reset, ClaudeResetSource.Server, origin);
            var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, Now, Now, null, null, null,
                [window], "claude-desktop-history")
            { Provider = UsageProviderId.Claude, LastSuccessfulObservationWasServer = false };
            var tooltip = ClaudeResetPresentation.Tooltip(window, Now, snapshot)!;
            Assert.Contains("새로운 리셋 시각은 아직 확인되지 않았습니다.", tooltip);
            Assert.Contains("다음 리셋 시각이 아닙니다.", tooltip);
            Assert.Contains("남은 사용량만으로 새 주기가 시작된 것은 아닙니다.", tooltip);
            Assert.Contains("사용량 · Desktop · 확인 ", tooltip);
            Assert.Contains("리셋 근거 · OAuth · 관측 ", tooltip);
            Assert.Contains(origin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), tooltip);
            Assert.Contains(Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), tooltip);
            Assert.Contains("마지막 제공 리셋: ", tooltip);
            Assert.Contains("경과", tooltip);
            Assert.DoesNotContain("분 후", tooltip);
            Assert.DoesNotContain("갱신 대기", tooltip);
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Theory]
    [InlineData(UiLanguage.English, "in 4h 32m", "Awaiting refresh")]
    [InlineData(UiLanguage.Korean, "4시간 32분 후", "갱신 대기")]
    public void CodexAndCursorDeadlineCopyStaysUnchanged(UiLanguage language, string future, string elapsed)
    {
        var previous = UiText.Language;
        UiText.SetLanguage(language);
        try
        {
            Assert.Equal(future, CodexDeadlineFormatting.ResetCountdown(Now.AddHours(4).AddMinutes(32), Now));
            Assert.Equal(elapsed, CodexDeadlineFormatting.ResetCountdown(Now.AddMinutes(-1), Now));
            Assert.Equal(UiText.ResetNotProvided, CodexDeadlineFormatting.ResetCountdown(null, Now));
        }
        finally { UiText.SetLanguage(previous); }
    }

    private static CodexQuotaWindow Window(ClaudeResetObservationState state, DateTimeOffset? resetsAt,
        DateTimeOffset? lastKnown, ClaudeResetSource source = ClaudeResetSource.Server, DateTimeOffset? observedAt = null) =>
        new("five_hour", 0, 300, resetsAt, CodexWindowKind.FiveHour)
        {
            ResetObservation = new(state, lastKnown is null && state == ClaudeResetObservationState.NotProvided ? null : source,
                observedAt ?? Now.AddMinutes(-4), lastKnown)
        };
}
