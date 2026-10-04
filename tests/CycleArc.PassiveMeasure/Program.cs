using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

// Opt-in, isolated process; explicit synthetic paths, fake auth and no live source.
var label = args.Length > 0 ? args[0] : "unspecified";
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
var rows = new List<Measurement>();
foreach (var accountCount in new[] { 1, 5 })
foreach (var samplesPerAccount in new[] { 1, 64, 512 })
foreach (var scenario in new[] { "reader-unchanged", "passive-unchanged", "passive-desktop-update", "passive-statusline-update" })
{
    await using var fixture = new Fixture(accountCount, samplesPerAccount);
    await fixture.Initialize();
    var ticks = scenario == "reader-unchanged" ? 80 : scenario == "passive-unchanged" ? 40 : 20;
    for (var warmup = 0; warmup < 12; warmup++) await Tick(scenario, fixture);
    for (var trial = 1; trial <= 5; trial++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        fixture.ResetCounters();
        long elapsedTicks = 0, allocated = 0;
        for (var tick = 0; tick < ticks; tick++)
        {
            await fixture.Prepare(scenario); // Fixture writes and JSON construction are excluded.
            var beforeAllocation = GC.GetTotalAllocatedBytes(precise: true);
            var beforeTime = Stopwatch.GetTimestamp();
            await fixture.Read(scenario);
            elapsedTicks += Stopwatch.GetTimestamp() - beforeTime;
            allocated += GC.GetTotalAllocatedBytes(precise: true) - beforeAllocation;
            fixture.AssertLatest(scenario); // Validation allocations are excluded.
        }
        var updated = scenario.Contains("update", StringComparison.Ordinal);
        var expectedChanges = scenario.StartsWith("passive", StringComparison.Ordinal) && updated ? ticks * accountCount : 0;
        Require(fixture.Changed == expectedChanges, "Changed count");
        Require(fixture.AuthCalls == (scenario == "passive-desktop-update" ? ticks * accountCount : 0), "Auth count");
        Require(fixture.HistoryOpens == ticks * accountCount && fixture.HistoryParses == ticks * accountCount, "History counts");
        var row = new Measurement(scenario, accountCount, samplesPerAccount, accountCount * samplesPerAccount,
            fixture.HistoryBytes, trial, ticks, elapsedTicks * 1000.0 / Stopwatch.Frequency / ticks,
            allocated / (double)ticks, fixture.HistoryOpens, fixture.HistoryParses,
            fixture.AuthCalls, fixture.Changed, fixture.LastAcceptedAt);
        rows.Add(row);
        Console.WriteLine($"{label} {scenario} accounts={accountCount} samples/account={samplesPerAccount} trial={trial}: " +
            $"{row.MillisecondsPerTick:F3} ms/tick, {row.AllocatedBytesPerTick:F0} B/tick; " +
            $"history open/parse={row.DesktopHistoryFileOpens}/{row.DesktopHistoryParseCalls}, auth={row.FakeAuthCalls}, Changed={row.Changed}");
    }
}
var report = new
{
    Label = label, Runtime = RuntimeInformation.FrameworkDescription,
    OperatingSystem = RuntimeInformation.OSDescription, ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
    WarmupTicks = 12, Trials = 5,
    Method = "Serial synthetic reader or complete ClaudeQuotaService.RefreshAsync calls (passive path), warm file cache; " +
        "fixture writes/assertions excluded; total process allocations via GC.GetTotalAllocatedBytes(true); " +
        "Desktop-history open and Parse invocation counts only, not connection/statusLine/cache I/O; no live source or CLI process.",
    Rows = rows
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (output is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, json);
}
else Console.WriteLine(json);

static async Task Tick(string scenario, Fixture fixture)
{
    await fixture.Prepare(scenario);
    await fixture.Read(scenario);
    fixture.AssertLatest(scenario);
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Synthetic measurement failed: " + message);
}

internal sealed record Measurement(string Scenario, int Accounts, int SamplesPerAccount, int TotalHistorySamples,
    int HistoryBytes, int Trial, int Ticks, double MillisecondsPerTick, double AllocatedBytesPerTick,
    int DesktopHistoryFileOpens, int DesktopHistoryParseCalls, int FakeAuthCalls, int Changed, DateTimeOffset LastAcceptedAt);

internal sealed class Fixture : IAsyncDisposable
{
    private static readonly DateTimeOffset InitialNow = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyclearc-passive-measure-" + Guid.NewGuid().ToString("N"));
    private readonly int _samplesPerAccount;
    private readonly List<Account> _accounts = [];
    private readonly MutableClock _clock = new(InitialNow);
    private readonly ClaudeDesktopUsageReader _reader;
    private readonly string _historyPath;
    private int _sequence;
    private DateTimeOffset _desktopAt = InitialNow;
    private double _desktopPercent = 15;
    private double _statusPercent;
    public int HistoryBytes { get; private set; }
    public int HistoryOpens, HistoryParses, AuthCalls, Changed;
    public DateTimeOffset LastAcceptedAt { get; private set; }

