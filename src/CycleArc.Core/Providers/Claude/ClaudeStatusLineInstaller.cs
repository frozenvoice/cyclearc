using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeBridgeOptions(int Version, string ProfileId, string ConfigDirectory,
    string CycleArcExecutable, string DataRoot, bool HadStatusLine, JsonObject? PreviousStatusLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BindingGeneration = null);

public enum ClaudeSetupFailure
{
    InvalidSettings,
    SettingsChanged,
    AlreadyLinked,
    ConnectionUnavailable,
    CommandTooLong,
    DisconnectCleanupIncomplete
}

public class ClaudeSetupException(ClaudeSetupFailure failure, Exception? innerException = null)
    : Exception("Claude connection settings could not be updated.", innerException)
{
    public ClaudeSetupFailure Failure { get; } = failure;
}

public sealed class ClaudeDisconnectCleanupException(Exception innerException)
    : ClaudeSetupException(ClaudeSetupFailure.DisconnectCleanupIncomplete, innerException)
{
}

public static class ClaudeStatusLineInstaller
{
    private const string Prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const string LegacyMarker = "# CycleArc automatic statusLine v1\n# ";
    private const string Marker = "# CycleArc automatic statusLine v2\n";
    private const string OptionsPrefix = "$options='";
    private const int MaxLegacyCommandLength = 28000;
    // Git Bash truncates its -c command around 8191 characters. Leave headroom
    // for the shell boundary so an installed wrapper reaches PowerShell intact.
    private const int MaxCommandLength = 8000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public const int MaxSettingsBytes = 1024 * 1024;

