using System;
using System.IO.Compression;
using ManagedCompressions.Lz4;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// What every benchmark of silesia.tar shares: the source, one destination large enough for any codec's
/// output, and the warm-up the JIT needs.
/// </summary>
/// <remarks>
/// NativeCompressions is the baseline of each group, so the Ratio column is a case's time over
/// NativeCompressions'. The managed compressors and decompressors, and .NET's Zstandard encoder, are
/// created once and reused, as their documentation says to: they keep their tables between calls. Every
/// decoder reads the same bytes, NativeCompressions' (the managed compressors write the same).
/// </remarks>
public abstract class SilesiaBenchmarkBase
{
    protected const string Compress = "Compress";
    protected const string Decompress = "Decompress";

    protected readonly byte[] Source = Silesia.Tar;

    protected readonly byte[] Destination = new byte[Bound(Silesia.Tar.Length) + (1 << 20)];

    private static int Bound(int length) => Math.Max(
        Math.Max(NativeCompressions.Zstandard.GetMaxCompressedLength(length), Lz4Compressor.GetMaxCompressedLength(length)),
        BrotliEncoder.GetMaxCompressedLength(length));

    protected byte[] Written(int length) => Destination.AsSpan(0, length).ToArray();

    /// <summary>
    /// The managed codecs warmed as a running service would have them. The tiered JIT recompiles a method
    /// after 30 calls, counted once no new method has been compiled for 100 ms, and with dynamic PGO it
    /// does so twice: instrumented to profile it, then optimized with the profile. One call of these cases
    /// runs for up to a minute, and BenchmarkDotNet makes a few, so a method that one call runs once, as
    /// LZ4's compressor is, would be measured instrumented. Four passes of 40 rounds over the first MiB of
    /// silesia.tar, each followed by a pause for the background compilations, take every method to its
    /// final code: a MiB is past the 256 KiB above which zstd chooses the parameters of a large input.
    /// Native AOT has nothing to warm, nor the native libraries.
    /// </summary>
    protected void WarmUp(Action<ReadOnlyMemory<byte>> round)
    {
        ReadOnlyMemory<byte> slice = Source.AsMemory(0, 1 << 20);
        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 0; i < 40; i++)
            {
                round(slice);
            }

            System.Threading.Thread.Sleep(250);
        }
    }
}
