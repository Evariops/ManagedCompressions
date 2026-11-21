using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using ManagedCompressions.Zstd;
using ManagedCompressions.Zstd.Bench;

namespace ManagedCompressions.Bench;

/// <summary>
/// <c>--jobs 1,2,4,…</c>: <see cref="ZstdCompressor.CompressAsync"/> at each degree of parallelism, on a
/// data set repeated <c>--repeat</c> times so that it makes jobs enough for the largest degree, against
/// libzstd built with threads (<c>tools/native-ref/out/libzstd_mt</c>, the zstdmt oracle of the tests)
/// at as many workers; both must write the same frame. That library is built by tools/native-ref, with
/// the compiler at hand and without zstd's assembly, so it anchors the scaling and the bytes, not the
/// speedups the level tables report against NativeCompressions' build. The candidates, two per degree, take turns in
/// rounds that rotate, as on one thread; each degree's speedup is paired round by round.
/// </summary>
/// <remarks>
/// Beside the throughputs, each candidate's process CPU time per GB of content, and for the managed
/// side the managed memory its compressor still holds after its first call (its job compressors, kept
/// for the next call): what a degree costs beyond its wall time. The process CPU time is counted in
/// scheduler quanta (some 16 ms on Windows): it means something on passes of seconds, not milliseconds.
/// </remarks>
internal static class Jobs
{
    public static List<Row> Measure(DataSet set, int level, int[] degrees, int repeat, double budget)
    {
        byte[] content = new byte[(long)set.Content.Length * repeat];
        for (int i = 0; i < repeat; i++)
        {
            set.Content.CopyTo(content, (long)i * set.Content.Length);
        }

        byte[] destination = Harness.Touched(ZstdCompressor.GetMaxCompressedLength(content.Length));
        string name = repeat == 1 ? set.Name : string.Create(CultureInfo.InvariantCulture, $"{set.Name} times {repeat}");
        ThreadedNative? native = ThreadedNative.TryLoad();
        if (native is null)
        {
            Console.WriteLine($"    (no {ThreadedNative.Path}: the managed side alone)");
        }

        var candidates = new List<Candidate>();
        var retained = new List<long>();
        foreach (int degree in degrees)
        {
            var compressor = new ZstdCompressor(level);
            candidates.Add(new Candidate(Label("managed", degree), $"ManagedCompressions.Zstd CompressAsync, degree {degree}", false,
                (s, d) => compressor.CompressAsync(content, destination, degree).AsTask().GetAwaiter().GetResult(), NoDecompression));
            if (native is not null)
            {
                Func<byte[], byte[], int> compress = native.WithWorkers(level, degree);
                candidates.Add(new Candidate(Label("native", degree), $"libzstd 1.5.7 (tools/native-ref build) with {degree} workers", true,
                    (s, d) => compress(content, destination), NoDecompression));
            }
        }

        int count = candidates.Count;
        var hashes = new ulong[count];
        var sizes = new int[count];
        var cpu = new double[count];
        var memory = new long[count];
        double[][] samples = Harness.Time(count, Enumerable.Range(0, count).ToList(), budget, (c, round) =>
        {
            long before = round == 0 ? GC.GetTotalMemory(forceFullCollection: true) : 0;
            TimeSpan processor = Process.GetCurrentProcess().TotalProcessorTime;
            long start = Stopwatch.GetTimestamp();
            int written = candidates[c].Compress!(default, default);
            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (round == 0)
            {
                memory[c] = GC.GetTotalMemory(forceFullCollection: true) - before;
                sizes[c] = written;
                hashes[c] = XxHash64.HashToUInt64(destination.AsSpan(0, Math.Max(written, 0)));
            }
            else
            {
                cpu[c] += (Process.GetCurrentProcess().TotalProcessorTime - processor).TotalSeconds;
            }

            return seconds;
        });

        var rows = new List<Row>();
        var md = new System.Text.StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"    {name}, level {level}, {content.Length:N0} bytes:");
        md.AppendLine("    | degree | managed MB/s | native MB/s | × | managed CPU s/GB | native CPU s/GB | managed held after the call |");
        md.AppendLine("    |--:|--:|--:|--:|--:|--:|--:|");
        for (int c = 0; c < count; c++)
        {
            Candidate candidate = candidates[c];
            int against = candidate.Native ? -1 : candidates.FindIndex(x => x.Native && x.Name == "native" + candidate.Name["managed".Length..]);
            bool differs = sizes[c] <= 0 || (against >= 0 && (sizes[c] != sizes[against] || hashes[c] != hashes[against]));
            var row = new Row("zstd", name, level, "compression", candidate.Name, candidate.Label, candidate.Native,
                content.Length / Harness.Median(samples[c]) / 1e6, (double)content.Length / sizes[c], differs);
            if (against >= 0)
            {
                PairedRatio paired = PairedRatio.OfMedian(samples[against], samples[c]);
                row = row with { Against = candidates[against].Name, Speedup = paired.Speedup, Low = paired.Low, High = paired.High, Pairs = paired.Pairs };
            }

            rows.Add(row);
        }

