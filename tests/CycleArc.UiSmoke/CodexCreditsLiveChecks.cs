using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.UiSmoke;

/// <summary>Opt-in metadata-only check, excluded from the offline verification gate.</summary>
internal static class CodexCreditsLiveChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var accounts = new CodexAccountStore();
            var state = accounts.ReadExisting();
            var profiles = state?.Profiles.Where(profile => profile.Provider == UsageProviderId.Codex).ToArray() ?? [];
            var command = new CodexExecutableLocator(new WindowsCodexFileSystem()).Locate(null);
            if (command is null || profiles.Length == 0)
            {
                Console.WriteLine("Codex credits live read: unavailable; no existing profiles or app-server.");
                return 2;
            }
            using var total = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var verified = 0;
            var failed = false;
            foreach (var profile in profiles)
            {
                var bindings = new CodexIdentityBindingStore(accounts.SnapshotPath(profile), profile.Id);
                var expected = bindings.ReadExisting();
                if (expected.Unavailable || expected.State is null || accounts.HasIdentityConflict(profile))
                {
                    Console.WriteLine("Codex credits live read: profile skipped; verified saved binding unavailable.");
                    continue;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var session = await new CodexAppServerClient().ReadQuotaAsync(
                    command with { CodexHome = profile.HomePath, ManagedHome = profile.IsManaged },
                    "credits-read-only-check", deadline.Token);
                var identity = CodexAccountIdentity.Parse(session.AccountResult);
                var binding = expected.State;
                var matches = identity.Status == CodexQuotaStatus.Available
                    && identity.StableAccountFingerprint is not null
                    && (binding.AccountFingerprint is { } account
                        ? account == identity.StableAccountFingerprint
                        : binding.LegacyIdentityFingerprint == identity.Fingerprint
                            || binding.LegacyIdentityFingerprint == identity.StableAccountFingerprint);
                if (!matches || bindings.ReadExisting() != expected || accounts.HasIdentityConflict(profile))
                {
                    Console.WriteLine("Codex credits live read: profile identity not verified; optional data hidden.");
                    failed = true;
                    continue;
                }
                if (session.Status != CodexQuotaStatus.Available)
                {
                    Console.WriteLine($"Codex credits live read: identity verified; quota request status={session.Status}.");
                    failed = true;
                    continue;
                }
                var parsed = CodexRateLimitParser.Parse(session.AccountResult, session.RateLimitsResult);
                var bucket = CodexRateLimitParser.SelectBucket(session.RateLimitsResult);
                var credits = bucket?["credits"] as JsonObject;
                var rawBalance = credits?["balance"];
                Console.WriteLine($"Codex credits live read: identity verified; quota status={parsed.Status}; "
                    + $"credits object present={credits is not null}; flags valid={parsed.UsageCredits is not null}; "
                    + $"balance type={rawBalance?.GetValueKind().ToString() ?? "missing-or-null"}; "
                    + $"balance parsed={parsed.UsageCredits?.Balance is not null}; workspace identifier exposed=false.");
                verified++;
            }
            return failed ? 1 : verified > 0 ? 0 : 2;
        }
        catch
        {
            // Neither diagnostics nor account/profile labels are safe probe output.
            Console.WriteLine("Codex credits live read: failed or timed out; authentication material was not recorded.");
            return 1;
        }
    }
}
