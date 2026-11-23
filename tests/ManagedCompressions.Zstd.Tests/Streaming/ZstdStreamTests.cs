using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary><see cref="ZstdStream"/>: reading frames from another stream, whatever it hands over at a time.</summary>
public sealed class ZstdStreamTests
{
    private static readonly byte[] Content =
        [.. DataKinds.Generate("json", 150_000, 1), .. DataKinds.Generate("text", 300_000, 2), .. DataKinds.Generate("walk64", 10, 3)];

    /// <summary>Three frames, one without a content size, and two skippable frames between them.</summary>
    private static readonly byte[] Compressed =
    [
        .. NativeZstd.Compress(Content.AsSpan(0, 150_000), new NativeZstdOptions { Quality = 3, AppendChecksum = true }),
        0x50, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 1, 2, 3,
        .. NativeZstd.Compress(Content.AsSpan(150_000, 300_000), new NativeZstdOptions { Quality = 1 }, noContentSize: true),
        0x5F, 0x2A, 0x4D, 0x18, 0, 0, 0, 0,
        .. NativeZstd.Compress(Content.AsSpan(450_000), new NativeZstdOptions { Quality = 19 }),
    ];

    [Theory]
    [InlineData(1, 1 << 20)]
    [InlineData(7, 3)]
    [InlineData(4096, 100)]
    [InlineData(1 << 20, 1 << 20)]
    public void Reads_frame_after_frame_whatever_the_pieces(int bufferSize, int handedOver)
    {
        using var zstd = new ZstdStream(new Trickle(Compressed, handedOver), CompressionMode.Decompress);
        Corpus.AssertSameBytes(Content, ReadAll(zstd, bufferSize), $"buffer {bufferSize}, underlying {handedOver}");
        Assert.Equal(0, zstd.Read(new byte[16], 0, 16));
    }

