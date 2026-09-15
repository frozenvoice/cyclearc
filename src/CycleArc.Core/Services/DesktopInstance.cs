using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace CycleArc.Services;

public enum DesktopInstanceCommand
{
    Status,
    Activate,
    Shutdown
}

public sealed record DesktopInstanceInfo(int ProcessId, string ExecutablePath, string Version, string InstanceId = "")
{
    public static DesktopInstanceInfo Current()
    {
        var path = Environment.ProcessPath ?? string.Empty;
        var version = string.Empty;
        try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new DesktopInstanceInfo(Environment.ProcessId, path, version);
    }
}

public sealed record DesktopInstanceResponse(
    bool Succeeded,
    string? Error,
    int ProcessId,
    string ExecutablePath,
    string Version,
    string InstanceId = "")
{
    public static DesktopInstanceResponse Failure(string error) => new(false, error, 0, string.Empty, string.Empty, string.Empty);
}

public static class DesktopInstancePipe
{
    public const int ProtocolVersion = 1;
    public const int MaxMessageBytes = 4096;
    public const string PipePrefix = "CycleArc.Instance.";

    public static string ForCurrentUserSession(string? testKey = null)
    {
        var user = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        var session = Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var material = user + "|" + session + "|" + (testKey ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24];
        return PipePrefix + hash;
    }

    public static string ForTests(string key) => ForCurrentUserSession("test:" + key);

    internal static PipeOptions ClientPipeOptions => OperatingSystem.IsWindows()
        ? PipeOptions.Asynchronous
        : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    internal static bool IsOwnedByCurrentUser(PipeStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var userSid = WindowsIdentity.GetCurrent().User;
        if (userSid is null) return false;
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            return owner is SecurityIdentifier ownerSid && ownerSid.Equals(userSid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        // CurrentUserOnly on Windows derives the pipe owner from the token owner.
        // For an elevated process that can be BUILTIN\\Administrators rather than
        // the interactive user's SID, which makes a same-user client fail with
        // ERROR_ACCESS_DENIED. Build an explicit user-only ACL instead.
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        var userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetOwner(userSid);
        security.AddAccessRule(new PipeAccessRule(userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security,
            HandleInheritability.None, 0);
    }
}

public sealed class DesktopInstanceLease : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LocalOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly int _ownerThreadId;
    private bool _disposed;

    private DesktopInstanceLease(Mutex mutex, string name)
    {
        _mutex = mutex;
        _name = name;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public static DesktopInstanceLease? TryAcquire(string name = LegacyInstallation.SingleInstanceMutexName)
    {
        if (!LocalOwners.TryAdd(name, 0)) return null;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, name, out _);
            var owns = false;
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                mutex.Dispose();
                LocalOwners.TryRemove(name, out _);
                return null;
            }
            return new DesktopInstanceLease(mutex, name);
        }
        catch
        {
            mutex?.Dispose();
            LocalOwners.TryRemove(name, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("DesktopInstanceLease must be disposed on its acquiring thread.");
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        finally
        {
            _mutex.Dispose();
            LocalOwners.TryRemove(_name, out _);
        }
    }
}

public sealed class DesktopInstanceServerOptions
{
    public string? PipeName { get; init; }
    public DesktopInstanceInfo? InstanceInfo { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan DisposalTimeout { get; init; } = TimeSpan.FromSeconds(1);

    internal string ResolvedPipeName => PipeName ?? DesktopInstancePipe.ForCurrentUserSession();
}

public sealed class DesktopInstanceServer : IAsyncDisposable
{
    private readonly DesktopInstanceServerOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _serverTask;
    private NamedPipeServerStream? _activePipe;
    private readonly DesktopInstanceInfo _instanceInfo;

    public DesktopInstanceServer(DesktopInstanceServerOptions? options = null)
    {
        _options = options ?? new();
        _instanceInfo = (_options.InstanceInfo ?? DesktopInstanceInfo.Current()) with
        {
            InstanceId = Guid.NewGuid().ToString("N")
        };
    }

