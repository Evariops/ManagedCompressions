using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;
using DotNetBrotliEncoder = System.IO.Compression.BrotliEncoder;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The streaming encoder takes the calls .NET's <c>BrotliEncoder</c> takes and answers as it does: the same
/// status, the same bytes consumed and written, the same stream, which decodes back.
/// </summary>
public sealed class StreamEncoderTests
{
    public static IEnumerable<TheoryDataRow<string, int, int, int, int>> Scripts()
    {
        int seed = 1;
        foreach (int quality in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })
        {
            foreach (int window in new[] { 10, 16, 22 })
            {
                foreach (string kind in new[] { "text", "mixed", "incompressible" })
                {
                    int size = quality >= 10 ? 40_000 : 300_000;
                    yield return new(kind, size, quality, window, seed++);
                }
            }

            yield return new("json", 2000, quality, 24, seed++);
            yield return new("text", 0, quality, 22, seed++);
        }
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void AnswersAsDotNetsEncoderDoes(string kind, int size, int quality, int window, int seed)
    {
        byte[] data = DataKinds.Generate(kind, size);
        var random = new Random(seed);
        var native = new DotNetBrotliEncoder(quality, window);
        var managed = new BrotliStreamEncoder(quality, window);
        var expected = new MemoryStream();
        var actual = new MemoryStream();
        byte[] nativeOut = new byte[1 << 18];
        byte[] managedOut = new byte[1 << 18];
        int position = 0;
        int step = 0;
        try
        {
            while (true)
            {
                step++;
                int roll = random.Next(16);
                int room = roll switch
                {
                    0 => 0,
                    1 => 1,
                    < 5 => random.Next(1, 64),
                    < 10 => random.Next(64, 8192),
                    _ => random.Next(8192, nativeOut.Length),
                };

                bool final = position == data.Length ? random.Next(3) == 0 : random.Next(24) == 0;
                bool flush = !final && random.Next(10) == 0;
                int piece = final ? data.Length - position : Math.Min(data.Length - position, random.Next(4) == 0 ? random.Next(0, 300_000) : random.Next(0, 5000));
                ReadOnlySpan<byte> source = data.AsSpan(position, piece);
                OperationStatus nativeStatus;
                OperationStatus managedStatus;
                int nativeConsumed = 0;
                int managedConsumed = 0;
                int nativeWritten;
                int managedWritten;
                if (flush)
                {
                    nativeStatus = native.Flush(nativeOut.AsSpan(0, room), out nativeWritten);
                    managedStatus = managed.Flush(managedOut.AsSpan(0, room), out managedWritten);
                }
                else
                {
                    nativeStatus = native.Compress(source, nativeOut.AsSpan(0, room), out nativeConsumed, out nativeWritten, final);
                    managedStatus = managed.Compress(source, managedOut.AsSpan(0, room), out managedConsumed, out managedWritten, final);
                }

                string where = $"step {step}, {(flush ? "flush" : final ? "final" : "compress")} of {piece} into {room}";
                Assert.True(nativeStatus == managedStatus, $"{where}: {managedStatus} where .NET returns {nativeStatus}");
                Assert.True(nativeConsumed == managedConsumed, $"{where}: {managedConsumed} consumed where .NET consumes {nativeConsumed}");
                Assert.True(nativeWritten == managedWritten, $"{where}: {managedWritten} written where .NET writes {nativeWritten}");
                Assert.True(nativeOut.AsSpan(0, nativeWritten).SequenceEqual(managedOut.AsSpan(0, managedWritten)), $"{where}: other bytes");
                expected.Write(nativeOut, 0, nativeWritten);
                actual.Write(managedOut, 0, managedWritten);
                position += managedConsumed;
                if (final && nativeStatus == OperationStatus.Done && position == data.Length)
                {
                    break;
                }

                Assert.True(step < 100_000, "the script does not end");
            }
        }
        finally
        {
            native.Dispose();
        }

        Assert.True(managed.IsFinished);
        byte[] content = new byte[data.Length];
        byte[] stream = actual.ToArray();
        Assert.Equal(OperationStatus.Done, new BrotliDecompressor().Decompress(stream, content, out int consumed, out int written));
        Assert.Equal(stream.Length, consumed);
        Assert.Equal(data.Length, written);
        Assert.True(data.AsSpan().SequenceEqual(content), "the stream does not decode back");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(11)]
    public void OneCallWritesTheOneShotStream(int quality)
    {
        byte[] data = DataKinds.Generate("text", quality >= 10 ? 50_000 : 200_000);
        byte[] expected = NativeBrotli.Compress(data, quality, 22);
        var encoder = new BrotliStreamEncoder(quality);
        byte[] output = new byte[BrotliCompressor.GetMaxCompressedLength(data.Length)];
        for (int round = 0; round < 2; round++)
        {
            encoder.Reset();
            Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true));
            Assert.Equal(data.Length, consumed);

            // BrotliEncoderCompress hints the size, which the streaming encoder learns from the first call.
            byte[] stream = output.AsSpan(0, written).ToArray();
            byte[] content = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, new BrotliDecompressor().Decompress(stream, content, out _, out _));
            Assert.True(data.AsSpan().SequenceEqual(content));
            if (quality < 4)
            {
                Assert.Equal(expected, stream);
            }
        }
    }

    [Fact]
    public void InputAfterTheEndIsRefused()
    {
        var encoder = new BrotliStreamEncoder(5);
        byte[] output = new byte[1024];
        Assert.Equal(OperationStatus.Done, encoder.Compress("abc"u8, output, out _, out _, isFinalBlock: true));
        Assert.True(encoder.IsFinished);
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress("d"u8, output, out _, out _, isFinalBlock: false));
        encoder.Reset();
        Assert.Equal(OperationStatus.Done, encoder.Compress("d"u8, output, out _, out int written, isFinalBlock: true));
        Assert.True(written > 0);
    }
}
