using System.Globalization;
using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CodexUsageCreditsTests
{
    [Theory]
    [InlineData("1250.5", "1250.5")]
    [InlineData("0", "0")]
    [InlineData("-12.375", "-12.375")]
    public void DecimalBalancesPreservePrecisionAndNegativeValues(string input, string expected)
    {
        var parsed = Parse(Credits(input));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), parsed.UsageCredits!.Balance);
        Assert.Equal("codex", parsed.UsageCredits.LimitId);
        Assert.Null(parsed.UsageCredits.ObservedAt);
        Assert.Null(parsed.UsageCreditsFailure);
        Assert.Equal(25, Assert.Single(parsed.Windows).UsedPercent);
        Assert.Null(parsed.ResetCreditsAvailable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FlagsNeverInventBalance(bool hasCredits, bool unlimited)
    {
        var credits = Credits(null);
        credits["hasCredits"] = hasCredits;
        credits["unlimited"] = unlimited;
        var parsed = Parse(credits);
        Assert.Equal(hasCredits, parsed.UsageCredits!.HasCredits);
        Assert.Equal(unlimited, parsed.UsageCredits.Unlimited);
        Assert.Null(parsed.UsageCredits.Balance);
        credits.Remove("balance");
        Assert.Null(Parse(credits).UsageCredits!.Balance);
        credits["balance"] = "-0.5";
        Assert.Equal(-0.5m, Parse(credits).UsageCredits!.Balance);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("$4.50")]
    [InlineData("1,000")]
    [InlineData("9999999999999999999999999999999999999")]
    public void MalformedCreditsDoNotDiscardSubscriptionWindows(string balance)
    {
        var parsed = Parse(Credits(balance));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(25, Assert.Single(parsed.Windows).UsedPercent);
        Assert.Null(parsed.UsageCredits);
        Assert.Equal("credits-malformed", parsed.UsageCreditsFailure);
    }

    [Fact]
    public void MissingCreditsAndWrongTypesStayUnknown()
    {
        Assert.Equal("credits-not-provided", Parse(null).UsageCreditsFailure);
        var credits = Credits("12");
        credits["hasCredits"] = "true";
        Assert.Null(Parse(credits).UsageCredits);
        credits["hasCredits"] = true;
        credits["balance"] = 12;
        Assert.Null(Parse(credits).UsageCredits);
        credits["balance"] = "12";
        credits.Remove("unlimited");
        Assert.Null(Parse(credits).UsageCredits);
    }

    [Fact]
    public void UsesSelectedBucketWithoutSummingOrBorrowingRootCredits()
    {
        var root = new JsonObject
        {
            ["credits"] = Credits("500"),
            ["rateLimits"] = Bucket(Credits("100")),
            ["rateLimitsByLimitId"] = new JsonObject
            {
                ["codex"] = Bucket(Credits("10")),
                ["other"] = Bucket(Credits("10"))
            },
            ["rateLimitResetCredits"] = new JsonObject { ["availableCount"] = 2 }
        };
        var parsed = CodexRateLimitParser.Parse(null, root);
        Assert.Equal(10m, parsed.UsageCredits!.Balance);
        Assert.Equal(2, parsed.ResetCreditsAvailable);
        root["rateLimitsByLimitId"]!["codex"]!.AsObject().Remove("credits");
        parsed = CodexRateLimitParser.Parse(null, root);
        Assert.Null(parsed.UsageCredits);
        Assert.Equal("credits-not-provided", parsed.UsageCreditsFailure);
    }

    [Fact]
    public async Task MissingMalformedAndFailedRefreshKeepOwnObservationThroughRestart()
    {
        using var fixture = new Fixture();
        var first = await fixture.Refresh();
        Assert.Equal(fixture.Clock.UtcNow, first.Snapshot.UsageCredits!.ObservedAt);
        var originalTime = first.Snapshot.UsageCredits.ObservedAt;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(5);
        fixture.CreditData = null;
        var missing = await fixture.Refresh();
        Assert.Equal(CodexQuotaStatus.Available, missing.Snapshot.Status);
        Assert.Equal(originalTime, missing.Snapshot.UsageCredits!.ObservedAt);
        Assert.Equal(fixture.Clock.UtcNow, missing.Snapshot.LastSuccessfulRefresh);
        Assert.Equal("credits-not-provided", missing.Snapshot.UsageCreditsFailure);

        fixture.Service = fixture.CreateService();
        Assert.Null(fixture.Service.Snapshot.UsageCredits); // Binding verification is required after restart.
        fixture.CreditData = Credits("broken");
        var malformed = await fixture.Refresh();
        Assert.Equal(-12.375m, malformed.Snapshot.UsageCredits!.Balance);
        Assert.Equal(originalTime, malformed.Snapshot.UsageCredits.ObservedAt);
        Assert.Equal("credits-malformed", malformed.Snapshot.UsageCreditsFailure);

        fixture.FailQuota = true;
        var failed = await fixture.Refresh();
        Assert.Equal(CodexQuotaStatus.Stale, failed.Snapshot.Status);
        Assert.Equal(originalTime, failed.Snapshot.UsageCredits!.ObservedAt);
        Assert.Equal(-12.375m, fixture.Store.Load()!.UsageCredits!.Balance);
        fixture.FailQuota = false;
        fixture.CreditData = Credits("0");
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        var recovered = await fixture.Refresh();
        Assert.Equal(0m, recovered.Snapshot.UsageCredits!.Balance);
        Assert.Equal(fixture.Clock.UtcNow, recovered.Snapshot.UsageCredits.ObservedAt);
        Assert.Null(recovered.Snapshot.UsageCreditsFailure);
    }

    [Fact]
    public async Task IdentityMismatchAndBucketSwitchNeverInheritCredits()
    {
        using var fixture = new Fixture();
        await fixture.Refresh();
        fixture.CreditData = null;
        fixture.LimitId = "another-bucket";
        Assert.Null((await fixture.Refresh()).Snapshot.UsageCredits);
        fixture.LimitId = "codex";
        fixture.CreditData = Credits("42");
        await fixture.Refresh();
        fixture.Email = "different@example.invalid";
        var mismatch = await fixture.Refresh();
        Assert.Null(mismatch.Snapshot.UsageCredits);
        Assert.Empty(mismatch.Snapshot.Windows);
        Assert.Equal("codex-identity-mismatch", mismatch.Snapshot.TechnicalDetail);
    }

    [Fact]
    public void ReadOnlyProbeProjectionsNeverCreateRegistryBindingOrLockFiles()
    {
        using var data = new AccountTestDirectory();
        var accounts = new CodexAccountStore(data.Root);
        Assert.Null(accounts.ReadExisting());
        var binding = new CodexIdentityBindingStore(Path.Combine(data.Root, "quota.json"), "default");
        Assert.Null(binding.ReadExisting().State);
        Assert.Empty(Directory.GetFiles(data.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CacheReadsLegacyAndStrictlyChecksOptionalCreditsWithBackupRecovery()
    {
        using var data = new AccountTestDirectory();
        var path = Path.Combine(data.Root, "credits-cache.json");
        var store = new CodexSnapshotStore(path);
        var old = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", null, null, null, null, null, [], null);
        store.Save(old);
        Assert.Null(store.Load()!.UsageCredits);
        var time = DateTimeOffset.Parse("2026-10-01T01:00:00Z");
        var snapshot = old with { UsageCredits = new(true, false, -0.25m, "codex", time) };
        store.Save(snapshot);
        Assert.Equal(snapshot.UsageCredits, store.Load()!.UsageCredits);
        store.Save(snapshot);
        var damaged = JsonNode.Parse(File.ReadAllText(path))!;
        damaged["usageCredits"]!.AsObject().Remove("hasCredits");
        File.WriteAllText(path, damaged.ToJsonString());
        Assert.Equal(snapshot.UsageCredits, store.Load()!.UsageCredits);
        Assert.True(store.RecoveredFromBackup);
        File.Delete(store.BackupPath);
        Assert.Null(store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(old with { UsageCredits = new(true, false, 1, "codex", null) }));
    }

    private static JsonObject Credits(string? balance) => new()
        { ["hasCredits"] = true, ["unlimited"] = false, ["balance"] = balance };
    private static JsonObject Bucket(JsonObject? credits) => new()
    {
        ["limitId"] = "codex", ["credits"] = credits?.DeepClone(),
        ["primary"] = new JsonObject { ["usedPercent"] = 25, ["windowDurationMins"] = 300, ["resetsAt"] = 1999999999 }
    };
    private static CodexParseResult Parse(JsonObject? credits) => CodexRateLimitParser.Parse(null,
        new JsonObject { ["rateLimits"] = Bucket(credits) });

    private sealed class Fixture : IDisposable
    {
        public AccountTestDirectory Data { get; } = new();
        public MutableClock Clock { get; } = new(DateTimeOffset.Parse("2026-10-01T01:00:00Z"));
        public CodexAccountProfile Profile { get; }
        public CodexSnapshotStore Store { get; }
        public CodexQuotaService Service { get; set; }
        public JsonObject? CreditData { get; set; } = Credits("-12.375");
        public bool FailQuota { get; set; }
        public string LimitId { get; set; } = "codex";
        public string Email { get; set; } = "synthetic@example.invalid";
        private readonly ScriptedCodexProcessFactory _factory = new();
        public Fixture()
        {
            var accounts = new CodexAccountStore(Data.Root);
            Profile = accounts.NewManaged("Synthetic");
            Store = new(accounts.SnapshotPath(Profile));
            _factory.Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                if (method == "account/read") return [AccountTestProtocol.Account(Email)];
                if (method != "account/rateLimits/read") return AccountTestProtocol.Standard(line);
                if (FailQuota) return ["""{"id":3,"error":{"code":-1}}"""];
                var bucket = Bucket(CreditData?.DeepClone().AsObject());
                bucket["limitId"] = LimitId;
                return [new JsonObject { ["id"] = 3, ["result"] = new JsonObject { ["rateLimits"] = bucket } }.ToJsonString()];
            };
            Service = CreateService();
        }
        public CodexQuotaService CreateService()
        {
            var files = new MemoryCodexFileSystem();
            files.Files.Add(AccountTestDirectory.Executable);
            return new(new CodexExecutableLocator(files), new CodexAppServerClient(_factory), Store, "test", clock: Clock, profile: Profile);
        }
        public Task<CodexRefreshResult> Refresh() => Service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        public void Dispose() => Data.Dispose();
    }
}
