using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeStatusLineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");

    [Fact]
    public void OfficialWindowsRetainFractionalPercentagesAndIndependentResetTimes()
    {
        var result = Parse("""{"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1893474000},"seven_day":{"used_percentage":41.2,"resets_at":1894060800}}} """);
        Assert.Equal(ClaudeInputStatus.Available, result.Status);
        Assert.Equal(new ClaudeRateLimit(23.5, 1893474000), result.FiveHour);
        Assert.Equal(new ClaudeRateLimit(41.2, 1894060800), result.SevenDay);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rate_limits\":null}")]
    [InlineData("{\"rate_limits\":{}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":null,\"seven_day\":null}}")]
    [InlineData("{\"rate_limits\":{\"future_window\":{\"used_percentage\":42}}}")]
    public void MissingOptionalDataIsNotZeroOrASchemaFailure(string json)
    {
        var result = Parse(json);
        Assert.Equal(ClaudeInputStatus.Missing, result.Status);
        Assert.Null(result.FiveHour);
        Assert.Null(result.SevenDay);
    }

    [Theory]
    [InlineData("five_hour", 0)]
    [InlineData("seven_day", 100)]
    public void EachWindowCanAppearAloneIncludingZeroAndFullUsage(string key, double used)
    {
        var json = JsonSerializer.Serialize(new { rate_limits = new Dictionary<string, object>
            { [key] = new { used_percentage = used, resets_at = 1894060800 } } });
        var result = Parse(json);
        Assert.Equal(ClaudeInputStatus.Available, result.Status);
        Assert.Equal(used, (key == "five_hour" ? result.FiveHour : result.SevenDay)!.UsedPercentage);
        Assert.Null(key == "five_hour" ? result.SevenDay : result.FiveHour);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"rate_limits\":[]}")]
    [InlineData("{\"rate_limits\":{},\"rate_limits\":{}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{},\"five_hour\":null}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":42}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":-1,\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":101,\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":\"12\",\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":null,\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":true,\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1e999,\"resets_at\":1894060800}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":1894060800000}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":0}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":null}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":\"1894060800\"}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":1894060800.5}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"used_percentage\":42,\"resets_at\":1894060800}}}")]
    public void MalformedPresentDataFailsWithoutAPartialSuccess(string json) =>
        Assert.Equal(ClaudeInputStatus.Malformed, Parse(json).Status);

    [Fact]
    public void ValidFiveHourDoesNotHideMalformedWeeklyData()
    {
        var result = Parse("""{"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1893474000},"seven_day":{"used_percentage":"bad","resets_at":1894060800}}} """);
        Assert.Equal(ClaudeInputStatus.Malformed, result.Status);
        Assert.Null(result.FiveHour);
    }

    [Fact]
    public void InputLimitsRejectOversizedAndDeepPayloads()
    {
        Assert.Equal(ClaudeInputStatus.Malformed, Parse(new string(' ', ClaudeStatusLineParser.MaxInputBytes + 1)).Status);
        Assert.Equal(ClaudeInputStatus.Malformed, Parse(new string('[', 40) + "0" + new string(']', 40)).Status);
    }

    [Fact]
    public async Task OnlyProjectedQuotaFieldsArePersistedOrPrinted()
    {
        using var data = new ClaudeTestData();
        var json = """{"session_id":"synthetic-session-private","transcript_path":"C:/synthetic/private.jsonl","workspace":{"current_dir":"secret-project"},"email":"person@example.invalid","access_token":"synthetic-secret","prompt":"do not save this","rate_limits":{"five_hour":{"used_percentage":0,"resets_at":1893474000,"private":"also-secret"},"seven_day":{"used_percentage":99.2,"resets_at":1894060800}}} """;
        var (code, output) = await data.Receive(json);
        Assert.Equal(0, code);
        Assert.Equal("Claude | 5h 0% | 7d 99.2%" + Environment.NewLine, output);
        var persisted = File.ReadAllText(data.Path);
        foreach (var forbidden in new[] { "synthetic-secret", "synthetic-session-private", "secret-project", "private.jsonl", "person@", "do not save", "also-secret", "isValid" })
            Assert.DoesNotContain(forbidden, persisted + output);
        using var doc = JsonDocument.Parse(persisted);
        var window = doc.RootElement.GetProperty("lastGood").GetProperty("fiveHour");
        Assert.Equal(new[] { "usedPercentage", "resetsAt" }, window.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task IdlePollingAndManualRefreshDoNotWarnOrRenewReceivedTimeOrAlterCache()
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload());
        var bytes = File.ReadAllBytes(data.Path);
        var service = data.Service();
        var changes = 0;
        service.Changed += _ => changes++;
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        foreach (var idle in new[] { TimeSpan.FromSeconds(299), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(12), TimeSpan.FromHours(1), TimeSpan.FromHours(4) })
        {
            data.Clock.UtcNow = Now + idle;
            await service.RefreshAsync(CancellationToken.None);
            await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
            Assert.False(ClaudeUsagePresentation.IsStale(service.Snapshot));
            Assert.Equal(0, changes); // Unchanged polls do not rebuild nickname editors or raise attention.
            Assert.Equal(23.5, service.Snapshot.Windows[0].UsedPercent);
            Assert.Equal(Now, service.Snapshot.LastSuccessfulRefresh);
            Assert.Equal(bytes, File.ReadAllBytes(data.Path));
            var restarted = data.Service().Snapshot;
            Assert.Equal(CodexQuotaStatus.Available, restarted.Status);
            Assert.Equal(Now, restarted.LastSuccessfulRefresh);
        }
        data.Clock.UtcNow = Now.AddHours(4).AddMinutes(1);
        await data.Receive(Payload(27));
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.False(ClaudeUsagePresentation.IsStale(service.Snapshot));
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(27, service.Snapshot.Windows[0].UsedPercent);
    }

    [Theory]
    [InlineData("{}", "claude-statusline-missing")]
    [InlineData("{broken", "claude-statusline-malformed")]
    public async Task MissingAndInvalidUpdatesKeepLastGoodAndMarkItStale(string json, string detail)
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload());
        data.Clock.UtcNow = Now.AddSeconds(30);
        await data.Receive(json);
        var service = data.Service();
        var snapshot = service.Snapshot;
        Assert.Equal(CodexQuotaStatus.Stale, snapshot.Status);
        Assert.Equal(Now, snapshot.LastSuccessfulRefresh);
        Assert.Equal(data.Clock.UtcNow, snapshot.LastAttemptedRefresh);
        Assert.Equal(23.5, snapshot.Windows[0].UsedPercent);
        Assert.Equal(detail, snapshot.TechnicalDetail);
        data.Clock.UtcNow = Now.AddHours(1);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        await data.Receive(Payload(27));
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Equal(27, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(data.Clock.UtcNow, service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task AbsentOptionalWindowIsNotFilledFromAnOlderSample()
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload());
        data.Clock.UtcNow = Now.AddSeconds(5);
        await data.Receive("""{"rate_limits":{"seven_day":{"used_percentage":0,"resets_at":1894060800}}} """);
        var snapshot = data.Service().Snapshot;
        Assert.Equal(CodexQuotaStatus.Available, snapshot.Status);
        Assert.Single(snapshot.Windows);
        Assert.Equal(CodexWindowKind.Weekly, snapshot.Windows[0].Kind);
        Assert.Equal(0, snapshot.Windows[0].UsedPercent);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7 * 24)]
    [InlineData(400 * 24)]
    public async Task ElapsedResetsRemainReceivedAcrossPollingAndRestartWithoutInventingANewPeriod(int hours)
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload());
        var bytes = File.ReadAllBytes(data.Path);
        var service = data.Service();
        var original = service.Snapshot;
        var changes = 0;
        service.Changed += _ => changes++;
        foreach (var elapsed in new[] { TimeSpan.FromHours(hours).Add(TimeSpan.FromSeconds(-1)),
            TimeSpan.FromHours(hours), TimeSpan.FromHours(hours).Add(TimeSpan.FromSeconds(1)) })
        {
            data.Clock.UtcNow = Now + elapsed;
            await service.RefreshAsync(default);
            await service.RefreshAsync(default);
            foreach (var snapshot in new[] { service.Snapshot, data.Service().Snapshot })
            {
                Assert.Equal(CodexQuotaStatus.Available, snapshot.Status);
                Assert.Null(snapshot.TechnicalDetail);
                Assert.Equal(original.Windows.ToArray(), snapshot.Windows.ToArray());
                Assert.Equal(Now, snapshot.LastSuccessfulRefresh);
                Assert.Equal(Now, snapshot.LastAttemptedRefresh);
            }
            Assert.Equal(0, changes);
            Assert.Equal(bytes, File.ReadAllBytes(data.Path));
        }
    }

    [Fact]
    public async Task ClockRollbackStillWarnsWithoutChangingTheReceivedValues()
    {
        using var data = new ClaudeTestData();
        await data.Receive(JsonSerializer.Serialize(new { rate_limits = new { five_hour = new
            { used_percentage = 100, resets_at = Now.AddSeconds(30).ToUnixTimeSeconds() } } }));
        var service = data.Service();
        data.Clock.UtcNow = Now.AddMinutes(-1);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-receipt-invalid", service.Snapshot.TechnicalDetail);
        Assert.Equal(100, service.Snapshot.CompactWindow!.UsedPercent);
        Assert.Equal(Now, service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(CodexQuotaStatus.Stale, data.Service().Snapshot.Status);
        data.Clock.UtcNow = Now.AddHours(1);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Equal(100, service.Snapshot.CompactWindow!.UsedPercent);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task WeeklyOnlySampleRemainsReceivedBeforeAndAfterItsReset(int days)
    {
        using var data = new ClaudeTestData();
        await data.Receive("""{"rate_limits":{"seven_day":{"used_percentage":41.2,"resets_at":1894060800}}}""");
        data.Clock.UtcNow = Now.AddDays(days);
        var snapshot = data.Service().Snapshot;
        Assert.Equal(CodexQuotaStatus.Available, snapshot.Status);
        Assert.Equal(Now, snapshot.LastSuccessfulRefresh);
        Assert.Equal(41.2, Assert.Single(snapshot.Windows).UsedPercent);
    }

    [Fact]
    public async Task CorruptCacheFallsBackToLastValidBackupWithoutReportingFresh()
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload(12));
        data.Clock.UtcNow = Now.AddSeconds(1);
        await data.Receive(Payload(24));
        File.WriteAllText(data.Path, "{broken");
        var snapshot = data.Service().Snapshot;
        Assert.Equal(CodexQuotaStatus.Stale, snapshot.Status);
        Assert.Equal(12, snapshot.Windows[0].UsedPercent);
        Assert.Equal(Now, snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task OutOfOrderConcurrentCallbacksCannotReplaceNewerQuotaWithOlderData()
    {
        using var data = new ClaudeTestData();
        var store = data.Store();
        var newer = store.RecordAsync(Parse(Payload(40)), Now.AddSeconds(2), CancellationToken.None);
        var older = store.RecordAsync(Parse(Payload(10)), Now.AddSeconds(1), CancellationToken.None);
        await Task.WhenAll(newer, older);
        Assert.Equal(40, store.Read().State!.LastGood!.FiveHour!.UsedPercentage);
        Assert.Equal(Now.AddSeconds(2), store.Read().State!.LastGood!.ReceivedAt);
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(data.Path)!, "*.tmp"));
    }

    [Fact]
    public async Task UnknownOrCodexProfileIsNotACollectorTarget()
    {
        using var data = new ClaudeTestData();
        foreach (var id in new[] { "default", Guid.NewGuid().ToString("N"), "../escape" })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload()));
            var output = new StringWriter();
            var code = await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, id], input, output, data.Accounts, data.Clock);
            Assert.Equal(2, code);
        }
        Assert.False(File.Exists(data.Path));
    }

    [Fact]
    public async Task StdinCanBeCancelledWithoutRecordingAPartialSample()
    {
        using var data = new ClaudeTestData();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var input = new NeverEndingInput();
        var code = await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, data.Profile.Id], input,
            new StringWriter(), data.Accounts, data.Clock, cancelled.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, code);
        Assert.False(File.Exists(data.Path));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public async Task SharedPresentationLabelsClaudeAndShowsReceiptAgeInsteadOfCodexAuthentication(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            using var data = new ClaudeTestData();
            Assert.DoesNotContain("Codex", CodexDisplayFormatting.StatusText(data.Service().Snapshot));
            await data.Receive(Payload());
            data.Clock.UtcNow = Now.AddMinutes(6);
            var snapshot = data.Service().Snapshot;
            Assert.Contains("Claude", CycleArcPresentation.Tooltip(snapshot));
            Assert.DoesNotContain("Codex", CycleArcPresentation.Tooltip(snapshot));
            Assert.StartsWith("Claude ", CycleArcPresentation.CompactText(snapshot));
            Assert.DoesNotContain("~", CycleArcPresentation.CompactText(snapshot));
            Assert.Equal(UiText.T("Received", "수신됨"), CycleArcPresentation.StatusLabel(snapshot));
            var rows = CodexDisplayFormatting.Rows(snapshot, data.Clock.UtcNow);
            Assert.Contains(rows, row => row.Value == "23.5% / 76.5%");
            Assert.Equal("23.5%", CodexRingPresentation.From(snapshot).CenterValueText);
            Assert.Equal("41.2%", CodexRingPresentation.From(snapshot, UsagePeriodPreference.Weekly).CenterValueText);
            Assert.Contains(rows, row => row.Label == UiText.T("Last received", "마지막 수신"));
            Assert.DoesNotContain(rows, row => row.Label == UiText.ResetCredits);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public async Task RecentClaudeSampleDescribesSharedSubscriptionAndReceiptWithoutPromisingCurrentUsage(UiLanguage language)
    {
        UiText.SetLanguage(language);
        try
        {
            using var data = new ClaudeTestData();
            await data.Receive(Payload());
            var snapshot = data.Service().Snapshot;
            Assert.Equal(CodexQuotaStatus.Available, snapshot.Status);
            Assert.Equal(UiText.T("Received", "수신됨"), CycleArcPresentation.StatusLabel(snapshot));
            Assert.Contains(UiText.T("Last values received", "마지막으로 받은 값"), ClaudeUsagePresentation.StatusText(snapshot));
            Assert.Contains(ClaudeUsagePresentation.Title, CycleArcPresentation.Tooltip(snapshot));
            Assert.Contains(ClaudeUsagePresentation.SharedScope, CycleArcPresentation.Tooltip(snapshot));
            foreach (var surface in new[] { "Web", "Desktop", "Code" }) Assert.Contains(surface, ClaudeUsagePresentation.SharedScope);
            Assert.Equal(UiText.T("Updated", "업데이트됨"), CycleArcPresentation.StatusLabel(snapshot with { Provider = UsageProviderId.Codex }));
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Theory]
    [InlineData(UiLanguage.English, false)]
    [InlineData(UiLanguage.Korean, false)]
    [InlineData(UiLanguage.English, true)]
    [InlineData(UiLanguage.Korean, true)]
    public async Task OldClaudeReceiptAndSharedScopeSurviveNativeTooltipLimitAndRestart(UiLanguage language, bool inputFailed)
    {
        UiText.SetLanguage(language);
        try
        {
            using var data = new ClaudeTestData();
            await data.Receive(Payload());
            if (inputFailed)
            {
                data.Clock.UtcNow = Now.AddSeconds(30);
                await data.Receive("{broken");
            }
            data.Clock.UtcNow = Now.AddDays(400);
            var snapshot = data.Service().Snapshot;
            Assert.Equal(inputFailed, ClaudeUsagePresentation.IsStale(snapshot));
            Assert.Equal(inputFailed ? UiText.T("Stale data", "오래된 데이터") : UiText.T("Received", "수신됨"),
                CycleArcPresentation.StatusLabel(snapshot));
            var stamp = Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var receipt = Assert.Single(CodexDisplayFormatting.Rows(snapshot, data.Clock.UtcNow),
                row => row.Label == ClaudeUsagePresentation.LastReceivedLabel);
            Assert.Equal(stamp[..10], receipt.Value);
            Assert.StartsWith(stamp[11..], receipt.Detail);
            Assert.Contains(stamp, receipt.Tooltip);
            Assert.Contains(stamp, CycleArcPresentation.Tooltip(snapshot));
            var account = new CodexAccountView(data.Profile with { Label = new string('x', 200) + "😀" }, snapshot);
            var other = new CodexAccountView(data.Profile with { Id = "other" }, snapshot);
            var tooltip = NotifyIconText.Safe(UsageAccountOverview.Create([other, account], account.Profile.Id).Tooltip);
            Assert.True(tooltip.Length <= NotifyIconText.MaximumLength);
            Assert.Contains(CycleArcPresentation.StatusLabel(snapshot), tooltip);
            Assert.Contains(ClaudeUsagePresentation.LastReceivedLabel + " " + stamp, tooltip);
            Assert.Contains("Web·Desktop·Code", tooltip);
            Assert.Contains(UiText.T("shared quota", "공유 한도"), tooltip);
            Assert.Contains(UiText.T("Via Code", "Code에서 수신"), tooltip);
            Assert.Contains("23.5%", tooltip);
            var weeklyTooltip = UsageAccountOverview.Create([other, account], account.Profile.Id, UsagePeriodPreference.Weekly).Tooltip;
            Assert.Contains("41.2%", weeklyTooltip);
            Assert.Contains(stamp, weeklyTooltip);
            Assert.Contains("Web·Desktop·Code", weeklyTooltip);
            Assert.True(weeklyTooltip.Length <= NotifyIconText.MaximumLength);
            Assert.False(char.IsHighSurrogate(tooltip[^1]));
            Assert.Equal(Now, snapshot.LastSuccessfulRefresh);
            Assert.Equal(inputFailed ? UiText.T("Saved data", "이전 데이터") : UiText.T("Updated", "업데이트됨"),
                CycleArcPresentation.StatusLabel(snapshot with { Provider = UsageProviderId.Codex }));
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Fact]
    public void SettingsCommandSafelyCarriesInstallationPathsAcrossWindowsShells()
    {
        var id = Guid.NewGuid().ToString("N");
        var json = ClaudeStatusLineCommand.SettingsJson(@"C:\A space\O'Brien $x` & folder\CycleArc.exe", id);
        var command = JsonNode.Parse(json)!["statusLine"]!["command"]!.GetValue<string>();
        Assert.StartsWith("powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ", command);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        Assert.Contains("& 'C:/A space/O''Brien $x` & folder/CycleArc.exe'", decoded);
        Assert.Contains("$input |", decoded);
        Assert.Contains(id, decoded);
        Assert.DoesNotContain("-ExecutionPolicy", command);
    }

    private static ClaudeStatusLineResult Parse(string json) => ClaudeStatusLineParser.Parse(Encoding.UTF8.GetBytes(json));
    internal static string Payload(double five = 23.5) => JsonSerializer.Serialize(new
    {
        rate_limits = new { five_hour = new { used_percentage = five, resets_at = Now.AddHours(5).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = 41.2, resets_at = Now.AddDays(7).ToUnixTimeSeconds() } }
    });

    private sealed class NeverEndingInput : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    internal sealed class ClaudeTestData : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cyclearc-claude-test-" + Guid.NewGuid().ToString("N"));
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public MutableClock Clock { get; } = new(Now);
        public string Path => Accounts.ClaudeStatusLinePath(Profile.Id);
        public ClaudeTestData()
        {
            Accounts = new(Root);
            var state = Accounts.LoadOrMigrate(System.IO.Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Claude test");
            Accounts.Save(state with { Version = 2, Profiles = state.Profiles.Append(Profile).ToArray() });
        }
        public ClaudeStatusLineStore Store() => new(Path, Profile.Id);
        public ClaudeQuotaService Service() => new(Store(), Clock);
        public async Task<(int Code, string Output)> Receive(string json)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var output = new StringWriter();
            var code = await ClaudeStatusLineCommand.RunAsync([ClaudeStatusLineCommand.Argument, Profile.Id], input, output, Accounts, Clock);
            return (code, output.ToString());
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
