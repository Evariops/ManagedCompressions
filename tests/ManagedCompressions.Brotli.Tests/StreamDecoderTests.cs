using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The streaming decoder decodes what the one-shot decoder decodes, from input and into destinations cut
/// at random: the same content, the same verdict, and nothing past the stream's end consumed.
/// </summary>
public sealed class StreamDecoderTests
{
    public static IEnumerable<TheoryDataRow<string, int, int, int, int>> Streams()
    {
        int seed = 1;
        foreach (int quality in new[] { 0, 1, 2, 3, 4, 5, 6, 9, 10, 11 })
        {
            foreach (int window in new[] { 10, 16, 22 })
            {
                foreach (string kind in new[] { "text", "mixed", "incompressible", "repeats" })
                {
                    int size = quality >= 10 ? 60_000 : 400_000;
                    yield return new(kind, size, quality, window, seed++);
                }
            }

            yield return new("json", 3000, quality, 24, seed++);
            yield return new("text", 0, quality, 22, seed++);
            yield return new("walk64", 1, quality, 22, seed++);
        }
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void DecodesInPieces(string kind, int size, int quality, int window, int seed)
    {
        byte[] data = DataKinds.Generate(kind, size);
        byte[] stream = NativeBrotli.Compress(data, quality, window);
        var random = new Random(seed);
        var decoder = new BrotliStreamDecoder();
        for (int round = 0; round < 2; round++)
        {
            decoder.Reset();

            // The stream, then bytes past it that the decoder must leave.
            byte[] source = [.. stream, 0xAA, 0x55, 0x00];
            var content = new MemoryStream();
            byte[] buffer = new byte[1 << 17];
            int position = 0;
            OperationStatus status;
            int steps = 0;
            do
            {
                int piece = random.Next(4) switch
                {
                    0 => random.Next(0, 3),
                    1 => random.Next(0, 100),
                    2 => random.Next(0, 5000),
                    _ => random.Next(0, 200_000),
                };
                piece = Math.Min(piece, source.Length - position);
                int room = random.Next(4) switch
                {
                    0 => random.Next(0, 3),
                    1 => random.Next(0, 1000),
                    _ => random.Next(0, buffer.Length),
                };
                status = decoder.Decompress(source.AsSpan(position, piece), buffer.AsSpan(0, room), out int consumed, out int written);
                Assert.True(consumed <= piece && written <= room);
                content.Write(buffer, 0, written);
                position += consumed;
                Assert.True(status != OperationStatus.InvalidData, $"round {round} step {steps}: {decoder.LastError} at {position} of {stream.Length}, piece {piece}, room {room}, written {content.Length}");
                Assert.True(++steps < 1_000_000, "the decoder does not progress");
                if (status == OperationStatus.NeedMoreData)
                {
                    Assert.Equal(piece, consumed);
                    Assert.True(position < stream.Length, $"more data asked for at {position} of {stream.Length}");
                }
            }
            while (status != OperationStatus.Done);

            Assert.True(decoder.IsFinished);
            Assert.Equal(stream.Length, position);
            Assert.True(data.AsSpan().SequenceEqual(content.ToArray()), "other content");

            // After the end, nothing more.
            Assert.Equal(OperationStatus.Done, decoder.Decompress(source.AsSpan(position), buffer, out int after, out int extra));
            Assert.Equal(0, after);
            Assert.Equal(0, extra);
        }
    }

    [Fact]
    public void ByteByByte()
    {
        foreach (int quality in new[] { 1, 5, 11 })
        {
            byte[] data = DataKinds.Generate("mixed", 30_000);
            byte[] stream = NativeBrotli.Compress(data, quality, 16);
            var decoder = new BrotliStreamDecoder();
            var content = new MemoryStream();
            byte[] buffer = new byte[7];
            int position = 0;
            OperationStatus status;
            do
            {
                int piece = Math.Min(1, stream.Length - position);
                status = decoder.Decompress(stream.AsSpan(position, piece), buffer, out int consumed, out int written);
                content.Write(buffer, 0, written);
                position += consumed;
            }
            while (status is OperationStatus.NeedMoreData or OperationStatus.DestinationTooSmall);

            Assert.Equal(OperationStatus.Done, status);
            Assert.True(data.AsSpan().SequenceEqual(content.ToArray()), $"q{quality}: other content");
        }
    }

    /// <summary>
    /// A meta-block whose header holds many prefix codes and context maps, the stream given a byte at a
    /// time: the header is read part by part, each part once, so that the decoder keeps a part of it at
    /// most, never the header from its start, which it would read again at every byte, and keeps a
    /// command at most of the commands.
    /// </summary>
    [Fact]
    public void AHeaderOfManyCodesIsReadPartByPart()
    {
        // Segments of unrelated distributions: at quality 11, many block types, codes and contexts.
        byte[] data = new byte[1 << 18];
        var random = new Random(7);
        byte[] permutation = new byte[256];
        for (int at = 0; at < data.Length; at += 8192)
        {
            for (int i = 0; i < 256; i++)
            {
                permutation[i] = (byte)i;
            }

            random.Shuffle(permutation);
            double scale = 4 + random.Next(20);
            for (int i = 0; i < 8192; i++)
            {
                data[at + i] = permutation[Math.Min((int)(-Math.Log(1.0 - random.NextDouble()) * scale), 255)];
            }
        }

        byte[] stream = NativeBrotli.Compress(data, 11, 22);
        var decoder = new BrotliStreamDecoder();
        var content = new MemoryStream();
        byte[] buffer = new byte[1 << 16];
        long kept = 0;
        int position = 0;
        OperationStatus status;
        do
        {
            status = decoder.Decompress(stream.AsSpan(position, Math.Min(1, stream.Length - position)), buffer, out int consumed, out int written);
            content.Write(buffer, 0, written);
            position += consumed;
            kept = Math.Max(kept, decoder.BufferedInput);
        }
        while (status is OperationStatus.NeedMoreData or OperationStatus.DestinationTooSmall);

        Assert.Equal(OperationStatus.Done, status);
        Assert.True(data.AsSpan().SequenceEqual(content.ToArray()), "other content");
        Assert.True(kept <= 1024, $"{kept} bytes of input kept");
    }

    /// <summary>An uncompressed meta-block's bytes are content as they arrive, before the meta-block is whole.</summary>
    [Fact]
    public void AnUncompressedMetaBlockIsContentAsItArrives()
    {
        // Quality 1 stores incompressible data in uncompressed meta-blocks of 128 KiB.
        byte[] data = DataKinds.Generate("incompressible", 300_000);
        byte[] stream = NativeBrotli.Compress(data, 1, 22);
        var decoder = new BrotliStreamDecoder();
        byte[] buffer = new byte[data.Length];
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(stream.AsSpan(0, 100_000), buffer, out _, out int written));
        Assert.True(written > 90_000, $"{written} bytes of content from 100,000 of stream");
        Assert.True(data.AsSpan(0, written).SequenceEqual(buffer.AsSpan(0, written)));
        Assert.Equal(OperationStatus.Done, decoder.Decompress(stream.AsSpan(100_000), buffer.AsSpan(written), out _, out int rest));
        Assert.True(data.AsSpan().SequenceEqual(buffer.AsSpan(0, written + rest)));
    }

