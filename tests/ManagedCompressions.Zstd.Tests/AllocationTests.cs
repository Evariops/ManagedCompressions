using System;
using System.Buffers;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests;

/// <summary>
/// A ratchet: a warm decoder allocates nothing per frame, and the bound below only ever goes down.
/// </summary>
public sealed class AllocationTests
{
    /// <summary>Bytes a warm decoder may allocate per frame. Never raise it.</summary>
    private const long MaxBytesPerFrame = 0;

    public static TheoryData<string> Frames() =>
    [
        CorpusCase.Name("walk64", 512 << 10, 3),
        CorpusCase.Name("text", 400_000, 3),
        CorpusCase.Name("json", 131073, 19, "chk"),
        CorpusCase.Name("repeats", 400_000, 1),
        CorpusCase.Name("urls", 4096, 3, "dict=trained"),
        CorpusCase.Name("walk64", 4096, 3, "dict=raw"),
        CorpusCase.Name("mixed", 400_000, 3, "nofcs"),
        CorpusCase.Name("text", 100_000, 3, "block=1340"),
    ];

    [Theory]
    [MemberData(nameof(Frames))]
    public void A_warm_decoder_allocates_nothing(string name)
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        byte[] frame = @case.Compress(data);
        byte[]? dictionary = @case.GetDictionary()?.Bytes;
        int size = data.Length;

        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        byte[] output = new byte[size];
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, out _, out _));
        }

        // The least of several rounds: an allocation the decoder makes shows in every round, while
        // the runtime's own (tiering, the test framework on a shared thread) come and go.
        const int Frames = 20;
        long perFrame = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Frames; i++)
            {
                decoder.Reset();
                decoder.Decompress(frame, output, out _, out _);
            }

            perFrame = Math.Min(perFrame, (GC.GetAllocatedBytesForCurrentThread() - before) / Frames);
        }

        Assert.True(perFrame <= MaxBytesPerFrame, $"{name}: {perFrame} bytes allocated per frame");
    }

    public static TheoryData<int, long> SmallRecordLevels() => new()
    {
        // level, bytes a compressor of 1 KiB records may allocate, construction included. Never raise them.
        { 1, 48 << 10 },
        { 3, 48 << 10 },
        { 5, 64 << 10 },
        { 9, 64 << 10 },
        { 13, 224 << 10 },
        { 19, 224 << 10 },
    };

    /// <summary>
    /// A compressor sizes its buffers for the blocks it is given: one created for records of 1 KiB
    /// takes some tens of kilobytes, not the megabyte the largest block would take.
    /// </summary>
    [Theory]
    [MemberData(nameof(SmallRecordLevels))]
    public void A_compressor_of_small_records_stays_small(int level, long maxBytes)
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        byte[] record = DataKinds.Generate("json", 1 << 10, 5);
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(record.Length)];
        new ZstdCompressor(level).Compress(record, frame, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var compressor = new ZstdCompressor(level);
        Assert.Equal(OperationStatus.Done, compressor.Compress(record, frame, out _, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= maxBytes, $"level {level}: {allocated} bytes allocated");
    }

    /// <summary>A decompressor of records of 1 KiB takes some tens of kilobytes too.</summary>
    [Fact]
    public void A_decompressor_of_small_records_stays_small()
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        byte[] record = DataKinds.Generate("json", 1 << 10, 5);
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(record.Length)];
        new ZstdCompressor(3).Compress(record, frame, out _, out int length);
        byte[] output = new byte[record.Length];
        new ZstdDecompressor().Decompress(frame.AsSpan(0, length), output, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var decompressor = new ZstdDecompressor();
        Assert.Equal(OperationStatus.Done, decompressor.Decompress(frame.AsSpan(0, length), output, out _, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 32 << 10, $"{allocated} bytes allocated");
    }
}
