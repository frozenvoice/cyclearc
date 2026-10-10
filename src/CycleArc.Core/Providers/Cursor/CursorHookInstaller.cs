using System.Diagnostics;
using System.Text;
using CycleArc.Providers.Claude;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Embedded in each CycleArc-owned Cursor hook command. The created flags record which
/// containers this entry introduced, so removal deletes only those, and only while empty.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CursorHookOptions(int Version, string Event, string CycleArcExecutable, string DataRoot,
    bool CreatedFile, bool CreatedHooks, bool CreatedEvent);

public enum CursorHookFailure
{
    CursorNotFound,
    InvalidSettings,
    SettingsChanged,
    AlreadyLinked,
    CommandTooLong
}

public sealed class CursorHookException(CursorHookFailure failure, Exception? innerException = null)
    : Exception("Cursor hook settings could not be updated.", innerException)
{
    public CursorHookFailure Failure { get; } = failure;
}

public enum CursorHookStatus
{
    NotInstalled,
    Installed,
    Partial,
    Unavailable
}

/// <summary>
/// Adds and removes CycleArc's entries in Cursor's user <c>hooks.json</c>. The file belongs to
/// the person: every other hook, property and order is preserved, an entry is ours only when it
/// decodes to exactly the command CycleArc would write, and a concurrent edit aborts the write.
/// </summary>
public static class CursorHookInstaller
{
    public static readonly IReadOnlyList<string> Events = [CursorHookEvent.SubmitEvent, CursorHookEvent.StopEvent];
    public const int TimeoutSeconds = 5;
    public const int MaxSettingsBytes = 1024 * 1024;
    private const string Prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const string Marker = "# CycleArc Cursor hook v1\n";
    private const string OptionsPrefix = "$options='";
    // Below cmd.exe's 8191-character command line, whichever shell Cursor uses on Windows.
    private const int MaxCommandLength = 8000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "hooks.json");

    public static string Payload(CursorHookOptions options) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(options));

    public static CursorHookOptions Decode(string payload)
    {
        try
        {
            if (payload.Length > 4096) throw new InvalidDataException();
            var options = JsonSerializer.Deserialize<CursorHookOptions>(Convert.FromBase64String(payload));
            if (options is null || !Valid(options)) throw new InvalidDataException();
            return options;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or ArgumentException)
        { throw new CursorHookException(CursorHookFailure.InvalidSettings, ex); }
    }

    /// <summary>
    /// An encoded PowerShell command runs the same way from cmd, PowerShell or Git Bash and quotes
    /// any installation path. Every failure exits 1, never 2: Cursor treats 2 as "block".
    /// </summary>
    public static string Command(CursorHookOptions options)
    {
        if (!Valid(options)) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = Marker + OptionsPrefix + Payload(options) + "'; "
            + "Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop; "
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; "
            + "$input | & '" + path + "' '" + CursorHookCommand.Argument + "' $options"
            + " | & { process { [Console]::Out.WriteLine($_) } }; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 }";
        var command = Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (command.Length > MaxCommandLength) throw new CursorHookException(CursorHookFailure.CommandTooLong);
        return command;
    }

    public static bool TryRead(string command, out CursorHookOptions? options)
    {
        options = null;
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > MaxCommandLength) return false;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            if (!script.StartsWith(Marker + OptionsPrefix, StringComparison.Ordinal)) return false;
            var start = Marker.Length + OptionsPrefix.Length;
            var end = script.IndexOf('\'', start);
            if (end < 0) return false;
            var parsed = Decode(script[start..end]);
            if (!string.Equals(Command(parsed), command, StringComparison.Ordinal)) return false;
            options = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CursorHookException or ArgumentException) { return false; }
    }

    public static CursorHookStatus ReadStatus(string hooksPath, string executable)
    {
        try
        {
            var settings = Parse(ReadBytes(hooksPath));
            if (settings is null) return CursorHookStatus.NotInstalled;
            var normalized = ClaudeConnectionPaths.Normalize(executable);
            var present = Events.Count(name => OwnedEntries(settings, name).Any(item =>
                string.Equals(item.Options.CycleArcExecutable, normalized, StringComparison.OrdinalIgnoreCase)));
            return present == 0 ? CursorHookStatus.NotInstalled
                : present == Events.Count ? CursorHookStatus.Installed : CursorHookStatus.Partial;
        }
        catch (Exception ex) when (ex is CursorHookException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        { return CursorHookStatus.Unavailable; }
    }

    /// <summary>
    /// Adds this executable's entry to each event. Requires Cursor's user folder to exist, never
    /// enables a file Cursor would not already load, and refuses to replace another existing
    /// CycleArc installation's entry.
    /// </summary>
    public static async Task InstallAsync(string hooksPath, string executable, string dataRoot,
        CancellationToken token, Action? beforeCommit = null)
    {
        var file = RequireFile(hooksPath);
        var exe = RequireExecutable(executable);
        var root = ClaudeConnectionPaths.Normalize(dataRoot) ?? throw new CursorHookException(CursorHookFailure.InvalidSettings);
        var directory = Path.GetDirectoryName(file)!;
        if (!Directory.Exists(directory)) throw new CursorHookException(CursorHookFailure.CursorNotFound);
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var original = ReadBytes(file);
        var createdFile = original is null;
        var settings = Parse(original) ?? new JsonObject { ["version"] = 1 };
        var existingCreatedFile = Events.SelectMany(name => OwnedEntries(settings, name))
            .Select(item => (bool?)item.Options.CreatedFile).FirstOrDefault();
        createdFile = existingCreatedFile ?? createdFile;
        var createdHooks = !settings.ContainsKey("hooks");
        var hooks = createdHooks ? new JsonObject()
            : settings["hooks"] as JsonObject ?? throw new CursorHookException(CursorHookFailure.InvalidSettings);
        if (createdHooks) settings["hooks"] = hooks;
        foreach (var name in Events)
        {
            var createdEvent = !hooks.ContainsKey(name);
            var list = createdEvent ? new JsonArray()
                : hooks[name] as JsonArray ?? throw new CursorHookException(CursorHookFailure.InvalidSettings);
            if (createdEvent) hooks[name] = list;
            var owned = OwnedEntries(settings, name).ToArray();
            if (owned.Length > 1) throw new CursorHookException(CursorHookFailure.InvalidSettings);
            var entryCreatedHooks = createdHooks;
            if (owned.Length == 1)
            {
                var existing = owned[0].Options;
                // Another installation that still exists keeps its entry; a deleted one is replaced.
                if (!string.Equals(existing.CycleArcExecutable, exe, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(existing.CycleArcExecutable))
                    throw new CursorHookException(CursorHookFailure.AlreadyLinked);
                createdEvent = existing.CreatedEvent;
                entryCreatedHooks = existing.CreatedHooks;
            }
            var entry = new JsonObject
            {
                ["command"] = Command(new CursorHookOptions(1, name, exe, root, createdFile, entryCreatedHooks, createdEvent)),
                ["timeout"] = TimeoutSeconds
            };
            if (owned.Length == 1) list[list.IndexOf(owned[0].Node)] = entry;
            else list.Add(entry);
        }
        beforeCommit?.Invoke();
        Write(file, original, settings, token);
    }

    /// <summary>Removes entries whose executable <paramref name="owns"/> accepts. Returns whether any was removed.</summary>
    public static async Task<bool> RemoveAsync(string hooksPath, Func<string, bool> owns, CancellationToken token,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(owns);
        var file = RequireFile(hooksPath);
        var directory = Path.GetDirectoryName(file)!;
        if (!Directory.Exists(directory) || !File.Exists(file)) return false;
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var original = ReadBytes(file);
        var settings = Parse(original);
        if (settings is null || settings["hooks"] is not JsonObject hooks) return false;
        var removed = false;
        var deleteFile = false;
        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray list) continue;
            foreach (var (node, options) in OwnedEntries(settings, name).Where(item => owns(item.Options.CycleArcExecutable)).ToArray())
            {
                list.Remove(node);
                removed = true;
                if (list.Count == 0 && options.CreatedEvent) hooks.Remove(name);
                if (hooks.Count == 0 && options.CreatedHooks) settings.Remove("hooks");
                deleteFile |= options.CreatedFile;
            }
        }
        if (!removed) return false;
        beforeCommit?.Invoke();
        // A file CycleArc created and that now holds nothing but its version goes away again.
        if (deleteFile && settings.Count == 1 && settings.ContainsKey("version"))
        {
            token.ThrowIfCancellationRequested();
            EnsureUnchanged(file, original);
            File.Delete(file);
            return true;
        }
        Write(file, original, settings, token);
        return true;
    }

    private static IEnumerable<(JsonNode Node, CursorHookOptions Options)> OwnedEntries(JsonObject settings, string name)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[name] is not JsonArray list) yield break;
        foreach (var node in list)
        {
            if (node is not JsonObject item || item.Count != 2
                || item["timeout"] is not JsonValue timeout || !timeout.TryGetValue<int>(out var seconds) || seconds != TimeoutSeconds
                || item["command"] is not JsonValue command || !command.TryGetValue<string>(out var text)
                || !TryRead(text, out var options) || options!.Event != name) continue;
            yield return (item, options);
        }
    }

    private static bool Valid(CursorHookOptions o) => o.Version == 1
        && Events.Contains(o.Event)
        && ClaudeConnectionPaths.Normalize(o.CycleArcExecutable) is { } exe && exe == o.CycleArcExecutable
        && Path.GetFileName(exe).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase)
        && ClaudeConnectionPaths.Normalize(o.DataRoot) is { } root && root == o.DataRoot;

    private static string RequireFile(string hooksPath) =>
        ClaudeConnectionPaths.Normalize(hooksPath) is { } file && Path.GetFileName(file) == "hooks.json"
            ? file : throw new CursorHookException(CursorHookFailure.InvalidSettings);

    private static string RequireExecutable(string executable) =>
        ClaudeConnectionPaths.Normalize(executable) is { } exe
        && Path.GetFileName(exe).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase)
            ? exe : throw new CursorHookException(CursorHookFailure.InvalidSettings);

    private static byte[]? ReadBytes(string file)
    {
        if (!File.Exists(file)) return null;
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSettingsBytes) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length > MaxSettingsBytes) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        return buffer.ToArray();
    }

    // Cursor requires a positive integer "version". A file without one is not one CycleArc
    // may rewrite, because adding it could enable hooks Cursor currently ignores.
    private static JsonObject? Parse(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            using (var document = JsonDocument.Parse(text)) CheckUnique(document.RootElement);
            if (JsonNode.Parse(text) is not JsonObject settings
                || settings["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number < 1)
                throw new CursorHookException(CursorHookFailure.InvalidSettings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        { throw new CursorHookException(CursorHookFailure.InvalidSettings, ex); }
    }

    private static void CheckUnique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                CheckUnique(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckUnique(item);
    }

    private static void EnsureUnchanged(string file, byte[]? original)
    {
        var current = ReadBytes(file);
        if (original is null ? current is not null : current is null || !original.AsSpan().SequenceEqual(current))
            throw new CursorHookException(CursorHookFailure.SettingsChanged);
    }

    private static void Write(string file, byte[]? original, JsonObject settings, CancellationToken token)
    {
        if (original is not null && JsonNode.DeepEquals(Parse(original), settings)) return;
        var output = Encoding.UTF8.GetBytes(settings.ToJsonString(JsonOptions) + Environment.NewLine);
        if (output.Length > MaxSettingsBytes) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(output);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            EnsureUnchanged(file, original);
            if (original is null) File.Move(temporary, file, false);
            else File.Replace(temporary, file, file + ".cyclearc.bak", true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<FileStream> LeaseAsync(string directory, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(directory, ".cyclearc-hooks.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }
}
