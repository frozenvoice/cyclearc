using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class SurfacePercentPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 3, 0, 0, TimeSpan.Zero);

    public SurfacePercentPresentationTests() => UiText.SetLanguage(UiLanguage.English);

    public static IEnumerable<object[]> PercentCases()
    {
        yield return new object[] { 76.91, "77%", "23%", "76.91%", "23.09%" };
        yield return new object[] { 76.5, "77%", "23%", "76.5%", "23.5%" };
        yield return new object[] { 76.499, "76%", "24%", "76.5%", "23.5%" };
        yield return new object[] { 12.5, "13%", "87%", "12.5%", "87.5%" };
        yield return new object[] { 99.6, ">99%", "<1%", "99.6%", "0.4%" };
        yield return new object[] { 0.5, "<1%", ">99%", "0.5%", "99.5%" };
        yield return new object[] { 1d, "1%", "99%", "1%", "99%" };
        yield return new object[] { 0d, "0%", "100%", "0%", "100%" };
        yield return new object[] { 100d, "100%", "0%", "100%", "0%" };
        yield return new object[] { -0d, "0%", "100%", "0%", "100%" };
    }

    [Theory]
    [MemberData(nameof(PercentCases))]
    public void AllProvidersUseWidgetIntegerTextAndPreciseDetailText(
        double used, string expectedWidgetUsed, string expectedWidgetRemaining,
        string expectedDetailUsed, string expectedDetailRemaining)
    {
        foreach (var provider in Providers())
        {
            var window = Window(provider, used);
            var snapshot = Snapshot(provider, window);
            var beforeSnapshot = JsonSerializer.Serialize(snapshot);
            var model = WidgetAccountModel.From(Account(provider, snapshot), selected: true,
                UsagePeriodPreference.Auto, Now);
            var period = Assert.Single(model.Periods);

            Assert.Equal(expectedWidgetUsed, model.RingValueText);
            Assert.Equal(expectedDetailUsed, model.Ring.CenterValueText);
            Assert.Equal(expectedDetailUsed, CodexRingPresentation.FromDetail(snapshot).CenterValueText);
            Assert.Equal(WidgetRemainingText(provider, expectedWidgetRemaining),
                period.DisplayRemainingText);

            var expectedDetailRemainingText = DetailRemainingText(provider, expectedDetailRemaining);
            Assert.Equal(expectedDetailRemainingText, period.RemainingText);

            var row = Assert.Single(CodexDisplayFormatting.Rows(snapshot),
                candidate => candidate.Value.Contains(expectedDetailRemaining, StringComparison.Ordinal));
            Assert.Equal(RowValue(provider, expectedDetailUsed, expectedDetailRemaining), row.Value);
            Assert.Equal(SummaryValue(provider, expectedDetailUsed, expectedDetailRemaining),
                CodexDisplayFormatting.QuotaSummaryText(window, provider));

            Assert.Same(window, snapshot.Windows[0]);
            Assert.Equal((double?)used, window.UsedPercent);
            Assert.NotNull(window.RemainingPercent);
            Assert.Equal(100d - used, window.RemainingPercent!.Value, 12);
            Assert.Equal(Math.Clamp(used, 0, 100), model.Ring.UsedPercent);
            Assert.Equal(used >= 100, model.Ring.IsDangerLevel);
            Assert.Equal(beforeSnapshot, JsonSerializer.Serialize(snapshot));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void InvalidRawPercentagesStayUnknownAcrossVisibleProviderSurfaces(double used)
    {
        foreach (var provider in Providers())
        {
            var window = Window(provider, used);
            var snapshot = Snapshot(provider, window);
            var model = WidgetAccountModel.From(Account(provider, snapshot), selected: false,
                UsagePeriodPreference.Auto, Now);
            var period = Assert.Single(model.Periods);

            Assert.Equal("?", model.RingValueText);
            Assert.Equal("?", model.Ring.CenterValueText);
            Assert.Equal(WidgetRemainingText(provider, "?"), period.DisplayRemainingText);

            var row = Assert.Single(CodexDisplayFormatting.Rows(snapshot),
                candidate => candidate.Label == RowLabel(provider, window));
            Assert.Equal(RowValue(provider, "?", "?"), row.Value);
            Assert.Equal(SummaryValue(provider, "?", "?"),
                CodexDisplayFormatting.QuotaSummaryText(window, provider));

            Assert.Same(window, snapshot.Windows[0]);
            Assert.Equal((double?)used, window.UsedPercent);
        }
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public void EachPeriodUsesItsOwnSourceAndOnlyItsChosenRepresentative(UsageProviderId provider)
    {
        CodexQuotaWindow first;
        CodexQuotaWindow second;
        UsagePeriodPreference preference;
        if (provider == UsageProviderId.Cursor)
        {
            first = Window(provider, 76.91, "cursor-auto");
            second = Window(provider, 12.5, "cursor-api");
            preference = UsagePeriodPreference.Auto;
        }
        else
        {
            first = Window(provider, 76.91, "five", CodexWindowKind.FiveHour);
            second = Window(provider, 12.5, "week", CodexWindowKind.Weekly);
            preference = UsagePeriodPreference.Weekly;
        }

        var snapshot = Snapshot(provider, first, second);
        var model = WidgetAccountModel.From(Account(provider, snapshot), selected: false, preference, Now);

        var representative = Assert.Single(model.Periods, period => period.IsRepresentative);
        Assert.Same(model.Ring.Window, provider == UsageProviderId.Cursor ? first : second);
        Assert.Equal(2, model.Periods.Count);
        var nonRepresentative = Assert.Single(model.Periods, period => !period.IsRepresentative);
        var expectedNonRepresentative = provider == UsageProviderId.Cursor ? "87%" : "23%";
        var expectedRepresentative = provider == UsageProviderId.Cursor ? "23%" : "87%";
        Assert.Equal(WidgetRemainingText(provider, expectedNonRepresentative), nonRepresentative.DisplayRemainingText);
        Assert.Equal(WidgetRemainingText(provider, expectedRepresentative), representative.DisplayRemainingText);
        Assert.Same(provider == UsageProviderId.Cursor ? first : second, model.Ring.Window);
        Assert.Equal(provider == UsageProviderId.Cursor ? "Cursor Models" : "Weekly",
            representative.PeriodLabel);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Claude)]
    [InlineData(UsageProviderId.Cursor)]
    public void SeparateAccountsNeverPairOrBorrowEachOthersPercentages(UsageProviderId provider)
    {
        var firstWindow = Window(provider, 76.5, provider == UsageProviderId.Cursor ? "cursor-auto" : "five");
        var secondWindow = Window(provider, 12.5, provider == UsageProviderId.Cursor ? "cursor-api" : "five");
        var first = WidgetAccountModel.From(Account(provider, Snapshot(provider, firstWindow), "first"), false, now: Now);
        var second = WidgetAccountModel.From(Account(provider, Snapshot(provider, secondWindow), "second"), false, now: Now);

        Assert.Equal("77%", first.RingValueText);
        Assert.Equal("13%", second.RingValueText);
        Assert.Equal(WidgetRemainingText(provider, "23%"), first.Periods[0].DisplayRemainingText);
        Assert.Equal(WidgetRemainingText(provider, "87%"), second.Periods[0].DisplayRemainingText);
        Assert.Equal(76.5, first.Ring.UsedPercent);
        Assert.Equal(12.5, second.Ring.UsedPercent);
    }

    [Fact]
    public void CursorAmountDisabledAndUnlimitedStatesKeepTheirMeaning()
    {
        var amountWindow = Window(UsageProviderId.Cursor, 76.91, "cursor-auto") with
        {
            UsedAmount = 76.91m,
            LimitAmount = 100m,
            RemainingAmount = 23.09m,
            Unit = "USD"
        };
        var amountSnapshot = Snapshot(UsageProviderId.Cursor, amountWindow);
        var amountModel = WidgetAccountModel.From(Account(UsageProviderId.Cursor, amountSnapshot), false, now: Now);
        Assert.Equal("$23.09", Assert.Single(amountModel.Periods).RemainingText);
        Assert.Equal("$23.09", Assert.Single(amountModel.Periods).DisplayRemainingText);
        Assert.Equal("Remaining $23.09",
            Assert.Single(CodexDisplayFormatting.Rows(amountSnapshot)).Value);
        Assert.Equal("Remaining $23.09",
            CodexDisplayFormatting.QuotaSummaryText(amountWindow, UsageProviderId.Cursor));

        var disabled = Window(UsageProviderId.Cursor, null, "cursor-auto") with { IsEnabled = false };
        var disabledModel = WidgetAccountModel.From(
            Account(UsageProviderId.Cursor, Snapshot(UsageProviderId.Cursor, disabled)), false, now: Now);
        Assert.Empty(disabledModel.Periods);
        Assert.Equal("Off", CursorUsagePresentation.RemainingText(disabled));
        Assert.Equal("Off", Assert.Single(CodexDisplayFormatting.Rows(Snapshot(UsageProviderId.Cursor, disabled))).Value);

        var unlimited = Window(UsageProviderId.Cursor, null, "cursor-auto") with { IsUnlimited = true };
        var unlimitedModel = WidgetAccountModel.From(
            Account(UsageProviderId.Cursor, Snapshot(UsageProviderId.Cursor, unlimited)), false, now: Now);
        Assert.Equal("Unlimited", Assert.Single(unlimitedModel.Periods).RemainingText);
        Assert.Equal("Unlimited", Assert.Single(unlimitedModel.Periods).DisplayRemainingText);
    }

    [Theory]
    [InlineData(UsageProviderId.Codex, 76.91, "77%", "76.91%")]
    [InlineData(UsageProviderId.Codex, 99.6, "100%", "99.6%")]
    [InlineData(UsageProviderId.Claude, 76.91, "76.91%", "76.91%")]
    [InlineData(UsageProviderId.Claude, 99.6, "99.6%", "99.6%")]
    [InlineData(UsageProviderId.Cursor, 76.91, "76.91%", "76.91%")]
    [InlineData(UsageProviderId.Cursor, 99.6, "99.6%", "99.6%")]
    public void DefaultRingCompactAndTrayKeepTheirExistingPrecision(
        UsageProviderId provider, double used, string expectedDefault, string expectedPrecise)
    {
        var window = Window(provider, used);
        var snapshot = Snapshot(provider, window);
        var ring = CodexRingPresentation.From(snapshot);

        Assert.Equal(expectedDefault, ring.CenterValueText);
        Assert.Equal(expectedDefault, CodexDisplayFormatting.PercentText(used, provider));
        Assert.Equal(provider.Name() + " " + expectedDefault, CycleArcPresentation.CompactText(snapshot));
        var tray = CycleArcPresentation.TrayTooltip(snapshot);
        Assert.Contains(provider == UsageProviderId.Cursor
            ? CursorUsagePresentation.RemainingText(window)
            : expectedDefault, tray, StringComparison.Ordinal);
        Assert.Equal(expectedPrecise, UsagePercentFormatting.Detail(used));
        Assert.NotEqual(expectedPrecise, UsagePercentFormatting.Widget(used));
    }

    private static IEnumerable<UsageProviderId> Providers() =>
        [UsageProviderId.Codex, UsageProviderId.Claude, UsageProviderId.Cursor];

    private static string WidgetRemainingText(UsageProviderId provider, string value) =>
        provider == UsageProviderId.Cursor ? value : "Left " + value;

    private static string DetailRemainingText(UsageProviderId provider, string value) =>
        provider == UsageProviderId.Cursor ? value : "Left " + value;

    private static string RowLabel(UsageProviderId provider, CodexQuotaWindow window) =>
        provider == UsageProviderId.Cursor
            ? CursorUsagePresentation.QuotaDisplayLabel(window.LimitId)
            : window.Kind == CodexWindowKind.Weekly
                ? UiText.WeeklyUsed + " / " + UiText.T("left", "남음")
                : UiText.FiveHourUsed + " / " + UiText.T("left", "남음");

    private static string RowValue(UsageProviderId provider, string used, string remaining) =>
        provider == UsageProviderId.Cursor
            ? "Remaining " + remaining
            : used + " / " + remaining;

    private static string SummaryValue(UsageProviderId provider, string used, string remaining) =>
        provider == UsageProviderId.Cursor
            ? "Remaining " + remaining
            : UiText.T($"Used {used} · Left {remaining}", $"사용 {used} · 잔여 {remaining}");

    private static CodexAccountView Account(UsageProviderId provider, CodexQuotaSnapshot snapshot,
        string id = "surface")
        => new(new CodexAccountProfile(id, "", id) { Provider = provider }, snapshot) { IsConnected = true };

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, params CodexQuotaWindow[] windows) =>
        new(CodexQuotaStatus.Available, "pro", null, null, null, null, null, windows, null)
        {
            Provider = provider
        };

    private static CodexQuotaWindow Window(UsageProviderId provider, double? used,
        string? limitId = null, CodexWindowKind? kind = null) =>
        new(limitId ?? (provider == UsageProviderId.Cursor ? "cursor-auto" : "five"),
            used,
            provider == UsageProviderId.Cursor ? null : kind == CodexWindowKind.Weekly ? CodexWindowClassifier.WeeklyMinutes : CodexWindowClassifier.FiveHourMinutes,
            null,
            provider == UsageProviderId.Cursor ? CodexWindowKind.Other : kind ?? CodexWindowKind.FiveHour)
        {
            IsEnabled = true
        };
}
