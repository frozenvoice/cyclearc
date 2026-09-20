using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Read-only Cursor first-party usage client. The two web JSON routes are an observed app
/// interface, so requests are deliberately bounded and do not follow redirects or refresh
/// credentials. The session cookie is constructed from the in-memory token; browser cookie
/// stores are never read.
/// </summary>
public sealed class CursorUsageClient : ICursorUsageClient, IDisposable
{
    public static readonly Uri ProfileUri = new("https://cursor.com/api/auth/me");
    public static readonly Uri UsageUri = new("https://cursor.com/api/usage-summary");
    public static readonly Uri SandUsageUri = new("https://cursor.com/api/dashboard/get-sand-usage-status");
    public const int MaxResponseBytes = 256 * 1024;

    private readonly ICursorAuthSource _auth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IClock _clock;

    public CursorUsageClient(ICursorAuthSource auth, HttpClient? http = null, IClock? clock = null)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _clock = clock ?? SystemClock.Instance;
        if (http is null)
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public async Task<CursorConnectionResult> ReadIdentityAsync(CancellationToken token)
    {
        var credentials = _auth.Read();
        if (credentials.AccessToken is null)
            return new(false, credentials.Failure ?? "cursor-live-auth-required");

        try
        {
            var response = await GetJsonAsync(ProfileUri, credentials.AccessToken, token).ConfigureAwait(false);
            if (response.Failure is not null) return new(false, response.Failure);
            var identity = ParseIdentity(response.Document!.Value);
            if (identity is null) return new(false, "cursor-live-unavailable");
            return new(true, IdentityFingerprint: identity.StableFingerprint, Email: identity.Email);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "cursor-live-unavailable"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        { return new(false, "cursor-live-unavailable"); }
    }

    public async Task<CursorUsageResponse> FetchAsync(CursorConnectionBinding binding, CancellationToken token,
        bool includeSand = true)
    {
        if (binding.Disconnected) return new(null, "cursor-disconnected");
        var credentials = _auth.Read();
        if (credentials.AccessToken is null)
            return new(null, credentials.Failure ?? "cursor-live-auth-required");

        try
        {
            var profile = await GetJsonAsync(ProfileUri, credentials.AccessToken, token).ConfigureAwait(false);
            if (profile.Failure is not null) return new(null, profile.Failure, profile.RetryAfter);
            var identity = ParseIdentity(profile.Document!.Value)
                ?? throw new InvalidDataException("Cursor identity response is incomplete.");
            if (!CursorIdentity.Matches(binding.IdentityFingerprint, identity))
                return new(null, "cursor-live-identity-mismatch", Email: identity.Email,
                    IdentityFingerprint: identity.StableFingerprint);

            var summary = await GetJsonAsync(UsageUri, credentials.AccessToken, token).ConfigureAwait(false);
            if (summary.Failure is not null)
                return new(null, summary.Failure, summary.RetryAfter, identity.Email, identity.StableFingerprint);
            var sample = ParseUsage(summary.Document!.Value, _clock.UtcNow);
            if (!includeSand) return new(sample, Email: identity.Email, IdentityFingerprint: identity.StableFingerprint);

            // Sand is independent: its failure/backoff must not mark the monthly summary
            // stale or stop monthly requests. Never carry an older Sand value into this sample.
            try
            {
                var sand = await PostJsonAsync(SandUsageUri, credentials.AccessToken, token).ConfigureAwait(false);
                if (sand.Failure is not null)
                    return new(sample, Email: identity.Email, IdentityFingerprint: identity.StableFingerprint,
                        SandFailure: "cursor-sand-unavailable", SandRetryAfter: sand.RetryAfter);
                var sandWindow = ParseSand(sand.Document!.Value, _clock.UtcNow);
                if (sandWindow is not null)
                    sample = sample with { Windows = sample.Windows.Concat([sandWindow]).ToArray() };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                or InvalidDataException or OperationCanceledException)
            {
                return new(sample, Email: identity.Email, IdentityFingerprint: identity.StableFingerprint,
                    SandFailure: "cursor-sand-unavailable");
            }

            return new(sample, null, null, identity.Email, identity.StableFingerprint);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(null, "cursor-live-unavailable"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        { return new(null, "cursor-live-unavailable"); }
    }

    public static CursorIdentity? ParseIdentity(JsonElement root)
    {
        foreach (var candidate in Candidates(root))
        {
            var email = StringProperty(candidate, "email") ?? StringProperty(candidate, "userEmail");
            var id = IdentifierProperty(candidate, "sub") ?? IdentifierProperty(candidate, "userId")
                ?? IdentifierProperty(candidate, "user_id") ?? IdentifierProperty(candidate, "id");
            var fingerprint = CursorIdentity.Fingerprint(email, id);
            if (fingerprint is not null) return new(email, id, fingerprint);
        }

        return null;
    }

    public static CursorUsageSample ParseUsage(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cursor usage is not an object.");
        var start = DateProperty(root, "billingCycleStart");
        var end = DateProperty(root, "billingCycleEnd");
        var membership = StringProperty(root, "membershipType");
        var limitType = StringProperty(root, "limitType");
        var windows = new List<CodexQuotaWindow>();

        if (TryObject(root, "individualUsage", out var individual))
        {
            if (TryObject(individual, "plan", out var plan))
            {
                var auto = PercentProperty(plan, "autoPercentUsed");
                var api = PercentProperty(plan, "apiPercentUsed");
                // Cursor reports these as independently billed allowances. A reported plan
                // total is only a fallback when neither split allowance exists.
                if (auto.HasValue || api.HasValue)
                {
                    windows.Add(Window("cursor-auto", auto, end, plan, "auto"));
                    windows.Add(Window("cursor-api", api, end, plan, "api"));
                }
                else if (PercentProperty(plan, "totalPercentUsed") is { } total)
                {
                    windows.Add(Window("cursor-plan", total, end, plan, null, includeAmounts: false));
                }
                else
                {
                    windows.Add(Window("cursor-plan", null, end, plan, null, includeAmounts: false));
                }
            }

            if (TryObject(individual, "onDemand", out var onDemand))
                windows.Add(Window("cursor-on-demand", PercentProperty(onDemand, "percentUsed")
                    ?? PercentProperty(onDemand, "usagePercent"), end, onDemand, null));
        }

        if (TryObject(root, "teamUsage", out var team))
        {
            if (TryObject(team, "onDemand", out var teamOnDemand))
                windows.Add(Window("cursor-team-on-demand", PercentProperty(teamOnDemand, "percentUsed")
                    ?? PercentProperty(teamOnDemand, "usagePercent"), end, teamOnDemand, null));
            if (TryObject(team, "pooled", out var teamPool))
                windows.Add(Window("cursor-team-pool", PercentProperty(teamPool, "percentUsed")
                    ?? PercentProperty(teamPool, "usagePercent"), end, teamPool, null));
        }

        if (TryObject(individual, "overall", out var overall))
            windows.Add(Window("cursor-overall", PercentProperty(overall, "percentUsed")
                ?? PercentProperty(overall, "usagePercent"), end, overall, null));

        if (windows.Count == 0) throw new InvalidDataException("Cursor usage contains no known allowance.");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (windows.Any(window => !unique.Add(window.LimitId ?? "")))
            throw new InvalidDataException("Cursor usage contains duplicate allowances.");
        return new(observedAt, windows, start, end, membership, limitType);
    }

    public static CodexQuotaWindow? ParseSand(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cursor Sand usage is not an object.");
        var includedLimitZero = Bool(root, "includedLimitZero");
        var hasNonZeroLimit = Bool(root, "hasNonZeroIncludedLimit");
        var trialEnd = DateProperty(root, "sandTrialExpiresAt");
        var trialActive = trialEnd is { } trial && trial > observedAt;
        var included = includedLimitZero is { } zero ? !zero : hasNonZeroLimit == true;
        if (!included && !trialActive) return null;
        // Exhaustion does not remove an allowance, and a boolean is not a percentage.
        // Trial expiry is not a recurring reset timestamp.
        var percent = PercentProperty(root, "usagePercent");
        var reset = included ? DateProperty(root, "nextResetTimestampUtc") : null;
        return Window("cursor-sand", percent, reset, root, null);
    }

    private async Task<JsonResult> GetJsonAsync(Uri uri, string token, CancellationToken cancellationToken) =>
        await SendJsonAsync(HttpMethod.Get, uri, token, null, cancellationToken).ConfigureAwait(false);

    private async Task<JsonResult> PostJsonAsync(Uri uri, string token, CancellationToken cancellationToken) =>
        await SendJsonAsync(HttpMethod.Post, uri, token, "{}", cancellationToken).ConfigureAwait(false);

    private async Task<JsonResult> SendJsonAsync(HttpMethod method, Uri uri, string token, string? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (!TryBuildSessionCookie(token, _clock.UtcNow, out var cookie))
            return new(null, "cursor-live-auth-required");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
            request.Headers.Referrer = new Uri("https://cursor.com/dashboard");
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        var retry = response.StatusCode == HttpStatusCode.TooManyRequests
            ? RetryAfter(response.Headers.RetryAfter, _clock.UtcNow) : null;
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            return new(null, "cursor-live-auth-required", retry);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(null, "cursor-live-rate-limited", retry);
        if ((int)response.StatusCode is >= 300 and < 400)
            return new(null, "cursor-live-request-failed", retry);
        if (!response.IsSuccessStatusCode)
            return new(null, "cursor-live-request-failed", retry);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            return new(null, "cursor-live-unavailable");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaxResponseBytes)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes) return new(null, "cursor-live-unavailable");
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
            new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false });
        // Clone before disposing the document. Only projected quotas enter the cache.
        return new(document.RootElement.Clone(), null, retry);
    }

    private static IEnumerable<JsonElement> Candidates(JsonElement root)
    {
        yield return root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object) yield return user;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object) yield return data;
    }

    private static bool TryBuildSessionCookie(string token, DateTimeOffset now, out string cookie)
    {
        cookie = "";
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sub", out var subject)
                || subject.ValueKind != JsonValueKind.String
                || subject.GetString() is not { } rawSubject
                || CursorIdentity.Normalize(rawSubject) is null)
                return false;
            if (!document.RootElement.TryGetProperty("exp", out var expiry)
                || expiry.ValueKind != JsonValueKind.Number
                || !expiry.TryGetInt64(out var exp)
                || exp <= now.AddSeconds(60).ToUnixTimeSeconds()) return false;
            var separator = rawSubject.LastIndexOf('|');
            var subjectId = separator >= 0 ? rawSubject[(separator + 1)..] : rawSubject;
            if (CursorIdentity.Normalize(subjectId) is null) return false;
            cookie = "WorkosCursorSessionToken="
                + Uri.EscapeDataString(subjectId.Trim() + "::" + token);
            return true;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    private static string? StringProperty(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? CursorIdentity.Normalize(value.GetString()) : null;

    private static string? RawString(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;

    private static string? IdentifierProperty(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? CursorIdentity.NormalizeIdentifier(value.GetString()) : null;

    private static DateTimeOffset? DateProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
            throw new InvalidDataException("Cursor date is malformed.");
        return result > DateTimeOffset.UnixEpoch ? result : null;
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out value)) return false;
        if (value.ValueKind == JsonValueKind.Null) { value = default; return false; }
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Cursor usage object is malformed.");
        return true;
    }

    private static double? PercentProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        return ReadPercent(value);
    }

    private static double ReadPercent(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 100)
            throw new InvalidDataException("Cursor percentage is malformed.");
        return number;
    }

    private static CodexQuotaWindow Window(string id, double? percent, DateTimeOffset? reset,
        JsonElement source, string? amountPrefix, bool includeAmounts = true)
    {
        decimal? used = includeAmounts ? Amount(source, amountPrefix is null ? "used" : amountPrefix + "Used") : null;
        decimal? limit = includeAmounts ? Amount(source, amountPrefix is null ? "limit" : amountPrefix + "Limit") : null;
        decimal? remaining = includeAmounts ? Amount(source, amountPrefix is null ? "remaining" : amountPrefix + "Remaining") : null;
        var enabled = Bool(source, "enabled");
        var unlimited = Bool(source, "isUnlimited") ?? false;
        return new CodexQuotaWindow(id, percent, null, reset, CodexWindowKind.Other)
        {
            UsedAmount = used,
            LimitAmount = limit,
            RemainingAmount = remaining,
            Unit = RawString(source, "unit") ?? "USD",
            IsUnlimited = unlimited,
            IsEnabled = enabled
        };
    }

    private static decimal? Amount(JsonElement source, string name)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)
            || number < 0) throw new InvalidDataException("Cursor amount is malformed.");
        return number / 100m;
    }

    private static bool? Bool(JsonElement source, string name)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Cursor boolean is malformed.");
        return value.GetBoolean();
    }

    private static DateTimeOffset? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null) return null;
        var value = header.Delta is { } delta ? now.Add(delta)
            : header.Date is { } date ? date : (DateTimeOffset?)null;
        if (value is null) return null;
        return value < now.AddSeconds(5) ? now.AddSeconds(5)
            : value > now.AddHours(24) ? now.AddHours(24) : value;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed record JsonResult(JsonElement? Document, string? Failure, DateTimeOffset? RetryAfter = null);
}
