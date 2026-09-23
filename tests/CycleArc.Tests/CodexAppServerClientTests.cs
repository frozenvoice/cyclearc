using System.Diagnostics;
using System.Text;
using CycleArc.Codex;
using Xunit.Abstractions;
using static CycleArc.Tests.CodexProcessTestSupport;

namespace CycleArc.Tests;

public class CodexAppServerClientTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExecutableNotFound_ReturnsCodexNotFound()
    {
        var client = new CodexAppServerClient(new MissingProcessFactory());
        var session = await client.ReadQuotaAsync(
            new CodexLaunchCommand(@"C:\missing\codex.exe", "app-server --stdio", @"C:\missing\codex.exe", false),
            "1.0.0",
            CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.CodexNotFound, session.Status);
        Assert.True(session.ProcessCleanedUp);
    }

    [Fact]
    public async Task InitializeIsSentBeforeAccountRequests()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = CodexScript.Standard };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.2.3", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, session.SentMethods);
        var received = factory.LastProcess!.Received.Select(line => JsonNode.Parse(line)!["method"]!.ToString()).ToList();
        Assert.Equal("initialize", received[0]);
        Assert.Contains("initialized", received);
        Assert.Contains("\"params\":{}", factory.LastProcess.Received.First(line => line.Contains("account/read", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("\"params\":{}", factory.LastProcess.Received.First(line => line.Contains("account/rateLimits/read", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain(received, method => method.StartsWith("thread/", StringComparison.Ordinal) || method.StartsWith("turn/", StringComparison.Ordinal));
        Assert.Contains("1.2.3", factory.LastProcess.Received[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountReadSendsParams_AndParsesLiveRateLimitShape()
    {
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var node = JsonNode.Parse(line) as JsonObject;
                var method = node?["method"]?.ToString();
                if (method == "initialize")
                {
                    return ["""{"id":1,"result":{"userAgent":"codex-app-server"}}"""];
                }

                if (method == "account/read")
                {
                    if (node?["params"] is null)
                    {
                        return ["""{"id":2,"error":{"code":-32600,"message":"Invalid request: missing field `params`"}}"""];
                    }

                    return ["""{"id":2,"result":{"account":{"planType":"plus"},"requiresOpenaiAuth":true}}"""];
                }

                if (method == "account/rateLimits/read")
                {
                    return
                    [
                        """{"method":"configWarning","params":{}}""",
                        """{"method":"remoteControl/status/changed","params":{}}""",
                        """{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":97,"windowDurationMins":10080,"resetsAt":1893456000},"secondary":null},"rateLimitsByLimitId":{"codex_extra":{"primary":{"usedPercent":0,"windowDurationMins":300}},"codex":{"primary":{"usedPercent":97,"windowDurationMins":10080,"resetsAt":1893456000}}},"rateLimitResetCredits":{"availableCount":3}}}"""
                    ];
                }

                return [];
            }
        };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        var parsed = CodexRateLimitParser.Parse(session.AccountResult, session.RateLimitsResult);
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(97, parsed.Windows[0].UsedPercent);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
        Assert.Equal(3, parsed.ResetCreditsAvailable);
        Assert.Equal("plus", parsed.PlanType);
        Assert.DoesNotContain("thread/", string.Join(",", session.SentMethods), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavedNotifications_AreIgnoredAndIdsMatch()
    {
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method switch
                {
                    "initialize" =>
                    [
                        """{"method":"thread/started","params":{}}""",
                        """{"id":1,"result":{"ok":true}}"""
                    ],
                    "account/read" =>
                    [
                        """{"method":"item/completed","params":{}}""",
                        """{"id":"2","result":{"loggedIn":true}}"""
                    ],
                    "account/rateLimits/read" => ["""{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}}}}"""],
                    _ => []
                };
            }
        };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.NotNull(session.RateLimitsResult);
    }

    [Fact]
    public async Task Timeout_ReturnsPromptly()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = _ => [], ResponseDelay = TimeSpan.FromSeconds(60) };
        var started = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", cts.Token);
        started.Stop();
        Assert.Equal(CodexQuotaStatus.Cancelled, session.Status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(session.ProcessCleanedUp);
        Assert.True(factory.LastProcess!.KillCalled || factory.LastProcess.HasExited);
    }

    [Fact]
    public async Task CleanupRunsAfterSuccess()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = CodexScript.Standard };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        Assert.True(session.ProcessCleanedUp);
        Assert.True(factory.LastProcess!.HasExited || factory.LastProcess.KillCalled);
    }

    [Fact]
    public void Stderr_IsBoundedAndSanitized()
    {
        var text = CodexProtocol.SanitizeDiagnostic("Authorization: Bearer secret-token " + new string('x', 8000));
        Assert.DoesNotContain("Bearer secret-token", text, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= CodexProtocol.MaxStderrBytes + 8);
    }

    [Fact]
    public async Task RealNodeServer_CleansUpAfterSuccess()
    {
        var (node, script) = RequireNode("fake-codex-app-server.js");

        using var fixture = new CodexProcessFixture();
        var command = new CodexLaunchCommand(node, $"\"{script}\" {fixture.Arguments}", script, false);
        var client = new CodexAppServerClient(fixture);
        var elapsed = Stopwatch.StartNew();
        var session = await client.ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        elapsed.Stop();
        Assert.True(session.Status == CodexQuotaStatus.Available, Describe(session, elapsed.Elapsed) + fixture.Describe());
        Assert.True(session.ProcessCleanedUp, Describe(session, elapsed.Elapsed) + fixture.Describe());
        fixture.AssertExited();
        Assert.DoesNotContain("thread/", string.Join(",", session.SentMethods), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealStderrContinuesDrainingAfterUtf8DiagnosticBudget()
    {
        var (node, script) = RequireNode("fake-codex-app-server.js");
        using var fixture = new CodexProcessFixture();
        var command = new CodexLaunchCommand(node, $"\"{script}\" --flood-stderr {fixture.Arguments}", script, false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var elapsed = Stopwatch.StartNew();
        var session = await new CodexAppServerClient(fixture)
            .ReadQuotaAsync(command, "test", deadline.Token);
        elapsed.Stop();
        // This one has failed on CI without leaving anything to go on, so every assertion
        // carries the whole session. See Describe for how to read it.
        Assert.True(session.Status == CodexQuotaStatus.Available, Describe(session, elapsed.Elapsed) + fixture.Describe());
        Assert.True(session.ProcessCleanedUp, Describe(session, elapsed.Elapsed) + fixture.Describe());
        fixture.AssertExited();
        Assert.True(session.SanitizedStderr.Length > 0, Describe(session, elapsed.Elapsed) + fixture.Describe());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(session.SanitizedStderr) <= CodexProtocol.MaxStderrBytes,
            Describe(session, elapsed.Elapsed));
        Assert.DoesNotContain('\uFFFD', session.SanitizedStderr);
    }

    /// <summary>
    /// Everything needed to tell one failure of these real-process tests from another, because
    /// the status alone does not say where the run stopped.
    ///
    /// "startup-timed-out" means process creation did not finish in its five-second budget;
    /// it does not establish child readiness. The fixture markers separately identify readiness,
    /// initialize receipt and response (after the oversized stderr write's callback).
    /// stderrChars=0 alone cannot identify a stalled drain: capture is published only when
    /// draining ends, and an incomplete drain is intentionally omitted by the product.
    /// </summary>
    private static string Describe(CodexProtocolSession session, TimeSpan elapsed) =>
        $"status={session.Status} detail={session.Detail ?? "(none)"} "
        + $"elapsed={elapsed.TotalSeconds:n1}s sent=[{string.Join(" ", session.SentMethods)}] "
        + $"stderrChars={session.SanitizedStderr.Length} cleanedUp={session.ProcessCleanedUp} "
        + $"killed={session.KillCalled} "
        + $"stageTimeoutMs={CodexProtocol.InitializeTimeoutMs} ceilingMs={CodexProtocol.TotalHardCeilingMs} "
        + $"stderr={session.SanitizedStderr[..Math.Min(512, session.SanitizedStderr.Length)]} ";

    [Fact]
    public async Task RealNodeServer_EarlyExitReportsPhaseAndExitCode()
    {
        var (node, script) = RequireNode("fake-codex-app-server.js");
        using var fixture = new CodexProcessFixture();
        var command = new CodexLaunchCommand(node,
            $"\"{script}\" --exit-on-initialize {fixture.Arguments}", script, false);
        var session = await new CodexAppServerClient(fixture)
            .ReadQuotaAsync(command, "test", CancellationToken.None);
        var diagnostic = Describe(session, TimeSpan.Zero) + fixture.Describe();
        Assert.True(session.Status == CodexQuotaStatus.Unavailable, diagnostic);
        Assert.True(session.ProcessCleanedUp, diagnostic);
        fixture.AssertExited();
        Assert.Contains("exit=23", diagnostic);
        Assert.DoesNotContain("initialize-received=(missing)", diagnostic);
        Assert.Contains("initialize-response=(missing)", diagnostic);
        Assert.Contains("synthetic initialize failure", session.SanitizedStderr);
    }

    [Fact]
    public async Task CompletionWaitsForStderrDrainBeforeSnapshot()
    {
        var factory = new DrainOrderingProcessFactory();
        var readTask = new CodexAppServerClient(factory)
            .ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        var process = await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await process.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(readTask.IsCompleted);

        process.ReleaseDrain();
        var session = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.Contains("after-dispose", session.SanitizedStderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteStderrDrainDoesNotRaceDiagnosticSnapshot()
    {
        var factory = new DrainOrderingProcessFactory();
        var readTask = new CodexAppServerClient(factory)
            .ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        var process = await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await process.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var session = await readTask.WaitAsync(
            TimeSpan.FromMilliseconds(CodexProtocol.GracefulShutdownTimeoutMs + 2000));
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.Empty(session.SanitizedStderr);

        process.ReleaseDrain();
        await process.DrainFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(session.SanitizedStderr);
    }

    [Fact]
    public async Task CmdWrapper_LaunchesAndCleansUp()
    {
        var (node, script) = RequireNode("fake-codex-app-server.js");

        using var fixture = new CodexProcessFixture();
        var cmdPath = Path.Combine(fixture.Root, "synthetic codex.cmd");
        await File.WriteAllTextAsync(cmdPath, $"@echo off{Environment.NewLine}\"{node}\" \"{script}\" %*{Environment.NewLine}");
        var files = new MemoryCodexFileSystem();
        files.Files.Add(Path.GetFullPath(cmdPath));
        var command = CodexExecutableLocator.ValidateConfigured(cmdPath, files);
        Assert.NotNull(command);
        Assert.True(command.UsesCmd);
        command = command with { Arguments = command.Arguments[..^1] + " " + fixture.Arguments + "\"" };
        var elapsed = Stopwatch.StartNew();
        var session = await new CodexAppServerClient(fixture)
            .ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        Assert.True(session.Status == CodexQuotaStatus.Available, Describe(session, elapsed.Elapsed) + fixture.Describe());
        Assert.True(session.ProcessCleanedUp, Describe(session, elapsed.Elapsed) + fixture.Describe());
        fixture.AssertExited();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HangProcess_CancelOrTimeoutCleansTree(bool cancelAfterReady)
    {
        var (node, hang) = RequireNode("hang-codex-app-server.js");
        var fixture = new CodexProcessFixture();
        var command = new CodexLaunchCommand(node, $"\"{hang}\" {fixture.Arguments}", hang, false);
        using var cts = new CancellationTokenSource();
        Process? child = null;
        var elapsed = Stopwatch.StartNew();
        var pending = new CodexAppServerClient(fixture).ReadQuotaAsync(command, "1.0.0", cts.Token);
        await RunWithCleanupAsync(async () =>
        {
            await fixture.WaitForStageAsync("child-ready", pending);
            child = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.StagePath("child-ready"))));
            _ = child.Handle;
            await fixture.WaitForStageAsync("initialize-received", pending);
            Assert.False(child.HasExited, fixture.Describe());
            var behavior = Stopwatch.StartNew();
            if (cancelAfterReady) cts.Cancel();
            // Keep the product's ten-second initialize and two-second cleanup limits.
            CodexProtocolSession session;
            try { session = await pending.WaitAsync(TimeSpan.FromSeconds(cancelAfterReady ? 5 : 15)); }
            catch (TimeoutException)
            {
                Assert.Fail($"protocol-completion cancel={cancelAfterReady}; {fixture.Describe()}");
                throw;
            }
            var expected = cancelAfterReady ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut;
            Assert.True(session.Status == expected, Describe(session, elapsed.Elapsed) + fixture.Describe());
            Assert.True(session.ProcessCleanedUp, Describe(session, elapsed.Elapsed) + fixture.Describe());
            Assert.True(behavior.Elapsed < TimeSpan.FromSeconds(cancelAfterReady ? 5 : 15), fixture.Describe());
            fixture.AssertExited();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { Assert.Fail($"descendant-exit pid={child.Id}; {fixture.Describe()}"); }
        }, output.WriteLine,
            ("cancel", () => { cts.Cancel(); return Task.CompletedTask; }),
            ("protocol-completion", async () => { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }),
            ("descendant-exit", async () =>
            {
                if (child is { HasExited: false })
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }),
            ("descendant-dispose", () => { child?.Dispose(); return Task.CompletedTask; }),
            ("fixture-dispose", () => { fixture.Dispose(); return Task.CompletedTask; }));
    }

    private static CodexLaunchCommand DummyCommand() =>
        new(@"C:\Tools\codex.exe", "app-server --stdio", @"C:\Tools\codex.exe", false);

    private sealed class MissingProcessFactory : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command) =>
            throw new FileNotFoundException("Codex executable was not found.", command.ResolvedExecutable);
    }

    private sealed class DrainOrderingProcessFactory : ICodexProcessFactory
    {
        public TaskCompletionSource<DrainOrderingProcess> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ICodexProcess Start(CodexLaunchCommand command)
        {
            var process = new DrainOrderingProcess();
            Started.TrySetResult(process);
            return process;
        }
    }

    private sealed class DrainOrderingProcess : ICodexProcess
    {
        private readonly Queue<string> _responses = new();
        private readonly SemaphoreSlim _responseReady = new(0);
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _releaseDrain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> DrainStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> DrainFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            var request = JsonNode.Parse(line) as JsonObject;
            var method = request?["method"]?.ToString();
            var id = request?["id"]?.ToString();
            var response = method switch
            {
                "initialize" => $"{{\"id\":{id},\"result\":{{\"ok\":true}}}}",
                "initialized" => "{\"method\":\"session/ready\",\"params\":{}}",
                "account/read" => $"{{\"id\":{id},\"result\":{{\"account\":{{\"planType\":\"plus\"}}}}}}",
                "account/rateLimits/read" => $"{{\"id\":{id},\"result\":{{\"rateLimits\":{{\"primary\":{{\"usedPercent\":1,\"windowDurationMins\":300}}}}}}}}",
                _ => null
            };
            if (response is not null)
            {
                lock (_gate) _responses.Enqueue(response);
                _responseReady.Release();
            }

            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken)
        {
            await _responseReady.WaitAsync(cancellationToken);
            lock (_gate) return _responses.Dequeue();
        }

        public async Task DrainStderrAsync(StringBuilder sink, int maxBytes, CancellationToken cancellationToken)
        {
            DrainStarted.TrySetResult(true);
            await _releaseDrain.Task;
            sink.Append("after-dispose");
            DrainFinished.TrySetResult(true);
        }

        public bool HasExited { get; private set; }
        public bool KillCalled { get; private set; }
        public int? ProcessId => null;
        public string FileName => "drain-ordering";
        public string Arguments => "";
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(HasExited);

        public void KillTree()
        {
            KillCalled = true;
            HasExited = true;
        }

        public ValueTask DisposeAsync()
        {
            HasExited = true;
            Disposed.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public void ReleaseDrain() => _releaseDrain.TrySetResult(true);
    }
}
