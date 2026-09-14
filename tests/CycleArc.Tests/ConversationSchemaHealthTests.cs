using CycleArc.Codex;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ConversationSchemaHealthTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 6, 5, 20, 14, TimeSpan.Zero);
    // Keep the scan and presentation inside the fixed fixture's quota period.
    private static readonly DateTimeOffset Now = T.AddHours(12);

    [Theory]
    [InlineData(1, 0, 1, 0, 0, false)]
    [InlineData(2, 0, 2, 0, 0, false)]
    [InlineData(3, 0, 3, 0, 0, true)]
    [InlineData(5, 1, 4, 0, 0, false)]
    [InlineData(4, 0, 3, 1, 0, false)]
    [InlineData(3, 0, 0, 3, 0, false)]
    [InlineData(0, 0, 0, 0, 0, false)]
    [InlineData(3, 0, 3, 0, 1, false)]
    public void Policy_UsesConservativeAllSchemaThreshold(
        int attempted,
        int success,
        int schema,
        int timeout,
        int other,
        bool systemic)
    {
        var assessment = ConversationSchemaHealthPolicy.Evaluate(
            new ConversationSchemaHealthEvidence(attempted, success, schema, timeout, other));
        Assert.Equal(3, ConversationSchemaHealthPolicy.MinSystemicSchemaMismatchSamples);
        Assert.Equal(systemic, assessment.SystemicBreak);
    }

    [Theory]
    [InlineData(false, 3, 0, 3, 0, 0, true)]
    [InlineData(true, 0, 0, 0, 0, 0, true)]
    [InlineData(true, 1, 0, 0, 1, 0, true)]
    [InlineData(true, 1, 1, 0, 0, 0, false)]
    [InlineData(false, 0, 0, 0, 0, 0, false)]
    [InlineData(false, 1, 0, 0, 1, 0, false)]
    public void NextLatch_SetsRetainsOrClearsFromCurrentEvidence(
        bool previous,
        int attempted,
        int success,
        int schema,
        int timeout,
        int other,
        bool expected)
    {
        var assessment = ConversationSchemaHealthPolicy.Evaluate(
            new ConversationSchemaHealthEvidence(attempted, success, schema, timeout, other));
        Assert.Equal(expected, ConversationSchemaHealthPolicy.NextLatch(previous, assessment));
    }

    [Theory]
    [InlineData("true", "3", 3, true)]
    [InlineData("false", "3", 3, false)]
    [InlineData("true", "2", 3, false)]
    [InlineData("true", null, 3, false)]
    [InlineData(null, "3", 3, false)]
    [InlineData("true", "3", 4, false)]
    public void RestoreLatch_RequiresMatchingParserVersion(
        string? storedValue,
        string? storedParserVersion,
        int currentParserVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            ConversationSchemaHealthPolicy.RestoreLatch(storedValue, storedParserVersion, currentParserVersion));
        Assert.Equal(4, ConversationFetchBackoff.ParserCompatibilityVersion);
    }

    [Fact]
    public async Task A_OneIsolatedSchemaMismatch_StaysPartialAndUsable()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(1);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        var snapshot = Snapshot(engine, store, settings);
        Assert.True(snapshot.CurrentCycleKnown);
        Assert.False(snapshot.Coverage.ConversationSchemaSystemicFailure);
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
        Assert.Equal(items.Count, provider.BodyFetches);
    }

    [Fact]
    public async Task B_TwoSchemaMismatches_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(2);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(2, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(2, provider.BodyFetches);
    }

    [Fact]
    public async Task C_ThreeSchemaMismatches_EscalateSystemicFailure()
    {
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal("true", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));
        Assert.Equal(
            ConversationFetchBackoff.ParserCompatibilityVersion.ToString(CultureInfo.InvariantCulture),
            store.GetState(ConversationSchemaHealthPolicy.LatchParserVersionStateKey));
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(0, engine.LastCoverage.LoadedConversations);
        var snapshot = Snapshot(engine, store, settings);
        Assert.True(snapshot.LastSync is not null);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
        Assert.True(health.Actionable);
        var view = DataStatusPresentation.From(snapshot, AvailableCodex());
        Assert.Contains(UiText.RepeatedConversationSchemaMismatch, view.AdvancedLines, StringComparer.Ordinal);
        Assert.DoesNotContain(UiText.CoverageNoUserActionNote, view.AdvancedLines, StringComparer.Ordinal);
        Assert.DoesNotContain("conv-schema-0", string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task D_FourSchemaMismatchesWithOneSuccess_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(5);
        fixture.LoadOverride = id => id == "conv-schema-4"
            ? ConversationDetailLoader.FromFixture(UniquePro("conv-schema-4", T.AddHours(1).ToUnixTimeSeconds()))
            : SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(4, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(1, engine.LastCoverage.LoadedConversations);
        Assert.Equal(5, provider.BodyFetches);
    }

    [Fact]
    public async Task E_ThreeSchemaMismatchesPlusTimeout_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(4);
        fixture.LoadOverride = id => id == "conv-schema-3" ? TimeoutLoad() : SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
    }

    [Fact]
    public async Task F_ThreeTimeouts_AreNotSystemicSchemaBreak()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => TimeoutLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
    }

    [Fact]
    public async Task G_IndexSchemaMismatch_EscalatesImmediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "index.db"));
        var models = new ModelNormalizer();
        var clock = new MutableClock(Now);
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var outcome = await engine.SyncAsync(new IndexMismatchProvider(), AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.False(engine.LastCoverage.NormalChats);
    }

    [Fact]
    public async Task H_DuplicateNormalAndProjectAppearance_CountsOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "dup.db"));
        var models = new ModelNormalizer();
        var clock = new MutableClock(Now);
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        var item = new ConversationIndexItem { Id = "conv-dup", UpdateTime = now, CreateTime = now - 10 };
        fixture.AddConversation(item, UniquePro("conv-dup", now));
        fixture.AddProject(
            new ProjectInfo { Id = "proj-dup" },
            [(new ConversationIndexItem { Id = "conv-dup", UpdateTime = now, CreateTime = now - 10 }, UniquePro("conv-dup", now))]);
        fixture.LoadOverride = _ => SchemaLoad();
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.UniqueConversations);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
    }

    [Fact]
    public async Task I_DeferredHistoricalSchemaFailures_DoNotEscalate()
    {
        var clock = new MutableClock(Now);
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "deferred.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        for (var i = 0; i < 3; i++)
        {
            var item = new ConversationIndexItem
            {
                Id = "conv-old-" + i,
                UpdateTime = now - i,
                CreateTime = now - 20 - i
            };
            fixture.AddConversation(item, UniquePro(item.Id, item.UpdateTime));
            store.RecordConversationFailure(
                null,
                item,
                ConversationScanStatus.SchemaMismatch,
                "mapping incomplete",
                ConversationFetchBackoff.SchemaMismatch,
                clock.UtcNow);
        }

        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        var outcome = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(0, provider.BodyFetches);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
    }

    [Fact]
    public async Task J_RecoveryAfterSystemicBreak_ClearsCurrentFailure()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run1.Status);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(UserFacingHealthKind.SyncFailed, UserFacingHealth.From(Snapshot(engine, store, settings)).Kind);

        foreach (var item in items)
        {
            item.UpdateTime += 90;
        }

        fixture.LoadOverride = id => ConversationDetailLoader.FromFixture(UniquePro(id, items[0].UpdateTime));
        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.False(engine.ConversationSchemaSystemicFailureLatched);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal("false", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.All(items, item =>
        {
            var record = store.GetConversation(item.Id);
            Assert.Equal(0, record?.ConsecutiveFetchFailures);
            Assert.Null(record?.LastFetchFailureCategory);
        });
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.NotEqual(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.False(health.Actionable);
    }

    [Fact]
    public async Task Latch_ZeroAttemptDeferredRun_RetainsProvenBreak()
    {
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run1.Status);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(3, provider.BodyFetches);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.Equal("true", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public async Task Latch_TimeoutOnlyRun_RetainsAndKeepsTimeoutClassification()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(provider, settings, force: true);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        items[0].UpdateTime += 90;
        fixture.LoadOverride = id => id == items[0].Id ? TimeoutLoad() : SchemaLoad();
        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(2, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(
            ConversationFetchBackoff.BodyTimeout,
            ConversationFetchBackoff.NormalizeCategory(store.GetConversation(items[0].Id)?.LastFetchFailureCategory));
        Assert.Equal("BridgeTimeout", SyncFailureClassifier.ClassifyLoad(TimeoutLoad()));
        Assert.NotEqual("SchemaMismatch", SyncFailureClassifier.ClassifyLoad(TimeoutLoad()));
    }

    [Fact]
    public async Task Latch_OneSuccessfulBody_ClearsGlobalBreak()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(provider, settings, force: true);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        items[0].UpdateTime += 90;
        fixture.LoadOverride = id => id == items[0].Id
            ? ConversationDetailLoader.FromFixture(UniquePro(id, items[0].UpdateTime))
            : SchemaLoad();
        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.False(engine.ConversationSchemaSystemicFailureLatched);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal("false", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.Equal(AppSyncStatus.PartialData, run2.Status);
        Assert.Equal(2, engine.LastCoverage.FailedConversations);
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(Snapshot(engine, store, settings)).Kind);
    }

    [Fact]
    public async Task Latch_RestartPreservesUntilRecovery()
    {
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(provider, settings, force: true);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        var restarted = RestartEngine(store);
        Assert.Equal(Now, restarted.LastSyncCompleted);
        Assert.True(restarted.ConversationSchemaSystemicFailureLatched);
        var deferred = await restarted.SyncAsync(provider, settings, force: false);
        Assert.Equal(Now, restarted.LastSyncCompleted);
        Assert.Equal(3, restarted.LastCoverage.FailureSummary.DeferredCount);
        Assert.True(restarted.ConversationSchemaSystemicFailureLatched);
        Assert.True(restarted.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, deferred.Status);
        Assert.Equal(0, restarted.LastCoverage.FailureSummary.FailedThisSyncCount);
    }

    [Fact]
    public async Task Latch_RestartAfterRecovery_StaysCleared()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(provider, settings, force: true);
        foreach (var item in items)
        {
            item.UpdateTime += 90;
        }

        fixture.LoadOverride = id => ConversationDetailLoader.FromFixture(UniquePro(id, items[0].UpdateTime));
        await engine.SyncAsync(provider, settings, force: false);
        Assert.False(engine.ConversationSchemaSystemicFailureLatched);
        Assert.Equal("false", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));

        var restarted = RestartEngine(store);
        Assert.False(restarted.ConversationSchemaSystemicFailureLatched);
        var deferred = await restarted.SyncAsync(provider, settings, force: false);
        Assert.False(restarted.ConversationSchemaSystemicFailureLatched);
        Assert.False(restarted.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, deferred.Status);
    }

    [Fact]
    public void Latch_ParserCompatibilityChange_DoesNotRestoreOldBreak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "parser.db"));
        store.SetState(ConversationSchemaHealthPolicy.LatchStateKey, "true");
        store.SetState(ConversationSchemaHealthPolicy.LatchParserVersionStateKey, "999");
        var engine = RestartEngine(store);
        Assert.False(engine.ConversationSchemaSystemicFailureLatched);
        Assert.Equal(4, ConversationFetchBackoff.ParserCompatibilityVersion);
        Assert.False(
            ConversationSchemaHealthPolicy.RestoreLatch(
                "true",
                "999",
                ConversationFetchBackoff.ParserCompatibilityVersion));
    }

    [Fact]
    public async Task FatalAuth_ReplacesStaleCoverage_ButKeepsLatch()
    {
        var clock = new MutableClock(Now);
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3, clock);
        fixture.LoadOverride = _ => SchemaLoad();
        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run1.Status);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        var completed = engine.LastSyncCompleted;
        var events = store.GetUsageEvents().Count;
        clock.UtcNow = Now.AddMinutes(1);

        var run2 = await engine.SyncAsync(
            new ThrowingAccountProvider(fixture, new ChatGptProviderException("Authentication required.", 401)),
            settings,
            force: true);
        Assert.Equal(AppSyncStatus.AuthenticationRequired, run2.Status);
        Assert.Equal(AppSyncStatus.AuthenticationRequired, engine.LastStatus);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(completed, engine.LastSyncCompleted);
        Assert.Equal(events, store.GetUsageEvents().Count);
        var snapshot2 = Snapshot(engine, store, settings);
        var health2 = UserFacingHealth.From(snapshot2);
        Assert.Equal(UserFacingHealthKind.NeedsSignIn, health2.Kind);
        Assert.Equal(UiText.SignInRequired, health2.HeaderText);
        Assert.Equal(UiText.SignInRequired, health2.DataStatusText);
        var view2 = DataStatusPresentation.From(snapshot2, AvailableCodex());
        Assert.Equal($"{UiText.DataStatus}: {UiText.SignInRequired}", view2.Headline);
        Assert.DoesNotContain(UiText.RepeatedConversationSchemaMismatch, view2.AdvancedLines, StringComparer.Ordinal);
        Assert.DoesNotContain(UiText.ResponseFormatMismatch, string.Join('\n', view2.AdvancedLines), StringComparison.Ordinal);

        var run3 = await engine.SyncAsync(provider, settings, force: false);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run3.Status);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        var health3 = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.Equal(UserFacingHealthKind.SyncFailed, health3.Kind);
        Assert.Equal(UiText.NeedsAttention, health3.DataStatusText);
    }

    [Theory]
    [InlineData(0, AppSyncStatus.CompanionDisconnected, UserFacingHealthKind.NeedsConnection)]
    [InlineData(1, AppSyncStatus.Offline, UserFacingHealthKind.NeedsConnection)]
    [InlineData(2, AppSyncStatus.Forbidden, UserFacingHealthKind.SyncFailed)]
    [InlineData(3, AppSyncStatus.RateLimited, UserFacingHealthKind.SyncFailed)]
    public async Task FatalCurrentStatus_ReplacesStaleCoverage_AndWinsInUi(
        int kind,
        AppSyncStatus expectedStatus,
        UserFacingHealthKind expectedHealth)
    {
        var (engine, store, fixture, _, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(new IncrementalSyncTests.CountingProvider(fixture), settings, force: true);
        engine.RetryAttempts = 1;
        engine.RetryBaseDelay = TimeSpan.Zero;
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        var error = kind switch
        {
            0 => new ChatGptProviderException(CompanionBridgeProtocol.NotConnectedError, 0),
            1 => new ChatGptProviderException("offline", 0),
            2 => new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403),
            _ => new ChatGptProviderException("Rate limited.", 429)
        };
        var outcome = await engine.SyncAsync(new ThrowingAccountProvider(fixture, error), settings, force: true);
        Assert.Equal(expectedStatus, outcome.Status);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.Equal(expectedHealth, health.Kind);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public async Task StaleLatch_CurrentIndexSchemaMismatch_RemainsPrimaryActionable()
    {
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        await engine.SyncAsync(provider, settings, force: true);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);

        var outcome = await engine.SyncAsync(new IndexMismatchProvider(), settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.NormalIndexState);
        Assert.False(engine.LastCoverage.NormalChats);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        var snapshot = Snapshot(engine, store, settings);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public async Task PausedAutoSync_ReportsCurrentRunCoverage_AndKeepsLatch()
    {
        var clock = new MutableClock(Now);
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3, clock);
        fixture.LoadOverride = _ => SchemaLoad();
        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run1.Status);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        var completed = engine.LastSyncCompleted;
        Assert.Equal(Now, completed);
        clock.UtcNow = Now.AddMinutes(1);

        engine.RetryAttempts = 1;
        engine.RetryBaseDelay = TimeSpan.Zero;
        foreach (var item in items)
        {
            item.UpdateTime += 90;
        }

        var rateLimited = new ConversationIndexItem
        {
            Id = "conv-rate-limited",
            UpdateTime = items[^1].UpdateTime - 10,
            CreateTime = items[^1].UpdateTime - 40
        };
        fixture.AddConversation(rateLimited, UniquePro(rateLimited.Id, rateLimited.UpdateTime));
        fixture.LoadOverride = id => id == rateLimited.Id
            ? throw new ChatGptProviderException("Rate limited.", 429, "0")
            : SchemaLoad();

        var run2 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.RateLimited, run2.Status);
        Assert.True(engine.IsPaused);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(3, engine.LastCoverage.FailedConversations);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        var staleCoverage = engine.LastCoverage;

        var run3 = await engine.SyncAsync(provider, settings, new SyncRunOptions { Origin = SyncOrigin.Auto });
        Assert.Equal(AppSyncStatus.RateLimited, run3.Status);
        Assert.Equal(UiText.AutoSyncPaused, run3.Detail);
        Assert.Equal(AppSyncStatus.RateLimited, engine.LastStatus);
        Assert.Equal(UiText.AutoSyncPaused, engine.LastStatusDetail);
        Assert.NotSame(staleCoverage, engine.LastCoverage);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.False(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        Assert.Equal("true", store.GetState(ConversationSchemaHealthPolicy.LatchStateKey));
        Assert.Equal(completed, engine.LastSyncCompleted);
    }

    [Fact]
    public void SystemicFlag_LastSyncDoesNotHideBreak()
    {
        var coverage = new CoverageInfo
        {
            NormalIndexState = CollectionState.Complete,
            ConversationSchemaSystemicFailure = true,
            FailedConversations = 3,
            ConversationIncomplete = true
        };
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        var snapshot = new QuotaSnapshot
        {
            Used = 5,
            ReconstructedUsed = 5,
            CurrentCycleKnown = true,
            LastSync = Now,
            Status = AppSyncStatus.ProviderSchemaMismatch,
            Coverage = coverage
        };
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
        Assert.True(health.Actionable);
    }

    private static SyncEngine RestartEngine(SqliteStore store)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var models = new ModelNormalizer();
        var clock = new MutableClock(Now);
        return new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
    }

    private static (SyncEngine Engine, SqliteStore Store, FixtureChatGptProvider Fixture, IncrementalSyncTests.CountingProvider Provider, AppSettings Settings, List<ConversationIndexItem> Items) CreateHarness(int count, MutableClock? clock = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "schema.db"));
        var models = new ModelNormalizer();
        clock ??= new MutableClock(Now);
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var after = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        var items = new List<ConversationIndexItem>();
        for (var i = 0; i < count; i++)
        {
            var item = new ConversationIndexItem
            {
                Id = "conv-schema-" + i,
                UpdateTime = after - i,
                CreateTime = after - 20 - i
            };
            items.Add(item);
            fixture.AddConversation(item, UniquePro(item.Id, item.UpdateTime));
        }

        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, store, fixture, new IncrementalSyncTests.CountingProvider(fixture), settings, items);
    }

    private static ConversationLoadResult SchemaLoad() => new()
    {
        Complete = false,
        SchemaMismatch = true,
        FailureKind = ConversationLoadFailureKind.SchemaMismatch,
        Diagnostics = ["mapping incomplete"]
    };

    private static ConversationLoadResult TimeoutLoad() => new()
    {
        Complete = false,
        FailureKind = ConversationLoadFailureKind.Timeout,
        Diagnostics =
        [
            "paginated-head timed out",
            "full-mapping unavailable status=403",
            "legacy-mapping timed out"
        ]
    };

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

    private sealed class ThrowingAccountProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        private readonly Exception _error;

        public ThrowingAccountProvider(FixtureChatGptProvider inner, Exception error)
        {
            _inner = inner;
            _error = error;
        }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            throw _error;

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            _inner.GetModelCatalogAsync(cancellationToken);

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            _inner.GetProjectsAsync(cancellationToken);

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            _inner.GetConversationMessagesAsync(conversationId, cancellationToken);

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }

    private sealed class IndexMismatchProvider : IChatGptProvider
    {
        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountStatus { IsSignedIn = true, Email = "x@example.com" });

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult { SchemaMismatch = true, Incomplete = true });

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectListResult());

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConversationDetailLoader.FromFixture(null));

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuotaMetadataSet());
    }
}