    [Fact]
    public async Task Reads_asynchronously()
    {
        await using var zstd = new ZstdStream(new Trickle(Compressed, 999), CompressionMode.Decompress);
        using var content = new MemoryStream();
        byte[] buffer = new byte[5000];
        int read;
        while ((read = await zstd.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        Corpus.AssertSameBytes(Content, content.ToArray(), "async");
    }

    [Fact]
    public void Byte_by_byte_and_CopyTo_agree()
    {
        using (var zstd = new ZstdStream(new MemoryStream(Compressed), CompressionMode.Decompress))
        {
            using var content = new MemoryStream();
            int b;
            while ((b = zstd.ReadByte()) >= 0)
            {
                content.WriteByte((byte)b);
            }

            Corpus.AssertSameBytes(Content, content.ToArray(), "ReadByte");
        }

        using (var zstd = new ZstdStream(new MemoryStream(Compressed), CompressionMode.Decompress))
        {
            using var content = new MemoryStream();
            zstd.CopyTo(content);
            Corpus.AssertSameBytes(Content, content.ToArray(), "CopyTo");
        }
    }

    /// <summary>
    /// CopyTo writes from the decoder's window rather than through a buffer: the same content, whatever
    /// the underlying stream hands over, after reads or not, synchronously or not.
    /// </summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(999, 0)]
    [InlineData(1 << 20, 0)]
    [InlineData(999, 1)]
    [InlineData(4096, 70_000)]
    [InlineData(1 << 20, 449_999)]
    public async Task CopyTo_writes_every_frame_whatever_the_pieces(int handedOver, int readFirst)
    {
        foreach (bool asynchronous in (bool[])[false, true])
        {
            using var zstd = new ZstdStream(new Trickle(Compressed, handedOver), CompressionMode.Decompress);
            using var content = new MemoryStream();
            byte[] buffer = new byte[readFirst];
            int read = 0;
            while (read < readFirst)
            {
                read += zstd.Read(buffer, read, readFirst - read);
            }

            content.Write(buffer);
            if (asynchronous)
            {
                await zstd.CopyToAsync(content, TestContext.Current.CancellationToken);
            }
            else
            {
                zstd.CopyTo(content);
            }

            Corpus.AssertSameBytes(Content, content.ToArray(), $"underlying {handedOver}, {readFirst} read first, async {asynchronous}");
            Assert.Equal(0, zstd.Read(new byte[16], 0, 16));
        }
    }

    [Fact]
    public void A_stream_cut_inside_a_frame_is_refused_at_its_end()
    {
        using var zstd = new ZstdStream(new MemoryStream(Compressed, 0, Compressed.Length - 10), CompressionMode.Decompress);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ReadAll(zstd, 4096));
        Assert.Contains("ends inside a frame", e.Message, StringComparison.Ordinal);

        using var copied = new ZstdStream(new MemoryStream(Compressed, 0, Compressed.Length - 10), CompressionMode.Decompress);
        e = Assert.Throws<InvalidDataException>(() => copied.CopyTo(Stream.Null));
        Assert.Contains("ends inside a frame", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_after_the_frames_that_is_not_Zstandard_is_refused()
    {
        byte[] data = [.. Compressed, (byte)'x', (byte)'y', (byte)'z', (byte)'!'];
        using var zstd = new ZstdStream(new MemoryStream(data), CompressionMode.Decompress);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ReadAll(zstd, 4096));
        Assert.Contains(nameof(ZstdError.UnknownMagic), e.Message, StringComparison.Ordinal);

        using var copied = new ZstdStream(new MemoryStream(data), CompressionMode.Decompress);
        e = await Assert.ThrowsAsync<InvalidDataException>(() => copied.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
        Assert.Contains(nameof(ZstdError.UnknownMagic), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadByte_allocates_nothing()
    {
        Assert.SkipWhen(TestBuild.Debug, "allocations are a property of the optimized build");
        using var zstd = new ZstdStream(new MemoryStream(Compressed), CompressionMode.Decompress);
        int[] read = new int[10_000];
        read[0] = zstd.ReadByte();

        // The least of several rounds: an allocation of ReadByte's shows in every round, while the
        // runtime's own (tiering, the test framework on a shared thread) come and go.
        long allocated = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1 + (round * 1000); i < 1 + ((round + 1) * 1000); i++)
            {
                read[i] = zstd.ReadByte();
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        for (int i = 0; i < 5001; i++)
        {
            Assert.True(read[i] == Content[i], $"byte {i}");
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void An_empty_stream_reads_nothing()
    {
        using var zstd = new ZstdStream(new MemoryStream(), CompressionMode.Decompress);
        Assert.Equal(0, zstd.Read(new byte[10]));
        Assert.Equal(0, zstd.Read([]));
    }

    [Fact]
    public void It_reads_and_does_nothing_else()
    {
        using var zstd = new ZstdStream(new MemoryStream(Compressed), CompressionMode.Decompress);
        Assert.True(zstd.CanRead);
        Assert.False(zstd.CanWrite);
        Assert.False(zstd.CanSeek);
        Assert.Throws<NotSupportedException>(() => zstd.Length);
        Assert.Throws<NotSupportedException>(() => zstd.Position);
        Assert.Throws<NotSupportedException>(() => zstd.Seek(0, SeekOrigin.Begin));

        // As System.IO.Compression's ZstandardStream: what the mode does not do is an invalid operation.
        Assert.Throws<InvalidOperationException>(() => zstd.Write([1], 0, 1));
        Assert.Throws<InvalidOperationException>(() => zstd.SetSourceLength(1));
        Assert.Throws<InvalidOperationException>(() => new ZstdStream(new MemoryStream(), CompressionMode.Compress).CopyTo(Stream.Null));
        Assert.Throws<InvalidOperationException>(() => new ZstdStream(new MemoryStream(), CompressionMode.Compress).ReadByte());
        Assert.Throws<ArgumentException>(() => new ZstdStream(new Unreadable(), CompressionMode.Decompress));
    }

    [Fact]
    public async Task It_closes_the_underlying_stream_unless_asked_not_to()
    {
        var closed = new MemoryStream(Compressed);
        new ZstdStream(closed, CompressionMode.Decompress).Dispose();
        Assert.False(closed.CanRead);

        var open = new MemoryStream(Compressed);
        var zstd = new ZstdStream(open, CompressionMode.Decompress, leaveOpen: true);
        Assert.Same(open, zstd.BaseStream);
        await zstd.DisposeAsync();
        Assert.True(open.CanRead);
        Assert.False(zstd.CanRead);
        Assert.Throws<ObjectDisposedException>(() => zstd.Read(new byte[1]));
    }

    [Fact]
    public void Options_bring_the_dictionary()
    {
        (byte[] dictionary, NativeZstdDictionary trained) = Dictionaries.Get("json", "trained");
        byte[] content = DataKinds.Generate("json", 40_000, 9);
        byte[] frame = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3, Dictionary = trained }, noContentSize: true);
        using var zstd = new ZstdStream(new MemoryStream(frame), new ZstdDecompressionOptions { Dictionary = dictionary });
        Corpus.AssertSameBytes(content, ReadAll(zstd, 1000), "dictionary");
    }

#if NET11_0_OR_GREATER
    [Fact]
    public void It_reads_what_the_platforms_ZstandardStream_reads()
    {
        using var platform = new ZstandardStream(new MemoryStream(Compressed), CompressionMode.Decompress);
        using var ours = new ZstdStream(new MemoryStream(Compressed), CompressionMode.Decompress);
        Corpus.AssertSameBytes(ReadAll(platform, 8192), ReadAll(ours, 8192), "against ZstandardStream");
    }
#endif

    /// <summary>
    /// A stream disposed of while a read waits on the underlying stream, as one is to abandon a read
    /// that hangs: the buffer the underlying read still fills stays the read's rather than going back
    /// to the pool, where another renter would get it, and the read then fails as disposed. A second
    /// operation meanwhile is refused, as System.IO.Compression's streams refuse it.
    /// </summary>
    [Fact]
    public async Task Disposing_during_a_pending_read_leaves_the_read_its_buffer()
    {
        var pending = new Pending();
        var stream = new ZstdStream(pending, CompressionMode.Decompress);
        ValueTask<int> read = stream.ReadAsync(new byte[100], TestContext.Current.CancellationToken);
        Memory<byte> held = await pending.ReadStarted;
        Assert.Throws<InvalidOperationException>(() => stream.Read(new byte[100]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(new byte[100], TestContext.Current.CancellationToken).AsTask());

        Assert.False(ReturnedToThePool(held, stream.Dispose));
        Compressed.AsSpan(0, 10).CopyTo(held.Span);
        pending.Complete(10);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask());
    }

    /// <summary>
    /// As for a read: a stream disposed of while a write waits on the underlying stream neither ends
    /// the frame through the buffer that write still sends, nor gives that buffer back.
    /// </summary>
    [Fact]
    public async Task Disposing_during_a_pending_write_leaves_the_write_its_buffer()
    {
        var pending = new Pending();
        var stream = new ZstdStream(pending, CompressionLevel.Fastest);
        byte[] data = DataKinds.Generate("incompressible", 400_000, 5);
        ValueTask write = stream.WriteAsync(data, TestContext.Current.CancellationToken);
        Memory<byte> held = await pending.WriteStarted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.FlushAsync(TestContext.Current.CancellationToken));

        Assert.False(ReturnedToThePool(held, stream.Dispose));
        Assert.Equal(0, pending.SynchronousWrites);
        pending.Complete(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write.AsTask());
    }

    /// <summary>
    /// Whether <paramref name="dispose"/> gives the array under <paramref name="buffer"/> back to
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>: the pool keeps, for this thread, the last array of a
    /// size it was given back, and rents it out again first. The thread's slot is emptied before.
    /// </summary>
    private static bool ReturnedToThePool(ReadOnlyMemory<byte> buffer, Action dispose)
    {
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment));
        byte[] array = segment.Array!;
        byte[] emptied = System.Buffers.ArrayPool<byte>.Shared.Rent(array.Length);
        dispose();
        byte[] next = System.Buffers.ArrayPool<byte>.Shared.Rent(array.Length);
        bool returned = ReferenceEquals(next, array);
        if (!returned)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(next);
        }

        System.Buffers.ArrayPool<byte>.Shared.Return(emptied);
        return returned;
    }

    private static byte[] ReadAll(Stream stream, int bufferSize)
    {
        using var content = new MemoryStream();
        byte[] buffer = new byte[bufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    /// <summary>A stream that hands over at most <c>pieces</c> bytes a read, as a socket may.</summary>
    private sealed class Trickle(byte[] data, int pieces) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, pieces)]);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, pieces));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, pieces)], cancellationToken);
    }

    private sealed class Unreadable : MemoryStream
    {
        public override bool CanRead => false;
    }

    /// <summary>
    /// A stream whose first asynchronous read or write waits until <see cref="Complete"/>, and tells
    /// which buffer it was handed; a second one meanwhile fails with an <see cref="IOException"/>, rather
    /// than wait for ever.
    /// </summary>
    internal sealed class Pending : Stream
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
