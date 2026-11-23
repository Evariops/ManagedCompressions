using System;
using System.IO;
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

    /// <summary>
    /// 40 literals, which leave the fast loop 32 bytes of output, then a match of 18 bytes at offset 1:
    /// the pattern's stores fit those 32 bytes, and the block is refused for the match that does not.
    /// The Debug build asserted 34 bytes there, two more than the stores take.
    /// </summary>
    [Fact]
    public void AShortPatternAfterLongLiteralsNearTheEndIsRefused()
    {
        byte[] block = new byte[2 + 40 + 2 + 30];
        block[0] = 0xFE;
        block[1] = 25;
        for (int i = 0; i < 40; i++)
        {
            block[2 + i] = (byte)('a' + (i % 26));
        }

        block[42] = 1;
        block.AsSpan(44).Fill(0x10);
        Assert.False(Lz4Block.TryDecompress(block, new byte[72], out int written));
        Assert.Equal(0, written);
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decompress(block, new byte[72]));
    }

    /// <summary>
    /// A block whose last match's length runs to four bytes from the end, followed by an empty last
    /// sequence: the careful loop reads it, and the fast loop, which reaches it here, hands it over
    /// rather than refusing it. A block is read or refused for what it holds, not for where it lies.
    /// </summary>
    [Fact]
    public void ABlockEndingOnALongMatchReadsWhereverTheFastLoopReachesIt()
    {
        // 14 literals and a long match at offset 1: 15 + 4 + 255 * 14 bytes; then an empty sequence.
        byte[] block = new byte[1 + 14 + 2 + 14 + 1 + 1];
        block[0] = 0xEF;
        for (int i = 0; i < 14; i++)
        {
            block[1 + i] = (byte)('a' + i);
        }

        block[15] = 1;
        block.AsSpan(17, 14).Fill(255);
        byte[] expected = new byte[14 + 15 + 4 + (255 * 14)];
        block.AsSpan(1, 14).CopyTo(expected);
        expected.AsSpan(14).Fill((byte)'n');

        byte[] read = new byte[expected.Length];
        Assert.True(Lz4Block.TryDecompress(block, read, out int written));
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, read);
    }
}
