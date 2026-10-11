using System.IO;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using NativeOptions = NativeCompressions.ZstandardCompressionOptions;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// For scale, as NativeCompressions' README shows them: .NET's Brotli and GZip at their defaults, against
/// NativeCompressions' Zstandard at its own.
/// </summary>
public class OtherCodecs : SilesiaBenchmarkBase
{
    private byte[] _zstd = [];
    private byte[] _brotli = [];
    private byte[] _gzip = [];

    [GlobalSetup]
    public void Setup()
    {
        _zstd = Written(NativeCompressions_Zstandard_Compress());
        _brotli = Written(Brotli_Compress());
        _gzip = Written(GZip_Compress());
    }

    [Benchmark(Baseline = true, Description = "NativeCompressions Zstandard Compress(Default)")]
    [BenchmarkCategory(Compress)]
    public int NativeCompressions_Zstandard_Compress() => NativeCompressions.Zstandard.Compress(Source, Destination, NativeOptions.Default);

    [Benchmark(Description = "BrotliEncoder TryCompress(Default)")]
    [BenchmarkCategory(Compress)]
    public int Brotli_Compress() => BrotliEncoder.TryCompress(Source, Destination, out int written) ? written : -1;

    [Benchmark(Description = "GZipStream Compress(Level: Optimal)")]
    [BenchmarkCategory(Compress)]
    public int GZip_Compress()
    {
        using var output = new MemoryStream(Destination);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Source);
        }

        return (int)output.Position;
    }

    [Benchmark(Baseline = true, Description = "NativeCompressions Zstandard Decompress(Default)")]
    [BenchmarkCategory(Decompress)]
    public int NativeCompressions_Zstandard_Decompress() => NativeCompressions.Zstandard.Decompress(_zstd, Destination);

    [Benchmark(Description = "BrotliDecoder TryDecompress(Default)")]
    [BenchmarkCategory(Decompress)]
    public int Brotli_Decompress() => BrotliDecoder.TryDecompress(_brotli, Destination, out int written) ? written : -1;

    [Benchmark(Description = "GZipStream Decompress(Level: Optimal)")]
    [BenchmarkCategory(Decompress)]
    public int GZip_Decompress()
    {
        using var input = new MemoryStream(_gzip);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Destination);
        gzip.CopyTo(output);
        return (int)output.Position;
    }
}