    public Task StartAsync(Func<Task>? onActivate = null, Func<Task>? onShutdown = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_serverTask is not null) throw new InvalidOperationException("Desktop instance server has already started.");
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _serverTask = ServeAsync(onActivate, onShutdown, _stop.Token);
            return _serverTask;
        }
    }

    private async Task ServeAsync(Func<Task>? onActivate, Func<Task>? onShutdown, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = DesktopInstancePipe.CreateServerStream(_options.ResolvedPipeName);
                lock (_gate) _activePipe = pipe;
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                var request = await ReadRequestAsync(pipe, token).ConfigureAwait(false);
                var response = Handle(request, _instanceInfo);
                await WriteResponseAsync(pipe, response, token).ConfigureAwait(false);

                // The acknowledgement is flushed before a callback can close the
                // server or dispatch a window activation/shutdown operation.
                if (response.Succeeded && request?.Command == DesktopInstanceCommand.Activate) await InvokeCallbackAsync(onActivate).ConfigureAwait(false);
                else if (response.Succeeded && request?.Command == DesktopInstanceCommand.Shutdown) await InvokeCallbackAsync(onShutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                }
                pipe?.Dispose();
            }
        }
    }

    private DesktopInstanceResponse Handle(DesktopInstanceRequest? request, DesktopInstanceInfo info)
    {
        if (request is null) return DesktopInstanceResponse.Failure("malformed-request");
        if (request.Version != DesktopInstancePipe.ProtocolVersion) return DesktopInstanceResponse.Failure("unsupported-version");
        if (!Enum.IsDefined(request.Command)) return DesktopInstanceResponse.Failure("unsupported-command");
        if (request.Command == DesktopInstanceCommand.Shutdown
            && (request.ExpectedProcessId != info.ProcessId
                || !string.Equals(request.ExpectedInstanceId, info.InstanceId, StringComparison.Ordinal)))
            return DesktopInstanceResponse.Failure("instance-mismatch");
        return new DesktopInstanceResponse(true, null, info.ProcessId, info.ExecutablePath, info.Version, info.InstanceId);
    }

    private async Task InvokeCallbackAsync(Func<Task>? callback)
    {
        if (callback is null) return;
        try { await callback().WaitAsync(_options.WriteTimeout).ConfigureAwait(false); }
        catch { /* A dispatched UI callback must not take down the IPC server. */ }
    }

    private async Task<DesktopInstanceRequest?> ReadRequestAsync(Stream stream, CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(_options.ReadTimeout);
        var payload = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (payload is null) return null;
        try
        {
            return JsonSerializer.Deserialize<DesktopInstanceRequest>(payload, WireJson.Options);
        }
        catch (JsonException) { return null; }
    }

    private async Task WriteResponseAsync(Stream stream, DesktopInstanceResponse response, CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(_options.WriteTimeout);
        var wire = new DesktopInstanceResponseWire(DesktopInstancePipe.ProtocolVersion, response.Succeeded,
            response.Error, response.ProcessId, response.ExecutablePath, response.Version, response.InstanceId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(wire, WireJson.Options);
        await WriteFrameAsync(stream, payload, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stop;
        Task? server;
        NamedPipeServerStream? pipe;
        lock (_gate)
        {
            stop = _stop;
            server = _serverTask;
            pipe = _activePipe;
            _stop = null;
            _serverTask = null;
            _activePipe = null;
        }
        if (stop is null) return;
        stop.Cancel();
        pipe?.Dispose();
        if (server is not null)
        {
            try { await server.WaitAsync(_options.DisposalTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }

    internal static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > DesktopInstancePipe.MaxMessageBytes) return null;
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);
        return payload;
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length == 0 || payload.Length > DesktopInstancePipe.MaxMessageBytes)
            throw new InvalidDataException("Desktop instance message exceeds the 4 KiB limit.");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private sealed record DesktopInstanceRequest(
        [property: JsonPropertyName("version"), JsonRequired] int Version,
        [property: JsonPropertyName("command"), JsonRequired] DesktopInstanceCommand Command,
        [property: JsonPropertyName("expectedPid")] int? ExpectedProcessId = null,
        [property: JsonPropertyName("expectedInstanceId")] string? ExpectedInstanceId = null);

    private sealed record DesktopInstanceResponseWire(
        [property: JsonPropertyName("version"), JsonRequired] int Version,
        [property: JsonPropertyName("ok"), JsonRequired] bool Succeeded,
        [property: JsonPropertyName("error"), JsonRequired] string? Error,
        [property: JsonPropertyName("pid"), JsonRequired] int ProcessId,
        [property: JsonPropertyName("exePath"), JsonRequired] string ExecutablePath,
        [property: JsonPropertyName("versionText"), JsonRequired] string VersionText,
        [property: JsonPropertyName("instanceId"), JsonRequired] string InstanceId);

    private static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }
}

