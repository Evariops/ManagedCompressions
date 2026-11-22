using System;
using System.Buffers;
using System.Collections.Generic;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// A ratchet: a warm decompressor allocates nothing per stream, and the bound below only ever goes down.
/// </summary>
public sealed class AllocationTests
{
    /// <summary>Bytes a warm decompressor may allocate per stream. Never raise it.</summary>
    private const long MaxBytesPerStream = 0;

    public static IEnumerable<TheoryDataRow<string, int, int>> Streams()
    {
        yield return new("text", 400_000, 5);
        yield return new("json", 131_073, 11);
        yield return new("repeats", 400_000, 1);
        yield return new("mixed", 400_000, 9);
        yield return new("urls", 4096, 11);
        yield return new("walk64", 1000, 0);
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void A_warm_decompressor_allocates_nothing(string kind, int size, int quality)
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        byte[] data = DataKinds.Generate(kind, size);
        byte[] stream = NativeBrotli.Compress(data, quality, 22);
        var decompressor = new BrotliDecompressor();
        byte[] output = new byte[size];
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(OperationStatus.Done, decompressor.Decompress(stream, output, out _, out _));
        }

        // The least of several rounds: an allocation the decoder makes shows in every round, while
        // the runtime's own (tiering, the test framework on a shared thread) come and go.
        const int Streams = 20;
        long perStream = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Streams; i++)
            {
                decompressor.Decompress(stream, output, out _, out _);
            }

            perStream = Math.Min(perStream, (GC.GetAllocatedBytesForCurrentThread() - before) / Streams);
        }

        Assert.True(perStream <= MaxBytesPerStream, $"{kind} q{quality}: {perStream} bytes allocated per stream");
    }
}
