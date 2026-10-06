using CycleArc.Codex;

namespace CycleArc.Providers.Usage;

/// <summary>Adapts the existing Codex service without changing its protocol or caches.</summary>
public sealed class CodexUsageProvider(
    Func<CodexAccountProfile, CodexQuotaService> create, Func<string?> configuredPath) : IUsageProvider
{
    public UsageProviderId Id => UsageProviderId.Codex;

    public IUsageAccountService Create(CodexAccountProfile profile)
    {
        if (profile.Provider != Id) throw new ArgumentException("Wrong usage provider.");
        return new AccountService(create(profile), configuredPath);
    }

    private sealed class AccountService(CodexQuotaService service, Func<string?> configuredPath)
        : IUsageAccountService, ICodexAccountOperations
    {
        public CodexQuotaSnapshot Snapshot => service.Snapshot;
        public string? Email => service.Identity?.Email;
        public string? IdentityFingerprint => service.ValidatedIdentityFingerprint;
        public bool IsRefreshing => service.IsRefreshing;
        public bool ReceivesPassiveUpdates => false;
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) =>
            CodexQuotaService.ShouldRefreshOnFlyoutOpen(Snapshot, now, interval);
        public event Action<CodexQuotaSnapshot>? Changed
        {
            add => service.Changed += value;
            remove => service.Changed -= value;
        }
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token) => service.RefreshAsync(configuredPath(), token);
        public Task<CodexAccountIdentity> ProbeAccountAsync(CancellationToken token) => service.ProbeAccountAsync(configuredPath(), token);
        public Task<CodexLoginResult> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token) =>
            service.LoginAsync(configuredPath(), openBrowser, token);
        public Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, CancellationToken token) =>
            service.ConsumeCreditAsync(creditId, configuredPath(), token);
    }
}
