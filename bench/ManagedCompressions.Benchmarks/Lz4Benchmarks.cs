using System;
using BenchmarkDotNet.Attributes;
using NativeCompressions.Interop;
using ManagedCompressions.Lz4;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// LZ4's block format on silesia.tar, a level a group: NativeCompressions (the baseline) and ManagedCompressions.Lz4.
/// Level 1 is NativeCompressions' <c>LZ4.Block.Compress</c> (<c>LZ4_compress_default</c>); its block API
/// has no level, so the high-compression levels call <c>LZ4_compress_HC</c> through its public interop.
/// Every decoder reads NativeCompressions' block.
/// </summary>
public abstract unsafe class Lz4BenchmarkBase : SilesiaBenchmarkBase
{
    private Lz4Compressor _compressor = null!;
    private byte[] _block = [];

    public abstract int Level { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _compressor = new Lz4Compressor(Level);
        _block = Written(NativeCompressions_Compress());
        byte[] block = new byte[Lz4Compressor.GetMaxCompressedLength(1 << 20)];
        byte[] content = new byte[1 << 20];
        WarmUp(slice =>
        {
            int length = _compressor.Compress(slice.Span, block);
            Lz4Block.TryDecompress(block.AsSpan(0, length), content, out _);
        });
    }

    [Benchmark(Baseline = true, Description = "NativeCompressions LZ4 Compress(Block)")]
    [BenchmarkCategory(Compress)]
    public int NativeCompressions_Compress()
    {
        if (Level <= 1)
        {
            return NativeCompressions.LZ4.Block.Compress(Source, Destination);
        }

        fixed (byte* source = Source)
        fixed (byte* destination = Destination)
        {
            return LZ4NativeMethods.LZ4_compress_HC(source, destination, Source.Length, Destination.Length, Level);
        }
    }

    [Benchmark(Description = "ManagedCompressions.Lz4 Compress(Block)")]
    [BenchmarkCategory(Compress)]
    public int ManagedCompressions_Compress() => _compressor.Compress(Source, Destination);

    [Benchmark(Baseline = true, Description = "NativeCompressions LZ4 Decompress(Block)")]
    [BenchmarkCategory(Decompress)]
    public int NativeCompressions_Decompress() => NativeCompressions.LZ4.Block.Decompress(_block, Destination);

    [Benchmark(Description = "ManagedCompressions.Lz4 Decompress(Block)")]
    [BenchmarkCategory(Decompress)]
    public int ManagedCompressions_Decompress() => Lz4Block.TryDecompress(_block, Destination, out int written) ? written : -1;
}

/// <summary>Level 1, <c>LZ4_compress_default</c>; 9, lz4's default high-compression level.</summary>
public class Lz4 : Lz4BenchmarkBase
{
    [Params(1, 9)]
    public override int Level { get; set; }
}

/// <summary>Level 12, the optimal parser at its most thorough: some 13 seconds a compression of silesia.tar, so fewer rounds.</summary>
[WarmupCount(1)]
[IterationCount(3)]
public class Lz4High : Lz4BenchmarkBase
{
    [Params(12)]
    public override int Level { get; set; }
}