    public static string Payload(ClaudeBridgeOptions options) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(options));
    public static ClaudeBridgeOptions Decode(string payload)
    {
        try
        {
            if (payload.Length > 12000) throw new InvalidDataException();
            var options = JsonSerializer.Deserialize<ClaudeBridgeOptions>(Convert.FromBase64String(payload));
            if (options is null || !Valid(options)) throw new InvalidDataException();
            return options;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or ArgumentException)
        { throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings); }
    }

    public static string Command(ClaudeBridgeOptions options)
    {
        if (!Valid(options)) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        var payload = Payload(options);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = Marker + OptionsPrefix + payload + "'; "
            // Native pipeline input invokes Out-String internally. Prepare it before
            // launching the bounded receiver, instead of spending its stdin deadline on module loading.
            + "Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop; "
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; "
            + "$input | & '" + path + "' '" + ClaudeStatusLineBridge.Argument + "' $options"
            + " | & { process { [Console]::Out.WriteLine($_) } }; exit $LASTEXITCODE";
        var command = Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (command.Length > MaxCommandLength) throw new ClaudeSetupException(ClaudeSetupFailure.CommandTooLong);
        return command;
    }

    private static string PreviousAutomaticCommand(ClaudeBridgeOptions options)
    {
        var payload = Payload(options);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = Marker + OptionsPrefix + payload + "'; "
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + ClaudeStatusLineBridge.Argument + "' $options"
            + " | ForEach-Object { $_ }; exit $LASTEXITCODE";
        return Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string LegacyCommand(ClaudeBridgeOptions options)
    {
        var payload = Payload(options);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = LegacyMarker + payload + "\n"
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + ClaudeStatusLineBridge.Argument + "' '" + payload
            + "' | ForEach-Object { $_ }; exit $LASTEXITCODE";
        return Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static bool TryRead(string command, out ClaudeBridgeOptions? options)
    {
        options = null;
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > MaxLegacyCommandLength) return false;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            string payload;
            bool legacy;
            if (script.StartsWith(LegacyMarker, StringComparison.Ordinal))
            {
                var end = script.IndexOf('\n', LegacyMarker.Length);
                if (end < 0) return false;
                payload = script[LegacyMarker.Length..end];
                legacy = true;
            }
            else
            {
                if (!script.StartsWith(Marker + OptionsPrefix, StringComparison.Ordinal)) return false;
                var payloadStart = Marker.Length + OptionsPrefix.Length;
                var payloadEnd = script.IndexOf('\'', payloadStart);
                if (payloadEnd < 0) return false;
                payload = script[payloadStart..payloadEnd];
                legacy = false;
            }

            var parsed = Decode(payload);
            // Check the previous v2 shape before building the current command. A valid
            // old wrapper may be below the old 8000-character limit while the added
            // UTF-8 sink makes the replacement too long to install.
            if (!legacy && string.Equals(PreviousAutomaticCommand(parsed), command, StringComparison.Ordinal))
            {
                options = parsed;
                return true;
            }
            var expected = legacy ? LegacyCommand(parsed) : Command(parsed);
            if (!string.Equals(expected, command, StringComparison.Ordinal)) return false;
            options = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ClaudeSetupException or ArgumentException) { return false; }
    }

    // Recognize only exact commands emitted by the current or previous manual setup window.
    private static string? LegacyTarget(string command)
    {
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > MaxLegacyCommandLength) return null;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            const string start = "$input | & '";
            const string middle = "' '--claude-statusline' '";
            var from = script.IndexOf(start, StringComparison.Ordinal);
            var divider = script.IndexOf(middle, from < 0 ? 0 : from + start.Length, StringComparison.Ordinal);
            if (from < 0 || divider < 0) return null;
            var path = script[(from + start.Length)..divider].Replace("''", "'", StringComparison.Ordinal);
            var idFrom = divider + middle.Length;
            if (script.Length < idFrom + 33 || script[idFrom + 32] != '\'') return null;
            var id = script.Substring(idFrom, 32);
            string? dataRoot = null;
            const string rootPrefix = " '--data-root' '";
            var tail = script[(idFrom + 33)..];
            if (tail.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                var rootEnd = tail.IndexOf("' | ", rootPrefix.Length, StringComparison.Ordinal);
                if (rootEnd < rootPrefix.Length) return null;
                dataRoot = tail[rootPrefix.Length..rootEnd].Replace("''", "'", StringComparison.Ordinal);
            }
            var current = ClaudeStatusLineCommand.SettingsCommand(path, id, dataRoot);
            var previous = ClaudeStatusLineCommand.SettingsCommand(path, id, dataRoot, legacyOutput: true);
            return current == command || previous == command ? id : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { return null; }
    }

    public static async Task<ClaudeBridgeOptions> InstallAsync(CodexAccountStore accounts, string profileId,
        string directory, string executable, CancellationToken token, Action? beforeCommit = null)
    {
        directory = RequireDirectory(directory);
        Directory.CreateDirectory(directory);
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var file = Path.Combine(directory, "settings.json");
        var original = ReadBytes(file);
        var settings = ParseSettings(original);
        var had = settings.ContainsKey("statusLine");
        JsonObject? previous = null;
        if (settings["statusLine"] is { } status)
        {
            if (status is not JsonObject obj || !ValidStatusLine(obj)) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
            var command = obj["command"]!.GetValue<string>();
            if (TryRead(command, out var installed))
            {
                if (installed!.ProfileId != profileId && accounts.ContainsClaude(installed.ProfileId))
                    throw new ClaudeSetupException(ClaudeSetupFailure.AlreadyLinked);
                had = installed.HadStatusLine;
                previous = installed.PreviousStatusLine?.DeepClone().AsObject();
            }
            else if (LegacyTarget(command) is { } legacyId)
            {
                if (legacyId != profileId && accounts.ContainsClaude(legacyId)) throw new ClaudeSetupException(ClaudeSetupFailure.AlreadyLinked);
                had = false;
            }
            else previous = obj.DeepClone().AsObject();
        }

        var binding = new ClaudeConnectionStore(accounts, profileId).Read().Binding;
        var generation = binding is { Disconnected: false }
            && string.Equals(binding.ConfigDirectory, directory, StringComparison.OrdinalIgnoreCase)
            && binding.BindingGeneration is not null ? binding.BindingGeneration : null;
        var options = new ClaudeBridgeOptions(1, profileId, directory, Path.GetFullPath(executable),
            accounts.RootDirectory, had, previous, generation);
        var replacement = (settings["statusLine"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject { ["type"] = "command" };
        replacement["command"] = Command(options);
        settings["statusLine"] = replacement;

        // The failure hook is installed only for a verified binding. Direct legacy/statusLine
        // installation remains compatible and cannot create a callback that bypasses binding checks.
        if (binding is { Disconnected: false, BindingGeneration: not null }
            && string.Equals(binding.ConfigDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            AddFailureHook(settings, accounts, profileId, directory, Path.GetFullPath(executable), binding);
        }
        beforeCommit?.Invoke();
        WriteIfChanged(file, original, settings, token);
        return options;
    }
    public static bool TryReadOwnedStatusLine(string directory, string profileId, out ClaudeBridgeOptions? options)
    {
        options = null;
        try
        {
            if (ClaudeConnectionPaths.Normalize(directory) is not { } normalized
                || !Guid.TryParseExact(profileId, "N", out _)) return false;
            var settings = ParseSettings(ReadBytes(Path.Combine(RequireDirectory(normalized), "settings.json")));
            if (settings["statusLine"] is not JsonObject obj || !ValidStatusLine(obj)) return false;
            if (!TryRead(obj["command"]!.GetValue<string>(), out var parsed) || parsed is null) return false;
            if (parsed.ProfileId != profileId || !string.Equals(parsed.ConfigDirectory, normalized, StringComparison.OrdinalIgnoreCase)) return false;
            options = parsed;
            return true;
        }
        catch (Exception ex) when (ex is ClaudeSetupException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }

    public static bool IsInstalled(string directory, string profileId)
    {
        try
        {
            var settings = ParseSettings(ReadBytes(Path.Combine(RequireDirectory(directory), "settings.json")));
            return settings["statusLine"] is JsonObject obj && ValidStatusLine(obj)
                && TryRead(obj["command"]!.GetValue<string>(), out var options) && options!.ProfileId == profileId
                && string.Equals(options.ConfigDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ClaudeSetupException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task RestoreAsync(string directory, string profileId, CancellationToken token)
    {
        directory = RequireDirectory(directory);
        if (!Directory.Exists(directory)) return;
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var file = Path.Combine(directory, "settings.json");
        var original = ReadBytes(file);
        var settings = ParseSettings(original);
        ClaudeBridgeOptions? installed = null;
        var ownsStatusLine = settings["statusLine"] is JsonObject obj && ValidStatusLine(obj)
            && TryRead(obj["command"]!.GetValue<string>(), out installed) && installed!.ProfileId == profileId;
        var hadOwnedFailureHook = HasOwnedFailureHook(settings, profileId);
        if (ownsStatusLine)
        {
            if (installed!.HadStatusLine) settings["statusLine"] = installed.PreviousStatusLine?.DeepClone();
            else settings.Remove("statusLine");
        }
        RemoveOwnedFailureHook(settings, profileId);
        if (ownsStatusLine || hadOwnedFailureHook)
            WriteIfChanged(file, original, settings, token);
    }
    private static bool Valid(ClaudeBridgeOptions o) => o.Version == 1 && Guid.TryParseExact(o.ProfileId, "N", out _)
        && ClaudeConnectionPaths.Normalize(o.ConfigDirectory) is { } directory && directory == o.ConfigDirectory
        && ClaudeConnectionPaths.Normalize(o.CycleArcExecutable) is { } executable && executable == o.CycleArcExecutable
        && Path.GetFileName(o.CycleArcExecutable).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase)
        && ClaudeConnectionPaths.Normalize(o.DataRoot) is { } root && root == o.DataRoot
        && (o.PreviousStatusLine is null || o.HadStatusLine && ValidStatusLine(o.PreviousStatusLine, 8192))
        && (o.BindingGeneration is null || Guid.TryParseExact(o.BindingGeneration, "N", out _));

    private static void AddFailureHook(JsonObject settings, CodexAccountStore accounts, string profileId,
        string directory, string executable, ClaudeConnectionBinding binding)
    {
        var createdHooks = !settings.ContainsKey("hooks");
        var hooks = settings.ContainsKey("hooks")
            ? settings["hooks"] as JsonObject ?? throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings)
            : new JsonObject();
        settings["hooks"] = hooks;
        var createdStop = !hooks.ContainsKey("StopFailure");
        var stop = hooks.ContainsKey("StopFailure")
            ? hooks["StopFailure"] as JsonArray ?? throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings)
            : new JsonArray();
        hooks["StopFailure"] = stop;
        JsonObject? owned = null;
        foreach (var entry in stop)
        {
            if (entry is not JsonObject item) continue;
            if (!TryReadOwnedFailureHook(item, out var existing)) continue;
            if (owned is not null) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
            if (existing!.ProfileId != profileId && accounts.ContainsClaude(existing.ProfileId))
                throw new ClaudeSetupException(ClaudeSetupFailure.AlreadyLinked);
            owned = item;
            createdHooks = existing.CreatedHooks;
            createdStop = existing.CreatedStopFailure;
        }
        var failureOptions = new ClaudeFailureBridgeOptions(1, profileId, directory, executable,
            accounts.RootDirectory, binding.IdentityFingerprint, binding.BindingGeneration!, createdHooks, createdStop);
        var command = ClaudeFailureCommand.Command(failureOptions);
        var replacement = new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command })
        };
        if (owned is null) stop.Add(replacement);
        else
        {
            // Replace only the exact CycleArc-owned item. Keep any future hook properties
            // and unrelated matchers untouched by refusing ambiguous shapes above.
            var index = stop.IndexOf(owned);
            stop[index] = replacement;
        }
    }

    private static bool HasOwnedFailureHook(JsonObject settings, string profileId)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks["StopFailure"] is not JsonArray stop) return false;
        foreach (var entry in stop)
            if (entry is JsonObject item && TryReadOwnedFailureHook(item, out var options) && options!.ProfileId == profileId) return true;
        return false;
    }

    private static void RemoveOwnedFailureHook(JsonObject settings, string profileId)
    {
        if (settings["hooks"] is null) return;
        if (settings["hooks"] is not JsonObject hooks) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        if (hooks["StopFailure"] is null) return;
        if (hooks["StopFailure"] is not JsonArray stop) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        var owned = new List<JsonNode>();
        ClaudeFailureBridgeOptions? original = null;
        foreach (var entry in stop)
        {
            if (entry is not JsonObject item) continue;
            if (TryReadOwnedFailureHook(item, out var options) && options!.ProfileId == profileId)
            {
                owned.Add(item); original = options;
            }
        }
        if (owned.Count == 0) return;
        if (owned.Count > 1) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        stop.Remove(owned[0]);
        // Remove only containers this exact owned hook created, and only while still empty.
        if (stop.Count == 0 && original!.CreatedStopFailure) hooks.Remove("StopFailure");
        if (hooks.Count == 0 && original!.CreatedHooks) settings.Remove("hooks");
    }

    private static bool TryReadOwnedFailureHook(JsonObject item, out ClaudeFailureBridgeOptions? options)
    {
        options = null;
        if (item.Count != 1 || item["hooks"] is not JsonArray hooks || hooks.Count != 1
            || hooks[0] is not JsonObject command || command.Count != 2
            || command["type"] is not JsonValue type || !type.TryGetValue<string>(out var typeText) || typeText != "command"
            || command["command"] is not JsonValue commandValue || !commandValue.TryGetValue<string>(out var text)) return false;
        if (!ClaudeFailureCommand.TryRead(text, out options) || options is null) return false;
        try { return string.Equals(ClaudeFailureCommand.Command(options), text, StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { options = null; return false; }
    }
    private static bool ValidStatusLine(JsonObject obj, int maxLength = 28000) => obj["type"] is JsonValue type && type.TryGetValue<string>(out var value)
        && value == "command" && obj["command"] is JsonValue command && command.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) && text.Length <= maxLength && !text.Contains('\0');

    private static string RequireDirectory(string directory)
    {
        var normalized = ClaudeConnectionPaths.Normalize(directory) ?? throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        if (Directory.Exists(normalized) && (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        return normalized;
    }
    private static byte[]? ReadBytes(string file)
    {
        if (!File.Exists(file)) return null;
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        if (buffer.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        return buffer.ToArray();
    }
    private static JsonObject ParseSettings(byte[]? bytes)
    {
        if (bytes is null) return new();
        try
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(text);
            CheckUnique(doc.RootElement);
            return JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings); }
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
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckUnique(item);
    }
    private static void WriteIfChanged(string file, byte[]? original, JsonObject settings, CancellationToken token)
    {
        if (original is not null && JsonNode.DeepEquals(ParseSettings(original), settings)) return;
        var output = Encoding.UTF8.GetBytes(settings.ToJsonString(JsonOptions) + Environment.NewLine);
        if (output.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(output); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            var current = ReadBytes(file);
            if (original is null ? current is not null : current is null || !original.AsSpan().SequenceEqual(current))
                throw new ClaudeSetupException(ClaudeSetupFailure.SettingsChanged);
            if (original is null) File.Move(temporary, file, false);
            else File.Replace(temporary, file, file + ".cyclearc.bak", true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<FileStream> LeaseAsync(string directory, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, ".cyclearc-settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
}
