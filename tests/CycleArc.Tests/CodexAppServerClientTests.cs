using System.Diagnostics;
using System.Text;
using CycleArc.Codex;

namespace CycleArc.Tests;

public class CodexAppServerClientTests
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
        if (!TryNode(out var node, out var script))
        {
            return;
        }

        var command = new CodexLaunchCommand(node, $"\"{script}\"", script, false);
        var client = new CodexAppServerClient(new CodexProcessFactory());
        var session = await client.ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.True(session.ProcessCleanedUp);
        Assert.DoesNotContain("thread/", string.Join(",", session.SentMethods), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealStderrContinuesDrainingAfterUtf8DiagnosticBudget()
    {
        if (!TryNode(out var node, out var script)) return;
        var command = new CodexLaunchCommand(node, $"\"{script}\" --flood-stderr", script, false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await new CodexAppServerClient(new CodexProcessFactory())
            .ReadQuotaAsync(command, "test", deadline.Token);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.True(session.ProcessCleanedUp);
        Assert.NotEmpty(session.SanitizedStderr);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(session.SanitizedStderr) <= CodexProtocol.MaxStderrBytes);
        Assert.DoesNotContain('\uFFFD', session.SanitizedStderr);
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
        if (!TryNode(out var node, out var script))
        {
            return;
        }

        var cmdPath = Path.Combine(Path.GetTempPath(), $"cyclearc-codex-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(cmdPath, $"@echo off{Environment.NewLine}\"{node}\" \"{script}\" %*{Environment.NewLine}");
        var files = new MemoryCodexFileSystem();
        files.Files.Add(Path.GetFullPath(cmdPath));
        var command = CodexExecutableLocator.ValidateConfigured(cmdPath, files);
        Assert.NotNull(command);
        Assert.True(command.UsesCmd);
        var session = await new CodexAppServerClient(new CodexProcessFactory())
            .ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.True(session.ProcessCleanedUp);
        File.Delete(cmdPath);
    }

    [Fact]
    public async Task HangProcess_CancelCleansTree()
    {
        if (!TryNode(out var node, out _, out var hang))
        {
            return;
        }

        var command = new CodexLaunchCommand(node, $"\"{hang}\"", hang, false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var session = await new CodexAppServerClient(new CodexProcessFactory())
            .ReadQuotaAsync(command, "1.0.0", cts.Token);
        Assert.True(session.Status is CodexQuotaStatus.Cancelled or CodexQuotaStatus.TimedOut);
        Assert.True(session.ProcessCleanedUp);
    }

    private static CodexLaunchCommand DummyCommand() =>
        new(@"C:\Tools\codex.exe", "app-server --stdio", @"C:\Tools\codex.exe", false);

    private static bool TryNode(out string node, out string script) => TryNode(out node, out script, out _);

    private static bool TryNode(out string node, out string script, out string hang)
    {
        node = "node";
        script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-codex-app-server.js");
        hang = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hang-codex-app-server.js");
        try
        {
            var where = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in where.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate) && File.Exists(script))
                {
                    node = candidate;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

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
