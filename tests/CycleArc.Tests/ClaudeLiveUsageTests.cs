using System.Net;
using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeLiveUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");
    private const string Organization = "73ea74ee-7e58-427a-8593-7bc000000001";
    private const string Profile = """{"account":{"email":"desktop@example.invalid","has_claude_pro":true},"organization":{"uuid":"73ea74ee-7e58-427a-8593-7bc000000001"}}""";
    private const string Usage = """{"five_hour":{"utilization":46.25,"resets_at":"2030-01-01T15:00:00+00:00"},"seven_day":{"utilization":11,"resets_at":"2030-01-08T10:00:00Z"},"extra_usage":{"is_enabled":true}}""";

    [Fact]
    public async Task ServerIdentityMustMatchBeforeQuotaIsQueried()
    {
        using var data = new Data();
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("api.anthropic.com", request.RequestUri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(Json(requests.Count == 1 ? Profile : Usage));
        }));
        var client = new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock);
        var result = await client.FetchAsync(data.Binding, default);
        Assert.Null(result.Failure);
        Assert.Equal(46.25, result.Sample!.FiveHour!.UsedPercentage);
        Assert.Equal(Now.AddHours(5), result.Sample.FiveHour.ResetsAt);
        Assert.Equal(11, result.Sample.SevenDay!.UsedPercentage);
        Assert.Equal(new[] { "/api/oauth/profile", "/api/oauth/usage" }, requests);
    }

    [Fact]
    public async Task AnotherAccountsCredentialNeverQueriesUsage()
    {
        using var data = new Data();
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.EndsWith("/profile", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Profile.Replace("desktop@example.invalid", "other@example.invalid")));
        }));
        var result = await new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock).FetchAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.Equal("claude-live-identity-mismatch", result.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredCredentialsNeverCauseTokenRefreshOrAnyRequest()
    {
        using var data = new Data();
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("No network request expected.")));
        var result = await new ClaudeOAuthUsageClient(new Credentials(Now.AddSeconds(-1)), http, data.Clock)
            .FetchAsync(data.Binding, default);
        Assert.Equal("claude-live-auth-required", result.Failure);
        Assert.DoesNotContain("fixture-token", new ClaudeDesktopCredential("fixture-token", Now).ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"five_hour\":null,\"seven_day\":null}")]
    [InlineData("{\"five_hour\":{\"utilization\":-1}}")]
    [InlineData("{\"five_hour\":{\"utilization\":101}}")]
    [InlineData("{\"five_hour\":{\"utilization\":\"27\"}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1,\"utilization\":2}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1,\"resets_at\":\"2030-01-01T15:00:00\"}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1,\"resets_at\":0}}")]
    public void InvalidWindowsAreFailuresRatherThanZeroOrPartialSuccess(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ClaudeOAuthUsageClient.ParseUsage(document.RootElement, Now));
    }

    [Fact]
    public void MissingWindowsAndResetTimesRemainUnknown()
    {
        using var document = JsonDocument.Parse("""{"five_hour":{"utilization":0,"resets_at":null}}""");
        var sample = ClaudeOAuthUsageClient.ParseUsage(document.RootElement, Now);
        Assert.Equal(0, sample.FiveHour!.UsedPercentage);
        Assert.Null(sample.FiveHour.ResetsAt);
        Assert.Null(sample.SevenDay);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"account\":{},\"organization\":{}}")]
    [InlineData("not-json")]
    public async Task InvalidIdentityResponseReturnsSafeFailure(string json)
    {
        using var data = new Data();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        var result = await new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock).FetchAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.Equal("claude-live-unavailable", result.Failure);
    }

    [Fact]
    public async Task RedirectResponseDoesNotCountAsAuthenticationSuccess()
    {
        using var data = new Data();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://example.invalid/redirect");
            return Task.FromResult(response);
        }));
        var result = await new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock).FetchAsync(data.Binding, default);
        Assert.Equal("claude-live-request-failed", result.Failure);
        Assert.Null(result.Sample);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeParsing()
    {
        using var data = new Data();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new string('x', 65537)))));
        var result = await new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock).FetchAsync(data.Binding, default);
        Assert.Equal("claude-live-unavailable", result.Failure);
    }

    [Fact]
    public async Task Http429RetainsRetryAfterWithoutParsingTheErrorBody()
    {
        using var data = new Data();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private server error") };
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }));
        var result = await new ClaudeOAuthUsageClient(new Credentials(), http, data.Clock).FetchAsync(data.Binding, default);
        Assert.Equal("claude-live-rate-limited", result.Failure);
        Assert.Equal(Now.AddMinutes(10), result.RetryAfter);
    }

    [Fact]
    public async Task PassiveReadsAndRestartNeverPerformHttpOrRenewTheSuccessfulTime()
    {
        using var data = new Data();
        var collector = data.Collector();
        var first = await collector.RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(4);
        for (var i = 0; i < 10; i++) Assert.Equal(first, collector.ReadCached(data.Binding));
        var restarted = data.Collector().ReadCached(data.Binding);
        Assert.Equal(first, restarted);
        Assert.Equal(1, data.Client.Calls);
        var json = File.ReadAllText(data.CachePath);
        Assert.DoesNotContain("fixture-token", json);
        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain(Organization, json);
        Assert.DoesNotContain("refreshToken", json);
    }

    [Fact]
    public async Task FailedRequestPreservesQuotaAndReceiptAcrossRestart()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(null, "claude-live-request-failed");
        var failure = await collector.RefreshAsync(data.Binding, default);
        Assert.Equal("claude-live-request-failed", failure.Failure);
        Assert.Equal(Now, failure.Sample!.ObservedAt);
        Assert.Equal(Now.AddMinutes(5), failure.AttemptedAt);
        Assert.Equal(failure, data.Collector().ReadCached(data.Binding));
    }

    [Fact]
    public async Task RateLimitBackoffSurvivesRestartAndManualRepeatedRefresh()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(null, "claude-live-rate-limited", Now.AddMinutes(15));
        await collector.RefreshAsync(data.Binding, default);
        var restarted = data.Collector();
        data.Clock.UtcNow = Now.AddMinutes(10);
        var skipped = await restarted.RefreshAsync(data.Binding, default);
        Assert.Equal("claude-live-rate-limited", skipped.Failure);
        Assert.Equal(2, data.Client.Calls);
        Assert.Equal(Now, skipped.Sample!.ObservedAt);
        data.Clock.UtcNow = Now.AddMinutes(15);
        data.Client.Next = new(new(data.Clock.UtcNow, new(51, Now.AddHours(5)), null));
        Assert.Null((await restarted.RefreshAsync(data.Binding, default)).Failure);
        Assert.Equal(3, data.Client.Calls);
    }

    [Fact]
    public async Task IdentityMismatchHidesTheLastGoodCacheUntilVerificationSucceeds()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        data.Clock.UtcNow = Now.AddMinutes(5);
        data.Client.Next = new(null, "claude-live-identity-mismatch");
        var failure = await collector.RefreshAsync(data.Binding, default);
        Assert.Null(failure.Sample);
        Assert.Null(data.Collector().ReadCached(data.Binding).Sample);
    }

    [Fact]
    public async Task DisconnectDuringRequestDoesNotCommitResponse()
    {
        using var data = new Data();
        data.Client.BeforeReturn = () => data.Connections.Save(data.Binding with { Disconnected = true });
        var result = await data.Collector().RefreshAsync(data.Binding, default);
        Assert.Null(result.Sample);
        Assert.False(File.Exists(data.CachePath));
    }

    [Fact]
    public async Task CancellationPreservesTheOldCacheAndDoesNotMarkASuccess()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        var before = File.ReadAllText(data.CachePath);
        using var cancel = new CancellationTokenSource();
        data.Client.BeforeReturn = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.RefreshAsync(data.Binding, cancel.Token));
        Assert.Equal(before, File.ReadAllText(data.CachePath));
    }

    [Fact]
    public async Task ANewConnectionGenerationCannotReusePreviousQuota()
    {
        using var data = new Data();
        var collector = data.Collector();
        await collector.RefreshAsync(data.Binding, default);
        var changed = data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") };
        data.Connections.Save(changed);
        Assert.Null(collector.ReadCached(changed).Sample);
        Assert.Null(data.Collector().ReadCached(changed).Sample);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Credentials(DateTimeOffset? expiry = null) : IClaudeDesktopCredentialSource
    {
        public ClaudeDesktopCredentialRead Read(DateTimeOffset now) => new([new("fixture-token", expiry ?? now.AddHours(1))]);
    }
    private sealed class Client : IClaudeLiveUsageClient
    {
        public ClaudeLiveUsageResponse Next { get; set; } = new(new(Now, new(46.25, Now.AddHours(5)), new(11, Now.AddDays(7))));
        public int Calls { get; private set; }
        public Action? BeforeReturn { get; set; }
        public Task<ClaudeLiveUsageResponse> FetchAsync(ClaudeConnectionBinding binding, CancellationToken token)
        { Calls++; BeforeReturn?.Invoke(); return Task.FromResult(Next); }
    }
    private sealed class Data : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-live-" + Guid.NewGuid().ToString("N"));
        public MutableClock Clock { get; } = new(Now);
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public ClaudeConnectionStore Connections { get; }
        public ClaudeConnectionBinding Binding { get; }
        public Client Client { get; } = new();
        public string CachePath => Path.Combine(Root, "accounts", Profile.Id, "claude-live-usage.json");
        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Live fixture");
            Accounts.Save(initial with { Version = 2, Profiles = initial.Profiles.Append(Profile).ToArray() });
            Binding = new(2, Profile.Id, Path.Combine(Root, "claude-home"), Path.Combine(Root, "claude.exe"), false,
                ClaudeIdentity.StableFingerprint("desktop@example.invalid", Organization), Now.AddDays(-1),
                BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro");
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
        }
        public ClaudeLiveUsageCollector Collector() => new(Accounts, Profile.Id, Client, Clock);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