public static class DesktopInstanceClient
{
    public static async Task<DesktopInstanceResponse> RequestAsync(string pipeName, DesktopInstanceCommand command,
        TimeSpan timeout, CancellationToken cancellationToken = default, DesktopInstanceResponse? expectedInstance = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (command == DesktopInstanceCommand.Shutdown && expectedInstance is null)
            return DesktopInstanceResponse.Failure("instance-required");

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(timeout);
        Exception? last = null;
        for (var attempt = 0; attempt < 2 && !overall.IsCancellationRequested; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    DesktopInstancePipe.ClientPipeOptions);
                await pipe.ConnectAsync(250, overall.Token).ConfigureAwait(false);
                if (!DesktopInstancePipe.IsOwnedByCurrentUser(pipe))
                    throw new UnauthorizedAccessException("Desktop instance pipe owner is not the current user.");
                var request = new
                {
                    version = DesktopInstancePipe.ProtocolVersion,
                    command = command.ToString().ToLowerInvariant(),
                    expectedPid = command == DesktopInstanceCommand.Shutdown ? expectedInstance!.ProcessId : (int?)null,
                    expectedInstanceId = command == DesktopInstanceCommand.Shutdown ? expectedInstance!.InstanceId : null
                };
                var payload = JsonSerializer.SerializeToUtf8Bytes(request);
                using var ioTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                ioTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await DesktopInstanceServer.WriteFrameAsync(pipe, payload, ioTimeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(ioTimeout.Token).ConfigureAwait(false);
                var responsePayload = await DesktopInstanceServer.ReadFrameAsync(pipe, ioTimeout.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Desktop instance response is invalid.");
                var wire = JsonSerializer.Deserialize<DesktopInstanceResponseWire>(responsePayload, ResponseJson.Options)
                    ?? throw new InvalidDataException("Desktop instance response is invalid.");
                if (wire.Version != DesktopInstancePipe.ProtocolVersion)
                    return DesktopInstanceResponse.Failure("unsupported-version");
                return new DesktopInstanceResponse(wire.Succeeded, wire.Error, wire.ProcessId, wire.ExecutablePath, wire.VersionText, wire.InstanceId);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException
                                           or JsonException or UnauthorizedAccessException or InvalidOperationException
                                           or OperationCanceledException)
            {
                last = ex;
                if (attempt == 0 && !overall.IsCancellationRequested)
                {
                    try { await Task.Delay(50, overall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (overall.IsCancellationRequested) { }
                }
            }
        }
        if (overall.IsCancellationRequested && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        return DesktopInstanceResponse.Failure(last is OperationCanceledException ? "timeout"
            : last is JsonException or InvalidDataException ? "malformed-response" : "unavailable");
    }

    private sealed record DesktopInstanceResponseWire(
        [property: JsonPropertyName("version"), JsonRequired] int Version,
        [property: JsonPropertyName("ok"), JsonRequired] bool Succeeded,
        [property: JsonPropertyName("error"), JsonRequired] string? Error,
        [property: JsonPropertyName("pid"), JsonRequired] int ProcessId,
        [property: JsonPropertyName("exePath"), JsonRequired] string ExecutablePath,
        [property: JsonPropertyName("versionText"), JsonRequired] string VersionText,
        [property: JsonPropertyName("instanceId"), JsonRequired] string InstanceId);

    private static class ResponseJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
    }
}
