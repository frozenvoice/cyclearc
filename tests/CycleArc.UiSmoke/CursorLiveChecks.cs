using CycleArc.Providers.Cursor;

namespace CycleArc.UiSmoke;

/// <summary>Explicitly requested read-only compatibility check; excluded from the offline gate.</summary>
internal static class CursorLiveChecks
{
    public static async Task<int> RunAsync()
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            using var client = new CursorUsageClient(new CursorAuthStateDatabaseReader());
            var identity = await client.ReadIdentityAsync(timeout.Token);
            if (!identity.Success || identity.IdentityFingerprint is null)
            {
                Console.WriteLine("Cursor live read: identity verification failed.");
                return 1;
            }

            // No account registry, binding, cache or settings are written. Identity and
            // credentials stay in memory; output is a fixed summary of verified shape.
            var binding = new CursorConnectionBinding(1, Guid.NewGuid().ToString("N"),
                identity.IdentityFingerprint, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            var response = await client.FetchAsync(binding, timeout.Token);
            if (response.Sample is not { } sample)
            {
                Console.WriteLine("Cursor live read: quota query failed.");
                return 1;
            }
            Console.WriteLine($"Cursor live read: identity verified; {sample.Windows.Count} separate quota rows; "
                + $"{sample.Windows.Count(window => window.UsedPercent is not null)} known percentages; "
                + $"{sample.Windows.Count(window => window.ResetsAt is not null)} reported resets; "
                + $"observed {sample.ObservedAt:O}; optional source complete: {response.Failure is null}.");
            return response.Failure is null ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cursor live read: cancelled or timed out.");
            return 1;
        }
        catch
        {
            // Exception messages may contain request details; never print them here.
            Console.WriteLine("Cursor live read: failed; no credentials were recorded.");
            return 1;
        }
    }
}
