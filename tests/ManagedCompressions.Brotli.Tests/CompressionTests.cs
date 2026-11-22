using System;
using System.Buffers;
using System.Collections.Generic;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The compressor writes brotli's bytes: those of <c>BrotliEncoderCompress</c>, at every quality, window
/// and mode, which decode back.
/// </summary>
public sealed class CompressionTests
{
    /// <summary>The qualities the compressor has so far.</summary>
    internal static readonly int[] Qualities = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    public static IEnumerable<TheoryDataRow<string, int, int, int, int>> Cases()
    {
        int[] sizes = [1, 2, 3, 15, 16, 17, 100, 1000, 4096, 65535, 65536, 65537, 100_000, 200_000, 300_000, 1 << 20];
        foreach (int quality in Qualities)
        {
            foreach (string kind in DataKinds.All)
            {
                foreach (int size in sizes)
                {
                    yield return new(kind, size, quality, 22, 0);
                }
            }

            foreach (int window in new[] { 10, 11, 12, 15, 16, 17, 18, 19, 20, 24 })
            {
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    yield return new("mixed", 300_000, quality, window, mode);
                    yield return new("text", 70_000, quality, window, mode);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WritesBrotlisBytes(string kind, int size, int quality, int window, int mode)
    {
        byte[] data = DataKinds.Generate(kind, size);
        byte[] expected = NativeBrotli.Compress(data, quality, window, mode);
        var compressor = new BrotliCompressor(quality, window, (BrotliMode)mode);
        byte[] output = new byte[BrotliCompressor.GetMaxCompressedLength(size)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out int consumed, out int written));
        Assert.Equal(size, consumed);
        Assert.True(expected.AsSpan().SequenceEqual(output.AsSpan(0, written)), $"{written} bytes where brotli writes {expected.Length}, first difference at {FirstDifference(expected, output.AsSpan(0, written))}");

        // Again, with the compressor's state from the first call.
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out written));
        Assert.True(expected.AsSpan().SequenceEqual(output.AsSpan(0, written)), "the second call writes other bytes");

        byte[] content = new byte[size];
        Assert.Equal(OperationStatus.Done, new BrotliDecompressor().Decompress(output.AsSpan(0, written), content, out _, out _));
        Assert.True(data.AsSpan().SequenceEqual(content), "the stream does not decode back");
    }

    [Fact]
    public void EmptyInput()
    {
        foreach (int quality in Qualities)
        {
            byte[] output = new byte[2];
            Assert.Equal(OperationStatus.Done, new BrotliCompressor(quality).Compress([], output, out _, out int written));
            Assert.Equal(NativeBrotli.Compress([], quality, 22), output.AsSpan(0, written).ToArray());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ADestinationTooSmall(int quality)
    {
        byte[] data = DataKinds.Generate("text", 50_000);
        byte[] expected = NativeBrotli.Compress(data, quality, 22);
        var compressor = new BrotliCompressor(quality);
        Assert.Equal(OperationStatus.DestinationTooSmall, compressor.Compress(data, new byte[expected.Length - 1], out _, out _));
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, new byte[expected.Length], out _, out int written));
        Assert.Equal(expected.Length, written);
    }

    internal static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return length;
    }
}