    /// <summary>A metadata block's bytes are skipped as they arrive, not kept until the block is whole.</summary>
    [Fact]
    public void AMetadataBlockIsSkippedAsItArrives()
    {
        // Window 16; a metadata block of 1 MiB (MSKIPBYTES 3, MSKIPLEN - 1 = 0x0FFFFF); the empty last block.
        const int Length = 1 << 20;
        byte[] header = [0xEC, 0xFF, 0xFF, 0x07];
        byte[] stream = [.. header, .. new byte[Length], 0x03];
        var native = NativeBrotli.Decompress(stream, 100);
        Assert.Equal(1, native.Result);
        Assert.Equal(stream.Length, native.Consumed);

        var decoder = new BrotliStreamDecoder();
        byte[] buffer = new byte[64];
        long kept = 0;
        int position = 0;
        OperationStatus status;
        do
        {
            status = decoder.Decompress(stream.AsSpan(position, Math.Min(4096, stream.Length - position)), buffer, out int consumed, out int written);
            Assert.Equal(0, written);
            position += consumed;
            kept = Math.Max(kept, decoder.BufferedInput);
        }
        while (status == OperationStatus.NeedMoreData);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(stream.Length, position);
        Assert.True(kept <= 8192, $"{kept} bytes of input kept");
    }

