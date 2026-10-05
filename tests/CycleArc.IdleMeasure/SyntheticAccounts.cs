using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.IdleMeasure;

// External adapters are synthetic; manager, protocol parser, providers, collectors,
// binding checks, cache writes and refresh gates are the current production code.
internal sealed class SyntheticAccounts : IAsyncDisposable
{
    public const int RequestDelayMilliseconds = 10;
    public const int SamplesPerClaudeAccount = 64;
    private static readonly string[] Ids =
    [
        "10000000000000000000000000000001", "10000000000000000000000000000002",
        "20000000000000000000000000000001", "20000000000000000000000000000002",
        "30000000000000000000000000000001"
    ];
    private readonly CodexAccountProfile[] _profiles;
    private readonly Dictionary<string, ClaudeAuthentication> _claudeIdentities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExpectedQuota> _expected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeSequences = new(StringComparer.Ordinal);
    private readonly HttpClient _cursorHttp;
    private readonly CursorUsageClient _cursorClient;
    private readonly string _historyPath;
    private readonly string _executable;
    private readonly DateTimeOffset _initialHistoryAt;
    private DateTimeOffset _desktopAt;
    private double _desktopPercent = 31.25;
    private int _passiveSequence;
    private long _codexStarts, _codexQuotaRequests, _claudeLiveRequests, _cursorUsageRequests;
    private long _cursorHttpRequests, _claudeAuthCalls, _claudeHistoryReadCalls, _changed;
    private long _syntheticRequestTicks;

    public string RootDirectory { get; }
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public IReadOnlyList<string> ProfileIds => Ids;
    public CodexAccountStore Store { get; }
    public CodexAccountManager Manager { get; }

