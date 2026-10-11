using System;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>
/// The codec's sources and destinations placed against inaccessible pages, so that one byte read or
/// written past either end faults: blocks mutated a few bytes and decoded into destinations of the
/// edge sizes, and every level compressing into exactly the bound and into less.
/// </summary>
public sealed class GuardedFuzzTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AMutatedBlockIsReadOrRefusedWithoutTouchingPastEitherEnd(int seed)
    {
        Random random = new(seed * 7919);
        using var source = new GuardedBuffer(1 << 20);
        using var destination = new GuardedBuffer(1 << 20);
        for (int trial = 0; trial < 1500; trial++)
        {
            int length = random.Next(4) == 0 ? random.Next(0, 200) : random.Next(0, 200_000);
            byte[] data = Lz4BlockTests.Shaped(random, length, random.Next(5));
            byte[] block = NativeLz4.Compress(data, random.Next(-3, 13));
            for (int e = random.Next(0, 4); e > 0 && block.Length > 0; e--)
            {
                int at = random.Next(block.Length);
                block[at] = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(block[at] ^ (1 << random.Next(8)));
            }

            int sourceLength = random.Next(5) == 0 ? random.Next(block.Length + 1) : block.Length;
            int[] capacities = [0, 1, Math.Max(0, length - 1), length, length + 1, length + 64, random.Next(length + 200)];
            foreach (int capacity in capacities)
            {
                foreach (bool atEnd in new[] { true, false })
                {
                    Span<byte> src = atEnd ? source.AtEnd(sourceLength) : source.AtStart(sourceLength);
                    block.AsSpan(0, sourceLength).CopyTo(src);
                    Span<byte> dst = atEnd ? destination.AtEnd(capacity) : destination.AtStart(capacity);
                    if (Lz4Block.TryDecompress(src, dst, out int written) && sourceLength == block.Length && capacity >= length)
                    {
                        Assert.True(written <= capacity);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    public void EveryLevelCompressesWithoutTouchingPastEitherEnd(int level)
    {
        Random random = new(level + 1000);
        var compressor = new Lz4Compressor(level);
        using var source = new GuardedBuffer(1 << 19);
        using var destination = new GuardedBuffer(1 << 20);
        for (int trial = 0; trial < (level >= 10 ? 60 : 300); trial++)
        {
            int length = random.Next(3) == 0 ? random.Next(0, 100) : random.Next(0, 300_000);
            byte[] data = Lz4BlockTests.Shaped(random, length, random.Next(5));
            int bound = Lz4Compressor.GetMaxCompressedLength(length);
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
                        Lz4Block.Decompress(dst[..written], read);
                        Assert.True(data.AsSpan().SequenceEqual(read), $"level {level}, length {length}, capacity {capacity}");
                    }
                    else
                    {
                        Assert.True(capacity < bound, $"level {level}, length {length}: the bound did not fit");
                    }
                }
            }
        }
    }
}
