using CycleArc.Codex;

namespace CycleArc.Providers.Cursor;

/// <summary>The legacy-plan pool a model draws from, as Cursor's plan documentation states it.</summary>
public enum CursorUsagePool
{
    /// <summary>No documented pool for this model, or no model was reported.</summary>
    Unknown,
    /// <summary>Auto or a router: the name does not say which model, and so which pool, served the request.</summary>
    Routed,
    /// <summary><c>cursor-auto</c>: Cursor's own models (Grok 4.5 to 4.7, Composer).</summary>
    CursorModels,
    /// <summary><c>cursor-api</c>: third-party models such as Claude, GPT and Gemini.</summary>
    OtherModels
}

/// <summary>
/// Maps a hook's model fields to the documented legacy-plan pool. Only model families the
/// official plan and pricing pages place in a pool are classified; everything else is Unknown.
/// Grok Bot's weekly allowance (<c>cursor-sand</c>) is never a target: no editor model draws
/// from it.
/// </summary>
public static class CursorModelPools
{
    // Providers listed under "Third-party models" on cursor.com/docs/models-and-pricing.
    private static readonly HashSet<string> ThirdPartyFamilies = new(StringComparer.Ordinal)
    {
        "claude", "gpt", "gemini", "glm", "kimi", "muse"
    };

    // Grok versions listed as Cursor models. Other Grok ids (grok-code-fast-1, grok-4) are older
    // models whose pool the current pages do not state.
    private static readonly HashSet<string> CursorGrokVersions = new(StringComparer.Ordinal) { "4.5", "4.6", "4.7" };

    /// <summary>The structured <c>model_id</c> decides when Cursor supplied it; otherwise the legacy <c>model</c> slug.</summary>
    public static CursorUsagePool Classify(string? modelId, string? model) =>
        Classify(string.IsNullOrWhiteSpace(modelId) ? model : modelId);

    public static CursorUsagePool Classify(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return CursorUsagePool.Unknown;
        var tokens = model.Trim().ToLowerInvariant()
            .Split(['-', '_', ' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return CursorUsagePool.Unknown;
        if (tokens[0] is "auto" or "default" or "router"
            || tokens is ["cursor", "auto" or "router", ..])
            return CursorUsagePool.Routed;
        if (ThirdPartyFamilies.Contains(tokens[0])) return CursorUsagePool.OtherModels;
        var own = tokens[0] == "cursor" ? tokens[1..] : tokens;
        if (own.Length == 0) return CursorUsagePool.Unknown;
        if (own[0] == "composer") return CursorUsagePool.CursorModels;
        if (own[0] == "grok" && own.Length > 1
            && (CursorGrokVersions.Contains(own[1])
                || own.Length > 2 && CursorGrokVersions.Contains(own[1] + "." + own[2])))
            return CursorUsagePool.CursorModels;
        return CursorUsagePool.Unknown;
    }

    public static string? LimitKey(CursorUsagePool pool) => pool switch
    {
        CursorUsagePool.CursorModels => "auto",
        CursorUsagePool.OtherModels => "api",
        _ => null
    };

    public static string? LimitId(CursorUsagePool pool) => pool switch
    {
        CursorUsagePool.CursorModels => "cursor-auto",
        CursorUsagePool.OtherModels => "cursor-api",
        _ => null
    };
}

public enum CursorRingLinkState
{
    /// <summary>Integration off or disconnected, account not verified, or nothing received yet.</summary>
    NoReceipt,
    /// <summary>The ring shows the pool of the latest request's model.</summary>
    Linked,
    /// <summary>Auto or a router: the representative limit stays.</summary>
    Routed,
    /// <summary>Model missing or without a documented pool.</summary>
    Unconfirmed,
    /// <summary>The account reported no usable value for that pool, or it is off.</summary>
    PoolUnavailable,
    /// <summary>The receipt is too old (or from the future) to stand for the current model.</summary>
    Expired
}

/// <summary>
/// How the latest request relates to the ring. <see cref="Window"/> is set only when
/// <see cref="State"/> is <see cref="CursorRingLinkState.Linked"/> and is the snapshot's own
/// window instance, so surfaces compare it by reference.
/// </summary>
public sealed record CursorRingLink(CursorRingLinkState State, CursorUsagePool Pool,
    CursorRecentActivity? Activity, CodexQuotaWindow? Window)
{
    public static CursorRingLink None { get; } = new(CursorRingLinkState.NoReceipt, CursorUsagePool.Unknown, null, null);
}

/// <summary>
/// The one rule the popup, widget and tray use to pick Cursor's representative ring. A verified,
/// recent hook receipt whose model has a documented pool selects that pool's already received
/// window. Every other case returns no window and each surface keeps its existing choice. The
/// quota values, their order and the snapshot are never changed, and nothing is requested.
/// The pool is what the plan documents for a selected model; it does not confirm billing.
/// </summary>
public static class CursorRingPolicy
{
    /// <summary>A composer model older than this is not taken as the one in use now.</summary>
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(12);

    /// <summary>Receipt times use the local clock; a small step back is tolerated, a future receipt is not.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static CursorRingLink Resolve(CodexQuotaSnapshot snapshot, CursorActivityView? view, DateTimeOffset now)
    {
        if (!CursorUsagePresentation.IsCursor(snapshot)
            || view is not { Integration: CursorActivityIntegration.Connected, AccountVerified: true, Latest: { } latest }
            || WidgetStatusPresentation.HidesQuota(snapshot))
            return CursorRingLink.None;
        var pool = CursorModelPools.Classify(latest.ModelId, latest.Model);
        if (latest.ReceivedAt > now + ClockTolerance || now - latest.ReceivedAt > ReceiptLifetime)
            return new(CursorRingLinkState.Expired, pool, latest, null);
        if (pool == CursorUsagePool.Routed) return new(CursorRingLinkState.Routed, pool, latest, null);
        if (CursorModelPools.LimitKey(pool) is not { } key) return new(CursorRingLinkState.Unconfirmed, pool, latest, null);
        var window = snapshot.Status is CodexQuotaStatus.Available or CodexQuotaStatus.Stale or CodexQuotaStatus.Refreshing
            ? snapshot.Windows.FirstOrDefault(candidate => candidate.IsEnabled != false
                && CursorUsagePresentation.AllowanceKey(candidate.LimitId) == key)
            : null;
        return window?.UsedPercent is double used && double.IsFinite(used)
            ? new(CursorRingLinkState.Linked, pool, latest, window)
            : new(CursorRingLinkState.PoolUnavailable, pool, latest, null);
    }

    /// <summary>When a linked receipt stops counting, so a display without other changes can rebind then.</summary>
    public static DateTimeOffset? LinkExpiresAt(CursorActivityView? view) =>
        view?.Latest is { } latest ? latest.ReceivedAt + ReceiptLifetime : null;
}
