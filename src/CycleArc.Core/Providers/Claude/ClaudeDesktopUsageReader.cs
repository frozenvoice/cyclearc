using System.Text.Json;

namespace CycleArc.Providers.Claude;

/// <summary>One usage-history sample written by Claude Desktop.</summary>
public sealed record ClaudeDesktopUsageSample(DateTimeOffset ObservedAt, double? FiveHour, double? SevenDay);

/// <summary>Result of reading Claude Desktop's local usage-history inbox.</summary>
public sealed record ClaudeDesktopUsageRead(ClaudeDesktopUsageSample? Sample, bool Unavailable = false);

/// <summary>
/// Reads the structured usage history that Claude Desktop keeps locally. This is a
/// source reader only: it does not infer reset times and does not retain any raw
/// Claude data.
/// </summary>
public sealed class ClaudeDesktopUsageReader
{
    public const int MaxInputBytes = 1024 * 1024;
    public const int MaxSamples = 4096;
    public const string FileName = "plan-usage-history.json";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = 16,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly IReadOnlyList<string> _paths;

    public ClaudeDesktopUsageReader(IEnumerable<string>? paths = null)
    {
        var candidates = paths ?? DiscoverDefaultPaths();
        _paths = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { return null; }
            })
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Reads all configured candidate files and returns the newest usable sample.
    /// A missing candidate is ordinary absence; an unreadable or partially written
    /// candidate is reported as unavailable so a caller can preserve its last-good
    /// receipt instead of treating the failed read as fresh data.
    /// </summary>
    public ClaudeDesktopUsageRead Read(string organizationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(organizationId)) return new(null, true);

        ClaudeDesktopUsageSample? newest = null;
        var unavailable = false;
        foreach (var path in _paths)
        {
            var read = ReadFile(path, organizationId, now);
            if (read.Sample is { } sample && (newest is null || sample.ObservedAt > newest.ObservedAt))
                newest = sample;
            unavailable |= read.Unavailable;
        }

        return new(newest, unavailable);
    }

    /// <summary>Parses one bounded Claude Desktop history document.</summary>
    public static ClaudeDesktopUsageRead Parse(ReadOnlyMemory<byte> input, string organizationId, DateTimeOffset now)
    {
        if (input.Length is 0 or > MaxInputBytes || string.IsNullOrWhiteSpace(organizationId))
            return new(null, true);

        try
        {
            using var document = JsonDocument.Parse(input, JsonOptions);
            var root = document.RootElement;
            if (!IsObjectWithUniqueAllowedProperties(root, "version", "samples")
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber) || versionNumber != 2
                || !root.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array
                || samples.GetArrayLength() > MaxSamples)
                return new(null, true);

            ClaudeDesktopUsageSample? newestValid = null;
            DateTimeOffset? newestMissing = null;
            DateTimeOffset? newestMalformed = null;
            var malformedWithoutTimestamp = false;

            foreach (var candidate in samples.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    return new(newestValid, true);

                // The organization is the boundary for which sample values are
                // relevant. Other organizations are ignored entirely, as the
                // history file can contain more than one account.
                if (!IsObjectWithUniqueAllowedProperties(candidate, "t", "org", "u"))
                    return new(newestValid, true);
                if (!candidate.TryGetProperty("org", out var org)
                    || org.ValueKind != JsonValueKind.String)
                    return new(newestValid, true);
                if (!string.Equals(org.GetString(), organizationId, StringComparison.Ordinal)) continue;

                var hasTimestamp = TryReadTimestamp(candidate, out var observedAt);
                if (!hasTimestamp)
                {
                    malformedWithoutTimestamp = true;
                    continue;
                }

                if (!candidate.TryGetProperty("u", out var usage)
                    || usage.ValueKind != JsonValueKind.Object
                    || !IsObjectWithUniqueAllowedProperties(usage, "fh", "sd", "xu"))
                {
                    newestMalformed = Newer(observedAt, newestMalformed);
                    continue;
                }

                if (!TryReadWindow(usage, "fh", out var fiveHour)
                    || !TryReadWindow(usage, "sd", out var sevenDay))
                {
                    newestMalformed = Newer(observedAt, newestMalformed);
                    continue;
                }

                if (observedAt > now)
                {
                    newestMalformed = Newer(observedAt, newestMalformed);
                    continue;
                }

                // xu is deliberately ignored. It is not a supported quota
                // window and must never become a displayed or persisted value.
                if (fiveHour is null && sevenDay is null)
                {
                    newestMissing = Newer(observedAt, newestMissing);
                    continue;
                }

                var sample = new ClaudeDesktopUsageSample(observedAt, fiveHour, sevenDay);
                if (newestValid is not null && observedAt == newestValid.ObservedAt && sample != newestValid)
                {
                    newestMalformed = Newer(observedAt, newestMalformed);
                    continue;
                }
                if (newestValid is null || observedAt >= newestValid.ObservedAt) newestValid = sample;
            }

            if (malformedWithoutTimestamp)
                return new(newestValid, true);

            // A malformed/future sample at or after the last valid receipt must
            // not be silently hidden by an older valid sample. Older malformed
            // history can be ignored once a newer valid receipt supersedes it.
            if (newestMalformed is { } malformed
                && (newestValid is null || malformed >= newestValid.ObservedAt))
                return new(newestValid, true);

            // A matching sample with both supported windows absent is a missing
            // reading. It does not fabricate zero and supersedes an older sample
            // for this read, while remaining distinguishable from file failure.
            if (newestMissing is { } missing
                && (newestValid is null || missing >= newestValid.ObservedAt))
                return new(null, false);

            return new(newestValid, false);
        }
        catch (JsonException) { return new(null, true); }
        catch (InvalidOperationException) { return new(null, true); }
        catch (ArgumentException) { return new(null, true); }
    }

    private static ClaudeDesktopUsageRead ReadFile(string path, string organizationId, DateTimeOffset now)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = stream.Read(chunk, 0, chunk.Length);
                if (count == 0) break;
                if (buffer.Length + count > MaxInputBytes) return new(null, true);
                buffer.Write(chunk, 0, count);
            }

            return Parse(buffer.ToArray(), organizationId, now);
        }
        catch (FileNotFoundException) { return new(null); }
        catch (DirectoryNotFoundException) { return new(null); }
        catch (IOException) { return new(null, true); }
        catch (UnauthorizedAccessException) { return new(null, true); }
        catch (ArgumentException) { return new(null, true); }
        catch (NotSupportedException) { return new(null, true); }
    }

    private static bool TryReadTimestamp(JsonElement sample, out DateTimeOffset observedAt)
    {
        observedAt = default;
        if (!sample.TryGetProperty("t", out var timestamp)
            || timestamp.ValueKind != JsonValueKind.Number
            || !timestamp.TryGetInt64(out var milliseconds)) return false;
        try
        {
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return observedAt > DateTimeOffset.UnixEpoch;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TryReadWindow(JsonElement usage, string name, out double? value)
    {
        value = null;
        if (!usage.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var percentage)
            || !double.IsFinite(percentage) || percentage is < 0 or > 100) return false;
        value = percentage;
        return true;
    }

    private static bool IsObjectWithUniqueAllowedProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name) || !seen.Add(property.Name)) return false;
        }
        return true;
    }

    private static DateTimeOffset Newer(DateTimeOffset candidate, DateTimeOffset? current) =>
        current is null || candidate > current.Value ? candidate : current.Value;

    private static IEnumerable<string> DiscoverDefaultPaths()
    {
        var paths = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) paths.Add(Path.Combine(appData, "Claude", FileName));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return paths;
        var packages = Path.Combine(localAppData, "Packages");
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(packages, "Claude_*", SearchOption.TopDirectoryOnly))
            {
                // Keep this to the immediate MSIX app-folder shape. In
                // particular, do not recursively search a user's profile.
                if (Path.GetFileName(directory).StartsWith("Claude_", StringComparison.Ordinal))
                    paths.Add(Path.Combine(directory, "LocalCache", "Roaming", "Claude", FileName));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }
}
