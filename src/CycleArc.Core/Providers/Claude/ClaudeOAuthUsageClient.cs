using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

/// <summary>Read-only first-party quota requests using Desktop-owned access credentials.
/// Never refreshes or writes credentials, follows redirects, sends model input, or reads cookies.</summary>
public sealed class ClaudeOAuthUsageClient : IClaudeLiveUsageClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IClaudeDesktopCredentialSource _credentials;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    public ClaudeOAuthUsageClient(IClaudeDesktopCredentialSource credentials, HttpClient? http = null, IClock? clock = null)
    { _credentials = credentials; _http = http ?? SharedClient; _clock = clock ?? SystemClock.Instance; }

    public async Task<ClaudeLiveUsageResponse> FetchAsync(ClaudeConnectionBinding binding, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var credentials = _credentials.Read(_clock.UtcNow);
            if (credentials.Credentials.Count == 0) return new(null, credentials.Failure ?? "claude-live-auth-required");
            var mismatch = false;
            foreach (var credential in credentials.Credentials.Take(4))
            {
                if (credential.ExpiresAt <= _clock.UtcNow) continue;
                using var profileReply = await SendAsync("profile", credential.AccessToken, bounded.Token).ConfigureAwait(false);
                if (profileReply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) continue;
                if (!profileReply.IsSuccessStatusCode) return Failure(profileReply);
                using var profile = await ReadAsync(profileReply, bounded.Token).ConfigureAwait(false);
                var identity = ParseIdentity(profile.RootElement);
                if (!ClaudeIdentityBinding.Matches(identity, binding)) { mismatch = true; continue; }
                using var usageReply = await SendAsync("usage", credential.AccessToken, bounded.Token).ConfigureAwait(false);
                if (!usageReply.IsSuccessStatusCode) return Failure(usageReply);
                using var usage = await ReadAsync(usageReply, bounded.Token).ConfigureAwait(false);
                var sample = ParseUsage(usage.RootElement, _clock.UtcNow);
                return new(sample);
            }
            return new(null, mismatch ? "claude-live-identity-mismatch" : "claude-live-auth-required");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or OperationCanceledException or JsonException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        { return new(null, ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException
            ? "claude-live-unavailable" : "claude-live-request-failed"); }
    }

    private async Task<HttpResponseMessage> SendAsync(string resource, string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/" + resource);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.CacheControl = new() { NoCache = true };
        request.Headers.UserAgent.ParseAdd("CycleArc/0.5.9");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private ClaudeLiveUsageResponse Failure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(null, "claude-live-rate-limited", response.Headers.RetryAfter?.Date
                ?? _clock.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1)));
        return new(null, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "claude-live-auth-required" : "claude-live-request-failed");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 65536;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Oversized Claude response.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + read > limit) throw new InvalidDataException("Oversized Claude response.");
            data.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    public static ClaudeAuthentication ParseIdentity(JsonElement root)
    {
        Object(root);
        var account = root.GetProperty("account"); Object(account);
        var organization = root.GetProperty("organization"); Object(organization);
        var email = Text(account, "email", 320) ?? Text(account, "email_address", 320);
        var org = Text(organization, "uuid", 200);
        if (string.IsNullOrWhiteSpace(email) || !Guid.TryParseExact(org, "D", out _)) throw new InvalidDataException("Invalid Claude identity.");
        var plan = account.TryGetProperty("has_claude_max", out var max) && max.ValueKind == JsonValueKind.True ? "max"
            : account.TryGetProperty("has_claude_pro", out var pro) && pro.ValueKind == JsonValueKind.True ? "pro" : null;
        return new(ClaudeAuthStatus.SignedIn, email, plan, ClaudeIdentity.StableFingerprint(email, org), org);
    }

    public static ClaudeLiveUsageSample ParseUsage(JsonElement root, DateTimeOffset observedAt)
    {
        Object(root);
        var sample = new ClaudeLiveUsageSample(observedAt, Window(root, "five_hour"), Window(root, "seven_day"));
        if (!ClaudeLiveUsageCollector.ValidSample(sample, observedAt)) throw new InvalidDataException("Invalid Claude usage.");
        return sample;
    }

    private static ClaudeLiveUsageWindow? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Object(value);
        if (!value.TryGetProperty("utilization", out var percent) || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used is < 0 or > 100)
            throw new InvalidDataException("Invalid Claude utilization.");
        var rawReset = Text(value, "resets_at", 80);
        DateTimeOffset? reset = null;
        if (rawReset is not null)
        {
            if (!DateTimeOffset.TryParse(rawReset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || parsed <= DateTimeOffset.UnixEpoch || (!rawReset.EndsWith('Z')
                    && !System.Text.RegularExpressions.Regex.IsMatch(rawReset, @"[+-]\d{2}:\d{2}$")))
                throw new InvalidDataException("Invalid Claude reset time.");
            reset = parsed;
        }
        return new(used, reset);
    }

    private static void Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() > 1))
            throw new InvalidDataException("Invalid Claude response shape.");
    }
    private static string? Text(JsonElement parent, string name, int limit)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Claude field.");
        var text = value.GetString()!;
        if (text.Length is 0 || text.Length > limit || text.Any(char.IsControl)) throw new InvalidDataException("Invalid Claude field.");
        return text;
    }
}
