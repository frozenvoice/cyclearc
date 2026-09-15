using System.Text;
using CycleArc.Codex;

namespace CycleArc.Tests;

public sealed class CodexJsonlIoTests
{
    [Fact]
    public async Task ReadBoundedLineAsync_PreservesUtf8CrLfMultipleLinesAndEofTail()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("첫 줄\r\n둘째 🌟\n마지막"));

        Assert.Equal("첫 줄", await CodexJsonlIo.ReadBoundedLineAsync(stream, 64, CancellationToken.None));
        Assert.Equal("둘째 🌟", await CodexJsonlIo.ReadBoundedLineAsync(stream, 64, CancellationToken.None));
        Assert.Equal("마지막", await CodexJsonlIo.ReadBoundedLineAsync(stream, 64, CancellationToken.None));
        Assert.Null(await CodexJsonlIo.ReadBoundedLineAsync(stream, 64, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBoundedLineAsync_EnforcesUtf8ByteLimit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("가\n"));

        await Assert.ThrowsAsync<CodexProtocolException>(() =>
            CodexJsonlIo.ReadBoundedLineAsync(stream, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBoundedLineAsync_AcceptsExactUtf8LimitBeforeCrLf()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc" + ((char)13) + ((char)10) + "next"));

        Assert.Equal("abc", await CodexJsonlIo.ReadBoundedLineAsync(stream, 4, CancellationToken.None));
        Assert.Equal("next", await CodexJsonlIo.ReadBoundedLineAsync(stream, 4, CancellationToken.None));
    }
    [Fact]
    public async Task ReadBoundedLineAsync_HonorsCancellationWhileWaitingForInput()
    {
        using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var read = CodexJsonlIo.ReadBoundedLineAsync(stream, 64, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task ReadBoundedLineAsync_UsesChunkedReadsForMaximumUtf8Line()
    {
        const int maxBytes = 262_145;
        var content = new string('x', maxBytes - 2) + "é";
        var payload = Encoding.UTF8.GetBytes(content + ((char)10) + "tail");
        using var stream = new CountingChunkStream(payload, 4096);

        Assert.Equal(content, await CodexJsonlIo.ReadBoundedLineAsync(stream, maxBytes, CancellationToken.None));
        Assert.Equal("tail", await CodexJsonlIo.ReadBoundedLineAsync(stream, maxBytes, CancellationToken.None));
        Assert.True(stream.ReadCalls < 100);
    }


    private sealed class CountingChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public CountingChunkStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            if (read <= 0) return 0;
            _data.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            ReadCalls++;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            if (read > 0)
            {
                _data.AsMemory(_position, read).CopyTo(buffer);
                _position += read;
                ReadCalls++;
            }

            return ValueTask.FromResult(Math.Max(read, 0));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
