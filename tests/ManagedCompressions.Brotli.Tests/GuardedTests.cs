using System;
using System.Buffers;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The codec's sources and destinations placed against inaccessible pages, so that one byte read or
/// written past either end faults: streams mutated a few bytes and decoded into destinations of the
/// edge sizes, and every quality compressing into exactly the bound and into less.
/// </summary>
public sealed class GuardedTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_mutated_stream_is_read_or_refused_without_touching_past_either_end(int seed)
    {
        Random random = new(seed * 7919);
        using var source = new GuardedBuffer(1 << 19);
        using var destination = new GuardedBuffer(1 << 19);
        var decoder = new BrotliDecompressor();
        for (int trial = 0; trial < 300; trial++)
        {
            int length = random.Next(4) == 0 ? random.Next(0, 200) : random.Next(0, 100_000);
            byte[] data = DataKinds.Generate(DataKinds.All[random.Next(DataKinds.All.Length)], length, random.Next());
            int quality = length > 20_000 ? random.Next(10) : random.Next(12);
            byte[] stream = Compress(data, quality);
            for (int e = random.Next(0, 4); e > 0 && stream.Length > 0; e--)
            {
                int at = random.Next(stream.Length);
                stream[at] = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(stream[at] ^ (1 << random.Next(8)));
            }

            int sourceLength = random.Next(5) == 0 ? random.Next(stream.Length + 1) : stream.Length;
            int[] capacities = [0, 1, Math.Max(0, length - 1), length, length + 1, length + 64, random.Next(length + 200)];
            foreach (int capacity in capacities)
            {
                foreach (bool atEnd in new[] { true, false })
                {
                    Span<byte> src = atEnd ? source.AtEnd(sourceLength) : source.AtStart(sourceLength);
                    stream.AsSpan(0, sourceLength).CopyTo(src);
                    Span<byte> dst = atEnd ? destination.AtEnd(capacity) : destination.AtStart(capacity);
                    OperationStatus status = decoder.Decompress(src, dst, out int consumed, out int written);
                    Assert.True(consumed <= sourceLength && written <= capacity, $"seed {seed}, trial {trial}: {status}, {consumed}/{sourceLength} read, {written}/{capacity} written");
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Every_quality_compresses_without_touching_past_either_end(int quality)
    {
        Random random = new(quality + 1000);
        var compressor = new BrotliCompressor(quality);
        var decoder = new BrotliDecompressor();
        using var source = new GuardedBuffer(1 << 18);
        using var destination = new GuardedBuffer(1 << 19);
        for (int trial = 0; trial < (quality >= 10 ? 25 : 120); trial++)
        {
            int length = random.Next(3) == 0 ? random.Next(0, 100) : random.Next(0, quality >= 10 ? 30_000 : 200_000);
            byte[] data = DataKinds.Generate(DataKinds.All[random.Next(DataKinds.All.Length)], length, random.Next());
            int bound = BrotliCompressor.GetMaxCompressedLength(length);
            foreach (bool atEnd in new[] { true, false })
            {
                Span<byte> src = atEnd ? source.AtEnd(length) : source.AtStart(length);
                data.CopyTo(src);
                foreach (int capacity in new[] { bound, random.Next(bound + 1), Math.Max(0, bound - 20) })
                {
                    Span<byte> dst = atEnd ? destination.AtEnd(capacity) : destination.AtStart(capacity);
                    if (compressor.TryCompress(src, dst, out int written))
                    {
                        byte[] read = new byte[length];
                        Assert.True(decoder.TryDecompress(dst[..written], read, out int decoded) && decoded == length);
                        Assert.True(data.AsSpan().SequenceEqual(read), $"quality {quality}, length {length}, capacity {capacity}");
                    }
                    else
                    {
                        Assert.True(capacity < bound, $"quality {quality}, length {length}: the bound did not fit");
                    }
                }
            }
        }
    }

    private static byte[] Compress(byte[] data, int quality)
    {
        byte[] output = new byte[BrotliCompressor.GetMaxCompressedLength(data.Length)];
        Assert.True(new BrotliCompressor(quality).TryCompress(data, output, out int written));
        return output.AsSpan(0, written).ToArray();
    }
}
