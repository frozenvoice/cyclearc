using System.Text;
using CycleArc.Codex;

namespace CycleArc.Tests;

public class CodexLoginTests
{
    [Theory]
    [InlineData("https://auth.openai.com/oauth/authorize?synthetic=1", true)]
    [InlineData("https://chatgpt.com/auth/authorize", true)]
    [InlineData("https://auth.openai.com.attacker.invalid/authorize", false)]
    [InlineData("https://auth.openai.com@attacker.invalid/authorize", false)]
    [InlineData("http://auth.openai.com/authorize", false)]
    [InlineData("https://auth.openai.com:444/authorize", false)]
    [InlineData("file:///C:/malicious.cmd", false)]
    [InlineData("https://auth.openai.com/\nfoo", false)]
    [InlineData("https://auth.openai.com\\@attacker.invalid/", false)]
    [InlineData("https://user@auth.openai.com/", false)]
    public void BrowserNavigationIsOfficialHttpsOnly(string value, bool expected) =>
        Assert.Equal(expected, CodexLoginUrl.IsAllowed(value, out _));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginHandlesEarlyAndNormalCompletionAndVerifiesAccount(bool early)
    {
        var factory = new ScriptedCodexProcessFactory { Responder = line =>
        {
            var standard = AccountTestProtocol.Standard(line);
            if (JsonNode.Parse(line)?["method"]?.ToString() != "account/login/start") return standard;
            return early ? new[] { AccountTestProtocol.Completed }.Concat(standard).ToArray()
                : standard.Concat([AccountTestProtocol.Completed]).ToArray();
        }};
        var opened = 0;
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (uri, _) =>
        { Assert.Equal("auth.openai.com", uri.Host); opened++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        Assert.Equal("synthetic@example.invalid", result.Identity?.Email);
        Assert.Equal(1, opened);
        Assert.True(factory.LastProcess!.HasExited);
        var methods = factory.LastProcess.Received.Select(line => JsonNode.Parse(line)!["method"]!.ToString()).ToArray();
        Assert.Equal(new[] { "initialize", "initialized", "account/login/start", "account/read" }, methods);
        Assert.DoesNotContain(factory.LastProcess.Received, line => line.Contains("accessToken") || line.Contains("refreshToken"));
    }

    [Fact]
    public async Task CancelSendsLoginCancelWithMatchingIdAndReapsProcess()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        using var cancel = new CancellationTokenSource();
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) =>
        { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
        Assert.Equal(CodexQuotaStatus.Cancelled, result.Status);
        var request = factory.LastProcess!.Received.Select(x => JsonNode.Parse(x)!)
            .Single(x => x["method"]?.ToString() == "account/login/cancel");
        Assert.Equal(AccountTestProtocol.LoginId, request["params"]!["loginId"]!.ToString());
        Assert.True(factory.LastProcess.HasExited);
    }

