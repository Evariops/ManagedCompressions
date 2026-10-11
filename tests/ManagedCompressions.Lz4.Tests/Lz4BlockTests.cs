using System;
using System.IO;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>
/// The LZ4 block codec: data of every shape and around the block boundaries read back, hand-built
/// blocks with overlapping matches, corrupt blocks refused, and lz4 itself as the oracle in both
/// directions.
/// </summary>
public sealed class Lz4BlockTests
{
    private static readonly int[] Lengths = [0, 1, 4, 5, 12, 13, 15, 16, 17, 100, 4096, 65_535, 65_536, 65_537, 300_000];

    internal static byte[] Shaped(Random random, int length, int shape)
    {
        byte[] data = new byte[length];
        switch (shape)
        {
            case 0:
                random.NextBytes(data);
                break;
            case 1:
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i / random.Next(1, 300 + 1));
                }

                break;
            case 2:
                byte[] words = "the quick brown fox jumps over the lazy dog, Paris 2026 "u8.ToArray();
                for (int i = 0; i < length; i++)
                {
                    data[i] = random.Next(10) == 0 ? (byte)random.Next(256) : words[(i * 7 / 3) % words.Length];
                }

                break;
            case 3:
                // Long runs and far repeats: matches longer than 15 + 255 and offsets near 64 KiB.
                for (int i = 0; i < length; i++)
                {
                    data[i] = i >= 70_000 && random.Next(4) != 0 ? data[i - 65_000 - random.Next(500)] : (byte)(i / 1000);
                }

                break;
            default:
                // Short periods: copies whose source overlaps what they write.
                int period = random.Next(1, 17);
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i % period);
                }

                break;
        }

        return data;
    }

    [Fact]
    public void EveryShapeAndLengthReadsBack()
    {
        Random random = new(5);
        foreach (int length in Lengths)
        {
            for (int shape = 0; shape < 5; shape++)
            {
                byte[] data = Shaped(random, length, shape);
                byte[] compressed = new byte[Lz4Block.MaxCompressedLength(length)];
                int size = Lz4Block.Compress(data, compressed);
                byte[] read = new byte[length];
                Lz4Block.Decompress(compressed.AsSpan(0, size), read);
                Assert.True(data.AsSpan().SequenceEqual(read), $"length {length}, shape {shape}");
            }
        }
    }

    [Fact]
    public unsafe void Lz4ReadsEveryBlockThisLibraryWrites()
    {
        Random random = new(7);
        foreach (int length in Lengths)
        {
            for (int shape = 0; shape < 5; shape++)
            {
                byte[] data = Shaped(random, length, shape);
                byte[] compressed = new byte[Lz4Block.MaxCompressedLength(length)];
                int size = Lz4Block.Compress(data, compressed);
                byte[] read = new byte[length + 64];
                int written;
                fixed (byte* src = compressed)
                fixed (byte* dst = read)
                {
                    written = LZ4NativeMethods.LZ4_decompress_safe(src, dst, size, read.Length);
                }

                Assert.Equal(length, written);
                Assert.True(data.AsSpan().SequenceEqual(read.AsSpan(0, length)), $"length {length}, shape {shape}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(12)]
    public unsafe void ThisLibraryReadsEveryBlockLz4Writes(int level)
    {
        Random random = new(11 + level);
        foreach (int length in Lengths)
        {
            for (int shape = 0; shape < 5; shape++)
            {
                byte[] data = Shaped(random, length, shape);
                byte[] compressed = new byte[LZ4NativeMethods.LZ4_compressBound(length) + 1];
                int size;

                // Never a null source, even when empty: lz4's optimal parser (levels 10 to 12) reads through it.
                fixed (byte* src = &MemoryMarshal.GetArrayDataReference(data))
                fixed (byte* dst = compressed)
                {
                    size = level < 3
                        ? LZ4NativeMethods.LZ4_compress_default(src, dst, length, compressed.Length)
                        : LZ4NativeMethods.LZ4_compress_HC(src, dst, length, compressed.Length, level);
                }

                Assert.True(size > 0);
                byte[] read = new byte[length];
                Lz4Block.Decompress(compressed.AsSpan(0, size), read);
                Assert.True(data.AsSpan().SequenceEqual(read), $"level {level}, length {length}, shape {shape}");
            }
        }
    }

    [Fact]
    public void ACompressibleRunShrinks()
    {
        byte[] data = new byte[1 << 20];
        byte[] compressed = new byte[Lz4Block.MaxCompressedLength(data.Length)];
        int size = Lz4Block.Compress(data, compressed);
        Assert.True(size < data.Length / 200, $"a megabyte of zeros took {size} bytes.");
    }

    [Fact]
    public void AHandBuiltBlockWithAnOverlappingMatchDecodes()
    {
        // A token of three literals and a match of nine, "abc", the offset 3, then the last
        // sequence, which holds no literal.
        byte[] block = [0x35, 0x61, 0x62, 0x63, 0x03, 0x00, 0x00];
        byte[] read = new byte[12];
        Lz4Block.Decompress(block, read);
        Assert.Equal("abcabcabcabc"u8.ToArray(), read);
    }

    [Theory]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x04, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x03, 0x00 })]
    public void ACorruptBlockIsRefused(byte[] block) =>
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decompress(block, new byte[12]));

    [Fact]
    public void AMutatedBlockIsReadOrRefusedIntoADestinationOfAnySize()
    {
        // Each block mutated a few bytes and read into a destination of the edge sizes, empty among
        // them, as a header that lies would give it.
        Random random = new(41);
        for (int trial = 0; trial < 4_000; trial++)
        {
            byte[] data = Shaped(random, random.Next(0, 3_000), random.Next(5));
            byte[] compressed = new byte[Lz4Block.MaxCompressedLength(data.Length)];
            int size = Lz4Block.Compress(data, compressed);
            byte[] stream = compressed.AsSpan(0, size).ToArray();
            for (int e = random.Next(0, 3); e > 0; e--)
            {
                int at = random.Next(stream.Length);
                stream[at] = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(stream[at] ^ (1 << random.Next(8)));
            }

            int length = random.Next(4) == 0 ? random.Next(stream.Length + 1) : stream.Length;
            int[] sizes = [0, 1, Math.Max(0, data.Length - 1), data.Length, data.Length + 1, random.Next(data.Length + 200)];
            foreach (int target in sizes)
            {
                try
                {
                    Lz4Block.Decompress(stream.AsSpan(0, length), new byte[target]);
                }
                catch (InvalidDataException)
                {
                }
            }
        }
    }
}
