using System.Text;
using System.Text.Json.Nodes;
using CycleArc.Providers.Cursor;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorHookInstallerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyclearc-cursor-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly string _cursor;
    private readonly string _hooks;
    private readonly string _exe;
    private readonly string _data;

    public CursorHookInstallerTests()
    {
        _cursor = Path.Combine(_root, ".cursor");
        _hooks = Path.Combine(_cursor, "hooks.json");
        _exe = Path.Combine(_root, "app", "current", "CycleArc.exe");
        _data = Path.Combine(_root, "ProMeter");
        Directory.CreateDirectory(_cursor);
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        Directory.CreateDirectory(_data);
        File.WriteAllText(_exe, "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private const string UserHooks = """
        {
          "version": 1,
          "custom": {"keep": [1, 2]},
          "hooks": {
            "beforeSubmitPrompt": [{"command": "./audit.sh", "timeout": 10}],
            "afterFileEdit": [{"command": "format.cmd"}]
          }
        }
        """;

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_hooks))!;

    [Fact]
    public async Task InstallAddsOneOwnedEntryPerEventAndPreservesEverythingElse()
    {
        File.WriteAllText(_hooks, UserHooks);

        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);

        var settings = Read();
        Assert.Equal(1, (int)settings["version"]!);
        Assert.Equal("[1,2]", settings["custom"]!["keep"]!.ToJsonString());
        var submit = settings["hooks"]!["beforeSubmitPrompt"]!.AsArray();
        Assert.Equal("./audit.sh", (string)submit[0]!["command"]!);
        Assert.Equal(2, submit.Count);
        Assert.Equal(CursorHookInstaller.TimeoutSeconds, (int)submit[1]!["timeout"]!);
        Assert.Single(settings["hooks"]!["stop"]!.AsArray());
        Assert.Equal("format.cmd", (string)settings["hooks"]!["afterFileEdit"]![0]!["command"]!);
        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, _exe));
        Assert.True(File.Exists(_hooks + ".cyclearc.bak"));
        Assert.Equal(UserHooks, File.ReadAllText(_hooks + ".cyclearc.bak"));
    }

    [Fact]
    public async Task InstallIsIdempotentAndRemoveRestoresTheOriginalContent()
    {
        File.WriteAllText(_hooks, UserHooks);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var once = File.ReadAllText(_hooks);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        Assert.Equal(once, File.ReadAllText(_hooks));

        Assert.True(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserHooks), Read()));
        Assert.Equal(CursorHookStatus.NotInstalled, CursorHookInstaller.ReadStatus(_hooks, _exe));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task RemoveRestoresTheExactBytesOfAFileInTheSameLayout(string newLine)
    {
        var original = string.Join(newLine,
            "{",
            "  \"version\": 1,",
            "  \"hooks\": {",
            "    \"stop\": [",
            "      {",
            "        \"command\": \"tool.cmd && echo 완료 > nul\"",
            "      }",
            "    ]",
            "  }",
            "}") + newLine;
        var bytes = new UTF8Encoding(false).GetBytes(original);
        File.WriteAllBytes(_hooks, bytes);

        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var installed = File.ReadAllText(_hooks);
        Assert.Contains("tool.cmd && echo 완료 > nul", installed, StringComparison.Ordinal);
        Assert.Equal(newLine == "\r\n", installed.Contains("\r\n", StringComparison.Ordinal));
        Assert.True(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));

        Assert.Equal(bytes, File.ReadAllBytes(_hooks));
    }

    [Fact]
    public async Task FileCreatedByCycleArcIsDeletedAgainWhenOnlyItsEntriesWereThere()
    {
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, _exe));

        Assert.True(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));

        Assert.False(File.Exists(_hooks));
    }

    [Fact]
    public async Task CreatedFileStaysWhenTheUserAddedTheirOwnHookMeanwhile()
    {
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var settings = Read();
        settings["hooks"]!["stop"]!.AsArray().Add(new JsonObject { ["command"] = "notify.cmd" });
        File.WriteAllText(_hooks, settings.ToJsonString());

        await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None);

        var after = Read();
        Assert.Null(after["hooks"]!["beforeSubmitPrompt"]);
        Assert.Equal("notify.cmd", (string)after["hooks"]!["stop"]![0]!["command"]!);
    }

    [Fact]
    public async Task ExistingEmptyContainersAreKeptOnRemoval()
    {
        File.WriteAllText(_hooks, """{"version":1,"hooks":{"stop":[]}}""");
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);

        await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None);

        var after = Read();
        Assert.Empty(after["hooks"]!["stop"]!.AsArray());
        Assert.Null(after["hooks"]!["beforeSubmitPrompt"]);
    }

    [Theory]
    [InlineData("""{"hooks":{}}""")]
    [InlineData("""{"version":0,"hooks":{}}""")]
    [InlineData("""{"version":"1"}""")]
    [InlineData("""{"version":1,"version":1}""")]
    [InlineData("""{"version":1,"hooks":{"stop":[],"stop":[]}}""")]
    [InlineData("""{"version":1,"hooks":[]}""")]
    [InlineData("""{"version":1,"hooks":{"stop":{}}}""")]
    [InlineData("not json")]
    public async Task InvalidOrAmbiguousSettingsAreNeverRewritten(string content)
    {
        File.WriteAllText(_hooks, content);

        var error = await Assert.ThrowsAsync<CursorHookException>(() =>
            CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None));

        Assert.Equal(CursorHookFailure.InvalidSettings, error.Failure);
        Assert.Equal(content, File.ReadAllText(_hooks));
    }

    [Fact]
    public async Task MissingCursorFolderIsNotCreated()
    {
        Directory.Delete(_cursor, true);

        var error = await Assert.ThrowsAsync<CursorHookException>(() =>
            CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None));

        Assert.Equal(CursorHookFailure.CursorNotFound, error.Failure);
        Assert.False(Directory.Exists(_cursor));
    }

    [Fact]
    public async Task ConcurrentEditAbortsWithoutOverwriting()
    {
        File.WriteAllText(_hooks, UserHooks);
        const string Edited = """{"version":1,"hooks":{"stop":[{"command":"mine"}]}}""";

        var error = await Assert.ThrowsAsync<CursorHookException>(() => CursorHookInstaller.InstallAsync(
            _hooks, _exe, _data, CancellationToken.None, () => File.WriteAllText(_hooks, Edited)));

        Assert.Equal(CursorHookFailure.SettingsChanged, error.Failure);
        Assert.Equal(Edited, File.ReadAllText(_hooks));
        Assert.Empty(Directory.GetFiles(_cursor, "*.tmp"));
    }

    [Fact]
    public async Task AnotherExistingInstallationKeepsItsEntryButADeletedOneIsReplaced()
    {
        var otherExe = Path.Combine(_root, "other", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);
        File.WriteAllText(otherExe, "");
        await CursorHookInstaller.InstallAsync(_hooks, otherExe, _data, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CursorHookException>(() =>
            CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None));
        Assert.Equal(CursorHookFailure.AlreadyLinked, error.Failure);
        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, otherExe));

        File.Delete(otherExe);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, _exe));
        Assert.Equal(CursorHookStatus.NotInstalled, CursorHookInstaller.ReadStatus(_hooks, otherExe));
        Assert.Single(Read()["hooks"]!["stop"]!.AsArray());

        // The replacement keeps the original "created the file" record, so removal still cleans up.
        await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None);
        Assert.False(File.Exists(_hooks));
    }

    [Fact]
    public async Task RemovalTouchesOnlyEntriesTheCallerOwns()
    {
        File.WriteAllText(_hooks, UserHooks);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var before = File.ReadAllText(_hooks);

        Assert.False(await CursorHookInstaller.RemoveAsync(_hooks, _ => false, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(_hooks));
    }

    [Fact]
    public async Task EditedLookalikeEntriesAreNotOwned()
    {
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var settings = Read();
        var stop = settings["hooks"]!["stop"]![0]!.AsObject();
        stop["timeout"] = 30;
        var submit = settings["hooks"]!["beforeSubmitPrompt"]![0]!.AsObject();
        submit["matcher"] = "x";
        File.WriteAllText(_hooks, settings.ToJsonString());

        Assert.Equal(CursorHookStatus.NotInstalled, CursorHookInstaller.ReadStatus(_hooks, _exe));
        Assert.False(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));
    }

    [Fact]
    public async Task OneMissingEventIsReportedAsPartial()
    {
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var settings = Read();
        settings["hooks"]!.AsObject().Remove("stop");
        File.WriteAllText(_hooks, settings.ToJsonString());

        Assert.Equal(CursorHookStatus.Partial, CursorHookInstaller.ReadStatus(_hooks, _exe));
        File.WriteAllText(_hooks, "{");
        Assert.Equal(CursorHookStatus.Unavailable, CursorHookInstaller.ReadStatus(_hooks, _exe));
    }

    [Fact]
    public async Task UninstallRemovesOnlyEntriesInsideTheInstallationBeingRemoved()
    {
        File.WriteAllText(_hooks, UserHooks);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var otherRoot = Path.Combine(_root, "elsewhere");

        Assert.False(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, otherRoot));
        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, _exe));
        Assert.False(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, "relative"));

        Assert.True(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, Path.Combine(_root, "app")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserHooks), Read()));
    }

    [Fact]
    public async Task UninstallCleanupIsTimeBoxedAndNeverThrows()
    {
        File.WriteAllText(_hooks, UserHooks);
        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);
        var before = File.ReadAllText(_hooks);
        using (new FileStream(Path.Combine(_cursor, ".cyclearc-hooks.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, Path.Combine(_root, "app"),
                TimeSpan.FromMilliseconds(200)));
        }
        Assert.Equal(before, File.ReadAllText(_hooks));

        File.WriteAllText(_hooks, "{");
        Assert.False(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, Path.Combine(_root, "app")));
        Directory.Delete(_cursor, true);
        Assert.False(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, Path.Combine(_root, "app")));
        Assert.False(Directory.Exists(_cursor));
    }

    // The exact command builds before the stdin byte copy wrote, kept here as a fixture independent
    // of the installer. "tail" lets a test produce a near-identical edited variant.
    private static string EarlierCommand(CursorHookOptions options, string tail = "")
    {
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = "# CycleArc Cursor hook v1\n$options='" + CursorHookInstaller.Payload(options) + "'; "
            + "Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop; "
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; "
            + "$input | & '" + path + "' '" + CursorHookCommand.Argument + "' $options"
            + " | & { process { [Console]::Out.WriteLine($_) } }; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 }" + tail;
        return "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    // Puts the hooks.json into the state an earlier build left: same entries, earlier command format.
    private void RegisterWithEarlierBuild(string executable, string tail = "")
    {
        CursorHookInstaller.InstallAsync(_hooks, executable, _data, CancellationToken.None).GetAwaiter().GetResult();
        var settings = Read();
        foreach (var name in CursorHookInstaller.Events)
            foreach (var entry in settings["hooks"]![name]!.AsArray().OfType<JsonObject>())
                if (CursorHookInstaller.TryRead((string)entry["command"]!, out var options))
                    entry["command"] = EarlierCommand(options!, tail);
        File.WriteAllText(_hooks, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void EarlierFormatIsRecognisedOnlyWhenItRegeneratesExactly()
    {
        var options = new CursorHookOptions(1, CursorHookEvent.StopEvent, _exe, _data, false, false, true);

        Assert.True(CursorHookInstaller.TryRead(EarlierCommand(options), out var parsed, out var current));
        Assert.Equal(options, parsed);
        Assert.False(current);
        Assert.True(CursorHookInstaller.TryRead(CursorHookInstaller.Command(options), out _, out current));
        Assert.True(current);
        Assert.False(CursorHookInstaller.TryRead(EarlierCommand(options, " "), out _, out _));
        Assert.False(CursorHookInstaller.TryRead(EarlierCommand(options, "; notify.cmd"), out _, out _));
    }

    [Fact]
    public async Task ConnectingReplacesAnEarlierBuildsEntriesInPlace()
    {
        File.WriteAllText(_hooks, UserHooks);
        RegisterWithEarlierBuild(_exe);
        Assert.Equal(CursorHookStatus.Outdated, CursorHookInstaller.ReadStatus(_hooks, _exe));

        await CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None);

        Assert.Equal(CursorHookStatus.Installed, CursorHookInstaller.ReadStatus(_hooks, _exe));
        var hooks = Read()["hooks"]!;
        var submit = hooks["beforeSubmitPrompt"]!.AsArray();
        Assert.Equal(2, submit.Count);
        Assert.Equal("./audit.sh", (string)submit[0]!["command"]!);
        Assert.True(CursorHookInstaller.TryRead((string)submit[1]!["command"]!, out _, out var current) && current);
        Assert.Single(hooks["stop"]!.AsArray());
        Assert.Equal("format.cmd", (string)hooks["afterFileEdit"]![0]!["command"]!);

        Assert.True(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserHooks), Read()));
    }

    [Fact]
    public async Task TurningOffAndUninstallingRemoveAnEarlierBuildsEntries()
    {
        File.WriteAllText(_hooks, UserHooks);
        RegisterWithEarlierBuild(_exe);
        Assert.True(await CursorHookInstaller.RemoveAsync(_hooks,
            owned => string.Equals(owned, _exe, StringComparison.OrdinalIgnoreCase), CancellationToken.None));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserHooks), Read()));
        Assert.Equal(CursorHookStatus.NotInstalled, CursorHookInstaller.ReadStatus(_hooks, _exe));

        RegisterWithEarlierBuild(_exe);
        Assert.True(await CursorHookInstaller.RemoveForUninstallAsync(_hooks, Path.Combine(_root, "app")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserHooks), Read()));
    }

    [Fact]
    public async Task EditedEarlierFormatEntriesAreNotOwned()
    {
        File.WriteAllText(_hooks, UserHooks);
        RegisterWithEarlierBuild(_exe, "; notify.cmd");
        var before = File.ReadAllText(_hooks);

        Assert.Equal(CursorHookStatus.NotInstalled, CursorHookInstaller.ReadStatus(_hooks, _exe));
        Assert.False(await CursorHookInstaller.RemoveAsync(_hooks, _ => true, CancellationToken.None));
        Assert.Equal(before, File.ReadAllText(_hooks));
    }

    [Fact]
    public async Task AnotherExistingInstallationsEarlierEntryIsKept()
    {
        var otherExe = Path.Combine(_root, "other", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);
        File.WriteAllText(otherExe, "");
        RegisterWithEarlierBuild(otherExe);
        var before = File.ReadAllText(_hooks);

        var error = await Assert.ThrowsAsync<CursorHookException>(() =>
            CursorHookInstaller.InstallAsync(_hooks, _exe, _data, CancellationToken.None));

        Assert.Equal(CursorHookFailure.AlreadyLinked, error.Failure);
        Assert.Equal(before, File.ReadAllText(_hooks));
        Assert.Equal(CursorHookStatus.Outdated, CursorHookInstaller.ReadStatus(_hooks, otherExe));
    }

    [Fact]
    public void CommandRoundTripsAndRejectsTamperedPayloads()
    {
        var options = new CursorHookOptions(1, CursorHookEvent.StopEvent, _exe, _data, false, false, true);
        var command = CursorHookInstaller.Command(options);

        Assert.True(CursorHookInstaller.TryRead(command, out var parsed));
        Assert.Equal(options, parsed);
        Assert.False(CursorHookInstaller.TryRead(command + " ", out _));
        Assert.False(CursorHookInstaller.TryRead("powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand !!", out _));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        Assert.DoesNotContain("exit 2", script, StringComparison.Ordinal);
        Assert.Contains(CursorHookCommand.Argument, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiverRecordsMatchingEventAndAlwaysLetsCursorContinue()
    {
        var payload = CursorHookInstaller.Payload(new(1, CursorHookEvent.SubmitEvent, _exe, _data, false, false, false));
        var output = new StringWriter();
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"hook_event_name":"beforeSubmitPrompt","model":"gpt-5","user_email":"a@example.com","prompt":"secret"}"""));

        var code = await CursorHookCommand.RunAsync(payload, input, output, new FixedClock(Now));

        Assert.Equal(0, code);
        Assert.Equal(CursorHookCommand.ContinueResponse, output.ToString().Trim());
        var entry = Assert.Single(new CursorActivityStore(_data).Read()!.Entries);
        Assert.Equal("gpt-5", entry.Model);
        Assert.Equal(Now, entry.ReceivedAt);
        Assert.DoesNotContain("secret", File.ReadAllText(new CursorActivityStore(_data).Path), StringComparison.Ordinal);
    }

    // Shape observed from Cursor 3.24.12 on Windows (values synthetic): stdin starts with a UTF-8
    // byte order mark, and stop also carries session and token fields.
    [Theory]
    [InlineData("beforeSubmitPrompt", """{"conversation_id":"c","generation_id":"g","model":"claude-opus-5-5-medium","model_id":"claude-opus-5-5","model_params":[{"id":"context","value":"300k"},{"id":"effort","value":"medium"},{"id":"fast","value":"false"}],"composer_mode":"agent","prompt":"보냈어","attachments":[{"type":"file","file_path":"e:/x/a.cs"}],"session_id":"s","hook_event_name":"beforeSubmitPrompt","cursor_version":"3.24.12","workspace_roots":["e:/x"],"user_email":"a@example.com","transcript_path":"c:/t.jsonl"}""")]
    [InlineData("stop", """{"conversation_id":"c","generation_id":"g","model":"claude-opus-5-5-medium","model_id":"claude-opus-5-5","model_params":[{"id":"effort","value":"medium"}],"status":"completed","loop_count":0,"input_tokens":10,"output_tokens":20,"cache_read_tokens":0,"cache_write_tokens":0,"session_id":"s","hook_event_name":"stop","cursor_version":"3.24.12","workspace_roots":["e:/x"],"user_email":"a@example.com","transcript_path":"c:/t.jsonl"}""")]
    public async Task ReceiverRecordsCursorWindowsInputWithAByteOrderMark(string hookEvent, string json)
    {
        var payload = CursorHookInstaller.Payload(new(1, hookEvent, _exe, _data, false, false, false));
        var input = new MemoryStream([.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(json)]);

        Assert.Equal(0, await CursorHookCommand.RunAsync(payload, input, new StringWriter(), new FixedClock(Now)));

        var entry = Assert.Single(new CursorActivityStore(_data).Read()!.Entries);
        Assert.Equal(hookEvent == "stop" ? CursorActivityKind.Completion : CursorActivityKind.Request, entry.Kind);
        Assert.Equal("claude-opus-5-5", entry.ModelId);
        Assert.Equal("medium", entry.Effort);
        var stored = File.ReadAllText(new CursorActivityStore(_data).Path);
        foreach (var secret in new[] { "보냈어", "a@example.com", "a.cs", "t.jsonl", "input_tokens" })
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiverIgnoresMismatchedEventMissingDataRootOversizedAndBadPayloads()
    {
        var stopPayload = CursorHookInstaller.Payload(new(1, CursorHookEvent.StopEvent, _exe, _data, false, false, false));
        var submit = """{"hook_event_name":"beforeSubmitPrompt","model":"gpt-5"}""";

        var output = new StringWriter();
        Assert.Equal(0, await CursorHookCommand.RunAsync(stopPayload, Stream(submit), output, new FixedClock(Now)));
        Assert.Equal(CursorHookCommand.EmptyResponse, output.ToString().Trim());
        Assert.False(File.Exists(new CursorActivityStore(_data).Path));

        var missing = Path.Combine(_root, "missing");
        var missingPayload = CursorHookInstaller.Payload(new(1, CursorHookEvent.SubmitEvent, _exe, missing, false, false, false));
        Assert.Equal(0, await CursorHookCommand.RunAsync(missingPayload, Stream(submit), new StringWriter(), new FixedClock(Now)));
        Assert.False(Directory.Exists(missing));

        var submitPayload = CursorHookInstaller.Payload(new(1, CursorHookEvent.SubmitEvent, _exe, _data, false, false, false));
        var huge = new MemoryStream(new byte[CursorHookCommand.MaxInputBytes + 1]);
        Assert.Equal(0, await CursorHookCommand.RunAsync(submitPayload, huge, new StringWriter(), new FixedClock(Now)));
        Assert.False(File.Exists(new CursorActivityStore(_data).Path));

        var bad = new StringWriter();
        Assert.Equal(0, await CursorHookCommand.RunAsync("not-base64", Stream(submit), bad, new FixedClock(Now)));
        Assert.Equal(CursorHookCommand.ContinueResponse, bad.ToString().Trim());
    }

    [Fact]
    public async Task ReceiverDoesNotFailWhenTheInboxIsLocked()
    {
        var payload = CursorHookInstaller.Payload(new(1, CursorHookEvent.SubmitEvent, _exe, _data, false, false, false));
        var store = new CursorActivityStore(_data);
        using var held = new FileStream(store.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var output = new StringWriter();

        var code = await CursorHookCommand.RunAsync(payload,
            Stream("""{"hook_event_name":"beforeSubmitPrompt","model":"gpt-5"}"""), output, new FixedClock(Now));

        Assert.Equal(0, code);
        Assert.Equal(CursorHookCommand.ContinueResponse, output.ToString().Trim());
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
