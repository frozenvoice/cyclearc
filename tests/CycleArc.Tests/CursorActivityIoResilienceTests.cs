using CycleArc.Providers.Cursor;
using Xunit.Abstractions;

namespace CycleArc.Tests;

public sealed class CursorActivityIoResilienceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);
    private static readonly string AccountA = new('A', 64);
    private static readonly string AccountB = new('B', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyclearc-cursor-io-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public CursorActivityIoResilienceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // Desired behavior: an initially unreadable inbox must be retried after unlock,
    // even though neither its timestamp nor its length changed.
    [Fact]
    public async Task Poll_FirstReadTemporarilyLocked_RetriesWithoutStampChange()
    {
        var store = await SeedAsync();
        var expected = store.Read()!;
        var monitor = new CursorActivityMonitor(_root);
        var stamp = Stamp(store.Path);
        bool lockedChanged;
        using (var held = LockData(store))
        {
            AssertReadBlocked(store);
            lockedChanged = monitor.Poll();
            Assert.True(monitor.Current is null, $"Initial locked poll: changed={lockedChanged}; {Describe(monitor.Current)}");
        }

        Assert.Equal(stamp, Stamp(store.Path));
        var recoveredChanged = monitor.Poll();
        Assert.True(expected.SameAs(monitor.Current),
            $"Must retry unchanged stamp after initial failure: lockedChanged={lockedChanged}, recoveredChanged={recoveredChanged}, stamp={stamp}; {Describe(monitor.Current)}");
        Assert.True(recoveredChanged, "Recovery from null to valid content must report a change.");
    }

    // Desired behavior: after a changed inbox is temporarily unreadable, recover the new
    // content without another write, preserving last-good data throughout the failure.
    [Fact]
    public async Task Poll_ChangedFileTemporarilyLocked_RetriesWithoutAnotherWrite()
    {
        var store = await SeedAsync();
        var monitor = new CursorActivityMonitor(_root);
        Assert.True(monitor.Poll());
        var lastGood = monitor.Current;
        var oldStamp = Stamp(store.Path);
        Assert.True(await store.RecordAsync(Entry(AccountA, "updated-model-with-different-length", 2), CancellationToken.None));
        var expected = store.Read()!;
        var stamp = Stamp(store.Path);
        Assert.NotEqual(oldStamp, stamp);
        bool lockedChanged;
        string lockedState;
        using (var held = LockData(store))
        {
            AssertReadBlocked(store);
            lockedChanged = monitor.Poll();
            lockedState = Describe(monitor.Current);
            Assert.False(lockedChanged);
            Assert.Same(lastGood, monitor.Current);
        }

        Assert.Equal(stamp, Stamp(store.Path));
        var recoveredChanged = monitor.Poll();
        Assert.True(expected.SameAs(monitor.Current),
            $"Must retry failed changed stamp: lockedChanged={lockedChanged}, lockedState={lockedState}, recoveredChanged={recoveredChanged}; {Describe(monitor.Current)}");
        Assert.False(monitor.Poll());
    }

    // Control: genuinely corrupt content stays unknown; a later valid write with a new
    // stamp must restore Current. No lock or transient read failure is involved here.
    [Fact]
    public async Task Poll_PermanentlyCorruptFileStaysNull_ThenNewValidStampRecovers()
    {
        var store = await SeedAsync();
        var valid = File.ReadAllBytes(store.Path);
        File.WriteAllText(store.Path, "garbage");
        var corruptStamp = Stamp(store.Path);
        var monitor = new CursorActivityMonitor(_root);
        var firstChanged = monitor.Poll();
        var secondChanged = monitor.Poll();
        Assert.True(monitor.Current is null && !firstChanged && !secondChanged,
            $"Corrupt file: firstChanged={firstChanged}, secondChanged={secondChanged}; {Describe(monitor.Current)}");
        File.WriteAllBytes(store.Path, valid);
        Assert.NotEqual(corruptStamp, Stamp(store.Path));
        var changed = monitor.Poll();
        Assert.True(changed && store.Read()!.SameAs(monitor.Current),
            $"Rewritten valid file: changed={changed}; {Describe(monitor.Current)}");
    }

    // Control: invalid JSON and a structurally invalid null entry are actual corruption,
    // not evidence of transient account loss. A fresh valid receipt can rebuild this inbox.
    [Theory]
    [InlineData("")]
    [InlineData("garbage bytes")]
    [InlineData("{\"version\":1,\"entries\":[null]}")]
    public async Task RecordAsync_GenuinelyCorruptInbox_RebuildsWithValidReceipt(string corrupt)
    {
        var store = new CursorActivityStore(_root);
        File.WriteAllText(store.Path, corrupt);
        Assert.True(store.Read() is null, $"Corrupt fixture unexpectedly accepted: {corrupt}");
        Assert.Equal(corrupt, File.ReadAllText(store.Path));
        var entry = Entry(AccountA, "replacement", 2);
        var changed = await store.RecordAsync(entry, CancellationToken.None);
        var actual = store.Read();
        Assert.True(changed && actual is { Entries.Count: 1 } && actual.Entries[0] == entry,
            $"Corrupt-inbox recovery: changed={changed}; {Describe(actual)}; disk={File.ReadAllText(store.Path)}");
    }

    // Desired behavior: an IOException while reading a valid inbox must never erase B.
    // Lock ONLY the data file, not AcquireAsync's separate .lock file. Keep the lock until
    // RecordAsync settles (bounded by cancellation), then unlock before rereading disk.
    // The read must fail before replacement is attempted. Linux advisory FileShare locking
    // exercises this host only and is not evidence of Windows sharing behavior.
    [Fact]
    public async Task RecordAsync_TemporaryReadIOException_PreservesOtherAccountOnDisk()
    {
        var store = await SeedAsync();
        var before = store.Read()!;
        var originalBytes = File.ReadAllBytes(store.Path);
        bool? changed = null;
        Exception? failure = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var held = LockData(store))
        {
            AssertReadBlocked(store);
            // Prove that the unrelated writer lease is available; release it before calling.
            using (var lease = new FileStream(store.Path + ".lock", FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None)) { }
            try { changed = await store.RecordAsync(Entry(AccountA, "new-A", 2), cancellation.Token); }
            catch (Exception ex) { failure = ex; }
        }

        var disk = File.ReadAllText(store.Path);
        var actual = store.Read();
        var bytesUnchanged = originalBytes.SequenceEqual(File.ReadAllBytes(store.Path));
        var diagnostic = $"OS={Environment.OSVersion}; changed={changed}; exception={failure}; bytesUnchanged={bytesUnchanged}; before={Describe(before)}; after={Describe(actual)}; disk={disk}";
        _output.WriteLine(diagnostic);
        // Always inspect the post-operation file before asserting completion/failure details.
        Assert.True(actual?.Entries.Any(e => e == before.Entries.Single(b => b.AccountKey == AccountB)) == true,
            "Transient read failure must preserve B in the actual file. " + diagnostic);
        // The lock is held for the entire call: fail immediately on the read error,
        // with no successful/false return and no write, rather than merging an empty inbox.
        Assert.IsAssignableFrom<IOException>(failure);
        Assert.Null(changed);
        Assert.True(bytesUnchanged, diagnostic);
    }

    // Inject at the read boundary as well as exercising actual locks above. Linux
    // FileShare.None uses advisory locking; its results do not validate Windows sharing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_InjectedReadFailure_PreservesStateAndRetries(bool accessDenied)
    {
        var store = await SeedAsync();
        var fail = true;
        var reads = 0;
        var reader = new CursorActivityStore(_root, () =>
        {
            reads++;
            if (fail)
            {
                if (accessDenied) throw new UnauthorizedAccessException("synthetic");
                throw new IOException("synthetic");
            }
        });
        var monitor = new CursorActivityMonitor(reader);
        Assert.False(monitor.Poll());
        Assert.Null(monitor.Current);
        fail = false;
        Assert.True(monitor.Poll());
        var lastGood = monitor.Current;
        Assert.True(await store.RecordAsync(Entry(AccountA, "longer-new-model", 2), CancellationToken.None));
        fail = true;
        Assert.False(monitor.Poll());
        Assert.Same(lastGood, monitor.Current);
        fail = false;
        Assert.True(monitor.Poll());
        Assert.True(store.Read()!.SameAs(monitor.Current));
        var completedReads = reads;
        Assert.False(monitor.Poll());
        Assert.Equal(completedReads, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_InjectedReadFailure_PreservesDiskAndReleasesLease(bool accessDenied)
    {
        var store = await SeedAsync();
        var original = File.ReadAllBytes(store.Path);
        var reader = new CursorActivityStore(_root, () =>
        {
            if (accessDenied) throw new UnauthorizedAccessException("synthetic");
            throw new IOException("synthetic");
        });
        var entry = Entry(AccountA, "new-model", 2);
        if (accessDenied)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.RecordAsync(entry, CancellationToken.None));
        else
            await Assert.ThrowsAsync<IOException>(() => reader.RecordAsync(entry, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(store.Path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.True(await store.RecordAsync(entry, CancellationToken.None));
        Assert.Contains(store.Read()!.Entries, e => e.AccountKey == AccountB && e.Model == "old-B");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{\"version\":1,\"entries\":[null]}")]
    [InlineData("")]
    public async Task Poll_CorruptStampIsCommitted_DeletionClearsCurrent(string corrupt)
    {
        var store = await SeedAsync();
        var reads = 0;
        var monitor = new CursorActivityMonitor(new CursorActivityStore(_root, () => reads++));
        Assert.True(monitor.Poll());
        File.WriteAllText(store.Path, corrupt);
        Assert.True(monitor.Poll());
        Assert.Null(monitor.Current);
        var corruptReads = reads;
        Assert.False(monitor.Poll());
        Assert.Equal(corruptReads, reads);
        Assert.True(await store.RecordAsync(Entry(AccountA, "recovered", 2), CancellationToken.None));
        Assert.True(monitor.Poll());
        File.Delete(store.Path);
        Assert.True(monitor.Poll());
        Assert.Null(monitor.Current);
        Assert.False(monitor.Poll());
        Assert.True(await store.RecordAsync(Entry(AccountB, "created", 3), CancellationToken.None));
        Assert.True(monitor.Poll());
    }

    private async Task<CursorActivityStore> SeedAsync()
    {
        var store = new CursorActivityStore(_root);
        Assert.True(await store.RecordAsync(Entry(AccountA, "old-A", 0), CancellationToken.None));
        Assert.True(await store.RecordAsync(Entry(AccountB, "old-B", 1), CancellationToken.None));
        Assert.True(store.Read() is { Entries.Count: 2 }, "Seed must contain two valid, distinct accounts.");
        return store;
    }

    private static CursorActivityEntry Entry(string key, string model, int seconds) =>
        new(CursorActivityKind.Request, model, null, null, null, key, null, Now.AddSeconds(seconds));

    private static FileStream LockData(CursorActivityStore store) =>
        new(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static void AssertReadBlocked(CursorActivityStore store)
    {
        // Assert the mechanism, rather than treating Read()==null alone as proof of an I/O error.
        var error = Record.Exception(() =>
        {
            using var probe = new FileStream(store.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.True(error is IOException,
            $"Host did not produce required read IOException under FileShare.None: OS={Environment.OSVersion}, error={error}");
        Assert.True(store.Read() is null, "Read must return null during confirmed sharing IOException.");
    }

    private static (DateTime Written, long Length) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }

    private static string Describe(CursorActivityState? state) => state is null ? "Current=null" :
        $"version={state.Version}, count={state.Entries.Count}, entries=[{string.Join(", ", state.Entries.Select(e => $"{e.AccountKey}:{e.Model}:{e.ReceivedAt:O}"))}]";
}
