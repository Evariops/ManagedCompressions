using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ManagedCompressions.Brotli;
using DotNetBrotliDecoder = System.IO.Compression.BrotliDecoder;
using DotNetBrotliEncoder = System.IO.Compression.BrotliEncoder;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// Brotli on silesia.tar, a quality a group: .NET's <c>System.IO.Compression</c> (the baseline: the
/// brotli 1.1.1 .NET ships, which NativeCompressions does not bind) and ManagedCompressions.Brotli, both
/// with brotli's default window of 4 MiB. Both decoders read .NET's stream.
/// </summary>
public abstract class BrotliBenchmarkBase : SilesiaBenchmarkBase
{
    private const int WindowLog = 22;

    private BrotliCompressor _compressor = null!;
    private readonly BrotliDecompressor _decompressor = new();
    private byte[] _stream = [];

    public abstract int Quality { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _compressor = new BrotliCompressor(Quality, WindowLog);
        _stream = Written(DotNet_Compress());
        byte[] stream = new byte[BrotliCompressor.GetMaxCompressedLength(1 << 20)];
        byte[] content = new byte[1 << 20];
        WarmUp(slice =>
        {
            _compressor.Compress(slice.Span, stream, out _, out int length);
            _decompressor.Decompress(stream.AsSpan(0, length), content, out _, out _);
        });
    }

    [Benchmark(Baseline = true, Description = ".NET BrotliEncoder TryCompress")]
    [BenchmarkCategory(Compress)]
    public int DotNet_Compress() => DotNetBrotliEncoder.TryCompress(Source, Destination, out int written, Quality, WindowLog) ? written : -1;

    [Benchmark(Description = "ManagedCompressions.Brotli Compress")]
    [BenchmarkCategory(Compress)]
    public int ManagedCompressions_Compress() => _compressor.Compress(Source, Destination, out _, out int written) == OperationStatus.Done ? written : -1;

    [Benchmark(Baseline = true, Description = ".NET BrotliDecoder TryDecompress")]
    [BenchmarkCategory(Decompress)]
    public int DotNet_Decompress() => DotNetBrotliDecoder.TryDecompress(_stream, Destination, out int written) ? written : -1;

    [Benchmark(Description = "ManagedCompressions.Brotli Decompress")]
    [BenchmarkCategory(Decompress)]
    public int ManagedCompressions_Decompress() => _decompressor.Decompress(_stream, Destination, out _, out int written) == OperationStatus.Done ? written : -1;
}

/// <summary>
/// Quality 1, the fastest of the hashers; 4, .NET's default; 9, the slowest before the optimal parser:
/// some 12 seconds a compression of silesia.tar. Quality 11, at some 3 minutes, is left to
/// ManagedCompressions.Bench, whose sample of silesia.tar takes it in seconds.
/// </summary>
public class Brotli : BrotliBenchmarkBase
{
    [Params(1, 4, 9)]
    public override int Quality { get; set; }
}
