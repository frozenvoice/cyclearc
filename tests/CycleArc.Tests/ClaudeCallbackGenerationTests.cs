using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using static CycleArc.Tests.ClaudeStatusLineTests;

namespace CycleArc.Tests;

public sealed class ClaudeCallbackGenerationTests
{
    [Fact]
    public async Task LateOldGenerationPreservesCurrentQuotaFailureAndPresentationWhileForwardingOutput()
    {
        using var data = new ClaudeTestData();
        var cli = new FixtureCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var config = Path.Combine(data.Root, "claude-home");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"),
            """{"statusLine":{"type":"command","command":"echo preserved-output"}}""");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        Assert.True((await connection.ConnectAsync(data.Profile.Id, app, false, config, default)).Success);
        var oldOptions = Options(config);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        Assert.True((await connection.ReauthenticateAsync(data.Profile.Id, app, default)).Success);
        var currentOptions = Options(config);
        Assert.NotEqual(oldOptions.BindingGeneration, currentOptions.BindingGeneration);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        Assert.Equal(0, (await Receive(data, cli, currentOptions, Payload(17))).Code);
        var service = new ClaudeUsageProvider(data.Accounts, data.Clock, connection).Create(data.Profile);
        var before = service.Snapshot;
        Assert.Equal(CodexQuotaStatus.Available, before.Status);
        var state = data.Store().Read().State;
        var quota = File.ReadAllBytes(data.Path);
        var failure = File.ReadAllBytes(data.Accounts.ClaudeFailurePath(data.Profile.Id));
        var calls = cli.Calls;

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        var rejected = await Receive(data, cli, oldOptions, Payload(92));
        Assert.Equal(1, rejected.Code);
        Assert.Equal("preserved-output", rejected.Output.Trim());
        Assert.Equal(calls, cli.Calls);
        Assert.Equal(quota, File.ReadAllBytes(data.Path));
        Assert.Equal(failure, File.ReadAllBytes(data.Accounts.ClaudeFailurePath(data.Profile.Id)));
        Assert.Equal(state, data.Store().Read().State);
        await service.RefreshAsync(default);
        Assert.Equal(before, service.Snapshot);

