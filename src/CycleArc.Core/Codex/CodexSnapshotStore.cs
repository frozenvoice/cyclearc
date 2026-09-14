using System.Text.Json;
using System.Text.Json.Serialization;

namespace CycleArc.Codex;

public sealed class CodexSnapshotStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _path;

    public string StoragePath => _path;

    public CodexSnapshotStore(string? path = null)
    {
        _path = path ?? Services.AppPaths.CodexSnapshot;
    }

    public CodexQuotaSnapshot? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (ContainsForbiddenPayload(json))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<PersistedSnapshot>(json, Options);
            return dto?.ToSnapshot();
        }
        catch
        {
            return null;
        }
    }

    public void Save(CodexQuotaSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var dto = PersistedSnapshot.From(snapshot);
        var json = JsonSerializer.Serialize(dto, Options);
        if (ContainsForbiddenPayload(json))
        {
            throw new InvalidOperationException("Refusing to persist a Codex snapshot that contains secrets.");
        }

        File.WriteAllText(_path, json);
    }

    public static bool ContainsForbiddenPayload(string json)
    {
        return json.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
               || json.Contains("access_token", StringComparison.OrdinalIgnoreCase)
               || json.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)
               || json.Contains("\"cookie\"", StringComparison.OrdinalIgnoreCase)
               || json.Contains("@")
               || json.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
               || json.Contains("accountId", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PersistedSnapshot
    {
        public int Version { get; set; } = CurrentVersion;
        public string Status { get; set; } = nameof(CodexQuotaStatus.Unavailable);
        public string? PlanType { get; set; }
        public DateTimeOffset? LastSuccessfulRefresh { get; set; }
        public DateTimeOffset? LastAttemptedRefresh { get; set; }
        public bool? OrdinaryUsageAllowed { get; set; }
        public string? RateLimitReachedType { get; set; }
        public int? ResetCreditsAvailable { get; set; }
        public List<DateTimeOffset?>? ResetCreditExpirations { get; set; }
        public List<PersistedWindow> Windows { get; set; } = [];
        public string? TechnicalDetail { get; set; }
        public string? IdentityFingerprint { get; set; }

        public static PersistedSnapshot From(CodexQuotaSnapshot snapshot) => new()
        {
            Status = snapshot.Status.ToString(),
            PlanType = snapshot.PlanType,
            LastSuccessfulRefresh = snapshot.LastSuccessfulRefresh,
            LastAttemptedRefresh = snapshot.LastAttemptedRefresh,
            OrdinaryUsageAllowed = snapshot.OrdinaryUsageAllowed,
            RateLimitReachedType = snapshot.RateLimitReachedType,
            ResetCreditsAvailable = snapshot.ResetCreditsAvailable,
            ResetCreditExpirations = snapshot.ResetCreditExpirations?.ToList(),
            Windows = snapshot.Windows.Select(PersistedWindow.From).ToList(),
            TechnicalDetail = snapshot.TechnicalDetail,
            IdentityFingerprint = snapshot.IdentityFingerprint
        };

        public CodexQuotaSnapshot ToSnapshot()
        {
            Enum.TryParse<CodexQuotaStatus>(Status, out var status);
            return new CodexQuotaSnapshot(
                status,
                PlanType,
                LastSuccessfulRefresh,
                LastAttemptedRefresh,
                OrdinaryUsageAllowed,
                RateLimitReachedType,
                ResetCreditsAvailable,
                Windows.Select(window => window.ToWindow()).ToList(),
                TechnicalDetail,
                ResetCreditExpirations) { IdentityFingerprint = IdentityFingerprint };
        }
    }

    private sealed class PersistedWindow
    {
        public string? LimitId { get; set; }
        public double? UsedPercent { get; set; }
        public int? WindowDurationMinutes { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public string Kind { get; set; } = nameof(CodexWindowKind.Other);

        public static PersistedWindow From(CodexQuotaWindow window) => new()
        {
            LimitId = window.LimitId,
            UsedPercent = window.UsedPercent,
            WindowDurationMinutes = window.WindowDurationMinutes,
            ResetsAt = window.ResetsAt,
            Kind = window.Kind.ToString()
        };

        public CodexQuotaWindow ToWindow()
        {
            Enum.TryParse<CodexWindowKind>(Kind, out var kind);
            if (kind == default && WindowDurationMinutes is int minutes)
            {
                kind = CodexWindowClassifier.FromDurationMinutes(minutes);
            }

            return new CodexQuotaWindow(LimitId, UsedPercent, WindowDurationMinutes, ResetsAt, kind);
        }
    }
}
