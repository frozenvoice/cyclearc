using System.Diagnostics;
using CycleArc.Codex;
using Xunit.Abstractions;
using static CycleArc.Tests.CodexProcessTestSupport;

namespace CycleArc.Tests;

public sealed class CodexColdStartTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FixtureBootstrap_UnreadyDescendantFailsAndCannotBeClaimed()
    {
        var (node, script) = RequireNode("hang-codex-app-server.js");
        using var fixture = new CodexProcessFixture();
        var command = new CodexLaunchCommand(node,
            $"\"{script}\" {fixture.Arguments} --gate-child-start", script, false);
        var elapsed = Stopwatch.StartNew();
        var startup = fixture.BootstrapAsync(command);
        await fixture.WaitForStageAsync("child-start-gated", startup);
        using var parent = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.StagePath("script-entered"))));
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.StagePath("after-spawn"))));
        _ = parent.Handle;
        _ = child.Handle;
        var failure = await Record.ExceptionAsync(() => startup);
        Assert.NotNull(failure);
        Assert.Contains("fixture-bootstrap parent/descendant readiness failed within 10s", failure.Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Start(command));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(10));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(13));
        output.WriteLine("expected bootstrap refusal: " + failure.Message);
        fixture.Dispose();
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("spawn")]
    [InlineData("child-start")]
    public async Task ColdLaunch_TimeoutReapsProcessesBeforeReadiness(string phase)
    {
        var (node, script) = RequireNode("hang-codex-app-server.js");
        using var fixture = new CodexProcessFixture();
        var arguments = $"\"{script}\" {fixture.Arguments}";
        var gate = phase switch
        {
            "bootstrap" => "bootstrap-gated",
            "spawn" => "spawn-gated",
            _ => "child-start-gated"
        };
        if (phase == "bootstrap")
        {
            var preload = Path.Combine(fixture.Root, "cold-bootstrap.cjs");
            await File.WriteAllTextAsync(preload, """
                const fs = require('fs');
                const path = require('path');
                const directory = process.argv[process.argv.indexOf('--fixture-directory') + 1];
                fs.writeFileSync(path.join(directory, 'bootstrap-gated'), String(process.pid));
                process.stderr.write('synthetic bootstrap blocked before script entry\n');
                Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60000);
                """);
            arguments = $"--require \"{preload}\" " + arguments;
        }
        else arguments += phase == "spawn" ? " --gate-before-spawn" : " --gate-child-start";

        var command = new CodexLaunchCommand(node, arguments, script, false);
        using var cancellation = new CancellationTokenSource();
        Process? child = null;
        var elapsed = Stopwatch.StartNew();
        var pending = new CodexAppServerClient(fixture).ReadQuotaAsync(command, "test", cancellation.Token);
        await RunWithCleanupAsync(async () =>
        {
            await fixture.WaitForStageAsync(gate, pending);
            if (phase == "child-start")
            {
                child = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.StagePath("after-spawn"))));
                _ = child.Handle;
                Assert.False(child.HasExited);
                await fixture.WaitForStageAsync("initialize-received", pending);
            }
            Assert.False(File.Exists(fixture.StagePath("child-ready")), fixture.Describe());
            Assert.Equal(phase != "bootstrap", File.Exists(fixture.StagePath("script-entered")));
            Assert.Equal(phase == "child-start", File.Exists(fixture.StagePath("after-spawn")));
            var session = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine($"phase={phase} status={session.Status} detail={session.Detail} "
                + $"elapsed={elapsed.Elapsed} sent={string.Join(',', session.SentMethods)} "
                + $"stderr={session.SanitizedStderr}; {fixture.Describe()}");
            Assert.Equal(CodexQuotaStatus.TimedOut, session.Status);
            Assert.True(session.ProcessCleanedUp);
            Assert.True(session.KillCalled);
            Assert.Equal(new[] { "initialize" }, session.SentMethods);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(CodexProtocol.InitializeTimeoutMs));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), fixture.Describe());
            fixture.AssertExited();
            if (child is not null) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (phase == "bootstrap") Assert.Contains("before script entry", session.SanitizedStderr);
        }, output.WriteLine,
            ("cancel", () => { cancellation.Cancel(); return Task.CompletedTask; }),
            ("protocol-completion", async () =>
            {
                var session = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                output.WriteLine($"cleanup phase={phase} status={session.Status} stderr={session.SanitizedStderr}; {fixture.Describe()}");
            }),
            ("descendant-cleanup", async () =>
            {
                if (child is { HasExited: false })
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                child?.Dispose();
            }));
    }
}
