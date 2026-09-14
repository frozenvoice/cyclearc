using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CodexMalformedWindowRegressionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-14T12:00:00Z");

    [Theory]
    [InlineData("primary", "\"broken\"")]
    [InlineData("primary", "42")]
    [InlineData("primary", "true")]
    [InlineData("primary", "[]")]
    [InlineData("primary", "{}")]
    [InlineData("primary", "{\"unknown\":true}")]
    [InlineData("secondary", "\"broken\"")]
    [InlineData("secondary", "42")]
    [InlineData("secondary", "true")]
    [InlineData("secondary", "[]")]
    [InlineData("secondary", "{}")]
    [InlineData("secondary", "{\"unknown\":true}")]
    public void PresentMalformedWindow_IsProtocolMismatch(string slot, string value)
    {
        var rateLimits = new JsonObject { [slot] = JsonNode.Parse(value) };
        var response = new JsonObject { ["rateLimits"] = rateLimits };
        var parsed = CodexRateLimitParser.Parse(null, response);

        Assert.Equal(CodexQuotaStatus.ProtocolMismatch, parsed.Status);
        Assert.Empty(parsed.Windows);
        Assert.Equal("rate-limits-malformed-window", parsed.Detail);
    }

    [Fact]
    public void MixedValidAndMalformedWindows_AreProtocolMismatch()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 12, "windowDurationMins": 300 },
                "secondary": "broken"
              }
            }
            """));

        Assert.Equal(CodexQuotaStatus.ProtocolMismatch, parsed.Status);
        Assert.Empty(parsed.Windows);
    }

    [Theory]
    [InlineData("[ { \"usedPercent\": 12, \"windowDurationMins\": 300 }, \"broken\" ]")]
    [InlineData("[ {} ]")]
    [InlineData("[ null ]")]
    public void MalformedWindowsArrayElement_IsProtocolMismatch(string windows)
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse(
            "{\"rateLimits\":{\"windows\":" + windows + "}}"));

        Assert.Equal(CodexQuotaStatus.ProtocolMismatch, parsed.Status);
        Assert.Empty(parsed.Windows);
    }

    [Fact]
    public void BothOptionalWindowsNull_AreAllowed()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": null,
                "secondary": null
              }
            }
            """));

        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Empty(parsed.Windows);
    }

    [Fact]
    public void MissingOptionalWindows_AreAllowed()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "ordinaryUsageAllowed": true
              }
            }
            """));

        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Empty(parsed.Windows);
        Assert.True(parsed.OrdinaryUsageAllowed);
    }

    [Fact]
    public void WeeklyOnlyWindow_IsAllowed()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 28, "windowDurationMins": 10080 }
              }
            }
            """));

        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        var window = Assert.Single(parsed.Windows);
        Assert.Equal(CodexWindowKind.Weekly, window.Kind);
        Assert.Equal(28, window.UsedPercent);
    }

    [Fact]
    public void InvalidPercentage_RemainsUnknownOnValidWindow()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": "unknown", "windowDurationMins": 300 }
              }
            }
            """));

        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        var window = Assert.Single(parsed.Windows);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
    }

    [Fact]
    public async Task MalformedWindow_PreservesLastGoodValuesAndSuccessTime()
    {
        var clock = new MutableClock(Start);
        var rateLimitReads = 0;
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var request = JsonNode.Parse(line) as JsonObject;
                var method = request?["method"]?.ToString();
                var id = request?["id"]?.ToString();
                if (method == "account/rateLimits/read")
                {
                    if (Interlocked.Increment(ref rateLimitReads) == 1)
                    {
                        clock.UtcNow = Start.AddMinutes(1);
                        return CodexScript.Standard(line);
                    }

                    return ["{\"id\":" + id + ",\"result\":{\"rateLimits\":{\"primary\":\"broken\",\"secondary\":null}}}"];
                }

                return CodexScript.Standard(line);
            }
        };
        var files = new MemoryCodexFileSystem();
        files.Files.Add(@"C:\Tools\codex.exe");
        var directory = Path.Combine(Path.GetTempPath(), $"cyclearc-malformed-window-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "snapshot.json");

        try
        {
            var store = new CodexSnapshotStore(path);
            var service = new CodexQuotaService(
                new CodexExecutableLocator(files),
                new CodexAppServerClient(factory),
                store,
                "1.0.0",
                clock: clock);

            var first = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
            Assert.Equal(CodexQuotaStatus.Available, first.Snapshot.Status);
            var successfulAt = first.Snapshot.LastSuccessfulRefresh;
            Assert.Equal(Start.AddMinutes(1), successfulAt);
            Assert.Equal(42, first.Snapshot.Windows.Single(window => window.Kind == CodexWindowKind.FiveHour).UsedPercent);

            clock.UtcNow = Start.AddMinutes(2);
            var second = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);

            Assert.True(second.UsedCache);
            Assert.Equal("rate-limits-malformed-window", second.FailureCategory);
            Assert.Equal(CodexQuotaStatus.Stale, second.Snapshot.Status);
            Assert.Equal(Start.AddMinutes(2), second.Snapshot.LastAttemptedRefresh);
            Assert.Equal(successfulAt, second.Snapshot.LastSuccessfulRefresh);
            Assert.Equal(42, second.Snapshot.Windows.Single(window => window.Kind == CodexWindowKind.FiveHour).UsedPercent);

            var persisted = store.Load();
            Assert.NotNull(persisted);
            Assert.Equal(CodexQuotaStatus.Stale, persisted.Status);
            Assert.Equal(successfulAt, persisted.LastSuccessfulRefresh);
            Assert.Equal(42, persisted.Windows.Single(window => window.Kind == CodexWindowKind.FiveHour).UsedPercent);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
