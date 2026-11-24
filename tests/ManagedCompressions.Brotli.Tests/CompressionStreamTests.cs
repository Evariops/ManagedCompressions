using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// <see cref="BrotliCompressionStream"/> writes the bytes .NET's <see cref="BrotliStream"/> writes for the
/// same writes and flushes, and reads back what it wrote, in pieces of any size.
/// </summary>
public sealed class CompressionStreamTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(9, 6)]
    [InlineData(11, 7)]
    public void WritesWhatBrotliStreamWrites(int quality, int seed)
    {
        byte[] data = DataKinds.Generate("mixed", quality >= 10 ? 80_000 : 600_000);
        var random = new Random(seed);
        var expected = new MemoryStream();
        var actual = new MemoryStream();
        using (var native = new BrotliStream(expected, new BrotliCompressionOptions { Quality = quality }, leaveOpen: true))
        using (var managed = new BrotliCompressionStream(actual, quality, leaveOpen: true))
        {
            int position = 0;
            while (position < data.Length)
            {
                int piece = Math.Min(data.Length - position, random.Next(4) == 0 ? random.Next(0, 200_000) : random.Next(0, 3000));
                native.Write(data, position, piece);
                managed.Write(data, position, piece);
                position += piece;
                if (random.Next(20) == 0)
                {
                    native.Flush();
                    managed.Flush();
                }
            }
        }

        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(data, ReadAll(new BrotliCompressionStream(new MemoryStream(actual.ToArray()), CompressionMode.Decompress), random));
    }

    [Fact]
    public async Task AsyncRoundTrip()
    {
        byte[] data = DataKinds.Generate("text", 300_000);
        var compressed = new MemoryStream();
        await using (var managed = new BrotliCompressionStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int i = 0; i < data.Length; i += 10_000)
            {
                await managed.WriteAsync(data.AsMemory(i, Math.Min(10_000, data.Length - i)), TestContext.Current.CancellationToken);
            }

            await managed.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(NativeBrotli.Decompress(compressed.ToArray(), data.Length + 1).Output, data);
        await using var reader = new BrotliCompressionStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress);
        var content = new MemoryStream();
        byte[] buffer = new byte[777];
        int read;
        while ((read = await reader.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        Assert.Equal(data, content.ToArray());
    }

    [Fact]
    public void ReadsWhatBrotliStreamWrites()
    {
        foreach (CompressionLevel level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            byte[] data = DataKinds.Generate("json", 200_000);
            var compressed = new MemoryStream();
            using (var native = new BrotliStream(compressed, level, leaveOpen: true))
            {
                native.Write(data);
            }

            Assert.Equal(data, ReadAll(new BrotliCompressionStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress), new Random(1)));
        }
    }

    [Fact]
    public void RefusesATruncatedStream()
    {
        byte[] stream = NativeBrotli.Compress(DataKinds.Generate("text", 100_000), 5, 22);
        using var reader = new BrotliCompressionStream(new MemoryStream(stream[..^10]), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => reader.CopyTo(Stream.Null));
    }

    /// <summary>
    /// An underlying stream that holds nothing at all reads as empty, as <see cref="BrotliStream"/> and
    /// ZstdStream read it: an empty HTTP body that says it is Brotli. A stream cut short inside its
    /// data is still refused (<see cref="RefusesATruncatedStream"/>).
    /// </summary>
    [Fact]
    public async Task AnEmptyUnderlyingStreamReadsAsEmpty()
    {
        using var native = new BrotliStream(new MemoryStream(), CompressionMode.Decompress);
        Assert.Equal(0, native.Read(new byte[16]));
        using var reader = new BrotliCompressionStream(new MemoryStream(), CompressionMode.Decompress);
        Assert.Equal(0, reader.Read(new byte[16]));
        Assert.Equal(-1, reader.ReadByte());
        using var asyncReader = new BrotliCompressionStream(new MemoryStream(), CompressionMode.Decompress);
        Assert.Equal(0, await asyncReader.ReadAsync(new byte[16], TestContext.Current.CancellationToken));
        using var copied = new BrotliCompressionStream(new MemoryStream(), CompressionMode.Decompress);
        var content = new MemoryStream();
        copied.CopyTo(content);
        Assert.Equal(0, content.Length);
    }

    /// <summary>The underlying stream gets no empty write when the encoder has nothing to hand out.</summary>
    [Fact]
    public async Task NothingIsWrittenWhenTheEncoderHasNothing()
    {
        var counting = new Counting();
        using (var writer = new BrotliCompressionStream(counting, 5, leaveOpen: true))
        {
            writer.Write(new byte[10]);
            writer.Flush();
            writer.Flush();
            await writer.WriteAsync(new byte[10], TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using (var writer = new BrotliCompressionStream(counting, 5, leaveOpen: true))
        {
            await writer.WriteAsync(new byte[10], TestContext.Current.CancellationToken);
        }

        Assert.True(counting.Writes > 0);
        Assert.Equal(0, counting.EmptyWrites);
    }

    [Fact]
    public void RefusesTheWrongDirection()
    {
        using var writer = new BrotliCompressionStream(new MemoryStream(), CompressionMode.Compress);
        Assert.False(writer.CanRead);
        Assert.Throws<InvalidOperationException>(() => writer.Read(new byte[1], 0, 1));
        using var reader = new BrotliCompressionStream(new MemoryStream([6]), CompressionMode.Decompress);
        Assert.False(reader.CanWrite);
        Assert.Throws<InvalidOperationException>(() => reader.Write([1], 0, 1));
        Assert.Equal(-1, reader.ReadByte());
    }

    /// <summary>
    /// A stream disposed of while a read waits on the underlying stream, as one is to abandon a read
    /// that hangs: the read then fails as disposed, rather than decode on a decoder whose buffers went
    /// back to their pool. A second operation meanwhile is refused, as <see cref="BrotliStream"/> refuses it.
    /// </summary>
    [Fact]
    public async Task DisposingDuringAPendingReadFailsTheRead()
    {
        var pending = new Pending();
        var stream = new BrotliCompressionStream(pending, CompressionMode.Decompress);
        ValueTask<int> read = stream.ReadAsync(new byte[100], TestContext.Current.CancellationToken);
        Memory<byte> held = await pending.ReadStarted;
        Assert.Throws<InvalidOperationException>(() => stream.Read(new byte[100]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(new byte[100], TestContext.Current.CancellationToken).AsTask());

        stream.Dispose();
        byte[] compressed = NativeBrotli.Compress(DataKinds.Generate("text", 1000), 5, 22);
        compressed.CopyTo(held.Span);
        pending.Complete(compressed.Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask());
    }

    /// <summary>
    /// A stream disposed of while a write waits on the underlying stream does not end the Brotli stream
    /// through the buffer that write still sends.
    /// </summary>
    [Fact]
    public async Task DisposingDuringAPendingWriteLeavesTheWriteItsBuffer()
    {
        var pending = new Pending();
        var stream = new BrotliCompressionStream(pending, 1);
        ValueTask write = stream.WriteAsync(DataKinds.Generate("incompressible", 400_000), TestContext.Current.CancellationToken);
        await pending.WriteStarted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.FlushAsync(TestContext.Current.CancellationToken));

        stream.Dispose();
        Assert.Equal(0, pending.SynchronousWrites);
        pending.Complete(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write.AsTask());
    }

    private static byte[] ReadAll(Stream stream, Random random)
    {
        using (stream)
        {
            var content = new MemoryStream();
            byte[] buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer, 0, random.Next(1, buffer.Length))) > 0)
            {
                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
    }

    /// <summary>A stream that counts the writes it gets, and the empty ones.</summary>
    private sealed class Counting : MemoryStream
    {
        public int Writes { get; private set; }

        public int EmptyWrites { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Count(int length)
        {
            Writes++;
            EmptyWrites += length == 0 ? 1 : 0;
        }
    }

    /// <summary>
    /// A stream whose first asynchronous read or write waits until <see cref="Complete"/>, and tells
    /// which buffer it was handed; a second one meanwhile fails with an <see cref="IOException"/>, rather
    /// than wait for ever.
    /// </summary>
    private sealed class Pending : Stream
    {
        private readonly TaskCompletionSource<Memory<byte>> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Memory<byte>> _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Memory<byte>> ReadStarted => _readStarted.Task;

        public Task<Memory<byte>> WriteStarted => _writeStarted.Task;

        public int SynchronousWrites { get; private set; }

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Complete(int read) => _done.SetResult(read);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_readStarted.TrySetResult(buffer) && !_done.Task.IsCompleted)
            {
                throw new IOException("a second read reached the underlying stream while the first one ran");
            }

            return await _done.Task;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_writeStarted.TrySetResult(System.Runtime.InteropServices.MemoryMarshal.AsMemory(buffer)) && !_done.Task.IsCompleted)
            {
                throw new IOException("a second write reached the underlying stream while the first one ran");
            }

            await _done.Task;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => SynchronousWrites++;

        public override void Write(ReadOnlySpan<byte> buffer) => SynchronousWrites++;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
