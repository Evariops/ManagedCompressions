using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ManagedCompressions.Zstd.Bench;

namespace ManagedCompressions.Bench;

/// <summary>
/// A candidate's median throughput, in MB/s of content (10^6 bytes), on a data set at a level, in one
/// direction; and its speedup over <see cref="Against"/> from the timings paired round by round, with
/// the speedup's 95% interval (<see cref="PairedRatio"/>). An A/A candidate is compared with the
/// candidate it duplicates, every other one with the native reference.
/// </summary>
internal sealed record Row(
    string Codec, string Set, int Level, string Direction, string Candidate, string Label, bool Native, double Speed, double Ratio, bool Differs,
    string Against = "", double Speedup = double.NaN, double Low = double.NaN, double High = double.NaN, int Pairs = 0)
{
    public string Key => $"{Codec}/{Set}/{Level}/{Direction}/{Candidate}";

    /// <summary>The paired speedup over <see cref="Against"/>, NaN where there is none.</summary>
    public PairedRatio Paired => new(Speedup, Low, High, Pairs);

    /// <summary>Whether this row is an A/A arm: the same code as <see cref="Against"/>, under another name.</summary>
    public bool IsAa => Candidate.EndsWith(Candidates.AaSuffix, StringComparison.Ordinal);

    public string ToTsv() => string.Join('\t', Codec, Set, Level.ToString(CultureInfo.InvariantCulture), Direction, Candidate, Label,
        Native ? "native" : "managed", Format(Speed), Format(Ratio), Differs ? "differs" : "same",
        Against, Format(Speedup), Format(Low), Format(High), Pairs.ToString(CultureInfo.InvariantCulture));

    /// <summary>A line of <see cref="ToTsv"/>, or of the earlier ten-column format, which has no paired speedup.</summary>
    public static Row FromTsv(string line)
    {
        string[] f = line.Split('\t');
        var row = new Row(f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), f[3], f[4], f[5], f[6] == "native",
            Parse(f[7]), Parse(f[8]), f[9] == "differs");
        return f.Length < 15 ? row : row with { Against = f[10], Speedup = Parse(f[11]), Low = Parse(f[12]), High = Parse(f[13]), Pairs = int.Parse(f[14], CultureInfo.InvariantCulture) };
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>
/// The measure of one codec on one data set at one level. Every candidate is first warmed up, so that
/// the JIT has its final code; then the candidates compress the data set, then decode the reference's
/// output, each direction in rounds that time every candidate once, in an order that rotates, so that
/// whatever the machine does at that moment lands on all of them alike. The first round is also the
/// check: every compressed record must decode back to its content with the reference's decoder, and
/// every decoder must give the content back.
/// </summary>
internal static class Harness
{
    private const int MinRounds = 3;
    private const int MaxRounds = 400;

    /// <summary><c>--samples 1</c>: every candidate's time in each round is printed, in the rounds' order.</summary>
    public static bool PrintSamples { get; set; }

    public static List<Row> Measure(string codec, DataSet set, int level, List<Candidate> candidates, double budget, bool compress, bool decompress, string? only = null)
    {
        int records = set.Records;
        int[] bounds = Bounds(codec, set);

        // Every page of the outputs touched before anything is timed.
        byte[] reference = Touched(bounds[^1]);
        byte[] scratch = Touched(bounds[^1]);
        byte[] decoded = Touched(set.Content.Length);
        int[] sizes = new int[records];
        int[] written = new int[records];
        int count = candidates.Count;
        Candidate referenceCandidate = candidates[0];

        Warm(codec, set, candidates);

        // The reference's records, written once untimed: the decoders' input and the frames to match.
        Pass(referenceCandidate.Compress!, set.Content, set.Offsets, reference, bounds, sizes);
        long referenceBytes = sizes.Sum(s => (long)s);

        var ratio = new double[count];
        var compressionDiffers = new bool[count];
        double[][] compression = new double[count][];
        // --only: the others are not timed (the reference still writes the records once).
        List<int> timed = Enumerable.Range(0, count).Where(c => only is null || candidates[c].Name == only).ToList();
        List<int> compressing = timed.Where(c => candidates[c].Compress is not null).ToList();
        if (compress)
        {
            compression = Time(count, compressing, budget, (c, round) =>
            {
                double seconds = Pass(candidates[c].Compress!, set.Content, set.Offsets, scratch, bounds, written);
                if (round == 0)
                {
                    ratio[c] = (double)set.Content.Length / written.Sum(w => (long)w);
                    compressionDiffers[c] = !RoundTrips(referenceCandidate, set, scratch, bounds, written, decoded)
                        || (c != 0 && !SameRecords(scratch, written, reference, sizes, bounds));
                }

                return seconds;
            });
        }

        (byte[] frames, int[] frameOffsets) = Concatenate(reference, bounds, sizes);
        var decompressionDiffers = new bool[count];
        double[][] decompression = new double[count][];
        if (decompress)
        {
            decompression = Time(count, timed, budget, (c, round) =>
            {
                if (round > 0)
                {
                    return Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
                }

                Array.Fill(decoded, (byte)1);
                double seconds = Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
                decompressionDiffers[c] = !Decoded(set, decoded, written);
                return seconds;
            });
        }

        double referenceRatio = (double)set.Content.Length / referenceBytes;
        var rows = new List<Row>();
        foreach (int c in compressing.Where(_ => compress))
        {
            rows.Add(MakeRow(codec, set.Name, level, "compression", candidates, compression, c, set.Content.Length, ratio[c], compressionDiffers[c]));
        }

        foreach (int c in timed.Where(_ => decompress))
        {
            rows.Add(MakeRow(codec, set.Name, level, "decompression", candidates, decompression, c, set.Content.Length, referenceRatio, decompressionDiffers[c]));
        }

        return rows;
    }

    /// <summary>
    /// The row of candidate <paramref name="c"/>: its median throughput over <paramref name="bytes"/> a
    /// pass, and its paired speedup over the candidate an A/A arm duplicates, or over the reference.
    /// </summary>
    internal static Row MakeRow(string codec, string set, int level, string direction, List<Candidate> candidates, double[][] samples, int c, double bytes, double ratio, bool differs)
    {
        Candidate candidate = candidates[c];
        if (PrintSamples)
        {
            Console.WriteLine($"    {direction} {candidate.Name} ms by round: " +
                string.Join(' ', samples[c].Select(s => (s * 1000).ToString("F2", CultureInfo.InvariantCulture))));
        }

        int against = candidate.Name.EndsWith(Candidates.AaSuffix, StringComparison.Ordinal)
            ? candidates.FindIndex(x => x.Name == candidate.Name[..^Candidates.AaSuffix.Length])
            : 0;
        var row = new Row(codec, set, level, direction, candidate.Name, candidate.Label, candidate.Native, bytes / Median(samples[c]) / 1e6, ratio, differs);
        if (against < 0 || against == c || samples[against] is null)
        {
            return row;
        }

        PairedRatio paired = PairedRatio.OfMedian(samples[against], samples[c]);
        return row with { Against = candidates[against].Name, Speedup = paired.Speedup, Low = paired.Low, High = paired.High, Pairs = paired.Pairs };
    }

    /// <summary>Each record's slot in the compressed output: the larger bound of the two implementations.</summary>
    internal static int[] Bounds(string codec, DataSet set)
    {
        int[] bounds = new int[set.Records + 1];
        for (int i = 0; i < set.Records; i++)
        {
            bounds[i + 1] = bounds[i] + Candidates.Bound(codec, set.Offsets[i + 1] - set.Offsets[i]);
        }

        return bounds;
    }

    /// <summary>A buffer with every page already touched, so that no page fault lands in a timed pass.</summary>
    internal static byte[] Touched(int length)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, (byte)1);
        return buffer;
    }

    /// <summary>The records <paramref name="sizes"/> says were written in their slots, one after the other.</summary>
    internal static (byte[] Frames, int[] Offsets) Concatenate(byte[] compressed, int[] bounds, int[] sizes)
    {
        int[] offsets = new int[sizes.Length + 1];
        for (int i = 0; i < sizes.Length; i++)
        {
            offsets[i + 1] = offsets[i] + sizes[i];
        }

        byte[] frames = new byte[offsets[^1]];
        for (int i = 0; i < sizes.Length; i++)
        {
            compressed.AsSpan(bounds[i], sizes[i]).CopyTo(frames.AsSpan(offsets[i]));
        }

        return (frames, offsets);
    }

    /// <summary>Whether a decoder gave every record back, at its length.</summary>
    internal static bool Decoded(DataSet set, byte[] decoded, int[] written)
    {
        if (!decoded.AsSpan().SequenceEqual(set.Content))
        {
            return false;
        }

        for (int i = 0; i < set.Records; i++)
        {
            if (written[i] != set.Offsets[i + 1] - set.Offsets[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a compressor wrote the reference's records, byte for byte.</summary>
    internal static bool SameRecords(byte[] scratch, int[] written, byte[] reference, int[] sizes, int[] bounds)
    {
        for (int i = 0; i < sizes.Length; i++)
        {
            if (written[i] != sizes[i] || !scratch.AsSpan(bounds[i], sizes[i]).SequenceEqual(reference.AsSpan(bounds[i], sizes[i])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Each candidate in both directions over the first MiB or so of the data set, at least 30 times
    /// and half a second, at most four seconds: long enough for the tiered JIT to give every method its
    /// final, profiled code. The first MiB of a large record selects the same parameters as the whole.
    /// </summary>
    internal static void Warm(string codec, DataSet set, List<Candidate> candidates)
    {
        var offsets = new List<int> { 0 };
        int total = 0;
        for (int i = 0; i < set.Records && total < 1 << 20; i++)
        {
            total += Math.Min(set.Offsets[i + 1] - set.Offsets[i], 1 << 20);
            offsets.Add(total);
        }

        byte[] content = new byte[total];
        for (int i = 0; i + 1 < offsets.Count; i++)
        {
            set.Content.AsSpan(set.Offsets[i], offsets[i + 1] - offsets[i]).CopyTo(content.AsSpan(offsets[i]));
        }

        int[] o = offsets.ToArray();
        int[] bounds = new int[o.Length];
        for (int i = 0; i + 1 < o.Length; i++)
        {
            bounds[i + 1] = bounds[i] + Candidates.Bound(codec, o[i + 1] - o[i]);
        }

        byte[] compressed = new byte[bounds[^1]];
        byte[] decoded = new byte[total];
        int[] sizes = new int[o.Length - 1];
        int[] written = new int[o.Length - 1];
        Pass(candidates[0].Compress!, content, o, compressed, bounds, sizes);
        (byte[] frames, int[] frameOffsets) = Concatenate(compressed, bounds, sizes);
        byte[] scratch = new byte[bounds[^1]];
        foreach (Candidate candidate in candidates)
        {
            long start = Stopwatch.GetTimestamp();
            for (int n = 0; ; n++)
            {
                double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                if ((n >= 30 && elapsed >= 0.5) || elapsed >= 4)
                {
                    break;
                }

                if (candidate.Compress is not null)
                {
                    Pass(candidate.Compress, content, o, scratch, bounds, written);
                }

                Pass(candidate.Decompress, frames, frameOffsets, decoded, o, written);
            }
        }
    }

    /// <summary>Whether every record a candidate wrote decodes back to its content with the reference's decoder.</summary>
    internal static bool RoundTrips(Candidate reference, DataSet set, byte[] compressed, int[] bounds, int[] written, byte[] decoded)
    {
        for (int i = 0; i < set.Records; i++)
        {
            if (written[i] < 0)
            {
                return false;
            }

            Span<byte> into = decoded.AsSpan(set.Offsets[i], set.Offsets[i + 1] - set.Offsets[i]);
            if (reference.Decompress(compressed.AsSpan(bounds[i], written[i]), into) != into.Length || !into.SequenceEqual(set.Record(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The codec over every record, into its slot of the output; the seconds it took.</summary>
    internal static double Pass(SpanCodec codec, byte[] input, int[] inputOffsets, byte[] output, int[] outputOffsets, int[] written)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = codec(
                input.AsSpan(inputOffsets[i], inputOffsets[i + 1] - inputOffsets[i]),
                output.AsSpan(outputOffsets[i], outputOffsets[i + 1] - outputOffsets[i]));
        }

        return Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    /// <summary>
    /// Rounds of <paramref name="run"/>(candidate, round), every candidate of <paramref name="which"/>
    /// once a round in an order that rotates; as many as fit in the budget given what the first took.
    /// Each candidate's seconds, round by round (element k measured in round k + 1), null for the others.
    /// </summary>
    /// <remarks>
    /// Round 0 checks the outputs and sizes the budget, and is not kept: it is the first pass over the
    /// whole data set, which the warm-up's first MiB does not prepare for (a decoder's first frame of
    /// 200 MB ran 40% slower than its next ones).
    /// </remarks>
    internal static double[][] Time(int count, List<int> which, double budget, Func<int, int, double> run)
    {
        var samples = new double[count][];
        if (which.Count == 0)
        {
            return samples;
        }

        var first = new double[count];
        foreach (int c in which)
        {
            first[c] = run(c, 0);
        }

        int rounds = Math.Clamp((int)Math.Ceiling(budget / first.Sum()), MinRounds, MaxRounds);
        foreach (int c in which)
        {
            samples[c] = new double[rounds];
        }

        for (int r = 1; r <= rounds; r++)
        {
            for (int k = 0; k < which.Count; k++)
            {
                int c = which[(r + k) % which.Count];
                samples[c][r - 1] = run(c, r);
            }
        }

        return samples;
    }

    /// <summary>The median of the samples, which are left in their order.</summary>
    internal static double Median(double[] samples)
    {
        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
