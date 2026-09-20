using System.Net;
using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorUsageClientTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private const string Account = "acct-fixture";
    private const string Email = "cursor@example.invalid";

    [Fact]
    public async Task VerifiesIdentityBeforeUsageAndBuildsTheBoundSessionCookie()
    {
        var calls = new List<HttpRequestMessage>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/me" => Json("{\"sub\":\"auth0|acct-fixture\",\"email\":\"cursor@example.invalid\"}"),
                "/api/usage-summary" => Json(Summary),
                "/api/dashboard/get-sand-usage-status" => Json("{\"usagePercent\":0,\"hasAvailableUsage\":true,\"includedLimitZero\":false,\"nextResetTimestampUtc\":\"2030-01-02T00:00:00Z\"}"),
                _ => new(HttpStatusCode.NotFound)
            });
        }));
        var token = Jwt("auth0|acct-fixture", Now.AddHours(1));
        using var client = new CursorUsageClient(new FakeAuth(token), http, new MutableClock(Now));
        var fingerprint = CursorIdentity.Fingerprint(Email, "auth0|" + Account)!;
        var binding = new CursorConnectionBinding(1, Guid.NewGuid().ToString("N"), fingerprint,
            Now.AddDays(-1), Guid.NewGuid().ToString("N"));

        var result = await client.FetchAsync(binding, default);

        Assert.Null(result.Failure);
        Assert.Contains(result.Sample!.Windows, window => window.LimitId == "cursor-auto" && window.UsedPercent == 76.91111111111111);
        Assert.Contains(result.Sample.Windows, window => window.LimitId == "cursor-api" && window.UsedPercent == 0);
        Assert.Contains(result.Sample.Windows, window => window.LimitId == "cursor-sand" && window.UsedPercent == 0);
        Assert.Equal(3, calls.Count);
        foreach (var request in calls)
        {
            Assert.Null(request.Headers.Authorization);
            var cookie = Assert.Single(request.Headers.GetValues("Cookie"));
            Assert.Equal("WorkosCursorSessionToken=acct-fixture%3A%3A" + token, cookie);
            Assert.Equal("cursor.com", request.RequestUri!.Host);
        }
    }

    [Fact]
    public async Task AProfileMismatchNeverQueriesUsage()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/api/auth/me"
                ? "{\"sub\":\"auth0|other\",\"email\":\"other@example.invalid\"}"
                : Summary));
        }));
        var token = Jwt("auth0|acct-fixture", Now.AddHours(1));
        using var client = new CursorUsageClient(new FakeAuth(token), http, new MutableClock(Now));
        var binding = new CursorConnectionBinding(1, Guid.NewGuid().ToString("N"),
            CursorIdentity.Fingerprint(Email, Account)!, Now.AddDays(-1), Guid.NewGuid().ToString("N"));

        var result = await client.FetchAsync(binding, default);

        Assert.Equal("cursor-live-identity-mismatch", result.Failure);
        Assert.Null(result.Sample);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProfileRateLimitKeepsRetryAfter()
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt(Account, Now.AddHours(1))), http, new MutableClock(Now));
        var result = await client.FetchAsync(Binding(), default);

        Assert.Equal("cursor-live-rate-limited", result.Failure);
        Assert.Equal(Now.AddMinutes(2), result.RetryAfter);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"sub\":\"acct-fixture\",\"exp\":0}")]
    public async Task MalformedOrExpiredCredentialNeverSendsARequest(string payload)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("{}"));
        }));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var client = new CursorUsageClient(new FakeAuth("fixture." + encoded + ".fixture"),
            http, new MutableClock(Now));

        var result = await client.FetchAsync(Binding(), default);

        Assert.Equal("cursor-live-auth-required", result.Failure);
        Assert.Null(result.Sample);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OptionalSandHttpFailureRetainsTheValidMonthlySample()
    {
        using var http = new HttpClient(new Handler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => Task.FromResult(Json("{\"sub\":\"auth0|acct-fixture\",\"email\":\"cursor@example.invalid\"}")),
            "/api/usage-summary" => Task.FromResult(Json(Summary)),
            "/api/dashboard/get-sand-usage-status" => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("synthetic sand outage")),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt("auth0|acct-fixture", Now.AddHours(1))),
            http, new MutableClock(Now));

        var result = await client.FetchAsync(Binding(), default);

        Assert.Null(result.Failure);
        Assert.Null(result.RetryAfter);
        Assert.Equal("cursor-sand-unavailable", result.SandFailure);
        Assert.Contains(result.Sample!.Windows, window => window.LimitId == "cursor-auto");
        Assert.DoesNotContain(result.Sample.Windows, window => window.LimitId == "cursor-sand");
        Assert.Equal(Now, result.Sample.ObservedAt);
    }

    [Fact]
    public async Task OptionalSandMalformedBodyRetainsTheValidMonthlySample()
    {
        using var http = new HttpClient(new Handler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => Task.FromResult(Json("{\"sub\":\"auth0|acct-fixture\",\"email\":\"cursor@example.invalid\"}")),
            "/api/usage-summary" => Task.FromResult(Json(Summary)),
            "/api/dashboard/get-sand-usage-status" => Task.FromResult(Json("{malformed")),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt("auth0|acct-fixture", Now.AddHours(1))),
            http, new MutableClock(Now));

        var result = await client.FetchAsync(Binding(), default);

        Assert.Null(result.Failure);
        Assert.Equal("cursor-sand-unavailable", result.SandFailure);
        Assert.Contains(result.Sample!.Windows, window => window.LimitId == "cursor-api");
        Assert.DoesNotContain(result.Sample.Windows, window => window.LimitId == "cursor-sand");
    }

    [Fact]
    public async Task OversizedOptionalSandBodyRetainsTheValidMonthlySample()
    {
        var oversized = new string('x', CursorUsageClient.MaxResponseBytes + 1);
        using var http = new HttpClient(new Handler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => Task.FromResult(Json("{\"sub\":\"auth0|acct-fixture\",\"email\":\"cursor@example.invalid\"}")),
            "/api/usage-summary" => Task.FromResult(Json(Summary)),
            "/api/dashboard/get-sand-usage-status" => Task.FromResult(Json(oversized)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt("auth0|acct-fixture", Now.AddHours(1))),
            http, new MutableClock(Now));

        var result = await client.FetchAsync(Binding(), default);

        Assert.Null(result.Failure);
        Assert.Equal("cursor-sand-unavailable", result.SandFailure);
        Assert.NotNull(result.Sample);
        Assert.Contains(result.Sample!.Windows, window => window.LimitId == "cursor-plan" || window.LimitId == "cursor-auto");
    }

    [Fact]
    public async Task OptionalRateLimitIsSeparateAndCanSkipOnlySand()
    {
        var profileCalls = 0;
        var summaryCalls = 0;
        var sandCalls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                profileCalls++;
                return Task.FromResult(Json("{\"sub\":\"auth0|acct-fixture\"}"));
            }
            if (request.RequestUri.AbsolutePath == "/api/usage-summary")
            {
                summaryCalls++;
                return Task.FromResult(Json(Summary));
            }
            sandCalls++;
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromMinutes(15));
            return Task.FromResult(limited);
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt("auth0|acct-fixture", Now.AddHours(1))),
            http, new MutableClock(Now));

        var limited = await client.FetchAsync(Binding(), default);
        Assert.Null(limited.Failure);
        Assert.Null(limited.RetryAfter);
        Assert.Equal("cursor-sand-unavailable", limited.SandFailure);
        Assert.Equal(Now.AddMinutes(15), limited.SandRetryAfter);
        Assert.NotNull(limited.Sample);

        var monthly = await client.FetchAsync(Binding(), default, includeSand: false);
        Assert.Null(monthly.Failure);
        Assert.NotNull(monthly.Sample);
        Assert.DoesNotContain(monthly.Sample.Windows, window => window.LimitId == "cursor-sand");
        Assert.Equal(2, profileCalls);
        Assert.Equal(2, summaryCalls);
        Assert.Equal(1, sandCalls);
    }

    [Fact]
    public async Task CallerCancellationDuringOptionalSandRequestPropagates()
    {
        var sandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
                return Json("{\"sub\":\"auth0|acct-fixture\",\"email\":\"cursor@example.invalid\"}");
            if (request.RequestUri.AbsolutePath == "/api/usage-summary") return Json(Summary);
            sandStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        }));
        using var client = new CursorUsageClient(new FakeAuth(Jwt("auth0|acct-fixture", Now.AddHours(1))),
            http, new MutableClock(Now));
        using var cancellation = new CancellationTokenSource();
        var pending = client.FetchAsync(Binding(), cancellation.Token);
        await sandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void SplitWindowsDoNotInventACombinedPlanOrAmounts()
    {
        using var document = JsonDocument.Parse(Summary);
        var sample = CursorUsageClient.ParseUsage(document.RootElement, Now);

        Assert.DoesNotContain(sample.Windows, window => window.LimitId == "cursor-plan");
        Assert.Null(sample.Windows.Single(window => window.LimitId == "cursor-auto").LimitAmount);
        Assert.Null(sample.Windows.Single(window => window.LimitId == "cursor-api").RemainingAmount);
    }

    [Fact]
    public void TotalFallbackAndUnknownPlanDoNotCarryMismatchedAmounts()
    {
        using var total = JsonDocument.Parse("""{"individualUsage":{"plan":{"totalPercentUsed":12,"used":2000,"limit":2000}}}""");
        var fallback = CursorUsageClient.ParseUsage(total.RootElement, Now).Windows.Single();
        Assert.Equal("cursor-plan", fallback.LimitId);
        Assert.Equal(12, fallback.UsedPercent);
        Assert.Null(fallback.UsedAmount);

        using var unknown = JsonDocument.Parse("""{"individualUsage":{"plan":{"enabled":true}}}""");
        var pending = CursorUsageClient.ParseUsage(unknown.RootElement, Now).Windows.Single();
        Assert.Equal("cursor-plan", pending.LimitId);
        Assert.Null(pending.UsedPercent);
    }

    [Fact]
    public void SandRequiresAnExplicitAvailableAllowance()
    {
        using var exhausted = JsonDocument.Parse("""{"usagePercent":100,"hasAvailableUsage":false,"includedLimitZero":false,"hasNonZeroIncludedLimit":true}""");
        Assert.Equal(100, CursorUsageClient.ParseSand(exhausted.RootElement, Now)!.UsedPercent);
        using var contradicted = JsonDocument.Parse("""{"usagePercent":100,"hasAvailableUsage":false,"includedLimitZero":true,"hasNonZeroIncludedLimit":true}""");
        Assert.Null(CursorUsageClient.ParseSand(contradicted.RootElement, Now));
        using var unknown = JsonDocument.Parse("""{"hasAvailableUsage":false,"includedLimitZero":false}""");
        Assert.Null(CursorUsageClient.ParseSand(unknown.RootElement, Now)!.UsedPercent);
        using var trial = JsonDocument.Parse("""{"usagePercent":0,"hasAvailableUsage":true,"sandTrialExpiresAt":"2030-01-02T00:00:00Z","nextResetTimestampUtc":"2030-01-03T00:00:00Z"}""");
        Assert.Null(CursorUsageClient.ParseSand(trial.RootElement, Now)!.ResetsAt);
        using var denied = JsonDocument.Parse("""{"usagePercent":0,"hasAvailableUsage":true}""");
        Assert.Null(CursorUsageClient.ParseSand(denied.RootElement, Now));
    }

    [Fact]
    public void TeamAndIndividualBudgetsKeepDistinctAmountsAndUnknownCaps()
    {
        using var document = JsonDocument.Parse("""
            {"individualUsage":{"onDemand":{"enabled":true,"used":450,"limit":2000,"remaining":1550},
             "overall":{"used":450,"limit":null,"remaining":null}},
             "teamUsage":{"pooled":{"used":1250,"limit":5000,"remaining":3750},
             "onDemand":{"enabled":true,"used":0,"limit":null,"remaining":null}}}
            """);

        var windows = CursorUsageClient.ParseUsage(document.RootElement, Now).Windows;

        Assert.Equal(4, windows.Count);
        Assert.Equal(15.5m, Assert.Single(windows, w => w.LimitId == "cursor-on-demand").RemainingAmount);
        Assert.Equal(37.5m, Assert.Single(windows, w => w.LimitId == "cursor-team-pool").RemainingAmount);
        foreach (var id in new[] { "cursor-team-on-demand", "cursor-overall" })
        {
            var unknown = Assert.Single(windows, w => w.LimitId == id);
            Assert.Null(unknown.RemainingAmount);
            Assert.Null(unknown.UsedPercent);
            Assert.False(unknown.IsUnlimited);
        }
    }

    [Fact]
    public void StableIdentityUsesOpaqueIdCaseAndDoesNotCollapseSameEmailAccounts()
    {
        using var upper = JsonDocument.Parse("""{"sub":"Acct-1","email":"same@example.invalid"}""");
        using var lower = JsonDocument.Parse("""{"sub":"acct-1","email":"same@example.invalid"}""");
        using var other = JsonDocument.Parse("""{"sub":"Acct-2","email":"same@example.invalid"}""");

        var first = CursorUsageClient.ParseIdentity(upper.RootElement)!;
        var second = CursorUsageClient.ParseIdentity(lower.RootElement)!;
        var third = CursorUsageClient.ParseIdentity(other.RootElement)!;

        Assert.NotEqual(first.StableFingerprint, second.StableFingerprint);
        Assert.NotEqual(first.StableFingerprint, third.StableFingerprint);
    }

    [Fact]
    public void MalformedKnownContainerIsAProtocolFailure()
    {
        using var document = JsonDocument.Parse("""{"individualUsage":"bad"}""");
        Assert.Throws<InvalidDataException>(() => CursorUsageClient.ParseUsage(document.RootElement, Now));
    }

    private CursorConnectionBinding Binding() => new(1, Guid.NewGuid().ToString("N"),
        CursorIdentity.Fingerprint(Email, "auth0|" + Account)!, Now.AddDays(-1), Guid.NewGuid().ToString("N"));

    private static string Jwt(string subject, DateTimeOffset expiry)
    {
        static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"none\"}") + "."
            + B64(JsonSerializer.Serialize(new { sub = subject, exp = expiry.ToUnixTimeSeconds() })) + ".sig";
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Summary = """
        {"billingCycleStart":"2030-01-01T00:00:00Z","billingCycleEnd":"2030-02-01T00:00:00Z","membershipType":"pro","limitType":"user","individualUsage":{"plan":{"enabled":true,"used":2000,"limit":2000,"remaining":0,"autoPercentUsed":76.91111111111111,"apiPercentUsed":0,"totalPercentUsed":69.91919191919192},"onDemand":{"enabled":false,"used":0,"limit":null,"remaining":null}},"teamUsage":{}}
        """;

    private sealed class FakeAuth(string token) : ICursorAuthSource
    { public CursorAuthRead Read() => new(token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
