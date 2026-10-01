using CycleArc.Codex;
using CycleArc.Providers.Claude;

namespace CycleArc.UiSmoke;

/// <summary>Opt-in first-party read-only probe. Never writes bindings, quota caches or settings.</summary>
internal static class ClaudeExtraUsageLiveChecks
{
    public static async Task<int> RunAsync()
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
            var client = new ClaudeOAuthUsageClient(new ClaudeDesktopCredentialReader());
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
}
