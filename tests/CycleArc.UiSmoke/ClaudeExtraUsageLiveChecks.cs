using CycleArc.Codex;
using CycleArc.Providers.Claude;
using System.Net.Http;
using System.Text.Json;

namespace CycleArc.UiSmoke;

/// <summary>Opt-in first-party read-only probe. Never writes bindings, quota caches or settings.</summary>
internal static class ClaudeExtraUsageLiveChecks
{
    public static async Task<int> RunAsync(bool programQueryProbe = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            var accounts = new CodexAccountStore();
            var profiles = accounts.ReadClaudeProfiles();
            if (!profiles.Available)
            {
                Console.WriteLine("Claude extra usage live read: account registry unavailable.");
                return 1;
            }
            var bindings = profiles.Ids.Select(id => new ClaudeConnectionStore(accounts, id).Read())
                .Where(read => read is { Unavailable: false, Binding: { Disconnected: false } })
                .Select(read => read.Binding!).Take(4).ToArray();
            if (bindings.Length == 0)
            {
                Console.WriteLine("Claude extra usage live read: no existing connected account.");
                return 2;
            }
            using var probeHandler = programQueryProbe ? new ResetQueryProbeHandler() : null;
            using var probeHttp = probeHandler is null ? null : new HttpClient(probeHandler) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new ClaudeOAuthUsageClient(new ClaudeDesktopCredentialReader(), probeHttp);
            foreach (var binding in bindings)
            {
                var response = await client.FetchAsync(binding, timeout.Token);
                if (response.Failure == "claude-live-identity-mismatch") continue;
                if (response.Sample is not { } sample)
                {
                    Console.WriteLine("Claude extra usage live read: existing Desktop credential or quota query unavailable.");
                    return 1;
                }
                // Recheck attribution after HTTP; removal/reconnection cannot turn an
                // older result into a current-account compatibility claim.
                if (!accounts.ContainsClaude(binding.ProfileId)
                    || new ClaudeConnectionStore(accounts, binding.ProfileId).Read().Binding != binding)
                {
                    Console.WriteLine("Claude extra usage live read: binding changed during query.");
                    return 1;
                }
                var extra = sample.ExtraUsage;
                if (programQueryProbe)
                    Console.WriteLine($"Claude reset opt-in probe: query-sent={probeHandler!.QuerySent}; unchanged-CycleArc-UA=True; "
                        + $"surface-restricted={probeHandler.SurfaceRestricted}; profile attribution verified; subscription and Extra usage parsers preserved.");
                Console.WriteLine("Claude reset inventory live read (" + (programQueryProbe ? "opt-in query probe" : "ordinary existing GET") + "): provided="
                    + (sample.ResetCredits is not null) + "; eligible=" + (sample.ResetCredits?.Eligible.ToString() ?? "unknown")
                    + "; optional shape accepted=" + (sample.ResetCreditsFailure is null)
                    + (programQueryProbe ? "; explicit one-shot read-only query probe; no redemption." : "; no inventory query or redemption was added."));
                Console.WriteLine("Claude extra usage live read: identity verified; subscription quota accepted; "
                    + (sample.ExtraUsageFailure is not null ? "optional extra usage shape unavailable."
                        : extra is null ? "extra usage not provided."
                        : $"extra usage provided; enabled={extra.IsEnabled}; used amount present={extra.UsedAmount is not null}; "
                            + $"monthly limit present={extra.MonthlyLimitAmount is not null}; unlimited={extra.IsUnlimited}; "
                            + $"currency={extra.Currency ?? "unknown"}; utilization present={extra.UsedPercentage is not null}; "
                            + $"remaining is calculated={extra.IsRemainingCalculated}."));
                return sample.ExtraUsageFailure is null ? 0 : 2;
            }
            Console.WriteLine("Claude extra usage live read: Desktop identity did not match existing connected accounts.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Claude extra usage live read: cancelled or timed out.");
            return 1;
        }
        catch
        {
            // Exception messages may include request details. Print a fixed summary only.
            Console.WriteLine("Claude extra usage live read: failed; no credentials or raw responses were recorded.");
            return 1;
        }
    }

    // Test-only compatibility probe. Production retains its ordinary GET. The existing
    // client still verifies profile/organization first and bounds the complete operation.
    private sealed class ResetQueryProbeHandler() : DelegatingHandler(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false })
    {
        public bool QuerySent { get; private set; }
        public bool SurfaceRestricted { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://api.anthropic.com/api/oauth/usage")
            {
                if (QuerySent || request.Headers.UserAgent.ToString() != "CycleArc/0.5.9")
                    throw new InvalidOperationException("Unexpected probe shape.");
                QuerySent = true;
                request.RequestUri = new Uri("https://api.anthropic.com/api/oauth/usage?cedar_ember=1");
                var response = await base.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(65536, token).ConfigureAwait(false);
                        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false),
                            new JsonDocumentOptions { MaxDepth = 16 });
                        SurfaceRestricted = document.RootElement.TryGetProperty("cedar_ember", out var program)
                            && program.ValueKind == JsonValueKind.Object && program.TryGetProperty("ineligible_reason", out var reason)
                            && reason.ValueKind == JsonValueKind.String && reason.GetString() == "surface";
                    }
                    catch { response.Dispose(); throw; }
                }
                return response;
            }
            return await base.SendAsync(request, token).ConfigureAwait(false);
        }
    }
}
