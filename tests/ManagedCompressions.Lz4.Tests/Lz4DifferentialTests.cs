using System;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>
/// lz4 1.10.0 as the oracle of the compressor: at every level, the same blocks, byte for byte, from a
/// fresh compressor and from one that has compressed other blocks before, into a destination of any
/// size; and the decoder against every block lz4 writes.
/// </summary>
public sealed class Lz4DifferentialTests
{
    public static TheoryData<int> Levels => new() { -64, -7, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

    private static readonly int[] Lengths = [0, 1, 4, 5, 12, 13, 14, 15, 16, 17, 31, 100, 1000, 4096, 65_535, 65_546, 65_547, 65_548, 100_000, 300_000];

    private static byte[] Managed(Lz4Compressor compressor, byte[] data, int capacity = -1)
    {
        byte[] compressed = new byte[capacity < 0 ? Lz4Compressor.GetMaxCompressedLength(data.Length) : capacity];
        return compressor.TryCompress(data, compressed, out int written) ? compressed.AsSpan(0, written).ToArray() : [];
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void EveryLevelWritesLz4sBlocks(int level)
    {
        Random random = new(100 + level);
        var compressor = new Lz4Compressor(level);
        foreach (int length in Lengths)
        {
            for (int shape = 0; shape < 5; shape++)
            {
                byte[] data = Lz4BlockTests.Shaped(random, length, shape);
                byte[] expected = NativeLz4.Compress(data, level);
                byte[] actual = Managed(compressor, data);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"level {level}, length {length}, shape {shape}: {actual.Length} bytes against lz4's {expected.Length}");

                byte[] read = new byte[length];
                Lz4Block.Decompress(expected, read);
                Assert.True(data.AsSpan().SequenceEqual(read), $"level {level}, length {length}, shape {shape}: lz4's block reads back wrong");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AReusedCompressorWritesWhatAFreshOneWrites(int level)
    {
        // Blocks of every size one after the other through one compressor, whose tables keep what
        // the earlier blocks left: each must still be lz4's block from a cleared state.
        Random random = new(200 + level);
        var compressor = new Lz4Compressor(level);
        for (int round = 0; round < 60; round++)
        {
            int length = random.Next(4) switch
            {
                0 => random.Next(0, 64),
                1 => random.Next(64, 4096),
                2 => random.Next(60_000, 70_000),
                _ => random.Next(4096, 200_000),
            };
            byte[] data = Lz4BlockTests.Shaped(random, length, random.Next(5));
            Assert.True(NativeLz4.Compress(data, level).AsSpan().SequenceEqual(Managed(compressor, data)), $"level {level}, round {round}, length {length}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-3)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    public void ADestinationTooSmallIsRefusedAsLz4RefusesIt(int level)
    {
        Random random = new(300 + level);
        var compressor = new Lz4Compressor(level);
        for (int round = 0; round < 200; round++)
        {
            byte[] data = Lz4BlockTests.Shaped(random, random.Next(0, 5000), random.Next(5));
            int bound = Lz4Compressor.GetMaxCompressedLength(data.Length);
            int capacity = random.Next(0, bound);
            byte[] expected = NativeLz4.Compress(data, level, capacity);
            byte[] actual = Managed(compressor, data, capacity);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"level {level}, round {round}, length {data.Length}, capacity {capacity}: {actual.Length} bytes against lz4's {expected.Length}");
        }
    }
}
