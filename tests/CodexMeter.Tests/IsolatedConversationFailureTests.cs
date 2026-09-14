using CodexMeter.Codex;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class IsolatedConversationFailureTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 6, 5, 20, 14, TimeSpan.Zero);
    // Keep the scan and presentation inside the fixed fixture's quota period.
    private static readonly DateTimeOffset Now = T.AddHours(12);

    [Fact]
    public void LiveTimeoutDiagnostics_AreBridgeTimeoutNotSchemaMismatch()
    {
        var load = new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = true,
            FailureKind = ConversationLoadFailureKind.Timeout,
            Diagnostics =
            [
                "paginated-head timed out",
                "full-mapping unavailable status=403",
                "legacy-mapping timed out",
                "Paginated conversation head was empty."
            ]
        };
        Assert.Equal("BridgeTimeout", SyncFailureClassifier.ClassifyLoad(load));
        Assert.NotEqual("SchemaMismatch", SyncFailureClassifier.ClassifyLoad(load));
    }

    [Fact]
    public async Task PaginatedTimeout_FullMapping403_LegacyTimeout_IsNotSchemaMismatch()
    {
        var loader = new ConversationDetailLoader { ConversationEndpointTimeout = TimeSpan.FromMilliseconds(40) };
        var load = await loader.LoadAsync(
            new DataExportTransport(),
            "conv-giant",
            async (_, path, _, cancellationToken) =>
            {
                if (path.Contains("include_full_conversation=true", StringComparison.Ordinal))
                {
                    throw new ChatGptProviderException("forbidden", 403);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return null;
            });

        Assert.Contains(load.Diagnostics, line => line.Contains("paginated-head timed out", StringComparison.Ordinal));
        Assert.Contains(load.Diagnostics, line => line.Contains("full-mapping unavailable status=403", StringComparison.Ordinal));
        Assert.Contains(load.Diagnostics, line => line.Contains("legacy-mapping timed out", StringComparison.Ordinal));
        Assert.False(load.SchemaMismatch);
        Assert.Equal(ConversationLoadFailureKind.Timeout, load.FailureKind);
        Assert.Equal("BridgeTimeout", SyncFailureClassifier.ClassifyLoad(load));
    }

    [Fact]
    public async Task LiveSequence_OneGiantTimeout_DoesNotFailMeterHealth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "live.db"));
        var models = new ModelNormalizer();
        var clock = new MutableClock(Now);
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var after = T.AddHours(1).ToUnixTimeSeconds();
        var giant = new ConversationIndexItem { Id = "conv-giant", UpdateTime = after, CreateTime = after - 10 };
        var other = new ConversationIndexItem { Id = "conv-ok", UpdateTime = after - 1, CreateTime = after - 20 };
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        fixture.AddConversation(giant, UniquePro("conv-giant", after));
        fixture.AddConversation(other, UniquePro("conv-ok", after - 1));
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;

        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.UpToDate, run1.Status);
        Assert.Equal(Now, engine.LastSyncCompleted);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.Equal(2, store.GetUsageEvents().Count(e => e.QuotaFamily == QuotaFamily.GptPro));
        var snapshot1 = Snapshot(engine, store, settings);
        Assert.Equal(2, snapshot1.ReconstructedUsed);
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot1).Kind);

        giant.UpdateTime += 90;
        fixture.LoadOverride = id => id == "conv-giant"
            ? new ConversationLoadResult
            {
                Complete = false,
                FailureKind = ConversationLoadFailureKind.Timeout,
                Diagnostics =
                [
                    "paginated-head timed out",
                    "full-mapping unavailable status=403",
                    "legacy-mapping timed out",
                    "Paginated conversation head was empty."
                ]
            }
            : ConversationDetailLoader.FromFixture(UniquePro("conv-ok", after - 1));

        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(AppSyncStatus.PartialData, run2.Status);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(ConversationFetchBackoff.BodyTimeout, store.GetConversation("conv-giant")?.LastFetchFailureCategory);
        Assert.NotNull(store.GetConversation("conv-giant")?.LastSuccessfulScan);
        Assert.Equal(2, store.GetUsageEvents().Count(e => e.QuotaFamily == QuotaFamily.GptPro));
        var snapshot2 = Snapshot(engine, store, settings);
        Assert.Equal(2, snapshot2.ReconstructedUsed);
        Assert.True(snapshot2.CurrentCycleKnown);
        var health2 = UserFacingHealth.From(snapshot2);
        Assert.Equal(UserFacingHealthKind.Usable, health2.Kind);
        Assert.Equal(UiText.DataUsable, health2.DataStatusText);
        Assert.NotEqual(UiText.SyncFailedShort, health2.HeaderText);
        Assert.Equal("2+", ProStatusPresentation.From(snapshot2).ConfirmedRequestsText);
        Assert.Equal("P? 2+", TaskbarStatusFormatter.ChatGptToken(snapshot2, TaskbarStripMode.Full));
        Assert.Equal("P?2+", TaskbarStatusFormatter.ChatGptToken(snapshot2, TaskbarStripMode.Compact));
        var view = DataStatusPresentation.From(snapshot2, AvailableCodex());
        Assert.Equal($"{UiText.DataStatus}: {UiText.DataUsable}", view.Headline);
        Assert.Contains(UiText.ReadTimeout, string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
        Assert.DoesNotContain("conv-giant", string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);

        giant.UpdateTime += 90;
        fixture.LoadOverride = id => id == "conv-giant"
            ? ConversationDetailLoader.FromFixture(ConversationFixtures.TwoProTurns("conv-giant", after, after + 90))
            : ConversationDetailLoader.FromFixture(UniquePro("conv-ok", after - 1));

        var run3 = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(AppSyncStatus.UpToDate, run3.Status);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.Equal(0, store.GetConversation("conv-giant")?.ConsecutiveFetchFailures);
        Assert.Null(store.GetConversation("conv-giant")?.LastFetchFailureCategory);
        var events = store.GetUsageEvents().Where(e => e.QuotaFamily == QuotaFamily.GptPro).ToList();
        Assert.Equal(4, events.Count);
        Assert.Equal(events.Count, events.Select(e => e.DedupeKey).Distinct(StringComparer.Ordinal).Count());
        var snapshot3 = Snapshot(engine, store, settings);
        Assert.Equal(4, snapshot3.ReconstructedUsed);
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot3).Kind);
        Assert.Equal(UiText.ReconstructedCount(4), ProStatusPresentation.From(snapshot3).ConfirmedRequestsText);
    }

    private static QuotaSnapshot Snapshot(SyncEngine engine, SqliteStore store, AppSettings settings) =>
        new QuotaEngine().Build(
            store.GetUsageEvents(),
            settings,
            Now,
            engine.LastSyncCompleted,
            engine.LastCoverage,
            engine.LastQuotaMetadata,
            engine.LastStatus,
            engine.LastStatusDetail);

    private static JsonNode UniquePro(string conversationId, double time)
    {
        var json = ConversationFixtures.NormalPro(conversationId, time).ToJsonString()
            .Replace("req-normal", "req-" + conversationId, StringComparison.Ordinal);
        return JsonNode.Parse(json)!;
    }

    private static QuotaMetadataSet CycleQuota() => new()
    {
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.Unknown,
            LastConfirmedResetAt = T
        }
    };

    private static CodexQuotaSnapshot AvailableCodex() => new(
        CodexQuotaStatus.Available,
        null,
        Now,
        Now,
        null,
        null,
        null,
        [new CodexQuotaWindow(null, 97, 10080, null, CodexWindowKind.Weekly)],
        null);
}
