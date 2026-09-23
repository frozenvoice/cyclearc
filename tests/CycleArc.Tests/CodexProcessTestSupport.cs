namespace CycleArc.Tests;

internal static class CodexProcessTestSupport
{
    internal static (string Node, string Script) RequireNode(
        string fixtureName, string? searchPath = null, string? baseDirectory = null)
    {
        var script = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(script), $"Required Codex process fixture is missing: {script}");
        var node = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists);
        Assert.True(node is not null,
            "Required Node.js runtime was not found: node.exe must be on PATH for Codex process tests.");
        return (node, script);
    }

    internal static async Task RunWithCleanupAsync(Func<Task> body, Action<string> report,
        params (string Stage, Func<Task> Run)[] cleanup)
    {
        var assertionsPassed = false;
        try
        {
            await body();
            assertionsPassed = true;
        }
        finally
        {
            var failures = new List<Exception>();
            // Attempt every step, including handle/fixture disposal after a timed-out join.
            foreach (var (stage, run) in cleanup)
            {
                try { await run(); }
                catch (Exception ex)
                {
                    report($"Codex process cleanup failed at {stage}: {ex}");
                    failures.Add(ex);
                }
            }

            // A pending body exception retains its original stack and diagnostic message.
            // Cleanup-only failures must still fail the test after all steps have run.
            if (assertionsPassed && failures.Count > 0)
                throw new AggregateException("Codex process test cleanup failed.", failures);
        }
    }
}
