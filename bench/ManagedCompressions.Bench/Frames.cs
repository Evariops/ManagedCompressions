using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ManagedCompressions.Zstd;
using ManagedCompressions.Zstd.Bench;
using NativeCompressions.Interop;

namespace ManagedCompressions.Bench;

/// <summary>
/// <c>--frames 1,2,4,…</c>: a data set compressed by <see cref="ZstdCompressor.CompressAsync"/> as one frame
/// and as independent frames (<see cref="ZstdJobFrames.Independent"/>), what the independent frames cost
/// in size and in compression time, then <see cref="ZstdDecompressor.DecompressAsync"/> at each degree of
/// parallelism on the independent frames, and on the single frame at one and two threads (the second
/// verifying a checksum, with <c>--checksum 1</c>, on its own thread); libzstd decodes the independent
/// frames on one thread beside them. Every decoder must give the content back.
/// </summary>
internal static unsafe class Frames
{
    public static void Measure(DataSet set, int level, int[] degrees, int repeat, double budget, bool checksum, int size = 0)
    {
        byte[] content = new byte[(long)set.Content.Length * repeat];
        for (int i = 0; i < repeat; i++)
        {
            set.Content.CopyTo(content, (long)i * set.Content.Length);
        }

        // --size: the first bytes only, for frames of a given size.
        content = size > 0 && size < content.Length ? content.AsSpan(0, size).ToArray() : content;

        var compressor = new ZstdCompressor(level) { AppendChecksum = checksum };
        byte[] buffer = Harness.Touched(ZstdCompressor.GetMaxCompressedLength(content.Length) + (1 << 20));
        byte[] single = buffer.AsSpan(0, compressor.CompressAsync(content, buffer).AsTask().GetAwaiter().GetResult()).ToArray();
        compressor.JobFrames = ZstdJobFrames.Independent;
        byte[] independent = buffer.AsSpan(0, compressor.CompressAsync(content, buffer).AsTask().GetAwaiter().GetResult()).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    {content.Length:N0} bytes at level {level}{(checksum ? " with checksums" : string.Empty)}: one frame {single.Length:N0} bytes (ratio {(double)content.Length / single.Length:F3}), independent frames {independent.Length:N0} bytes (ratio {(double)content.Length / independent.Length:F3}), {100.0 * (independent.Length - single.Length) / single.Length:+0.00;-0.00}% larger"));

        // Compression: the two layouts at the machine's degree, paired.
        var singleCompressor = new ZstdCompressor(level) { AppendChecksum = checksum };
        var independentCompressor = new ZstdCompressor(level) { AppendChecksum = checksum, JobFrames = ZstdJobFrames.Independent };
        var compression = new List<(string Name, Func<double> Run)>
        {
            ("one frame", () => Timed(() => singleCompressor.CompressAsync(content, buffer).AsTask().GetAwaiter().GetResult())),
            ("independent", () => Timed(() => independentCompressor.CompressAsync(content, buffer).AsTask().GetAwaiter().GetResult())),
        };
        double[][] compressed = Harness.Time(compression.Count, [0, 1], budget, (c, _) => compression[c].Run());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    compression at degree {Environment.ProcessorCount}: one frame {content.Length / Harness.Median(compressed[0]) / 1e6:F0} MB/s, independent {content.Length / Harness.Median(compressed[1]) / 1e6:F0} MB/s, independent over one frame {PairedRatio.OfMedian(compressed[0], compressed[1])}"));

        // Decompression.
        byte[] output = Harness.Touched(content.Length);
        var candidates = new List<(string Name, Func<double> Run)>();
        var decompressors = new List<ZstdDecompressor>();
        foreach (int degree in degrees)
        {
            var decompressor = new ZstdDecompressor();
            decompressors.Add(decompressor);
            candidates.Add((string.Create(CultureInfo.InvariantCulture, $"independent, {degree} threads"),
                () => Timed(() => decompressor.DecompressAsync(independent, output, degree).AsTask().GetAwaiter().GetResult())));
        }

        foreach (int degree in new[] { 1, 2 })
        {
            var decompressor = new ZstdDecompressor();
            candidates.Add((string.Create(CultureInfo.InvariantCulture, $"one frame, {degree} threads"),
                () => Timed(() => decompressor.DecompressAsync(single, output, degree).AsTask().GetAwaiter().GetResult())));
        }

        ZSTD_DCtx_s* dctx = ZstandardNativeMethods.ZSTD_createDCtx();
        candidates.Add(("libzstd, independent, 1 thread", () => Timed(() =>
        {
            fixed (byte* src = independent)
            fixed (byte* dst = output)
            {
                nuint r = ZstandardNativeMethods.ZSTD_decompressDCtx(dctx, dst, (nuint)output.Length, src, (nuint)independent.Length);
                return ZstandardNativeMethods.ZSTD_isError(r) != 0 ? -1 : (int)r;
            }
        })));

        foreach ((string name, Func<double> run) in candidates)
        {
            Array.Fill(output, (byte)1);
            run();
            if (!output.AsSpan().SequenceEqual(content))
            {
                Console.WriteLine($"    {name}: DIFFERS");
            }
        }

        double[][] samples = Harness.Time(candidates.Count, Enumerable.Range(0, candidates.Count).ToList(), budget, (c, _) => candidates[c].Run());
        Console.WriteLine("    | decoder | MB/s | over independent, 1 thread |");
        Console.WriteLine("    |:--|--:|--:|");
        for (int c = 0; c < candidates.Count; c++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    | {candidates[c].Name} | {content.Length / Harness.Median(samples[c]) / 1e6:F0} | {(c == 0 ? "1" : PairedRatio.OfMedian(samples[0], samples[c]).ToString())} |"));
        }

        ZstandardNativeMethods.ZSTD_freeDCtx(dctx);
    }

    private static double Timed(Func<int> run)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (run() < 0)
        {
            throw new InvalidOperationException("a candidate failed");
        }

        return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
    }
}
