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
