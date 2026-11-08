using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary>
/// <see cref="ZstdStream"/> compressing: a write is <c>ZSTD_e_continue</c>, a flush <c>ZSTD_e_flush</c>,
/// disposing <c>ZSTD_e_end</c> of nothing more. The frame must be libzstd's for those calls.
/// </summary>
public sealed class ZstdStreamCompressionTests
{
    [Theory]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(9, true)]
    [InlineData(19, false)]
    public async Task Writes_and_flushes_make_libzstds_frame(int level, bool checksum)
    {
        foreach (string kind in (string[])["text", "json", "incompressible", "walk64"])
        {
            var random = new Random(level + kind.Length);
            byte[] data = DataKinds.Generate(kind, random.Next(0, 700_000), level + 50);
            var options = new ZstdCompressionOptions { Level = level, AppendChecksum = checksum };
            using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level, AppendChecksum = checksum });
            var expected = new ArrayBufferWriter<byte>();

            var output = new MemoryStream();
            var zstd = new ZstdStream(output, options, leaveOpen: true);
            int at = 0;
            int call = 0;
            while (at < data.Length)
            {
                int size = Math.Min(data.Length - at, random.Next(5) switch { 0 => random.Next(0, 10), 1 => 128 << 10, _ => random.Next(10, 100_000) });
                ReadOnlyMemory<byte> piece = data.AsMemory(at, size);
                if (call++ % 2 == 0)
                {
                    zstd.Write(piece.Span);
                }
                else
                {
                    await zstd.WriteAsync(piece, TestContext.Current.CancellationToken);
                }

                Native(native, expected, piece.Span, StreamOp.Continue);
                at += size;
                if (random.Next(6) == 0)
                {
                    if (call % 2 == 0)
                    {
                        zstd.Flush();
                    }
                    else
                    {
                        await zstd.FlushAsync(TestContext.Current.CancellationToken);
                    }

                    Native(native, expected, default, StreamOp.Flush);
                }
            }

            await zstd.DisposeAsync();
            Native(native, expected, default, StreamOp.End);
            Corpus.AssertSameBytes(expected.WrittenSpan, output.ToArray(), $"{kind} L{level}");
            Assert.Equal(data, Decompress(output.ToArray()));
        }
    }

    [Fact]
    public void Disposed_without_writing_it_writes_an_empty_frame()
    {
        var output = new MemoryStream();
        new ZstdStream(output, CompressionMode.Compress, leaveOpen: true).Dispose();

        // A final first call of nothing: libzstd's one-shot frame of 0 bytes, which declares its size.
        Assert.Equal(Convert.FromHexString("28B52FFD2000010000"), output.ToArray());
    }

    [Fact]
    public void A_source_length_is_declared_and_held_to()
    {
        byte[] data = DataKinds.Generate("json", 5000, 4);
        var output = new MemoryStream();
        using (var zstd = new ZstdStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            zstd.SetSourceLength(data.Length);
            zstd.Write(data);
            Assert.Throws<InvalidOperationException>(() => zstd.SetSourceLength(data.Length));
        }

        Assert.True(ZstdDecompressor.TryGetFrameContentSize(output.ToArray(), out ulong declared));
        Assert.Equal((ulong)data.Length, declared);
        Assert.Equal(data, Decompress(output.ToArray()));

        // More than set: refused as it is written. Less: refused at the frame's end.
        var more = new ZstdStream(new MemoryStream(), CompressionMode.Compress);
        more.SetSourceLength(100);
        Assert.Throws<InvalidDataException>(() => more.Write(data));
        var less = new ZstdStream(new MemoryStream(), CompressionMode.Compress);
        less.SetSourceLength(100_000);
        less.Write(data);
        Assert.Throws<InvalidDataException>(less.Dispose);
        Assert.False(less.CanWrite);
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal, ZstdCompressor.DefaultLevel)]
    [InlineData(CompressionLevel.Fastest, 1)]
    [InlineData(CompressionLevel.NoCompression, ZstdCompressor.MinLevel)]
    [InlineData(CompressionLevel.SmallestSize, ZstdCompressor.MaxLevel)]
    public void Compression_levels_map_as_the_platforms(CompressionLevel compressionLevel, int level)
    {
        byte[] data = DataKinds.Generate("text", 200_000, 6);
        Assert.Equal(Compress(data, s => new ZstdStream(s, new ZstdCompressionOptions { Level = level }, leaveOpen: true)), Compress(data, s => new ZstdStream(s, compressionLevel, leaveOpen: true)));
    }

    [Fact]
    public void It_writes_and_does_nothing_else()
    {
        var output = new MemoryStream();
        var zstd = new ZstdStream(output, CompressionMode.Compress);
        Assert.True(zstd.CanWrite);
        Assert.False(zstd.CanRead);
        Assert.False(zstd.CanSeek);
        Assert.Same(output, zstd.BaseStream);
        Assert.Throws<InvalidOperationException>(() => zstd.Read(new byte[1]));
        Assert.Throws<InvalidOperationException>(() => zstd.ReadByte());
        Assert.Throws<NotSupportedException>(() => zstd.Length);
        zstd.WriteByte(42);
        zstd.Dispose();
        Assert.False(zstd.CanWrite);
        Assert.False(output.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => zstd.Write([1]));
        zstd.Dispose();

        Assert.Throws<ArgumentException>(() => new ZstdStream(new MemoryStream([], writable: false), CompressionMode.Compress));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdStream(new MemoryStream(), (CompressionMode)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdStream(new MemoryStream(), (CompressionLevel)7));
    }

    [Fact]
    public void An_encoder_given_is_used_and_left_to_its_owner()
    {
        byte[] data = DataKinds.Generate("mixed", 300_000, 8);
        var encoder = new ZstdEncoder(5);
        byte[] first = Compress(data, s => new ZstdStream(s, encoder, leaveOpen: true));
        byte[] second = Compress(data, s => new ZstdStream(s, encoder, leaveOpen: true));
        Assert.Equal(first, second);
        Assert.Equal(Compress(data, s => new ZstdStream(s, new ZstdCompressionOptions { Level = 5 }, leaveOpen: true)), first);
    }