    /// <summary>
    /// A metadata block can be the stream's last meta-block: "hello" uncompressed at window 16, then the
    /// last one, metadata of no byte or of three, then bytes past the end, which the decoder must leave.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x0D })]
    [InlineData(new byte[] { 0x2D, 0x01, 0x61, 0x62, 0x63 })]
    public void EndsAtALastMetadataBlock(byte[] last)
    {
        byte[] stream = [0x40, 0x00, 0x10, .. "hello"u8, .. last];
        var native = NativeBrotli.Decompress([.. stream, 0xFF, 0xFF], 100);
        Assert.Equal(1, native.Result);
        Assert.Equal(stream.Length, native.Consumed);
        Assert.Equal("hello"u8.ToArray(), native.Output);

        foreach (int pieceSize in new[] { 1, 3, stream.Length + 2 })
        {
            byte[] source = [.. stream, 0xFF, 0xFF];
            var decoder = new BrotliStreamDecoder();
            var content = new MemoryStream();
            byte[] buffer = new byte[64];
            int position = 0;
            OperationStatus status;
            do
            {
                int piece = Math.Min(pieceSize, source.Length - position);
                status = decoder.Decompress(source.AsSpan(position, piece), buffer, out int consumed, out int written);
                content.Write(buffer, 0, written);
                position += consumed;
            }
            while (status == OperationStatus.NeedMoreData && position < source.Length);

            Assert.Equal(OperationStatus.Done, status);
            Assert.Equal(stream.Length, position);
            Assert.Equal("hello"u8.ToArray(), content.ToArray());
        }
    }

    [Fact]
    public void RefusesWhatTheOneShotDecoderRefuses()
    {
        var random = new Random(7);
        int refused = 0;
        for (int i = 0; i < 3000; i++)
        {
            int quality = random.Next(12);
            byte[] data = DataKinds.Generate(DataKinds.All[random.Next(DataKinds.All.Length)], random.Next(1, 20_000), i);
            byte[] stream = NativeBrotli.Compress(data, quality, 10 + random.Next(15));
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
            {
                stream[random.Next(stream.Length)] ^= (byte)(1 << random.Next(8));
            }

            var oneShot = new BrotliDecompressor();
            OperationStatus expected = oneShot.Decompress(stream, new byte[1 << 22], out int expectedConsumed, out int expectedWritten);
            var decoder = new BrotliStreamDecoder();
            var content = new MemoryStream();
            byte[] buffer = new byte[1 << 14];
            int position = 0;
            OperationStatus status;
            do
            {
                int piece = Math.Min(random.Next(1, 3000), stream.Length - position);
                status = decoder.Decompress(stream.AsSpan(position, piece), buffer, out int consumed, out int written);
                content.Write(buffer, 0, written);
                position += consumed;
            }
            while ((status == OperationStatus.NeedMoreData && position < stream.Length) || status == OperationStatus.DestinationTooSmall);

            switch (expected)
            {
                case OperationStatus.Done:
                    Assert.Equal(OperationStatus.Done, status);
                    Assert.Equal(expectedConsumed, position);
                    Assert.Equal(expectedWritten, (int)content.Length);
                    break;
                case OperationStatus.InvalidData:
                    refused++;
                    Assert.Equal(OperationStatus.InvalidData, status);
                    Assert.Equal(oneShot.LastError, decoder.LastError);
                    break;
                case OperationStatus.NeedMoreData:
                    Assert.Equal(OperationStatus.NeedMoreData, status);
                    break;
                default:
                    // More content than 4 MiB: the streaming decoder decodes on.
                    break;
            }
        }

        Assert.True(refused > 1000, $"only {refused} streams refused");
    }
}