    public SyntheticAccounts(string root)
    {
        RootDirectory = Path.GetFullPath(root);
        Directory.CreateDirectory(RootDirectory);
        _executable = Path.Combine(RootDirectory, "external-adapters", "codex.exe");
        _historyPath = Path.Combine(RootDirectory, "synthetic-desktop", ClaudeDesktopUsageReader.FileName);
        _initialHistoryAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds());
        _desktopAt = _initialHistoryAt;
        Store = new CodexAccountStore(RootDirectory);
        _profiles = Ids.Select((id, index) => new CodexAccountProfile(id,
            index < 2 ? Directory.CreateDirectory(Path.Combine(RootDirectory, "accounts", id, "codex-home")).FullName : "",
            $"Synthetic {index + 1}", index < 2)
        { Provider = index < 2 ? UsageProviderId.Codex : index < 4 ? UsageProviderId.Claude : UsageProviderId.Cursor }).ToArray();
        // Seed before manager construction: never migrate a user's/default Codex home.
        Store.Save(new CodexAccountConfiguration(3, Ids[0], _profiles));
        for (var index = 2; index < 4; index++)
        {
            var profile = _profiles[index];
            var auth = new ClaudeAuthentication(ClaudeAuthStatus.SignedIn, $"synthetic-{index}@example.invalid", "pro",
                OrganizationId: $"73ea74ee-7e58-427a-8593-7bc000000{index:000}");
            _claudeIdentities.Add(profile.Id, auth);
            new ClaudeConnectionStore(Store, profile.Id).Save(new ClaudeConnectionBinding(2, profile.Id,
                Path.Combine(RootDirectory, "claude-config", profile.Id), Path.Combine(RootDirectory, "external-adapters", "claude.exe"),
                false, auth.StableFingerprint!, _initialHistoryAt.AddDays(-1), BindingGeneration: profile.Id, Plan: "pro"));
        }
        new CursorConnectionStore(Store, Ids[4]).Save(new CursorConnectionBinding(1, Ids[4],
            CursorIdentity.Fingerprint("synthetic-cursor@example.invalid", "auth0|synthetic-cursor")!,
            _initialHistoryAt.AddDays(-1), Ids[4]));
        _cursorHttp = new HttpClient(new SyntheticCursorHandler(this)) { Timeout = TimeSpan.FromSeconds(15) };
        _cursorClient = new CursorUsageClient(new SyntheticCursorAuth(), _cursorHttp);
        var reader = new ClaudeDesktopUsageReader([_historyPath]);
        var locator = new CodexExecutableLocator(new SyntheticFileSystem(_executable));
        Manager = new CodexAccountManager(Store, Path.Combine(RootDirectory, "unused-default-home"),
        [
            new CodexUsageProvider(profile => new CodexQuotaService(locator,
                new CodexAppServerClient(new SyntheticCodexFactory(this, profile)),
                new CodexSnapshotStore(Store.SnapshotPath(profile)), "idle-synthetic", profile: profile), () => _executable),
            new ClaudeUsageProvider(Store,
                desktopFactory: profile => new ClaudeDesktopUsageCollector(Store, profile.Id,
                    async (_, token) =>
                    {
                        Interlocked.Increment(ref _claudeAuthCalls);
                        await DelayRequestAsync(token).ConfigureAwait(false);
                        return _claudeIdentities[profile.Id];
                    }, read: (organization, now) =>
                    {
                        // Counts delegated reader calls, not file opens or Parse invocations.
                        Interlocked.Increment(ref _claudeHistoryReadCalls);
                        return reader.Read(organization, now);
                    }),
                liveFactory: profile => new ClaudeLiveUsageCollector(Store, profile.Id, new SyntheticClaudeClient(this, profile))),
            new CursorUsageProvider(Store, clientFactory: _ => _cursorClient,
                authFactory: _ => new SyntheticCursorAuth(),
                sourceFactory: profile => new CursorUsageCollector(Store, profile.Id, _cursorClient))
        ]);
        Manager.Changed += () => Interlocked.Increment(ref _changed);
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        WriteHistory();
        for (var index = 2; index < 4; index++)
            await WriteStatusLineAsync(Ids[index], _initialHistoryAt.AddSeconds(-1), 21.25 + index, token).ConfigureAwait(false);
        await Manager.RefreshManuallyAsync(token).ConfigureAwait(false);
        await Manager.RefreshPassiveAsync(token).ConfigureAwait(false);
        AssertLatestAndIsolation();
    }

    public FixtureCounters CaptureCounters()
    {
        var observations = Manager.ObservationStatistics;
        return new(
        Interlocked.Read(ref _codexStarts), Interlocked.Read(ref _codexQuotaRequests),
        Interlocked.Read(ref _claudeLiveRequests), Interlocked.Read(ref _cursorUsageRequests),
        Interlocked.Read(ref _cursorHttpRequests), Interlocked.Read(ref _claudeAuthCalls),
        Interlocked.Read(ref _claudeHistoryReadCalls), Interlocked.Read(ref _changed),
        Interlocked.Read(ref _syntheticRequestTicks) * 1000.0 / Stopwatch.Frequency,
        observations.AcceptedSnapshots, observations.IgnoredSnapshots, observations.LoadAttempts,
        observations.SaveAttempts, observations.SavedFiles, observations.EpochWrites,
        observations.Failures, observations.PublishedRevisions);
    }

    public async Task PublishPassiveUpdateAsync(CancellationToken token = default)
    {
        // Keep fixture observations strictly later than a just-completed live sample.
        await Task.Delay(20, token).ConfigureAwait(false);
        var sequence = Interlocked.Increment(ref _passiveSequence);
        _desktopAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _desktopPercent = 69.25 + sequence % 10 / 10.0;
        WriteHistory();
        var statusAt = DateTimeOffset.UtcNow;
        var statusPercent = 82.5 + sequence % 10 / 10.0;
        await WriteStatusLineAsync(Ids[3], statusAt, statusPercent, token).ConfigureAwait(false);
        _expected[Ids[2]] = new(_desktopPercent, _desktopAt, false, "claude-desktop-history");
        _expected[Ids[3]] = new(statusPercent, statusAt, false, null);
    }

    public void AssertLatestAndIsolation()
    {
        var accounts = Manager.Accounts;
        Require(accounts.Count == 5 && accounts.Select(account => account.Profile.Id).SequenceEqual(Ids), "account count/order");
        Require(accounts.Select(account => account.Profile.Provider).SequenceEqual(_profiles.Select(profile => profile.Provider)), "provider isolation");
        foreach (var account in accounts)
        {
            Require(_expected.TryGetValue(account.Profile.Id, out var expected), "missing synthetic expectation");
            var snapshot = account.Snapshot;
            Require(account.IsConnected && !account.HasMatchingIdentity && snapshot.Status == CodexQuotaStatus.Available,
                "connected independent available accounts: " + account.Profile.Id + "/" + snapshot.TechnicalDetail);
            var window = account.Profile.Provider == UsageProviderId.Cursor
                ? snapshot.Windows.Single(window => window.LimitId == "cursor-auto")
                : snapshot.Windows.Single(window => window.Kind == CodexWindowKind.FiveHour);
            Require(window.UsedPercent == expected!.Percent, "latest per-account percentage: " + account.Profile.Id);
            if (expected.At is { } at) Require(snapshot.LastSuccessfulRefresh == at, "original receipt timestamp: " + account.Profile.Id);
            if (account.Profile.Provider == UsageProviderId.Claude)
            {
                Require(snapshot.LastSuccessfulObservationWasServer == expected.Server, "receipt/live distinction");
                Require(snapshot.TechnicalDetail == expected.Detail, "source ordering: " + account.Profile.Id);
            }
        }
        Require(accounts.Select(account => _expected[account.Profile.Id].Percent).Distinct().Count() == 5, "distinct quotas");
    }

    private async Task DelayRequestAsync(CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        try { await Task.Delay(RequestDelayMilliseconds, token).ConfigureAwait(false); }
        finally { Interlocked.Add(ref _syntheticRequestTicks, Stopwatch.GetTimestamp() - start); }
    }

    private void WriteHistory()
    {
        var samples = _profiles.Skip(2).Take(2).SelectMany((profile, index) =>
            Enumerable.Range(0, SamplesPerClaudeAccount).Select(sample => new
            {
                t = (sample == SamplesPerClaudeAccount - 1
                    ? index == 0 ? _desktopAt : _desktopAt.AddSeconds(-1)
                    : _initialHistoryAt.AddMinutes(-5 * (SamplesPerClaudeAccount - 1 - sample))).ToUnixTimeMilliseconds(),
                org = _claudeIdentities[profile.Id].OrganizationId,
                u = new { fh = sample == SamplesPerClaudeAccount - 1 ? index == 0 ? _desktopPercent : 43.75 : 10.25 + index,
                    sd = 7.25 + index, xu = (double?)null }
            }));
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        var temporary = _historyPath + ".new";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new { version = 2, samples }));
        File.Move(temporary, _historyPath, overwrite: true);
    }

    private Task WriteStatusLineAsync(string id, DateTimeOffset at, double percent, CancellationToken token)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(new { rate_limits = new
        {
            five_hour = new { used_percentage = percent, resets_at = at.AddHours(5).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = 9.25, resets_at = at.AddDays(7).ToUnixTimeSeconds() }
        }});
        return new ClaudeStatusLineStore(Store.ClaudeStatusLinePath(id), id)
            .RecordAsync(ClaudeStatusLineParser.Parse(input), at, token);
    }

    private int NextActiveSequence(string id) => _activeSequences.AddOrUpdate(id, 1, (_, count) => count + 1);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Synthetic fixture failed: " + message);
    }

    public ValueTask DisposeAsync()
    {
        _cursorClient.Dispose();
        _cursorHttp.Dispose();
        // Preserve the isolated fixture and reports for later verification.
        return ValueTask.CompletedTask;
    }

    private sealed record ExpectedQuota(double Percent, DateTimeOffset? At = null, bool? Server = null, string? Detail = null);

    private sealed class SyntheticFileSystem(string executable) : ICodexFileSystem
    {
        public bool FileExists(string path) => path.Equals(executable, StringComparison.OrdinalIgnoreCase);
        public IReadOnlyList<string> PathEntries() => [];
        public IReadOnlyList<string> CommonDirectories() => [];
    }

    private sealed class SyntheticCodexFactory(SyntheticAccounts fixture, CodexAccountProfile profile) : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command)
        {
            Require(command.CodexHome == profile.HomePath && command.ResolvedExecutable == fixture._executable,
                "explicit synthetic Codex command/home");
            Interlocked.Increment(ref fixture._codexStarts);
            return new SyntheticCodexProcess(fixture, profile);
        }
    }

    // No subprocess is launched. JSON-RPC still passes through production protocol parsing,
    // account verification, bounded read APIs and normal client completion/cancellation.
    private sealed class SyntheticCodexProcess(SyntheticAccounts fixture, CodexAccountProfile profile) : ICodexProcess
    {
        private readonly Channel<string> _responses = Channel.CreateUnbounded<string>();
        public bool HasExited { get; private set; }
        public bool KillCalled { get; private set; }
        public int? ProcessId => null;
        public string FileName => fixture._executable;
        public string Arguments => CodexProcessQuoting.AppServerArguments;

        public async Task WriteLineAsync(string line, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialized") return;
            var id = root.GetProperty("id").GetInt32();
            await fixture.DelayRequestAsync(token).ConfigureAwait(false);
            object result;
            if (method == "initialize") result = new { ok = true };
            else if (method == "account/read") result = new
            {
                account = new { type = "chatgpt", email = profile.Id == Ids[0] ? "synthetic-codex-1@example.invalid" : "synthetic-codex-2@example.invalid", planType = "pro" },
                requiresOpenaiAuth = true
            };
            else if (method == "account/rateLimits/read")
            {
                Interlocked.Increment(ref fixture._codexQuotaRequests);
                var sequence = fixture.NextActiveSequence(profile.Id);
                var percent = (profile.Id == Ids[0] ? 11.25 : 23.5) + sequence % 10 / 10.0;
                var now = DateTimeOffset.UtcNow;
                var bucket = new
                {
                    limitId = "codex", planType = "pro",
                    primary = new { usedPercent = percent, windowDurationMins = 300, resetsAt = now.AddHours(5).ToUnixTimeSeconds() },
                    secondary = new { usedPercent = 8.25, windowDurationMins = 10080, resetsAt = now.AddDays(7).ToUnixTimeSeconds() },
                    rateLimitReachedType = (string?)null
                };
                result = new { ordinaryUsageAllowed = true, rateLimits = bucket,
                    rateLimitsByLimitId = new { codex = bucket }, rateLimitResetCredits = new { availableCount = 0, credits = (object?)null } };
                // Production service owns the response time; only values are predicted here.
                fixture._expected[profile.Id] = new(percent);
            }
            else throw new InvalidDataException("Unexpected synthetic Codex method.");
            await _responses.Writer.WriteAsync(JsonSerializer.Serialize(new { id, result }), token).ConfigureAwait(false);
        }

        public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken token)
        {
            var line = await _responses.Reader.ReadAsync(token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(line) > maxBytes) throw new InvalidDataException("Synthetic protocol exceeds bounded read.");
            return line;
        }
        public Task DrainStderrAsync(StringBuilder sink, int maxBytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(HasExited); }
        public void KillTree() { KillCalled = true; HasExited = true; _responses.Writer.TryComplete(); }
        public ValueTask DisposeAsync() { KillTree(); return ValueTask.CompletedTask; }
    }

    private sealed class SyntheticClaudeClient(SyntheticAccounts fixture, CodexAccountProfile profile) : IClaudeLiveUsageClient
    {
        public async Task<ClaudeLiveUsageResponse> FetchAsync(ClaudeConnectionBinding binding, CancellationToken token)
        {
            Require(binding.ProfileId == profile.Id && binding.IdentityFingerprint == fixture._claudeIdentities[profile.Id].StableFingerprint,
                "synthetic Claude live binding");
            Interlocked.Increment(ref fixture._claudeLiveRequests);
            await fixture.DelayRequestAsync(token).ConfigureAwait(false);
            var sequence = fixture.NextActiveSequence(profile.Id);
            var percent = (profile.Id == Ids[2] ? 37.25 : 49.5) + sequence % 10 / 10.0;
            var now = DateTimeOffset.UtcNow;
            fixture._expected[profile.Id] = new(percent, now, true, "claude-live");
            return new(new ClaudeLiveUsageSample(now, new(percent, now.AddHours(5)), new(12.25, now.AddDays(7))));
        }
    }

    private sealed class SyntheticCursorAuth : ICursorAuthSource
    {
        private readonly string _token = Jwt();
        public CursorAuthRead Read() => new(_token);
        private static string Jwt()
        {
            static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Base64("{\"alg\":\"none\"}") + "."
                + Base64(JsonSerializer.Serialize(new { sub = "auth0|synthetic-cursor", exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() })) + ".synthetic";
        }
    }

    // All Cursor HTTP traffic terminates here: no HttpClientHandler/socket/fallback exists.
    private sealed class SyntheticCursorHandler(SyntheticAccounts fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri?.Host == "cursor.com", "synthetic Cursor host");
            Interlocked.Increment(ref fixture._cursorHttpRequests);
            await fixture.DelayRequestAsync(token).ConfigureAwait(false);
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/me":
                    body = new { sub = "auth0|synthetic-cursor", email = "synthetic-cursor@example.invalid" };
                    break;
                case "/api/usage-summary":
                    Interlocked.Increment(ref fixture._cursorUsageRequests);
                    var sequence = fixture.NextActiveSequence(Ids[4]);
                    var percent = 61.75 + sequence % 10 / 10.0;
                    var now = DateTimeOffset.UtcNow;
                    body = new { billingCycleStart = now.AddDays(-1), billingCycleEnd = now.AddDays(29), membershipType = "pro", limitType = "user",
                        individualUsage = new { plan = new { enabled = true, used = 1250, limit = 2000, remaining = 750,
                            autoPercentUsed = percent, apiPercentUsed = 14.25 },
                            onDemand = new { enabled = false, used = 0, limit = (int?)null, remaining = (int?)null } }, teamUsage = new { } };
                    fixture._expected[Ids[4]] = new(percent);
                    break;
                case "/api/dashboard/get-sand-usage-status":
                    body = new { usagePercent = 3.25, hasAvailableUsage = true, includedLimitZero = false,
                        nextResetTimestampUtc = DateTimeOffset.UtcNow.AddDays(1) };
                    break;
                default: throw new InvalidDataException("Unexpected synthetic Cursor route.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}

internal sealed record FixtureCounters(long CodexStarts, long CodexQuotaRequests, long ClaudeLiveRequests,
    long CursorUsageRequests, long CursorHttpRequests, long ClaudeAuthCalls, long ClaudeHistoryReadCalls,
    long Changed, double SyntheticRequestMilliseconds,
    long ObservationAcceptedSnapshots, long ObservationIgnoredSnapshots, long ObservationLoadAttempts,
    long ObservationSaveAttempts, long ObservationSavedFiles, long ObservationEpochWrites,
    long ObservationFailures, long ObservationPublishedRevisions);
