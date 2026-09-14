using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("CycleArc.Tests")]

namespace CycleArc.Codex;

internal interface ICodexSnapshotFileSystem
{
    void CreateDirectory(string path);
    bool Exists(string path);
    bool TryReadAllText(string path, int maxBytes, out string contents);
    void WriteAndFlush(string path, string contents);
    void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName);
    void Move(string sourceFileName, string destinationFileName, bool overwrite);
    void Delete(string path);
}

internal sealed class LocalCodexSnapshotFileSystem : ICodexSnapshotFileSystem
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool Exists(string path) => File.Exists(path);

    public bool TryReadAllText(string path, int maxBytes, out string contents)
    {
        contents = "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > maxBytes)
            {
                return false;
            }

            // The length can change after opening if another process writes in place. Read at
            // most one byte beyond the limit so a growing file cannot allocate unbounded data.
            var bytes = new byte[maxBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            if (count == 0 || count > maxBytes)
            {
                return false;
            }

            contents = StrictUtf8.GetString(bytes, 0, count);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    public void WriteAndFlush(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        var bytes = StrictUtf8.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName) =>
        File.Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors: true);

    public void Move(string sourceFileName, string destinationFileName, bool overwrite) =>
        File.Move(sourceFileName, destinationFileName, overwrite);

    public void Delete(string path) => File.Delete(path);
}

public sealed class CodexSnapshotStore
{
    public const int CurrentVersion = 1;
    public const int MaxSerializedBytes = 64 * 1024;
    private const int MaxWindows = 128;
    private const int MaxResetCreditExpirations = 128;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly ICodexSnapshotFileSystem _files;

    public string StoragePath => _path;
    public string BackupPath => _path + ".bak";
    public bool RecoveredFromBackup { get; private set; }

    public CodexSnapshotStore(string? path = null)
        : this(path, new LocalCodexSnapshotFileSystem())
    {
    }

    internal CodexSnapshotStore(string? path, ICodexSnapshotFileSystem files)
    {
        _path = path ?? Services.AppPaths.CodexSnapshot;
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public CodexQuotaSnapshot? Load()
    {
        RecoveredFromBackup = false;
        if (TryRead(_path, out var snapshot))
        {
            return snapshot;
        }

        if (TryRead(BackupPath, out snapshot))
        {
            RecoveredFromBackup = true;
            return snapshot;
        }

        return null;
    }

    public void Save(CodexQuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var dto = PersistedSnapshot.From(snapshot);
        if (!dto.IsValid(out _))
        {
            throw new InvalidDataException("Invalid Codex snapshot.");
        }

        var json = JsonSerializer.Serialize(dto, Options);
        if (ContainsForbiddenPayload(json))
        {
            throw new InvalidOperationException("Refusing to persist a Codex snapshot that contains secrets.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxSerializedBytes)
        {
            throw new InvalidDataException("Codex snapshot is too large.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".";
        _files.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // A flushed same-directory temporary file makes the replacement atomic and leaves
            // the previous primary untouched if serialization or the write fails.
            _files.WriteAndFlush(temporary, json);
            if (TryRead(_path, out _))
            {
                _files.Replace(temporary, _path, BackupPath);
            }
            else
            {
                // A damaged/missing primary must not replace a known-good backup. Move the
                // new complete file over it and leave the backup in place for recovery.
                _files.Move(temporary, _path, overwrite: true);
            }
        }
        finally
        {
            try { if (_files.Exists(temporary)) _files.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

    private bool TryRead(string path, out CodexQuotaSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (!_files.Exists(path))
            {
                return false;
            }

            if (!_files.TryReadAllText(path, MaxSerializedBytes, out var json)
                || string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxSerializedBytes
                || ContainsForbiddenPayload(json))
            {
                return false;
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != CurrentVersion
                || !document.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("windows", out var windows)
                || windows.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var dto = JsonSerializer.Deserialize<PersistedSnapshot>(json, Options);
            if (dto is null || !dto.IsValid(out _))
            {
                return false;
            }

            snapshot = dto.ToSnapshot();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or InvalidOperationException or NotSupportedException
            or ArgumentException or OverflowException)
        {
            return false;
        }
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
        public List<PersistedWindow>? Windows { get; set; } = [];
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
            Windows = snapshot.Windows?.Select(PersistedWindow.From).ToList(),
            TechnicalDetail = snapshot.TechnicalDetail,
            IdentityFingerprint = snapshot.IdentityFingerprint
        };

        public bool IsValid(out CodexQuotaStatus status)
        {
            if (Version != CurrentVersion
                || !Enum.TryParse(Status, ignoreCase: false, out status)
                || !Enum.IsDefined(status)
                || Windows is null || Windows.Count > MaxWindows
                || ResetCreditsAvailable is < 0
                || ResetCreditExpirations is { Count: > MaxResetCreditExpirations })
            {
                status = default;
                return false;
            }

            if (IdentityFingerprint is not null && !ValidFingerprint(IdentityFingerprint))
            {
                return false;
            }

            if (Windows.Any(window => window is null || !window.IsValid()))
            {
                return false;
            }

            return true;
        }

        public CodexQuotaSnapshot ToSnapshot()
        {
            Enum.TryParse<CodexQuotaStatus>(Status, ignoreCase: false, out var status);
            return new CodexQuotaSnapshot(
                status,
                PlanType,
                LastSuccessfulRefresh,
                LastAttemptedRefresh,
                OrdinaryUsageAllowed,
                RateLimitReachedType,
                ResetCreditsAvailable,
                Windows!.Select(window => window.ToWindow()).ToList(),
                TechnicalDetail,
                ResetCreditExpirations) { IdentityFingerprint = IdentityFingerprint };
        }

        private static bool ValidFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
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

        public bool IsValid()
        {
            if (Kind is null || !Enum.TryParse<CodexWindowKind>(Kind, ignoreCase: false, out var kind)
                || !Enum.IsDefined(kind)
                || WindowDurationMinutes is <= 0
                || UsedPercent is { } used && (!double.IsFinite(used) || used is < 0 or > 100))
            {
                return false;
            }

            return LimitId is null || LimitId.Length <= 256;
        }

        public CodexQuotaWindow ToWindow()
        {
            Enum.TryParse<CodexWindowKind>(Kind, ignoreCase: false, out var kind);

            return new CodexQuotaWindow(LimitId, UsedPercent, WindowDurationMinutes, ResetsAt, kind);
        }
    }
}
