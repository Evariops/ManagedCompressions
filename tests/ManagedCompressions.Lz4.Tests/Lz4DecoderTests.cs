using System;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>The decoder's destination: a whole block read into more room than it needs, and refused with less.</summary>
public sealed class Lz4DecoderTests
{
    [Fact]
    public void TryDecompressReadsIntoALargerDestination()
    {
        Random random = new(400);
        for (int round = 0; round < 500; round++)
        {
            byte[] data = Lz4BlockTests.Shaped(random, random.Next(0, 3000), random.Next(5));
            byte[] block = NativeLz4.Compress(data, random.Next(-2, 13));
            byte[] read = new byte[data.Length + random.Next(0, 200)];
            Assert.True(Lz4Block.TryDecompress(block, read, out int written));
            Assert.Equal(data.Length, written);
            Assert.True(data.AsSpan().SequenceEqual(read.AsSpan(0, written)));
            if (data.Length > 0)
            {
                Assert.False(Lz4Block.TryDecompress(block, new byte[data.Length - 1], out _));
            }
        }
    }
}
