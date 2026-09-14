using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CycleArc.Providers.Usage;

namespace CycleArc.Codex;

// Only local references and user-chosen labels are persisted. Codex owns credentials.
public sealed record CodexAccountProfile(string Id, string HomePath, string Label, bool IsManaged = false)
{
    // Missing in legacy registries, which always contain Codex accounts.
    public UsageProviderId Provider { get; init; } = UsageProviderId.Codex;
}

public sealed record CodexAccountView(CodexAccountProfile Profile, CodexQuotaSnapshot Snapshot,
    string? Email = null, bool IsSigningIn = false, bool HasMatchingIdentity = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Profile.Label)
        ? Email ?? (Profile.Id == CodexAccountStore.LegacyProfileId ? Services.UiText.T("Existing Codex", "기존 Codex")
            : Profile.Provider.Name() + " · " + Profile.Id[..Math.Min(6, Profile.Id.Length)]) : Profile.Label;
    public string ProviderName => Profile.Provider.Name();
    public bool IsConnected { get; init; }
    public bool IsAwaitingUsage => IsConnected && Profile.Provider == UsageProviderId.Claude
        && Snapshot.Status == CodexQuotaStatus.Unavailable && !Snapshot.HasUsablePercentages
        && Snapshot.TechnicalDetail == "claude-connected-waiting";
}

public sealed record CodexAccountIdentity(CodexQuotaStatus Status, string? Email = null, string? PlanType = null)
{
    // A change in reported identity invalidates cached percentages, including after restart.
    // The protocol does not expose workspace identity; this is not a workspace identifier.
    public string? Fingerprint => Email is null ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Email + "\n" + PlanType)));

    // Stable account identity is normalized email only; subscription plan changes do not rebind it.
    public string? StableAccountFingerprint => Email is null ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Email.Trim().ToLowerInvariant())));

    public static CodexAccountIdentity Parse(JsonNode? response)
    {
        if (response is not JsonObject envelope || CodexProtocol.HasError(envelope)
            || envelope["result"] is not JsonObject result
            || result["requiresOpenaiAuth"] is not JsonValue auth || !auth.TryGetValue<bool>(out _)
            || !result.ContainsKey("account")) return new(CodexQuotaStatus.ProtocolMismatch);
        if (result["account"] is null) return new(CodexQuotaStatus.SignedOut);
        if (result["account"] is not JsonObject account
            || account["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
            return new(CodexQuotaStatus.ProtocolMismatch);
        if (type is "apiKey" or "amazonBedrock") return new(CodexQuotaStatus.Unavailable);
        if (type != "chatgpt" || !account.ContainsKey("email")
            || account["planType"] is not JsonValue planValue || !planValue.TryGetValue<string>(out var plan))
            return new(CodexQuotaStatus.ProtocolMismatch);
        string? email = null;
        if (account["email"] is not null
            && (account["email"] is not JsonValue emailValue || !emailValue.TryGetValue(out email)))
            return new(CodexQuotaStatus.ProtocolMismatch);
        if (email is { Length: > 320 } || email?.Any(char.IsControl) == true || plan.Length > 80)
            return new(CodexQuotaStatus.ProtocolMismatch);
        return new(CodexQuotaStatus.Available, email, plan);
    }
}

public static class CodexHomeDiscovery
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string DefaultHome => Normalize(Environment.GetEnvironmentVariable("CODEX_HOME"))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    // Bounded, known locations only. Never enumerate credentials, sessions, projects, or rollouts.
    public static IReadOnlyList<string> Candidates(IEnumerable<string>? additional = null) => Candidates(
        new[] { Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Machine),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") }
            .Concat(additional ?? []), Directory.Exists);

    public static IReadOnlyList<string> Candidates(IEnumerable<string?> paths, Func<string, bool> directoryExists) =>
        paths.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(directoryExists).ToArray();
}
