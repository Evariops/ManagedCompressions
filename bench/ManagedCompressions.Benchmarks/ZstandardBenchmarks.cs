using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ManagedCompressions.Zstd;
using DotNetZstandardDecoder = System.IO.Compression.ZstandardDecoder;
using DotNetZstandardEncoder = System.IO.Compression.ZstandardEncoder;
using NativeOptions = NativeCompressions.ZstandardCompressionOptions;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// Zstandard on silesia.tar, a level a group: NativeCompressions (the baseline), ManagedCompressions.Zstd, and
/// .NET's own <c>System.IO.Compression</c> encoder and decoder. Every decoder reads NativeCompressions' frame.
/// </summary>
public abstract class ZstandardBenchmarkBase : SilesiaBenchmarkBase
{
    private ZstdCompressor _compressor = null!;
    private readonly ZstdDecompressor _decompressor = new();
    private DotNetZstandardEncoder _dotNetEncoder = null!;
    private byte[] _frame = [];

    public abstract int Level { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _compressor = new ZstdCompressor(Level);
        _dotNetEncoder = new DotNetZstandardEncoder(Level);
        _frame = Written(NativeCompressions_Compress());
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(1 << 20)];
        byte[] content = new byte[1 << 20];
        WarmUp(slice =>
        {
            _compressor.Compress(slice.Span, frame, out _, out int length);
            _decompressor.Decompress(frame.AsSpan(0, length), content, out _, out _);
        });
    }

    [Benchmark(Baseline = true, Description = "NativeCompressions Zstandard Compress")]
    [BenchmarkCategory(Compress)]
    public int NativeCompressions_Compress() =>
        NativeCompressions.Zstandard.Compress(Source, Destination, NativeOptions.Default with { CompressionLevel = Level });

    [Benchmark(Description = "ManagedCompressions.Zstd Compress")]
    [BenchmarkCategory(Compress)]
    public int ManagedCompressions_Compress() => _compressor.Compress(Source, Destination, out _, out int written) == OperationStatus.Done ? written : -1;

    [Benchmark(Description = ".NET ZstandardEncoder Compress")]
    [BenchmarkCategory(Compress)]
    public int DotNet_Compress()
    {
        _dotNetEncoder.Reset();
        return _dotNetEncoder.Compress(Source, Destination, out _, out int written, isFinalBlock: true) == OperationStatus.Done ? written : -1;
    }

    [Benchmark(Baseline = true, Description = "NativeCompressions Zstandard Decompress")]
    [BenchmarkCategory(Decompress)]
    public int NativeCompressions_Decompress() => NativeCompressions.Zstandard.Decompress(_frame, Destination);

    [Benchmark(Description = "ManagedCompressions.Zstd Decompress")]
    [BenchmarkCategory(Decompress)]
    public int ManagedCompressions_Decompress() => _decompressor.Decompress(_frame, Destination, out _, out int written) == OperationStatus.Done ? written : -1;

    [Benchmark(Description = ".NET ZstandardDecoder Decompress")]
    [BenchmarkCategory(Decompress)]
    public int DotNet_Decompress() => DotNetZstandardDecoder.TryDecompress(_frame, Destination, out int written) ? written : -1;
}

/// <summary>Level 1, the fastest; 3, the default of zstd, NativeCompressions and .NET; 9, the lazy search at its most thorough.</summary>
public class Zstandard : ZstandardBenchmarkBase
{
    [Params(1, 3, 9)]
    public override int Level { get; set; }
}

/// <summary>Level 19, the highest before the ultra levels: some 45 seconds a compression of silesia.tar, so fewer rounds.</summary>
[WarmupCount(1)]
[IterationCount(3)]
public class ZstandardHigh : ZstandardBenchmarkBase
{
    [Params(19)]
    public override int Level { get; set; }
}
