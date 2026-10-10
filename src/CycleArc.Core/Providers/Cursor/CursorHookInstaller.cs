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
    /// <summary>Every event has this executable's entry, but at least one in an earlier command format.</summary>
    Outdated,
    Partial,
    Unavailable
}

/// <summary>
/// Adds and removes CycleArc's entries in Cursor's user <c>hooks.json</c>. The file belongs to
/// the person: every other hook, property and order is preserved, an entry is ours only when it
/// decodes to exactly the command CycleArc writes or wrote in its earlier format, and a concurrent
/// edit aborts the write. Connecting replaces an earlier-format entry in place.
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
    /// Stdin is copied to the receiver as bytes: Windows PowerShell's <c>$input</c> decodes it with the
    /// console code page (CP949 on Korean Windows), which corrupted Cursor's byte order mark and text.
    /// The receiver writes its response straight to the inherited stdout.
    /// </summary>
    public static string Command(CursorHookOptions options)
    {
        if (!Valid(options)) throw new CursorHookException(CursorHookFailure.InvalidSettings);
        var command = Encode(options, CurrentBody);
        if (command.Length > MaxCommandLength) throw new CursorHookException(CursorHookFailure.CommandTooLong);
        return command;
    }

    private static string CurrentBody(string path) =>
        "try { $p = New-Object System.Diagnostics.Process; $s = $p.StartInfo; $s.FileName = '" + path + "'; "
        + "$s.Arguments = '" + CursorHookCommand.Argument + " ' + $options; $s.UseShellExecute = $false; $s.RedirectStandardInput = $true; "
        + "$null = $p.Start(); [Console]::OpenStandardInput().CopyTo($p.StandardInput.BaseStream); $p.StandardInput.Close(); "
        + "$p.WaitForExit(); if ($p.ExitCode -eq 0) { exit 0 } } catch { }; exit 1";

    // The earlier format, which piped PowerShell's decoded $input. Recognised only so that existing
    // entries are replaced on connection and deleted on removal; never written.
    private static string LegacyBody(string path) =>
        "Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop; "
        + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; "
        + "$input | & '" + path + "' '" + CursorHookCommand.Argument + "' $options"
        + " | & { process { [Console]::Out.WriteLine($_) } }; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 }";

    private static string Encode(CursorHookOptions options, Func<string, string> body)
    {
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = Marker + OptionsPrefix + Payload(options) + "'; " + body(path);
        return Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static bool TryRead(string command, out CursorHookOptions? options) => TryRead(command, out options, out _);

    /// <summary>
    /// Recognises a command only when its decoded options regenerate it byte for byte, in the current
    /// format or the single earlier one; <paramref name="current"/> tells which.
    /// </summary>
    public static bool TryRead(string command, out CursorHookOptions? options, out bool current)
    {
        options = null;
        current = false;
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > MaxCommandLength) return false;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            if (!script.StartsWith(Marker + OptionsPrefix, StringComparison.Ordinal)) return false;
            var start = Marker.Length + OptionsPrefix.Length;
            var end = script.IndexOf('\'', start);
            if (end < 0) return false;
            var parsed = Decode(script[start..end]);
            current = string.Equals(Encode(parsed, CurrentBody), command, StringComparison.Ordinal);
            if (!current && !string.Equals(Encode(parsed, LegacyBody), command, StringComparison.Ordinal)) return false;
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
            var mine = Events.Select(name => OwnedEntries(settings, name).Where(item =>
                string.Equals(item.Options.CycleArcExecutable, normalized, StringComparison.OrdinalIgnoreCase)).ToArray()).ToArray();
            var present = mine.Count(entries => entries.Length > 0);
            return present == 0 ? CursorHookStatus.NotInstalled
                : present != Events.Count ? CursorHookStatus.Partial
                : mine.All(entries => entries.All(item => item.Current)) ? CursorHookStatus.Installed : CursorHookStatus.Outdated;
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
            foreach (var (node, options, _) in OwnedEntries(settings, name).Where(item => owns(item.Options.CycleArcExecutable)).ToArray())
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

    public static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Uninstall cleanup: removes only entries whose executable is inside the installation being
    /// removed. Time-boxed, never throws and never creates Cursor's folder or file.
    /// </summary>
    public static async Task<bool> RemoveForUninstallAsync(string hooksPath, string installationRoot, TimeSpan? budget = null)
    {
        try
        {
            if (ClaudeConnectionPaths.Normalize(installationRoot) is null) return false;
            using var timeout = new CancellationTokenSource(budget ?? UninstallBudget);
            return await RemoveAsync(hooksPath,
                executable => ClaudeUninstallCleanup.IsInsideInstallation(installationRoot, executable),
                timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CursorHookException or IOException or UnauthorizedAccessException
            or OperationCanceledException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static IEnumerable<(JsonNode Node, CursorHookOptions Options, bool Current)> OwnedEntries(JsonObject settings, string name)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[name] is not JsonArray list) yield break;
        foreach (var node in list)
        {
            if (node is not JsonObject item || item.Count != 2
                || item["timeout"] is not JsonValue timeout || !timeout.TryGetValue<int>(out var seconds) || seconds != TimeoutSeconds
                || item["command"] is not JsonValue command || !command.TryGetValue<string>(out var text)
                || !TryRead(text, out var options, out var current) || options!.Event != name) continue;
            yield return (item, options, current);
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
        var output = Encoding.UTF8.GetBytes(Format(settings, original));
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

    // Keeps the file's line endings, final newline and readable characters such as && or Korean text,
    // so other tools' entries keep their bytes and removal restores a file in this layout exactly.
    private static string Format(JsonObject settings, byte[]? original)
    {
        var text = original is null ? null : Encoding.UTF8.GetString(original);
        var newLine = text is null || !text.Contains('\n') ? Environment.NewLine : text.Contains("\r\n") ? "\r\n" : "\n";
        var json = settings.ToJsonString(new JsonSerializerOptions(JsonOptions)
        {
            NewLine = newLine,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return text is null || text.EndsWith('\n') ? json + newLine : json;
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