        double gigabytes = content.Length / 1e9;
        int rounds = samples[0].Length;
        foreach (int degree in degrees)
        {
            int m = candidates.FindIndex(x => x.Name == Label("managed", degree));
            int n = candidates.FindIndex(x => x.Name == Label("native", degree));
            Row managed = rows[m];
            md.Append(CultureInfo.InvariantCulture, $"    | {degree} | {managed.Speed:F1}{(managed.Differs ? " ≠" : string.Empty)} | ");
            md.Append(n < 0 ? "– | – | " : string.Create(CultureInfo.InvariantCulture, $"{rows[n].Speed:F1} | {managed.Paired} | "));
            md.Append(CultureInfo.InvariantCulture, $"{cpu[m] / rounds / gigabytes:F2} | ");
            md.Append(n < 0 ? "– | " : string.Create(CultureInfo.InvariantCulture, $"{cpu[n] / rounds / gigabytes:F2} | "));
            md.AppendLine(CultureInfo.InvariantCulture, $"{memory[m] / 1048576.0:F0} MiB |");
        }

        Console.Write(md);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB"));
        return rows;
    }

    /// <summary>The candidates here only compress: the whole content, into the shared destination.</summary>
    private static int NoDecompression(ReadOnlySpan<byte> source, Span<byte> destination) => -1;

    private static string Label(string side, int degree) => string.Create(CultureInfo.InvariantCulture, $"{side}-d{degree}");

    /// <summary>
    /// libzstd built with threads, the zstdmt oracle of the tests (<c>tools/native-ref</c>), loaded by
    /// its path: <c>ZSTD_compress2</c> with <c>ZSTD_c_nbWorkers</c>.
    /// </summary>
    private sealed unsafe class ThreadedNative
    {
        private const int CompressionLevel = 100;
        private const int NbWorkers = 400;

        private readonly delegate* unmanaged<nint> _create;
        private readonly delegate* unmanaged<nint, int, int, nuint> _setParameter;
        private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint> _compress2;
        private readonly delegate* unmanaged<nuint, uint> _isError;

        private ThreadedNative(nint library)
        {
            _create = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createCCtx");
            _setParameter = (delegate* unmanaged<nint, int, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_setParameter");
            _compress2 = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_compress2");
            _isError = (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        }

        public static string Path => System.IO.Path.Combine(DataSets.RepositoryRoot(), "tools", "native-ref", "out",
            "libzstd_mt" + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so"));

        public static ThreadedNative? TryLoad() =>
            File.Exists(Path) && NativeLibrary.TryLoad(Path, out nint library) ? new ThreadedNative(library) : null;

        /// <summary>A context of its own at <paramref name="level"/> with <paramref name="workers"/> workers, kept from call to call.</summary>
        public Func<byte[], byte[], int> WithWorkers(int level, int workers)
        {
            nint context = _create();
            if (_isError(_setParameter(context, CompressionLevel, level)) != 0 || _isError(_setParameter(context, NbWorkers, workers)) != 0)
            {
                throw new InvalidOperationException("this library has no workers");
            }

            return (source, destination) =>
            {
                fixed (byte* src = source)
                fixed (byte* dst = destination)
                {
                    nuint result = _compress2(context, dst, (nuint)destination.Length, src, (nuint)source.Length);
                    return _isError(result) != 0 ? -1 : (int)result;
                }
            };
        }
    }
}
