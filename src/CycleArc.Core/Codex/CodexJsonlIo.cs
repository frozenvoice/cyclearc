using System.Runtime.CompilerServices;
using System.Text;

namespace CycleArc.Codex;

public static class CodexJsonlIo
{
    private static readonly ConditionalWeakTable<Stream, CodexJsonlReader> Readers = new();

    public static async Task<string?> ReadBoundedLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
        => await Readers.GetValue(stream, static key => new CodexJsonlReader(key))
            .ReadLineAsync(maxBytes, cancellationToken)
            .ConfigureAwait(false);

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CodexJsonlReader
{
    private const int ReadBufferSize = 4096;
    private readonly Stream _stream;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private int _bufferOffset;
    private int _bufferCount;

    public CodexJsonlReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream(maxBytes > 0 ? Math.Min(maxBytes, ReadBufferSize) : 0);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
            {
                return line.Length == 0 ? null : Decode(line, trimCarriageReturn: false);
            }

            var available = _bufferCount - _bufferOffset;
            var newline = Array.IndexOf(_readBuffer, (byte)'\n', _bufferOffset, available);
            var segmentLength = newline >= 0 ? newline - _bufferOffset : available;
            if (segmentLength > maxBytes - line.Length)
            {
                throw new CodexProtocolException("JSONL line exceeded the safe maximum size.");
            }

            if (segmentLength > 0)
            {
                line.Write(_readBuffer, _bufferOffset, segmentLength);
                _bufferOffset += segmentLength;
            }

            if (newline >= 0)
            {
                _bufferOffset++;
                return Decode(line, trimCarriageReturn: true);
            }
        }
    }

    private async ValueTask<bool> EnsureBufferedAsync(CancellationToken cancellationToken)
    {
        if (_bufferOffset < _bufferCount)
        {
            return true;
        }

        _bufferOffset = 0;
        _bufferCount = await _stream.ReadAsync(_readBuffer.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        return _bufferCount > 0;
    }

    private static string Decode(MemoryStream line, bool trimCarriageReturn)
    {
        var bytes = line.ToArray();
        var length = trimCarriageReturn && bytes.Length > 0 && bytes[^1] == (byte)'\r'
            ? bytes.Length - 1
            : bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
