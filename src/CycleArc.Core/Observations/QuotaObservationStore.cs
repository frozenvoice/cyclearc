using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Services;

namespace CycleArc.Observations;

internal interface IQuotaObservationFiles
{
    byte[]? ReadBounded(string path, int maxBytes);
    void CreateDirectory(string path);
    bool Exists(string path);
    void WriteAndFlush(string path, byte[] bytes);
    void Replace(string temporary, string path, string backup);
    void Move(string temporary, string path);
    void Delete(string path);
}

internal sealed class LocalQuotaObservationFiles : IQuotaObservationFiles
{
    public byte[]? ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 || stream.Length > maxBytes) return null;
        // Allocate the actual observed length, not the 8 MiB maximum on every tiny load.
        // Reject growth/truncation during the read rather than trusting an unstable file.
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) return null;
        return bytes;
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool Exists(string path) => File.Exists(path);
    public void WriteAndFlush(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    public void Replace(string temporary, string path, string backup) => File.Replace(temporary, path, backup, true);
    public void Move(string temporary, string path) => File.Move(temporary, path, true);
    public void Delete(string path) => File.Delete(path);
}

/// <summary>Separate optional history file. Nothing here touches account, settings or quota caches.</summary>
public sealed class QuotaObservationStore
{
    public const int CurrentVersion = 1;
    public const int MaxSerializedBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 20,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _path;
    private readonly QuotaObservationContext _context;
    private readonly IClock _clock;
    private readonly IQuotaObservationFiles _files;

    public QuotaObservationStore(string path, QuotaObservationContext context, IClock? clock = null)
        : this(path, context, clock, new LocalQuotaObservationFiles()) { }

    internal QuotaObservationStore(string path, QuotaObservationContext context, IClock? clock, IQuotaObservationFiles files)
    {
        if (!QuotaObservationHistory.ValidContext(context)) throw new ArgumentException("Invalid observation context.", nameof(context));
        _path = Path.GetFullPath(path);
        _context = context;
        _clock = clock ?? SystemClock.Instance;
        _files = files;
    }

    public string StoragePath => _path;
    public string BackupPath => _path + ".bak";
    public bool RecoveredFromBackup { get; private set; }
    public bool Unavailable { get; private set; }

    public QuotaObservationHistorySnapshot? Load()
    {
        RecoveredFromBackup = false;
        Unavailable = false;
        if (TryRead(_path, out var snapshot, out var primaryMismatch)) return TrimExpired(snapshot!);
        if (TryRead(BackupPath, out snapshot, out var backupMismatch))
        { RecoveredFromBackup = true; Unavailable = true; return TrimExpired(snapshot!); }
        var primaryExists = _files.Exists(_path);
        Unavailable = primaryExists && !primaryMismatch
            || !primaryExists && _files.Exists(BackupPath) && !backupMismatch;
        return null;
    }

    public void Save(QuotaObservationHistorySnapshot snapshot)
    {
        if (!QuotaObservationHistory.ValidSnapshot(snapshot, _context, _clock.UtcNow, enforceRetention: true))
            throw new InvalidDataException("Invalid quota observation history.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(CurrentVersion, snapshot), Options);
        if (bytes.Length > MaxSerializedBytes) throw new InvalidDataException("Quota observation history is too large.");
        _files.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _files.WriteAndFlush(temporary, bytes);
            if (TryRead(_path, out var primary, out _)
                && QuotaObservationHistory.ValidSnapshot(primary!, _context, _clock.UtcNow, enforceRetention: true))
                _files.Replace(temporary, _path, BackupPath);
            else _files.Move(temporary, _path); // Never replace a valid backup with a damaged primary.
            // A successful new primary must not keep an expired observation payload alive
            // solely through its backup. Cleanup occurs after the atomic commit.
            if (TryRead(BackupPath, out var backup, out _)
                && !QuotaObservationHistory.ValidSnapshot(backup!, _context, _clock.UtcNow, enforceRetention: true))
                _files.Delete(BackupPath);
            Unavailable = false;
        }
        finally
        {
            try { if (_files.Exists(temporary)) _files.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool TryRead(string path, out QuotaObservationHistorySnapshot? snapshot, out bool contextMismatch)
    {
        snapshot = null;
        contextMismatch = false;
        try
        {
            var bytes = _files.ReadBounded(path, MaxSerializedBytes);
            if (bytes is null || bytes.Length is 0 or > MaxSerializedBytes) return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 20 });
            if (!UniqueProperties(document.RootElement)) return false;
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options);
            if (envelope is null || envelope.Version != CurrentVersion || envelope.Snapshot is null
                || !QuotaObservationHistory.ValidSnapshot(envelope.Snapshot, envelope.Snapshot.Context, _clock.UtcNow)) return false;
            if (envelope.Snapshot.Context != _context) { contextMismatch = true; return false; }
            snapshot = envelope.Snapshot;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        { return false; }
    }

    private QuotaObservationHistorySnapshot TrimExpired(QuotaObservationHistorySnapshot snapshot)
    {
        var cutoff = _clock.UtcNow - QuotaObservationHistory.Retention;
        var series = snapshot.Series.Select(s => s with
            { Points = s.Points.Where(p => p.ObservedAt >= cutoff).ToImmutableArray() })
            .Where(s => !s.Points.IsEmpty || s.Watermark >= cutoff).ToImmutableArray();
        return snapshot with { Series = series };
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!UniqueProperties(child)) return false;
        return true;
    }

    private sealed record Envelope(int Version, QuotaObservationHistorySnapshot Snapshot);
}
