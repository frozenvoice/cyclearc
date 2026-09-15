namespace CycleArc.Models;

public enum SubscriptionPreset
{
    Pro100,
    Pro200,
    Custom
}

public enum QuotaFamily
{
    Unknown,
    Instant,
    SolReasoning,
    GptPro
}

public enum ReasoningEffort
{
    Unknown,
    None,
    Low,
    Medium,
    High,
    ExtraHigh
}

public enum UsageSource
{
    ConversationSync,
    ProjectSync,
    ArchivedSync,
    OfficialExport,
    Catalog
}

public enum SyncOrigin
{
    Startup,
    Auto,
    Manual,
    FlyoutStaleRefresh
}

public enum AppSyncStatus
{
    Idle,
    SignedOut,
    AuthenticationRequired,
    DetectingAccount,
    LoadingCatalog,
    Syncing,
    UpToDate,
    RateLimited,
    ApiChanged,
    ProviderSchemaMismatch,
    PartialData,
    Offline,
    Error,
    Forbidden,
    ChatGptTabRequired,
    PageBridgeUnavailable,
    CompanionDisconnected,
    BridgeTimeout,
    BridgeWriteFailed
}

public enum CoverageConfidence
{
    Authoritative,
    HighConfidence,
    Estimated,
    Incomplete
}

public enum DedupeConfidence
{
    High,
    Heuristic
}

public enum TimestampProvenance
{
    Unspecified,
    ResponseFragment,
    RequestStart,
    LinkedUserThisInvocation,
    Unknown,
    LegacyUnverified
}

public enum ModelEvidenceConfidence
{
    Unspecified,
    ObservedResponse,
    RequestedOnly,
    Catalog,
    Conflicting,
    Unknown
}

public enum UnresolvedEvidenceKind
{
    None,
    Identity,
    Model,
    Timestamp,
    Period,
    DuplicateCluster,
    LegacyUnverified
}

public enum BodyFetchReason
{
    None,
    Force,
    NewConversation,
    ParserCompatibilityRetry,
    RemoteChanged,
    UnknownRemoteTime,
    FailedRetry,
    ReconstructionRevalidation
}

public enum QuotaWindowKind
{
    Unclassified,
    SharedProWeekly,
    Gpt6ProWeekly,
    SolProDaily,
    CombinedProDaily
}

public enum ResetAnchorSource
{
    Default,
    UserConfigured,
    Server,
    RetainedServer
}

public enum ProRestrictionState
{
    Unknown,
    NoCorrelatedRestrictionObserved,
    CorrelatedRestriction
}

public enum ServerResetConfidence
{
    None,
    Server,
    Ambiguous
}

public enum TrayIconStyle
{
    RemainingNumber,
    ProgressRing
}

public enum UsagePeriodPreference
{
    Auto,
    FiveHour,
    Weekly
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum DisplayMode
{
    TrayOnly,
    TrayAndWidget
}

public enum UiLanguage
{
    English,
    Korean
}

public enum CollectionState
{
    Unavailable,
    Failed,
    Partial,
    Complete,
    Estimated
}
