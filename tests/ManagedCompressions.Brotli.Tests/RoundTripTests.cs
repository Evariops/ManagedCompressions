using System;
using System.Buffers;
using System.Collections.Generic;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>Every stream brotli writes, at every quality, window and mode, decodes to its input.</summary>
public sealed class RoundTripTests
{
    public static IEnumerable<TheoryDataRow<string, int, int, int, int>> Cases()
    {
        int[] sizes = [0, 1, 2, 3, 15, 100, 1000, 65536, 200_000];
        foreach (string kind in DataKinds.All)
        {
            foreach (int size in sizes)
            {
                for (int quality = 0; quality <= 11; quality++)
                {
                    if (size > 65536 && quality >= 10)
                    {
                        continue;
                    }

                    yield return new(kind, size, quality, 22, 0);
                }
            }
        }

        foreach (int window in new[] { 10, 11, 16, 18, 24 })
        {
            foreach (int quality in new[] { 0, 1, 2, 5, 9, 11 })
            {
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    yield return new("mixed", 150_000, quality, window, mode);
                    yield return new("text", 5000, quality, window, mode);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesWhatBrotliWrites(string kind, int size, int quality, int window, int mode)
    {
        byte[] data = DataKinds.Generate(kind, size);
        byte[] stream = NativeBrotli.Compress(data, quality, window, mode);
        var decompressor = new BrotliDecompressor();
        byte[] output = new byte[size + 64];
        OperationStatus status = decompressor.Decompress(stream, output, out int consumed, out int written);
        Assert.Equal(BrotliError.None, decompressor.LastError);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(stream.Length, consumed);
        Assert.Equal(size, written);
        Assert.True(data.AsSpan().SequenceEqual(output.AsSpan(0, written)), "the content differs");

        // Into a destination of the exact size: the copies near its end take the careful path.
        byte[] exact = new byte[size];
        Assert.Equal(OperationStatus.Done, decompressor.Decompress(stream, exact, out _, out _));
        Assert.True(data.AsSpan().SequenceEqual(exact), "the content differs in an exact destination");

        if (size > 0)
        {
            Assert.Equal(OperationStatus.DestinationTooSmall, decompressor.Decompress(stream, output.AsSpan(0, size - 1), out _, out _));
        }

        Assert.Equal(OperationStatus.NeedMoreData, decompressor.Decompress(stream.AsSpan(0, stream.Length - 1), output, out _, out _));
    }
}