    [Fact]
    public async Task TimeoutIsDistinctFromCancelAndCleansUp()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) => Task.CompletedTask,
            CancellationToken.None, TimeSpan.FromMilliseconds(200));
        Assert.Equal(CodexQuotaStatus.TimedOut, result.Status);
        Assert.True(factory.LastProcess!.HasExited);
        Assert.Contains(factory.LastProcess.Received, x => x.Contains("account/login/cancel"));
    }

    [Fact]
    public async Task CancellationBeforeResponseWaitIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new ImmediateCancellationFactory(cancellation);

        var result = await new CodexAppServerClient(factory).LoginAsync(
            AccountTestProtocol.Command, "test", (_, _) => Task.CompletedTask, cancellation.Token);

        Assert.Equal(CodexQuotaStatus.Cancelled, result.Status);
        Assert.True(factory.LastProcess!.HasExited || factory.LastProcess.KillCalled);
    }

    [Theory]
    [InlineData("{\"id\":10,\"result\":{}}")]
    [InlineData("{\"id\":10,\"error\":{\"code\":-32601}}")]
    [InlineData("{\"id\":10,\"result\":{\"type\":\"chatgpt\",\"loginId\":\"3e2f5a8d-9263-49f5-8407-9fa2e9468434\",\"authUrl\":\"https://attacker.invalid/\"}}")]
    public async Task InvalidLoginResponseNeverOpensBrowserOrSucceeds(string reply)
    {
        var factory = new ScriptedCodexProcessFactory { Responder = line => JsonNode.Parse(line)?["method"]?.ToString() == "account/login/start"
            ? [reply] : AccountTestProtocol.Standard(line) };
        var opened = false;
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) =>
        { opened = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(opened);
        Assert.NotEqual(CodexQuotaStatus.Available, result.Status);
        Assert.True(factory.LastProcess!.HasExited);
    }

    [Fact]
    public async Task WrongLoginIdCannotCompleteCurrentAttempt()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = line => JsonNode.Parse(line)?["method"]?.ToString() == "account/login/start"
            ? AccountTestProtocol.Standard(line).Concat([AccountTestProtocol.Completed.Replace(AccountTestProtocol.LoginId, Guid.NewGuid().ToString())]).ToArray()
            : AccountTestProtocol.Standard(line) };
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) => Task.CompletedTask,
            CancellationToken.None, TimeSpan.FromMilliseconds(250));
        Assert.Equal(CodexQuotaStatus.TimedOut, result.Status);
        Assert.DoesNotContain(factory.LastProcess!.Received, line => line.Contains("account/read"));
    }

    [Fact]
    public async Task SuccessNotificationWithSignedOutAccountIsNotLoginSuccess()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = line => JsonNode.Parse(line)?["method"]?.ToString() switch
        {
            "account/login/start" => AccountTestProtocol.Standard(line).Append(AccountTestProtocol.Completed).ToArray(),
            "account/read" => ["""{"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}"""],
            _ => AccountTestProtocol.Standard(line)
        }};
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.SignedOut, result.Status);
    }

    [Fact]
    public async Task InvalidInitializeNeverStartsLogin()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = _ => ["""{"id":1,"error":{"code":-1}}"""] };
        var result = await new CodexAppServerClient(factory).LoginAsync(AccountTestProtocol.Command, "test", (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.ProtocolMismatch, result.Status);
        Assert.Single(factory.LastProcess!.Received);
    }

    [Fact]
    public async Task CancelDuringStartReapsProcessEvenWhenFactoryReturnsLate()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reaped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new GatedAccountProcess(AccountTestProtocol.Command, _ => { }, () => reaped.TrySetResult());
        var factory = new LateFactory(() =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic start release");
            return process;
        });
        using var cancel = new CancellationTokenSource();
        var pending = new CodexAppServerClient(factory).ReadQuotaAsync(AccountTestProtocol.Command, "test", cancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.Equal(CodexQuotaStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        }
        finally { release.Set(); }
        await reaped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.KillCalled);
        Assert.True(process.HasExited);
    }

    private sealed class ImmediateCancellationFactory(CancellationTokenSource cancellation) : ICodexProcessFactory
    {
        public ImmediateCancellationProcess? LastProcess { get; private set; }
        public ICodexProcess Start(CodexLaunchCommand command)
        {
            LastProcess = new ImmediateCancellationProcess(command, cancellation);
            return LastProcess;
        }
    }

    private sealed class ImmediateCancellationProcess(CodexLaunchCommand command, CancellationTokenSource cancellation) : ICodexProcess
    {
        public bool HasExited { get; private set; }
        public bool KillCalled { get; private set; }
        public int? ProcessId => null;
        public string FileName => command.FileName;
        public string Arguments => command.Arguments;
        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (JsonNode.Parse(line)?["method"]?.ToString() == "initialize") cancellation.Cancel();
            return Task.CompletedTask;
        }
        public Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken)
            => Task.FromResult<string?>("{\"id\":1,\"result\":{}}");
        public Task DrainStderrAsync(StringBuilder sink, int maxBytes, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(HasExited);
        public void KillTree()
        {
            KillCalled = true;
            HasExited = true;
        }
        public ValueTask DisposeAsync()
        {
            KillTree();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateFactory(Func<ICodexProcess> start) : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command) => start();
    }
}
