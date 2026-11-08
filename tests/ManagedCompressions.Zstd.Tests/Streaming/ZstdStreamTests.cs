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

    [Fact]
    public void A_stream_cut_inside_a_frame_is_refused_at_its_end()
    {
        using var zstd = new ZstdStream(new MemoryStream(Compressed, 0, Compressed.Length - 10), CompressionMode.Decompress);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ReadAll(zstd, 4096));
        Assert.Contains("ends inside a frame", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_after_the_frames_that_is_not_Zstandard_is_refused()
    {
        using var zstd = new ZstdStream(new MemoryStream([.. Compressed, (byte)'x', (byte)'y', (byte)'z', (byte)'!']), CompressionMode.Decompress);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ReadAll(zstd, 4096));
        Assert.Contains(nameof(ZstdError.UnknownMagic), e.Message, StringComparison.Ordinal);
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
}