    public Fixture(int count, int samplesPerAccount)
    {
        _samplesPerAccount = samplesPerAccount;
        Directory.CreateDirectory(_root);
        _historyPath = Path.Combine(_root, ClaudeDesktopUsageReader.FileName);
        _reader = new ClaudeDesktopUsageReader([_historyPath], observed =>
        {
            if (observed == ClaudeDesktopUsageReadOperation.FileOpened) HistoryOpens++;
            else if (observed == ClaudeDesktopUsageReadOperation.ParseInvoked) HistoryParses++;
        });
        var store = new CodexAccountStore(_root);
        var registry = store.LoadOrMigrate(Path.Combine(_root, "synthetic-codex-home"));
        for (var i = 0; i < count; i++)
        {
            var organization = $"73ea74ee-7e58-427a-8593-7bc000000{i + 1:000}";
            var identity = new ClaudeAuthentication(ClaudeAuthStatus.SignedIn, $"synthetic-{i}@example.invalid", "pro", OrganizationId: organization);
            var profile = store.NewClaude($"Synthetic {i}");
            registry = registry with { Version = 3, Profiles = registry.Profiles.Append(profile).ToArray() };
            store.Save(registry);
            var connections = new ClaudeConnectionStore(store, profile.Id);
            connections.Save(new(2, profile.Id, Path.Combine(_root, "config-" + i), Path.Combine(_root, "fake-claude.exe"),
                false, identity.StableFingerprint!, InitialNow.AddDays(-30), BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro"));
            var inbox = new ClaudeStatusLineStore(store.ClaudeStatusLinePath(profile.Id), profile.Id);
            var collector = new ClaudeDesktopUsageCollector(store, profile.Id, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                AuthCalls++;
                return Task.FromResult(identity);
            }, _clock, _reader.Read);
            var service = new ClaudeQuotaService(inbox, _clock, connections.Read, failureStore: new(store), desktop: collector);
            service.Changed += _ => Changed++;
            _accounts.Add(new(organization, inbox, service));
        }
    }

    public async Task Initialize()
    {
        WriteHistory();
        foreach (var account in _accounts)
        {
            await WriteStatusLine(account, InitialNow.AddMinutes(-1), 4);
            await account.Service.RefreshAsync(default);
        }
        ResetCounters();
    }

    public async Task Prepare(string scenario)
    {
        if (!scenario.Contains("update", StringComparison.Ordinal)) return;
        _sequence++;
        _clock.UtcNow = InitialNow.AddSeconds(_sequence * 2);
        if (scenario == "passive-desktop-update")
        {
            _desktopAt = _clock.UtcNow;
            _desktopPercent = 15 + _sequence % 70 / 10.0;
            WriteHistory();
        }
        else
        {
            _statusPercent = 25 + _sequence % 70 / 10.0;
            foreach (var account in _accounts) await WriteStatusLine(account, _clock.UtcNow, _statusPercent);
        }
    }

    public async Task Read(string scenario)
    {
        foreach (var account in _accounts)
        {
            if (scenario == "reader-unchanged")
            {
                var read = _reader.Read(account.Organization, _clock.UtcNow);
                if (read.Unavailable || read.Sample?.ObservedAt != _desktopAt || read.Sample.FiveHour != _desktopPercent)
                    throw new InvalidOperationException("Synthetic reader did not accept newest sample.");
            }
            else await account.Service.RefreshAsync(default);
        }
    }

    public void AssertLatest(string scenario)
    {
        LastAcceptedAt = scenario == "passive-statusline-update" ? _clock.UtcNow : _desktopAt;
        if (scenario == "reader-unchanged") return;
        foreach (var account in _accounts)
        {
            var snapshot = account.Service.Snapshot;
            var expectedPercent = scenario == "passive-statusline-update" ? _statusPercent : _desktopPercent;
            var expectedSource = scenario == "passive-statusline-update" ? null : "claude-desktop-history";
            if (snapshot.LastSuccessfulRefresh != LastAcceptedAt || snapshot.Windows[0].UsedPercent != expectedPercent
                || snapshot.TechnicalDetail != expectedSource)
                throw new InvalidOperationException("Synthetic passive call failed latest data/source assertion: " + snapshot.TechnicalDetail);
        }
    }

    private void WriteHistory()
    {
        var samples = _accounts.SelectMany(account => Enumerable.Range(0, _samplesPerAccount).Select(index => new
        {
            t = (index == _samplesPerAccount - 1 ? _desktopAt : InitialNow.AddMinutes(-5 * (_samplesPerAccount - 1 - index))).ToUnixTimeMilliseconds(),
            org = account.Organization,
            u = new { fh = index == _samplesPerAccount - 1 ? _desktopPercent : 10.5, sd = 7.25, xu = (double?)null }
        }));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, samples });
        HistoryBytes = bytes.Length;
        var temporary = _historyPath + ".new";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, _historyPath, overwrite: true);
    }

    private async Task WriteStatusLine(Account account, DateTimeOffset at, double percent)
    {
        // Official protocol-shaped callback, projected by production parser/store.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { rate_limits = new
        {
            five_hour = new { used_percentage = percent, resets_at = at.AddHours(5).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = 7.25, resets_at = at.AddDays(7).ToUnixTimeSeconds() }
        }});
        await account.Inbox.RecordAsync(ClaudeStatusLineParser.Parse(bytes), at, default);
    }

    public void ResetCounters() => HistoryOpens = HistoryParses = AuthCalls = Changed = 0;
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
    private sealed record Account(string Organization, ClaudeStatusLineStore Inbox, ClaudeQuotaService Service);
}