#if NET11_0_OR_GREATER
    /// <summary>System.IO.Compression's <c>ZstandardStream</c>, for the same writes and flushes: the same bytes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void It_writes_what_the_platforms_ZstandardStream_writes(int level)
    {
        byte[] data = DataKinds.Generate("json", 500_000, level);
        var random = new Random(level);
        var ours = new MemoryStream();
        var theirs = new MemoryStream();
        using (var zstd = new ZstdStream(ours, new ZstdCompressionOptions { Level = level }, leaveOpen: true))
        using (var platform = new System.IO.Compression.ZstandardStream(theirs, new System.IO.Compression.ZstandardCompressionOptions { Quality = level }, leaveOpen: true))
        {
            int at = 0;
            while (at < data.Length)
            {
                int size = Math.Min(data.Length - at, random.Next(1, 150_000));
                zstd.Write(data, at, size);
                platform.Write(data, at, size);
                at += size;
                if (random.Next(4) == 0)
                {
                    zstd.Flush();
                    platform.Flush();
                }
            }
        }

        Corpus.AssertSameBytes(theirs.ToArray(), ours.ToArray(), $"L{level}");
    }
#endif

    /// <summary>The call libzstd gets for a write, a flush or the end, run until it is done, its bytes appended.</summary>
    private static void Native(NativeZstdEncoder native, ArrayBufferWriter<byte> output, ReadOnlySpan<byte> source, StreamOp op)
    {
        while (true)
        {
            Span<byte> room = output.GetSpan(1 << 20);
            int consumed = 0;
            int written;
            OperationStatus status = op == StreamOp.Flush
                ? native.Flush(room, out written)
                : native.Compress(source, room, out consumed, out written, op == StreamOp.End);
            output.Advance(written);
            source = source.Slice(consumed);

            if (status == OperationStatus.Done)
            {
                return;
            }

            Assert.Equal(OperationStatus.DestinationTooSmall, status);
        }
    }

    private static byte[] Compress(byte[] data, Func<Stream, ZstdStream> open)
    {
        var output = new MemoryStream();
        using (ZstdStream zstd = open(output))
        {
            zstd.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] frames)
    {
        using var zstd = new ZstdStream(new MemoryStream(frames), CompressionMode.Decompress);
        var content = new MemoryStream();
        zstd.CopyTo(content);
        return content.ToArray();
    }
}
