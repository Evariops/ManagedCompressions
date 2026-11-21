using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ManagedCompressions.Zstd.Bench;

namespace ManagedCompressions.Bench;

/// <summary>
/// The managed codecs against NativeCompressions, level by level, on one thread or in a crowd.
/// </summary>
/// <remarks>
/// <para>
/// Usage: <c>ManagedCompressions.Bench [--codecs zstd,lz4] [--sets silesia,github] [--levels 1,3]
/// [--seconds S] [--directions compression,decompression] [--results file.tsv] [--markdown file.md]
/// [--core N] [--aa 1] [--checksum 1] [--samples 1] [--crowd N [--crowd-cpus 0-15]] [--jobs 1,2,4 | --frames 1,2,4] [--repeat N]</c>,
/// or <c>--report file.tsv [--markdown file.md]</c>
/// to render results already measured, or <c>--compare-base a.tsv;b.tsv --compare-head c.tsv;d.tsv</c> to
/// compare two builds measured in alternate processes (<c>bench/ab.ps1</c>, <c>bench/ab.sh</c>).
/// </para>
/// <para>
/// <c>--core N</c> keeps the timed thread on logical processor N (Windows). A direction is timed for
/// some <c>--seconds</c> (3 by default), never fewer than three rounds. Every speedup is paired round by
/// round, with its 95% interval (<see cref="PairedRatio"/>). <c>--aa 1</c> adds a second instance of the
/// managed codec, whose speedup over the first must hold 1 within ±1%: a run that fails it measured
/// noise. <c>--crowd N</c> runs N threads at once, each with its own instances (<see cref="Crowd"/>),
/// kept by <c>--crowd-cpus</c> on the logical processors given. <c>--checksum 1</c> has both Zstandard
/// implementations write and verify the content checksum. <c>--samples 1</c> prints every round's times.
/// <c>--jobs 1,2,4</c> measures Zstandard's CompressAsync at those degrees of parallelism against
/// libzstd with as many workers (<see cref="Jobs"/>), on the data set repeated <c>--repeat</c> times;
/// <c>--frames 1,2,4</c> measures independent frames and DecompressAsync at those degrees (<see cref="Frames"/>).
/// <c>--results</c> appends each level's measures as soon as they are taken, and the tables are rendered
/// from the whole file, a later measure replacing an earlier one.
/// </para>
/// <para>
/// An interval covers one process. The managed code is compiled anew in each, and where its loops land
/// moves it from process to process: on a Ryzen 9 7950X, Zstandard level 3 compression of silesia-sample
/// came out at 0.94x to 0.98x of the native one in five processes, each interval ±0.2%. A change of a
/// few percent is therefore judged over several processes, as <c>bench/ab.ps1</c> does, never from one.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly Dictionary<string, int[]> DefaultLevels = new()
    {
        ["zstd"] = [1, 3, 5, 7, 9, 13, 16, 19],
        ["lz4"] = [1, 2, 3, 6, 9, 12],
    };

    private static readonly Dictionary<string, string[]> DefaultSets = new()
    {
        ["zstd"] = ["silesia", "silesia-64k", "github", "github-dict"],
        ["lz4"] = ["silesia", "silesia-64k", "github"],
    };

    public static int Main(string[] args)
    {
        Dictionary<string, string> options = Options(args);
        if (OperatingSystem.IsMacOS() && options.GetValueOrDefault("qos", "1") != "0")
        {
            NativeThreads.Raise();
        }
        else if (OperatingSystem.IsWindows())
        {
            // The process and the timed thread at a high priority, and with --core N the thread kept on
            // logical processor N, which no migration then disturbs.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            NativeThreads.Raise();
            if (options.TryGetValue("core", out string? core))
            {
                NativeThreads.PinCurrentThread(int.Parse(core, CultureInfo.InvariantCulture));
            }
        }

        if (options.TryGetValue("report", out string? report))
        {
            return Render(Read(report), options.GetValueOrDefault("markdown"));
        }

        if (options.TryGetValue("compare-base", out string? baseFiles) && options.TryGetValue("compare-head", out string? headFiles))
        {
            return Compare(baseFiles.Split(';', StringSplitOptions.RemoveEmptyEntries), headFiles.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        string[] codecs = options.GetValueOrDefault("codecs", "zstd,lz4").Split(',');
        double budget = double.Parse(options.GetValueOrDefault("seconds", "3"), CultureInfo.InvariantCulture);
        string directions = options.GetValueOrDefault("directions", "compression,decompression");
        bool compress = directions.Contains("compression", StringComparison.Ordinal) && !directions.Equals("decompression", StringComparison.Ordinal);
        bool decompress = directions.Contains("decompression", StringComparison.Ordinal);
        string? results = options.GetValueOrDefault("results");
        bool aa = options.GetValueOrDefault("aa") == "1";
        int crowd = int.Parse(options.GetValueOrDefault("crowd", "0"), CultureInfo.InvariantCulture);
        int[]? cpus = options.TryGetValue("crowd-cpus", out string? list) ? Crowd.ParseCpus(list) : null;
        string? only = options.GetValueOrDefault("only");
        Harness.PrintSamples = options.GetValueOrDefault("samples") == "1";
        Candidates.Checksum = options.GetValueOrDefault("checksum") == "1";
        int[]? jobs = options.TryGetValue("jobs", out string? degrees)
            ? degrees.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : null;
        int repeat = int.Parse(options.GetValueOrDefault("repeat", "1"), CultureInfo.InvariantCulture);
        int[]? frames = options.TryGetValue("frames", out string? frameDegrees)
            ? frameDegrees.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : null;

        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")}, " +
            $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, some {budget} s a direction" +
            (crowd > 0 ? $", {crowd} threads at once" : string.Empty));
        var rows = new List<Row>();
        var cache = new Dictionary<string, DataSet>();
        foreach (string codec in codecs)
        {
            string[] sets = options.TryGetValue("sets", out string? s) ? s.Split(',') : DefaultSets[codec];
            int[] levels = options.TryGetValue("levels", out string? l)
                ? l.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
                : DefaultLevels[codec];
            foreach (string name in sets)
            {
                if (codec == "lz4" && name.EndsWith("-dict", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!cache.TryGetValue(name, out DataSet? set))
                {
                    cache[name] = set = DataSets.Load(name);
                }

                Console.WriteLine();
                Console.WriteLine($"{codec}: {set.Title}");
                foreach (int level in levels)
                {
                    List<Row> measured;
                    if (frames is not null)
                    {
                        if (codec == "zstd")
                        {
                            Frames.Measure(set, level, frames, repeat, budget, Candidates.Checksum, int.Parse(options.GetValueOrDefault("size", "0"), CultureInfo.InvariantCulture));
                        }

                        continue;
                    }

                    if (jobs is not null)
                    {
                        if (codec != "zstd")
                        {
                            continue;
                        }

                        measured = Jobs.Measure(set, level, jobs, repeat, budget);
                        rows.AddRange(measured);
                        if (results is not null)
                        {
                            File.AppendAllLines(results, measured.Select(r => r.ToTsv()));
                        }

                        continue;
                    }

                    if (crowd > 0)
                    {
                        measured = Crowd.Measure(codec, set, level, crowd, cpus, budget, compress, decompress, aa, only);
                    }
                    else
                    {
                        var release = new List<Action>();
                        List<Candidate> candidates = Candidates.For(codec, set, level, release, aa);
                        measured = Harness.Measure(codec, set, level, candidates, budget, compress, decompress, only);
                        release.ForEach(r => r());
                    }

                    Print(measured);
                    rows.AddRange(measured);
                    if (results is not null)
                    {
                        File.AppendAllLines(results, measured.Select(r => r.ToTsv()));
                    }
                }
            }
        }

        // --jobs prints its own table, a line a degree; the level table would mix the degrees.
        return jobs is not null || frames is not null ? 0 : Render(results is null ? rows : Read(results), options.GetValueOrDefault("markdown"));
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException("expected --name value pairs, got " + args[i]);
            }

            options[args[i][2..]] = args[++i];
        }

        return options;
    }

    /// <summary>
    /// Each direction's line: every candidate's throughput and ratio, and its paired speedup over the
    /// reference with the 95% interval; an A/A arm says whether the run passes it.
    /// </summary>
    private static void Print(List<Row> rows)
    {
        foreach (IGrouping<string, Row> direction in rows.GroupBy(r => r.Direction))
        {
            Row reference = direction.FirstOrDefault(r => r.Native) ?? direction.First();
            var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"  L{reference.Level,-3} {direction.Key,-13}"));
            foreach (Row row in direction)
            {
                line.Append(CultureInfo.InvariantCulture, $" | {row.Candidate} {row.Speed,8:F1} MB/s ratio {row.Ratio:F3}");
                if (row.IsAa)
                {
                    PairedRatio aa = row.Paired;
                    string verdict = aa.Pairs < PairedRatio.MinPairs ? $"too few rounds ({aa.Pairs}): raise --seconds"
                        : aa.PassesAa ? "ok" : "NOT VALID, beyond ±1%";
                    line.Append(CultureInfo.InvariantCulture, $" (A/A {aa}: {verdict})");
                }
                else if (!double.IsNaN(row.Speedup))
                {
                    line.Append(CultureInfo.InvariantCulture, $" ({row.Paired})");
                }

                if (row.Differs)
                {
                    line.Append(" DIFFERS");
                }
            }

            Console.WriteLine(line);
        }
    }

    private static List<Row> Read(string results)
    {
        var rows = new List<Row>();
        var index = new Dictionary<string, int>();
        foreach (string line in File.ReadLines(results).Where(l => l.Length > 0))
        {
            Row row = Row.FromTsv(line);
            if (index.TryGetValue(row.Key, out int at))
            {
                rows[at] = row;
            }
            else
            {
                index[row.Key] = rows.Count;
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// A table per codec and data set, a line per level: each side's ratio, then each direction's
    /// throughputs and the managed side's speedup over the native one, with its 95% interval when the
    /// results carry one. A/A arms are left out of the tables.
    /// </summary>
    private static int Render(List<Row> rows, string? markdown)
    {
        var md = new StringBuilder();
        foreach (IGrouping<(string Codec, string Set), Row> table in rows.GroupBy(r => (r.Codec, r.Set)))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"### {table.Key.Codec}, {table.Key.Set}");
            md.AppendLine();
            md.AppendLine("| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |");
            md.AppendLine("|--:|--:|--:|--:|--:|--:|--:|--:|--:|");
            foreach (IGrouping<int, Row> level in table.GroupBy(r => r.Level).OrderBy(g => g.Key))
            {
                Row? Find(string direction, bool native) => level.LastOrDefault(r => r.Direction == direction && r.Native == native && !r.IsAa);
                Row? cn = Find("compression", true);
                Row? cm = Find("compression", false);
                Row? dn = Find("decompression", true);
                Row? dm = Find("decompression", false);
                double? ratioNative = cn?.Ratio ?? dn?.Ratio;
                md.Append(CultureInfo.InvariantCulture, $"| {level.Key} | {Format(ratioNative, "F3")} | {Format(cm?.Ratio, "F3")}{(cm?.Differs == true ? " ≠" : string.Empty)} ");
                md.Append(CultureInfo.InvariantCulture, $"| {Speed(cn)} | {Speed(cm)} | {Speedup(cm, cn)} ");
                md.Append(CultureInfo.InvariantCulture, $"| {Speed(dn)} | {Speed(dm)} | {Speedup(dm, dn)} |");
                md.AppendLine();
            }

            md.AppendLine();
        }

        md.AppendLine("Throughputs in MB/s of uncompressed data (10^6 bytes a second), median of the rounds; a crowd's (a set " +
            "named `x N`) are N threads' together. × is the managed speedup over the native one, paired round by round, with " +
            "its 95% interval where measured. Decompression decodes the native side's output at each level. ≠ marks an " +
            "output that differs from the native one where it should not, or that does not read back.");
        Console.WriteLine();
        Console.Write(md);
        if (markdown is not null)
        {
            File.WriteAllText(markdown, md.ToString());
        }

        return 0;
    }

    /// <summary>
    /// Two builds measured in alternate processes, <paramref name="baseFiles"/>[i] and
    /// <paramref name="headFiles"/>[i] a pair. In each process the managed throughput is taken over the
    /// native one measured beside it, which cancels what the machine did during that process; the head's
    /// over the base's, pair by pair, then their geometric mean with its 95% interval.
    /// </summary>
    private static int Compare(string[] baseFiles, string[] headFiles)
    {
        int pairs = Math.Min(baseFiles.Length, headFiles.Length);
        if (pairs == 0)
        {
            Console.WriteLine("no pair of results to compare");
            return 1;
        }

        List<Dictionary<string, double>> baseRuns = baseFiles.Take(pairs).Select(Normalized).ToList();
        List<Dictionary<string, double>> headRuns = headFiles.Take(pairs).Select(Normalized).ToList();
        Console.WriteLine($"head over base, managed over native in each process, {pairs} pairs of processes:");
        int slower = 0;
        foreach (string key in headRuns[0].Keys.Where(k => baseRuns.All(r => r.ContainsKey(k)) && headRuns.All(r => r.ContainsKey(k))))
        {
            double[] baseTimes = baseRuns.Select(r => 1 / r[key]).ToArray();
            double[] headTimes = headRuns.Select(r => 1 / r[key]).ToArray();
            PairedRatio ratio = PairedRatio.Of(baseTimes, headTimes);
            string verdict = !ratio.HasInterval ? string.Empty : ratio.Low > 1 ? "faster" : ratio.High < 1 ? "slower" : "no change";
            slower += verdict == "slower" ? 1 : 0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key,-40} {ratio,-26} {verdict}"));
        }

        return slower == 0 ? 0 : 2;
    }

    /// <summary>A process's managed throughput over its native one, by codec, set, level and direction.</summary>
    private static Dictionary<string, double> Normalized(string file)
    {
        List<Row> rows = Read(file);
        var normalized = new Dictionary<string, double>();
        foreach (IGrouping<string, Row> group in rows.GroupBy(r => $"{r.Codec}/{r.Set}/L{r.Level}/{r.Direction}"))
        {
            Row? native = group.FirstOrDefault(r => r.Native);
            Row? managed = group.FirstOrDefault(r => !r.Native && !r.IsAa);
            if (native is not null && managed is not null)
            {
                normalized[group.Key] = managed.Speed / native.Speed;
            }
        }

        return normalized;
    }

    private static string Format(double? value, string format) => value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "–";

    private static string Speed(Row? row) => row is null ? "–"
        : row.Speed.ToString(row.Speed >= 100 ? "F0" : row.Speed >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture) + (row.Differs ? " ≠" : string.Empty);

    private static string Speedup(Row? row, Row? reference)
    {
        if (row is null || reference is null)
        {
            return "–";
        }

        if (double.IsNaN(row.Low))
        {
            return (row.Speed / reference.Speed).ToString("F2", CultureInfo.InvariantCulture) + "×";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{row.Speedup:F2}× ({row.Low:F2}–{row.High:F2})");
    }
}
