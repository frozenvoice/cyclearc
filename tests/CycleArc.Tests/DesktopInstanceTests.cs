using System.IO.Pipes;
using System.Text;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class DesktopInstanceTests
{
    [Fact]
    public void Mutex_IsFirstInstanceWins_AndReleaseAllowsNextOwner()
    {
        var name = "CycleArc.Tests." + Guid.NewGuid().ToString("N");
        using var first = DesktopInstanceLease.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(DesktopInstanceLease.TryAcquire(name));

        first!.Dispose();
        using var second = DesktopInstanceLease.TryAcquire(name);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Status_ReturnsFixedInstanceInformation()
    {
        var pipeName = DesktopInstancePipe.ForTests(Guid.NewGuid().ToString("N"));
        var info = new DesktopInstanceInfo(4321, @"C:\CycleArc\CycleArc.exe", "0.5.7+test");
        await using var server = new DesktopInstanceServer(new DesktopInstanceServerOptions
        {
            PipeName = pipeName,
            InstanceInfo = info
        });
        var serverTask = server.StartAsync();

        var response = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status,
            TimeSpan.FromSeconds(2));

        Assert.True(response.Succeeded);
        Assert.Null(response.Error);
        Assert.Equal(info.ProcessId, response.ProcessId);
        Assert.Equal(info.ExecutablePath, response.ExecutablePath);
        Assert.Equal(info.Version, response.Version);
        Assert.False(string.IsNullOrWhiteSpace(response.InstanceId));
        await server.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Shutdown_RejectsResponseFromAnOlderServerInstance()
    {
        var pipeName = DesktopInstancePipe.ForTests(Guid.NewGuid().ToString("N"));
        var info = new DesktopInstanceInfo(4321, @"C:\CycleArc\CycleArc.exe", "0.5.7+test");
        await using var serverA = new DesktopInstanceServer(new DesktopInstanceServerOptions
        {
            PipeName = pipeName,
            InstanceInfo = info
        });
        var serverTaskA = serverA.StartAsync();
        var statusA = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status,
            TimeSpan.FromSeconds(2));
        await serverA.DisposeAsync();
        await serverTaskA.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdownCalls = 0;
        await using var serverB = new DesktopInstanceServer(new DesktopInstanceServerOptions
        {
            PipeName = pipeName,
            InstanceInfo = info
        });
        var serverTaskB = serverB.StartAsync(onShutdown: () =>
        {
            shutdownCalls++;
            return Task.CompletedTask;
        });
        var statusB = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status,
            TimeSpan.FromSeconds(2));
        Assert.NotEqual(statusA.InstanceId, statusB.InstanceId);

        var response = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Shutdown,
            TimeSpan.FromSeconds(2), expectedInstance: statusA);

        Assert.False(response.Succeeded);
        Assert.Equal("instance-mismatch", response.Error);
        Assert.Equal(0, shutdownCalls);
        await serverB.DisposeAsync();
        await serverTaskB.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Activate_AcknowledgesBeforeCallbackCompletes()
    {
        var pipeName = DesktopInstancePipe.ForTests(Guid.NewGuid().ToString("N"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DesktopInstanceServer(new DesktopInstanceServerOptions { PipeName = pipeName });
        var serverTask = server.StartAsync(async () =>
        {
            callbackStarted.SetResult();
            await callbackRelease.Task;
        });

        var response = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Activate,
            TimeSpan.FromSeconds(2));

        Assert.True(response.Succeeded);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callbackRelease.SetResult();
        await server.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MalformedAndOversizedRequests_AreRejected()
    {
        var pipeName = DesktopInstancePipe.ForTests(Guid.NewGuid().ToString("N"));
        await using var server = new DesktopInstanceServer(new DesktopInstanceServerOptions { PipeName = pipeName });
        var serverTask = server.StartAsync();

        var malformed = await SendRawAsync(pipeName, Encoding.UTF8.GetBytes("{bad"));
        Assert.False(malformed.Succeeded);
        Assert.Equal("malformed-request", malformed.Error);

        var missingRequired = await SendRawAsync(pipeName, Encoding.UTF8.GetBytes("{\"version\":1}"));
        Assert.False(missingRequired.Succeeded);
        Assert.Equal("malformed-request", missingRequired.Error);

        var unsupported = await SendRawAsync(pipeName, Encoding.UTF8.GetBytes("{\"version\":1,\"command\":99}"));
        Assert.False(unsupported.Succeeded);
        Assert.Equal("unsupported-command", unsupported.Error);

        var oversized = await SendOversizedAsync(pipeName);
        Assert.False(oversized.Succeeded);
        Assert.Equal("malformed-request", oversized.Error);

        await server.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Client_ReturnsTimeoutWhenServerIsUnavailable()
    {
        var pipeName = DesktopInstancePipe.ForTests(Guid.NewGuid().ToString("N"));
        var response = await DesktopInstanceClient.RequestAsync(pipeName, DesktopInstanceCommand.Status,
            TimeSpan.FromMilliseconds(150));
        Assert.False(response.Succeeded);
        Assert.Equal("timeout", response.Error);
    }

    private static async Task<DesktopInstanceResponse> SendRawAsync(string pipeName, byte[] payload)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            DesktopInstancePipe.ClientPipeOptions);
        await pipe.ConnectAsync(2000);
        await DesktopInstanceServer.WriteFrameAsync(pipe, payload, CancellationToken.None);
        await pipe.FlushAsync();
        var response = await DesktopInstanceServer.ReadFrameAsync(pipe, CancellationToken.None);
        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response!);
        var root = document.RootElement;
        return new DesktopInstanceResponse(
            root.GetProperty("ok").GetBoolean(),
            root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ? error.GetString() : null,
            root.GetProperty("pid").GetInt32(),
            root.GetProperty("exePath").GetString() ?? string.Empty,
            root.GetProperty("versionText").GetString() ?? string.Empty,
            root.TryGetProperty("instanceId", out var instance) ? instance.GetString() ?? string.Empty : string.Empty);
    }

    private static async Task<DesktopInstanceResponse> SendOversizedAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            DesktopInstancePipe.ClientPipeOptions);
        await pipe.ConnectAsync(2000);
        await pipe.WriteAsync(BitConverter.GetBytes(DesktopInstancePipe.MaxMessageBytes + 1));
        await pipe.FlushAsync();
        var response = await DesktopInstanceServer.ReadFrameAsync(pipe, CancellationToken.None);
        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response!);
        var root = document.RootElement;
        return new DesktopInstanceResponse(
            root.GetProperty("ok").GetBoolean(),
            root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ? error.GetString() : null,
            root.GetProperty("pid").GetInt32(),
            root.GetProperty("exePath").GetString() ?? string.Empty,
            root.GetProperty("versionText").GetString() ?? string.Empty,
            root.TryGetProperty("instanceId", out var instance) ? instance.GetString() ?? string.Empty : string.Empty);
    }
}
