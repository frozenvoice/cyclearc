using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Headless receiver for CycleArc's Cursor hook entries, routed before WPF, the desktop mutex or
/// account startup. It keeps only the fields <see cref="CursorHookEvent"/> extracts, never logs
/// or echoes stdin, makes no network request, and always lets Cursor continue.
/// </summary>
public static class CursorHookCommand
{
    public const string Argument = "--cursor-hook";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    // A prompt with pasted code can be large. Beyond this the receipt is skipped, not truncated.
    public const int MaxInputBytes = 8 * 1024 * 1024;
    public const string ContinueResponse = "{\"continue\":true}";
    public const string EmptyResponse = "{}";

    public static string ResponseFor(string? hookEvent) =>
        hookEvent == CursorHookEvent.StopEvent ? EmptyResponse : ContinueResponse;

    /// <summary>Always returns 0 after writing a response Cursor accepts; a lost receipt is not an error to Cursor.</summary>
    public static async Task<int> RunAsync(string payload, Stream input, TextWriter output,
        IClock? clock = null, CancellationToken token = default)
    {
        // Receipt order is captured before reading, so a slow process cannot pass a newer one.
        var received = (clock ?? SystemClock.Instance).UtcNow;
        string? hookEvent = null;
        try
        {
            var options = CursorHookInstaller.Decode(payload);
            hookEvent = options.Event;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Deadline);
            var bytes = await ReadBoundedAsync(input, deadline.Token).ConfigureAwait(false);
            var entry = bytes is null ? null : CursorHookEvent.Parse(bytes, received);
            var expected = options.Event == CursorHookEvent.SubmitEvent
                ? CursorActivityKind.Request : CursorActivityKind.Completion;
            // A hook never creates CycleArc's data folder; without it there is no account to show.
            if (entry is not null && entry.Kind == expected && Directory.Exists(options.DataRoot))
                await new CursorActivityStore(options.DataRoot).RecordAsync(entry, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidDataException or CursorHookException or JsonException or NotSupportedException or ArgumentException)
        {
            // No exception text: stdin and paths may contain unrelated private session data.
        }
        await output.WriteLineAsync(ResponseFor(hookEvent)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(chunk.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > MaxInputBytes) return null;
            buffer.Write(chunk, 0, count);
        }
    }
}
