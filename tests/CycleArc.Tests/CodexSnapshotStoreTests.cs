using System.Text;
using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CodexSnapshotStoreTests
{
    [Fact]
    public void ValidSnapshot_SurvivesReload()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        var snapshot = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            new DateTimeOffset(2026, 9, 5, 1, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 5, 1, 15, 0, TimeSpan.Zero),
            true,
            null,
            1,
            [
                new CodexQuotaWindow("codex", 42, 300, DateTimeOffset.FromUnixTimeSeconds(1893456000), CodexWindowKind.FiveHour)
            ],
            "ok");
        store.Save(snapshot);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(CodexQuotaStatus.Available, loaded.Status);
        Assert.Equal(42, loaded.Windows[0].UsedPercent);
        Assert.Equal(1, loaded.ResetCreditsAvailable);
        Assert.DoesNotContain("@", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptPrimary_RecoversValidBackup_WithoutOverwritingBackup()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(Snapshot(42));
        store.Save(Snapshot(17));
        var backup = File.ReadAllText(store.BackupPath);

        File.WriteAllText(path, "{broken");

        var recoveredStore = new CodexSnapshotStore(path);
        var recovered = recoveredStore.Load();
        Assert.True(recoveredStore.RecoveredFromBackup);
        Assert.Equal(42, recovered?.Windows.Single().UsedPercent);
        Assert.Equal(backup, File.ReadAllText(store.BackupPath));
    }

    [Fact]
    public void InvalidPrimaryAndBackup_ReturnsNoSnapshot()
    {
        var path = TempFile();
        File.WriteAllText(path, "{\"version\":99,\"status\":\"Available\",\"windows\":[]}");
        File.WriteAllText(path + ".bak", "{\"version\":1,\"status\":\"NotAStatus\",\"windows\":[]}");

        var store = new CodexSnapshotStore(path);
        Assert.Null(store.Load());
        Assert.False(store.RecoveredFromBackup);
    }

    [Fact]
    public void FailedOrInterruptedWrite_PreservesPrimaryAndBackup()
    {
        var files = new FaultingSnapshotFileSystem();
        var store = new CodexSnapshotStore("snapshot.json", files);
        store.Save(Snapshot(42));
        store.Save(Snapshot(17));
        var primary = files.Read("snapshot.json");
        var backup = files.Read("snapshot.json.bak");

        files.FailAfterWrite = true;
        Assert.Throws<IOException>(() => store.Save(Snapshot(5)));
        Assert.Equal(primary, files.Read("snapshot.json"));
        Assert.Equal(backup, files.Read("snapshot.json.bak"));
        Assert.DoesNotContain(files.Paths, path => path.EndsWith(".tmp", StringComparison.Ordinal));

        files.FailAfterWrite = false;
        files.FailReplace = true;
        Assert.Throws<IOException>(() => store.Save(Snapshot(5)));
        Assert.Equal(primary, files.Read("snapshot.json"));
        Assert.Equal(backup, files.Read("snapshot.json.bak"));
        Assert.DoesNotContain(files.Paths, path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingVersion_IsRejected()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(Snapshot(42));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("version");
        File.WriteAllText(path, json.ToJsonString());

        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("status")]
    [InlineData("kind")]
    public void InvalidVersionOrEnum_IsRejected(string field)
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(Snapshot(42));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (field == "version") json["version"] = CodexSnapshotStore.CurrentVersion + 1;
        else if (field == "status") json["status"] = "not-a-status";
        else json["windows"]![0]!["kind"] = "not-a-window-kind";
        File.WriteAllText(path, json.ToJsonString());

        Assert.Null(store.Load());
    }

    [Fact]
    public void OversizedSnapshot_IsRejected()
    {
        var path = TempFile();
        var oversized = new string('x', CodexSnapshotStore.MaxSerializedBytes);
        File.WriteAllText(path, "{\"version\":1,\"status\":\"Available\",\"technicalDetail\":\"" + oversized + "\",\"windows\":[]}");

        Assert.Null(new CodexSnapshotStore(path).Load());
    }

    [Fact]
    public async Task TransientFailure_PreservesLastGoodAsStale()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            true,
            null,
            1,
            [new CodexQuotaWindow(null, 42, 300, null, CodexWindowKind.FiveHour)],
            null));
        var files = new MemoryCodexFileSystem();
        files.PathFolders.Add(@"C:\Tools");
        files.Files.Add(@"C:\Tools\codex.exe");
        var factory = new ScriptedCodexProcessFactory { Responder = _ => [], ResponseDelay = TimeSpan.FromSeconds(60) };
        var service = new CodexQuotaService(
            new CodexExecutableLocator(files),
            new CodexAppServerClient(factory),
            store,
            "1.0.0");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await service.RefreshAsync(@"C:\Tools\codex.exe", cts.Token);
        Assert.True(result.UsedCache);
        Assert.Equal(42, result.Snapshot.Windows[0].UsedPercent);
        Assert.True(result.Snapshot.Status is CodexQuotaStatus.Stale or CodexQuotaStatus.Cancelled);
        Assert.NotEqual(0, result.Snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task ProtocolMismatch_PreservesLastGoodAsStale()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            true,
            null,
            1,
            [new CodexQuotaWindow(null, 42, 300, null, CodexWindowKind.FiveHour)],
            null));
        var files = new MemoryCodexFileSystem();
        files.PathFolders.Add(@"C:\Tools");
        files.Files.Add(@"C:\Tools\codex.exe");
        var logs = new List<string>();
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method switch
                {
                    "initialize" => ["""{"id":1,"result":{"ok":true}}"""],
                    "account/read" => ["""{"id":2,"error":{"code":-32600,"message":"missing params"}}"""],
                    "account/rateLimits/read" => ["""{"id":3,"error":{"code":-32600,"message":"missing params"}}"""],
                    _ => []
                };
            }
        };
        var service = new CodexQuotaService(
            new CodexExecutableLocator(files),
            new CodexAppServerClient(factory),
            store,
            "1.0.0",
            logs.Add);
        var result = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, result.Snapshot.Status);
        Assert.Equal(42, result.Snapshot.Windows[0].UsedPercent);
        Assert.Contains("C42%", TaskbarStatusFormatter.Format(new QuotaSnapshot(), result.Snapshot, TaskbarStripMode.Compact), StringComparison.Ordinal);
        Assert.Contains(UiText.CodexDataStale, CodexDisplayFormatting.StatusText(result.Snapshot), StringComparison.Ordinal);
        Assert.Contains(UiText.CodexProtocolChanged, CodexDisplayFormatting.StatusText(result.Snapshot), StringComparison.Ordinal);
        Assert.Contains("codex refresh status=ProtocolMismatch", logs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("codex refresh status=Stale", string.Join(" ", logs), StringComparison.Ordinal);
        Assert.DoesNotContain("accountId", string.Join(" ", logs), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimedOut_PreservesLastGoodAsStale_AndLogsTimedOut()
    {
        var (service, logs) = CachedService(new CanceledReadProcessFactory());
        var result = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, result.Snapshot.Status);
        Assert.Equal(42, result.Snapshot.Windows[0].UsedPercent);
        Assert.Contains("codex refresh status=TimedOut", logs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("codex refresh status=Stale", string.Join(" ", logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_PreservesLastGoodAsStale_AndLogsUnavailable()
    {
        var (service, logs) = CachedService(new BoomProcessFactory());
        var result = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, result.Snapshot.Status);
        Assert.Equal(42, result.Snapshot.Windows[0].UsedPercent);
        Assert.Contains("codex refresh status=Unavailable", logs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("codex refresh status=Stale", string.Join(" ", logs), StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_IsNotDisplayedAsZero()
    {
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "missing");
        Assert.False(snapshot.HasUsablePercentages);
        Assert.Equal("?", CodexDisplayFormatting.PercentText(null));
        Assert.DoesNotContain("0%", CodexDisplayFormatting.OverviewText(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void ForbiddenPayload_IsRejected()
    {
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"email":"user@example.com"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"Authorization":"Bearer abc"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"access_token":"x"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"accountId":"must-not-be-persisted"}"""));
        Assert.False(CodexSnapshotStore.ContainsForbiddenPayload("""{"status":"Available","usedPercent":42}"""));
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"cyclearc-codex-{Guid.NewGuid():N}.json");

    private static CodexQuotaSnapshot Snapshot(double usedPercent) => new(
        CodexQuotaStatus.Available,
        "plus",
        DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
        DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
        true,
        null,
        1,
        [new CodexQuotaWindow(null, usedPercent, 300, null, CodexWindowKind.FiveHour)],
        null);

    private static (CodexQuotaService Service, List<string> Logs) CachedService(ICodexProcessFactory factory)
    {
        var store = new CodexSnapshotStore(TempFile());
        store.Save(new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            true,
            null,
            1,
            [new CodexQuotaWindow(null, 42, 300, null, CodexWindowKind.FiveHour)],
            null));
        var files = new MemoryCodexFileSystem();
        files.PathFolders.Add(@"C:\Tools");
        files.Files.Add(@"C:\Tools\codex.exe");
        var logs = new List<string>();
        var service = new CodexQuotaService(
            new CodexExecutableLocator(files),
            new CodexAppServerClient(factory),
            store,
            "1.0.0",
            logs.Add);
        return (service, logs);
    }

    private sealed class BoomProcessFactory : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command) => throw new InvalidOperationException("unavailable");
    }

    private sealed class CanceledReadProcessFactory : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command) => new CanceledReadProcess();
    }

    private sealed class CanceledReadProcess : ICodexProcess
    {
        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken) =>
            Task.FromCanceled<string?>(new CancellationToken(canceled: true));
        public Task DrainStderrAsync(System.Text.StringBuilder sink, int maxBytes, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public bool HasExited => true;
        public bool KillCalled => true;
        public int? ProcessId => null;
        public string FileName => "canceled";
        public string Arguments => "";
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
        public void KillTree()
        {
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultingSnapshotFileSystem : ICodexSnapshotFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool FailAfterWrite { get; set; }
        public bool FailReplace { get; set; }
        public IEnumerable<string> Paths => _files.Keys;

        public void CreateDirectory(string path)
        {
        }

        public bool Exists(string path) => _files.ContainsKey(path);

        public string Read(string path) => _files.TryGetValue(path, out var value)
            ? value
            : throw new FileNotFoundException(path);

        public bool TryReadAllText(string path, int maxBytes, out string contents)
        {
            contents = "";
            if (!_files.TryGetValue(path, out var value)
                || Encoding.UTF8.GetByteCount(value) > maxBytes)
            {
                return false;
            }

            contents = value;
            return true;
        }

        public void WriteAndFlush(string path, string contents)
        {
            _files[path] = contents;
            if (FailAfterWrite) throw new IOException("synthetic flush failure");
        }

        public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
        {
            if (FailReplace) throw new IOException("synthetic replace failure");
            var previous = Read(destinationFileName);
            _files[destinationBackupFileName] = previous;
            _files[destinationFileName] = Read(sourceFileName);
            _files.Remove(sourceFileName);
        }

        public void Move(string sourceFileName, string destinationFileName, bool overwrite)
        {
            if (!overwrite && _files.ContainsKey(destinationFileName)) throw new IOException("destination exists");
            _files[destinationFileName] = Read(sourceFileName);
            _files.Remove(sourceFileName);
        }

        public void Delete(string path) => _files.Remove(path);
    }
}
