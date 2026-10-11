using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ManagedCompressions.Bench;

/// <summary>
/// The managed codecs against NativeCompressions, level by level, on one thread.
/// </summary>
/// <remarks>
/// Usage: <c>ManagedCompressions.Bench [--codecs zstd,lz4] [--sets silesia,github] [--levels 1,3]
/// [--seconds S] [--directions compression,decompression] [--results file.tsv] [--markdown file.md]</c>,
/// or <c>--report file.tsv [--markdown file.md]</c> to render results already measured. A direction
/// is timed for some <c>--seconds</c> (3 by default), never fewer than three rounds. <c>--results</c>
/// appends each level's measures as soon as they are taken, and the tables are rendered from the
/// whole file, a later measure replacing an earlier one.
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
        if (options.TryGetValue("report", out string? report))
        {
            return Render(Read(report), options.GetValueOrDefault("markdown"));
        }

        string[] codecs = options.GetValueOrDefault("codecs", "zstd,lz4").Split(',');
        double budget = double.Parse(options.GetValueOrDefault("seconds", "3"), CultureInfo.InvariantCulture);
        string directions = options.GetValueOrDefault("directions", "compression,decompression");
        bool compress = directions.Contains("compression", StringComparison.Ordinal) && !directions.Equals("decompression", StringComparison.Ordinal);
        bool decompress = directions.Contains("decompression", StringComparison.Ordinal);
        string? results = options.GetValueOrDefault("results");

        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")}, " +
            $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, some {budget} s a direction");
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
                    var release = new List<Action>();
                    List<Candidate> candidates = Candidates.For(codec, set, level, release);
                    List<Row> measured = Harness.Measure(codec, set, level, candidates, budget, compress, decompress);
                    release.ForEach(r => r());
                    Print(measured);
                    rows.AddRange(measured);
                    if (results is not null)
                    {
                        File.AppendAllLines(results, measured.Select(r => r.ToTsv()));
                    }
                }
            }
        }

        return Render(results is null ? rows : Read(results), options.GetValueOrDefault("markdown"));
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

    private static void Print(List<Row> rows)
    {
        foreach (IGrouping<string, Row> direction in rows.GroupBy(r => r.Direction))
        {
            Row reference = direction.First(r => r.Native);
            var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"  L{reference.Level,-3} {direction.Key,-13}"));
            foreach (Row row in direction)
            {
                line.Append(CultureInfo.InvariantCulture, $" | {row.Candidate} {row.Speed,8:F1} MB/s ratio {row.Ratio:F3}");
                if (row != reference)
                {
                    line.Append(CultureInfo.InvariantCulture, $" ({row.Speed / reference.Speed:F2}x)");
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
    /// throughputs and the managed side's speedup over the native one.
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
                Row? Find(string direction, bool native) => level.LastOrDefault(r => r.Direction == direction && r.Native == native);
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

        md.AppendLine("Throughputs in MB/s of uncompressed data (10^6 bytes a second), one thread, median of the rounds. " +
            "Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.");
        Console.WriteLine();
        Console.Write(md);
        if (markdown is not null)
        {
            File.WriteAllText(markdown, md.ToString());
        }

        return 0;
    }

    private static string Format(double? value, string format) => value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "–";

    private static string Speed(Row? row) => row is null ? "–"
        : row.Speed.ToString(row.Speed >= 100 ? "F0" : row.Speed >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture) + (row.Differs ? " ≠" : string.Empty);

    private static string Speedup(Row? row, Row? reference) => row is null || reference is null ? "–"
        : (row.Speed / reference.Speed).ToString("F2", CultureInfo.InvariantCulture) + "×";
}
