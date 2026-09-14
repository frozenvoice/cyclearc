using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class PrimaryIndexSchemaSignalTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 6, 5, 20, 14, TimeSpan.Zero);
    // Keep the scan and presentation inside the fixed fixture's quota period.
    private static readonly DateTimeOffset Now = T.AddHours(12);

    [Fact]
    public async Task A_NormalIndexSchemaMismatch_SetsPrimarySignal()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            Normal = () => new ConversationIndexResult { SchemaMismatch = true, Incomplete = true }
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.False(engine.ConversationSchemaSystemicFailureLatched);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.NormalIndexState);
        AssertActionableSyncFailure(engine, store, settings);
    }

    [Fact]
    public async Task B_ArchivedIndexSchemaMismatch_SetsPrimarySignal()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            Archived = () => new ConversationIndexResult { SchemaMismatch = true, Incomplete = true }
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.ArchivedIndexState);
        Assert.Equal(CollectionState.Complete, engine.LastCoverage.NormalIndexState);
        AssertActionableSyncFailure(engine, store, settings);
    }

    [Fact]
    public async Task C_ProjectsCollectionSchemaMismatch_SetsPrimarySignal()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            ProjectList = () => new ProjectListResult { SchemaMismatch = true, Incomplete = true }
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.ProjectsIndexState);
        Assert.False(engine.LastCoverage.Projects);
        AssertActionableSyncFailure(engine, store, settings);
    }

    [Fact]
    public async Task D_ProjectConversationIndexSchemaMismatch_SetsPrimarySignal()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            ProjectList = () => new ProjectListResult { Projects = [new ProjectInfo { Id = "proj-a" }] },
            ProjectIndex = _ => new ConversationIndexResult { SchemaMismatch = true, Incomplete = true }
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.ProjectsIndexState);
        Assert.Equal(CollectionState.Complete, engine.LastCoverage.NormalIndexState);
        AssertActionableSyncFailure(engine, store, settings);
    }

    [Fact]
    public async Task E_ArchivedGenericFailure_IsNotPrimarySchemaMismatch()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            Archived = () => throw new InvalidOperationException("archived index unavailable")
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.ArchivedIndexState);
        Assert.False(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        var snapshot = Snapshot(engine, store, settings);
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot).Kind);
    }

    [Fact]
    public async Task F_ProjectsGenericFailure_IsNotPrimarySchemaMismatch()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        var provider = new IndexScriptProvider(fixture)
        {
            ProjectList = () => throw new InvalidOperationException("projects unavailable")
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.Equal(CollectionState.Failed, engine.LastCoverage.ProjectsIndexState);
        Assert.False(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        var snapshot = Snapshot(engine, store, settings);
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot).Kind);
    }

    [Fact]
    public async Task G_IsolatedConversationBodySchemaMismatch_IsNotPrimarySchemaMismatch()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(new IndexScriptProvider(fixture), settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(Now, engine.LastSyncCompleted);
        Assert.Equal(1, engine.LastBodyFetches);
        Assert.False(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        var snapshot = Snapshot(engine, store, settings);
        Assert.True(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot).Kind);
    }

    [Fact]
    public async Task H_SystemicConversationBodyBreak_IsNotPrimarySchemaMismatch()
    {
        var (engine, store, fixture, settings) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(new IndexScriptProvider(fixture), settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.True(engine.ConversationSchemaSystemicFailureLatched);
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public async Task I_PrimaryIndexSchemaPlusConversationFailure_StaysActionable()
    {
        var (engine, store, fixture, settings) = CreateHarness();
        fixture.LoadOverride = _ => SchemaLoad();
        var provider = new IndexScriptProvider(fixture)
        {
            ProjectList = () => new ProjectListResult { Projects = [new ProjectInfo { Id = "proj-a" }] },
            ProjectIndex = _ => new ConversationIndexResult { SchemaMismatch = true, Incomplete = true }
        };
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.PrimaryIndexSchemaMismatch);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(CollectionState.Complete, engine.LastCoverage.NormalIndexState);
        var snapshot = Snapshot(engine, store, settings);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.True(health.Actionable);
        Assert.NotEqual(UiText.DataUsable, health.DataStatusText);
    }

    private static void AssertActionableSyncFailure(SyncEngine engine, SqliteStore store, AppSettings settings)
    {
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.True(health.Actionable);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    private static (SyncEngine Engine, SqliteStore Store, FixtureChatGptProvider Fixture, AppSettings Settings) CreateHarness(int count = 1)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "index-schema.db"));
        var models = new ModelNormalizer();
        var clock = new MutableClock(Now);
        var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var after = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        for (var i = 0; i < count; i++)
        {
            var item = new ConversationIndexItem
            {
                Id = "conv-index-" + i,
                UpdateTime = after - i,
                CreateTime = after - 20 - i
            };
            fixture.AddConversation(item, UniquePro(item.Id, item.UpdateTime));
        }

        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, store, fixture, settings);
    }

    private static ConversationLoadResult SchemaLoad() => new()
    {
        Complete = false,
        SchemaMismatch = true,
        FailureKind = ConversationLoadFailureKind.SchemaMismatch,
        Diagnostics = ["mapping incomplete"]
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

    private sealed class IndexScriptProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;

        public IndexScriptProvider(FixtureChatGptProvider inner) => _inner = inner;

        public Func<ConversationIndexResult>? Normal { get; init; }
        public Func<ConversationIndexResult>? Archived { get; init; }
        public Func<ProjectListResult>? ProjectList { get; init; }
        public Func<string, ConversationIndexResult>? ProjectIndex { get; init; }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            _inner.GetAccountStatusAsync(cancellationToken);

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            _inner.GetModelCatalogAsync(cancellationToken);

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default)
        {
            if (archived)
            {
                return GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
            }

            return Normal is null
                ? _inner.GetConversationIndexAsync(false, minUpdateTime, cancellationToken)
                : Task.FromResult(Normal());
        }

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Archived is null
                ? _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken)
                : Task.FromResult(Archived());

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            ProjectList is null
                ? _inner.GetProjectsAsync(cancellationToken)
                : Task.FromResult(ProjectList());

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            ProjectIndex is null
                ? _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken)
                : Task.FromResult(ProjectIndex(projectId));

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            _inner.GetConversationMessagesAsync(conversationId, cancellationToken);

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }
}