        // The older manual receiver is also inert after automatic connection.
        Assert.Equal(0, (await data.Receive(Payload(99))).Code);
        Assert.Equal(quota, File.ReadAllBytes(data.Path));
        await service.RefreshAsync(default);
        Assert.Equal(before, service.Snapshot);
    }

    [Fact]
    public async Task CallbackWaitingForQuotaLockRechecksBindingBeforeWriting()
    {
        using var data = new ClaudeTestData();
        var cli = new FixtureCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var config = Path.Combine(data.Root, "claude-home");
        var result = await connection.ConnectAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), false, config, default);
        Assert.True(result.Success);
        var store = data.Store();
        var sample = ClaudeStatusLineParser.Parse(Encoding.UTF8.GetBytes(Payload(22)));
        await store.RecordForBindingAsync(sample, data.Clock.UtcNow, data.Accounts, result.Binding!, default);
        var before = File.ReadAllBytes(data.Path);
        using var lease = new FileStream(data.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pending = store.RecordForBindingAsync(sample, data.Clock.UtcNow.AddMinutes(1), data.Accounts, result.Binding!, default);
        Assert.False(pending.IsCompleted);
        var current = result.Binding! with { BindingGeneration = Guid.NewGuid().ToString("N") };
        new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Save(current);
        lease.Dispose();
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.Equal(before, File.ReadAllBytes(data.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(data.Path)!, "*.tmp"));
        await store.RecordForBindingAsync(sample, data.Clock.UtcNow.AddMinutes(2), data.Accounts, current, default);
        Assert.Equal(data.Clock.UtcNow.AddMinutes(2), store.Read().State!.LastReceivedAt);
    }

    [Fact]
    public async Task ManualCallbackWaitingForQuotaLockCannotWriteAfterAutomaticConnection()
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload(15));
        var before = File.ReadAllBytes(data.Path);
        using var lease = new FileStream(data.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pending = data.Receive(Payload(99));
        Assert.False(pending.IsCompleted);
        var auth = ClaudeAuthentication.Parse(ClaudeConnectionTests.AuthJson, 0);
        new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Save(new(2, data.Profile.Id,
            Path.Combine(data.Root, "claude-home"), Path.Combine(data.Root, "claude.exe"), false,
            auth.Fingerprint!, data.Clock.UtcNow, BindingGeneration: Guid.NewGuid().ToString("N")));
        lease.Dispose();
        Assert.Equal(1, (await pending).Code);
        Assert.Equal(before, File.ReadAllBytes(data.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindingMutationsWaitForTheReceiptCommitLease(bool delete)
    {
        using var data = new ClaudeTestData();
        var connections = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var auth = ClaudeAuthentication.Parse(ClaudeConnectionTests.AuthJson, 0);
        var binding = new ClaudeConnectionBinding(2, data.Profile.Id, Path.Combine(data.Root, "claude-home"),
            Path.Combine(data.Root, "claude.exe"), false, auth.Fingerprint!, data.Clock.UtcNow,
            BindingGeneration: Guid.NewGuid().ToString("N"));
        connections.Save(binding);
        var updated = binding with { BindingGeneration = Guid.NewGuid().ToString("N") };
        using var lease = await connections.AcquireLeaseAsync(default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = Task.Factory.StartNew(() =>
        {
            started.SetResult();
            if (delete) connections.Delete(); else connections.Save(updated);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotSame(mutation, await Task.WhenAny(mutation, Task.Delay(100)));
            Assert.Equal(binding, connections.Read().Binding);
        }
        finally { lease.Dispose(); }
        await mutation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(delete ? null : updated, connections.Read().Binding);
    }

    [Fact]
    public async Task CurrentGenerationAuthFailurePreservesQuotaReceiptAndRecordsSeparateFailure()
    {
        using var data = new ClaudeTestData();
        var cli = new FixtureCli(data.Root);
        var config = Path.Combine(data.Root, "claude-home");
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), false, config, default)).Success);
        var options = Options(config);
        Assert.Equal(0, (await Receive(data, cli, options, Payload())).Code);
        var before = File.ReadAllBytes(data.Path);
        cli.Response = new(ClaudeAuthStatus.SignedOut);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        Assert.Equal(1, (await Receive(data, cli, options, Payload(90))).Code);
        Assert.Equal(before, File.ReadAllBytes(data.Path));
        Assert.Equal(ClaudeFailureKind.AuthRequired, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);
        var service = new ClaudeUsageProvider(data.Accounts, data.Clock, connection).Create(data.Profile);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.Equal(23.5, Assert.Single(service.Snapshot.Windows, window => window.Kind == CodexWindowKind.FiveHour).UsedPercent);
    }

    private static ClaudeBridgeOptions Options(string config)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "settings.json")))!;
        Assert.True(ClaudeStatusLineInstaller.TryRead(json["statusLine"]!["command"]!.GetValue<string>(), out var options));
        return options!;
    }

    private static async Task<(int Code, string Output)> Receive(ClaudeTestData data, IClaudeCli cli,
        ClaudeBridgeOptions options, string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = new StringWriter();
        var code = await ClaudeStatusLineBridge.RunAsync(options, input, output, data.Accounts, cli, data.Clock);
        return (code, output.ToString());
    }

    private sealed class FixtureCli(string root) : IClaudeCli
    {
        public ClaudeAuthentication Response { get; set; } = ClaudeAuthentication.Parse(ClaudeConnectionTests.AuthJson, 0);
        public int Calls { get; private set; }
        public string? FindExecutable() => Path.Combine(root, "synthetic-claude.exe");
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Response);
        }
    }
}
