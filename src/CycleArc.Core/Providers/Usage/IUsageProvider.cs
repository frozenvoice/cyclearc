using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Usage;

[JsonConverter(typeof(JsonStringEnumConverter<UsageProviderId>))]
public enum UsageProviderId { Codex, Claude }

public static class UsageProviders
{
    public static string Name(this UsageProviderId provider) => provider switch
    {
        UsageProviderId.Codex => "Codex",
        UsageProviderId.Claude => "Claude",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

/// <summary>Creates isolated account services; authentication remains provider-owned.</summary>
public interface IUsageProvider
{
    UsageProviderId Id { get; }
    IUsageAccountService Create(CodexAccountProfile profile);
}

// The existing snapshot/profile type names remain for cache and caller compatibility.
// They are the shared presentation model, not a requirement to use the Codex transport.
public interface IUsageAccountService
{
    CodexQuotaSnapshot Snapshot { get; }
    string? Email { get; }
    string? IdentityFingerprint { get; }
    bool IsConnected => Snapshot.HasUsablePercentages
        && Snapshot.Status is not (CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound);
    bool IsRefreshing { get; }
    bool ReceivesPassiveUpdates { get; }
    bool ShouldRefresh(DateTimeOffset now, TimeSpan interval);
    event Action<CodexQuotaSnapshot>? Changed;
    Task<CodexRefreshResult> RefreshAsync(CancellationToken token);
}

/// <summary>
/// Optional active-refresh capability. Passive providers keep <see cref="IUsageAccountService.RefreshAsync"/>
/// local-only; an implementation exposes its explicitly requested live/remote read here.
/// </summary>
public interface ILiveUsageAccountService
{
    Task<CodexRefreshResult> RefreshLiveAsync(CancellationToken token);
}

/// <summary>Optional capabilities, unavailable to the Claude statusLine provider.</summary>
public interface ICodexAccountOperations
{
    Task<CodexAccountIdentity> ProbeAccountAsync(CancellationToken token);
    Task<CodexLoginResult> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token);
    Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, CancellationToken token);
}
